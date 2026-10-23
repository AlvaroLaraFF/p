using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Resolve;
using AceEngine.Transport;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Resolve
{
    public class NetworkKeyTests
    {
        [Fact]
        public void Ipv4GroupsBySlash24AndIpv6BySlash48()
        {
            Assert.Equal(TrustStore.NetworkKey(IPAddress.Parse("10.1.2.3")), TrustStore.NetworkKey(IPAddress.Parse("10.1.2.250")));
            Assert.NotEqual(TrustStore.NetworkKey(IPAddress.Parse("10.1.2.3")), TrustStore.NetworkKey(IPAddress.Parse("10.1.3.3")));
            Assert.Equal(TrustStore.NetworkKey(IPAddress.Parse("2001:db8:1:1::1")), TrustStore.NetworkKey(IPAddress.Parse("2001:db8:1:ffff::9")));
            Assert.NotEqual(TrustStore.NetworkKey(IPAddress.Parse("2001:db8:1::1")), TrustStore.NetworkKey(IPAddress.Parse("2001:db8:2::1")));
            Assert.Equal(TrustStore.NetworkKey(IPAddress.Parse("1.2.3.4")), TrustStore.NetworkKey(IPAddress.Parse("::ffff:1.2.3.99")));
        }
    }

    public class NameCheckTests
    {
        [Theory]
        [InlineData("M+ Liga de Campeones 3 720p *", "Campeones 3 720 elcano.top https://x.com/ace_stream_ https://t.me/acestreamf1", true)]
        [InlineData("M+ Baloncesto 1080p **", "M+ Baloncesto 1080 elcano.top zeronet", true)]
        [InlineData("M+ Liga de Campeones 3 720p *", "Pelicula de accion 2019", false)]
        [InlineData("DAZN 1", "Canal 1", false)]
        [InlineData("", "cualquier cosa", true)]
        [InlineData("algo", "", true)]
        [InlineData("Teledeporte HD", "TELEDEPORTE", true)]
        [InlineData("Fútbol Cuatro", "Futbol cuatro 720", true)]
        public void Plausibility(string list, string transport, bool expected) { Assert.Equal(expected, NameCheck.Plausible(list, transport)); }
    }

    public class TrustStoreTests
    {
        private static string Tmp() { return Path.Combine(Path.GetTempPath(), "trust-" + Guid.NewGuid().ToString("N") + ".json"); }
        private static readonly string Id = "2940120bf034db79a7f5849846ccea0255172eae";

        private static byte[] Real(string name) { return TestUtil.Fixture("real/" + name + ".acelive"); }

        private static TransportFile Load(byte[] raw) { return TransportLoader.Load(raw, new TransportLoadOptions { Strict = true }); }

        [Fact]
        public void RealTransportFilesPassStrictValidation()
        {
            foreach (var n in new[] { "2940120bf034db79a7f5849846ccea0255172eae", "38f7b2044e549df2039ff26cefa6f9a60c854d5e", "54a4a28882d3cae1ca19de5d4b022ebea40351f9" })
            {
                var tf = Load(Real(n));
                Assert.NotNull(tf.Live);
                Assert.True(tf.PaddingWasValid);
            }
        }

        [Fact]
        public void FirstUseIsRecordedAndSecondUseIsRecognised()
        {
            var path = Tmp();
            try
            {
                var raw = Real(Id);
                var tf = Load(raw);
                var ts = new TrustStore(path);
                var d1 = ts.Evaluate(Id, tf, raw, new FetchReport { Networks = 1, Peers = 1 }, 2, true);
                Assert.Equal(TrustLevel.Unconfirmed, d1.Level);
                Assert.True(d1.IsNew);
                Assert.NotNull(d1.Note);
                var ts2 = new TrustStore(path);                     // persisted
                var d2 = ts2.Evaluate(Id, tf, raw, new FetchReport { Networks = 2, Peers = 2 }, 2, true);
                Assert.Equal(TrustLevel.Confirmed, d2.Level);
                Assert.False(d2.IsNew);
                // once confirmed, a single peer no longer lowers the level
                var d3 = new TrustStore(path).Evaluate(Id, tf, raw, new FetchReport { Networks = 1, Peers = 1 }, 2, true);
                Assert.Equal(TrustLevel.Confirmed, d3.Level);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ChangedIdentityWithoutQuorumIsRejected()
        {
            var ts = new TrustStore(null);
            var rawA = Real("2940120bf034db79a7f5849846ccea0255172eae");
            var rawB = Real("54a4a28882d3cae1ca19de5d4b022ebea40351f9");          // another channel: another key and another infohash
            ts.Evaluate(Id, Load(rawA), rawA, new FetchReport { Networks = 3, Peers = 3 }, 2, true);
            var ex = Assert.Throws<ResolveException>(() => ts.Evaluate(Id, Load(rawB), rawB, new FetchReport { Networks = 1, Peers = 1 }, 2, true));
            Assert.Contains("has changed", ex.Message);
            // what was saved has not been touched
            Assert.Equal(Hex.ToHex(HashUtil.Sha1(rawA)), ts.Get(Id).Checksum);
        }

        [Fact]
        public void ChangedIdentityWithClearQuorumIsAcceptedWithAWarning()
        {
            var ts = new TrustStore(null);
            var rawA = Real("2940120bf034db79a7f5849846ccea0255172eae");
            var rawB = Real("54a4a28882d3cae1ca19de5d4b022ebea40351f9");
            ts.Evaluate(Id, Load(rawA), rawA, new FetchReport { Networks = 1, Peers = 1 }, 2, true);
            var d = ts.Evaluate(Id, Load(rawB), rawB, new FetchReport { Networks = 3, Peers = 4 }, 2, true);
            Assert.Equal(TrustLevel.Changed, d.Level);
            Assert.Contains("ha cambiado", d.Note);
            Assert.Equal(Hex.ToHex(HashUtil.Sha1(rawB)), ts.Get(Id).Checksum);
        }

        [Fact]
        public void ChangedIdentityIsOnlyAWarningWhenNotEnforced()
        {
            var ts = new TrustStore(null);
            var rawA = Real("2940120bf034db79a7f5849846ccea0255172eae");
            var rawB = Real("54a4a28882d3cae1ca19de5d4b022ebea40351f9");
            ts.Evaluate(Id, Load(rawA), rawA, new FetchReport { Networks = 1, Peers = 1 }, 2, false);
            var d = ts.Evaluate(Id, Load(rawB), rawB, new FetchReport { Networks = 1, Peers = 1 }, 2, false);
            Assert.Equal(TrustLevel.Changed, d.Level);
        }

        [Fact]
        public void SameIdentityDifferentBytesIsAcceptedAndUpdated()
        {
            var ts = new TrustStore(null);
            var rawA = Real("2940120bf034db79a7f5849846ccea0255172eae");
            var tfA = Load(rawA);
            // same descriptor with other padding/order: it is rebuilt with other trackers but the same identity
            var root = (BDict)BencodeParser.Parse(tfA.Payload);
            var d2 = new BDict();
            foreach (var kv in root.Entries) d2.Add(kv.Key, kv.Value);
            d2.Add("zz_extra", BValue.Int(1));
            var rawB = TransportLoader.Build(d2, TransportFormat.AceStreamTransportV2, IvMode.Sha1OfMd5Key);
            var tfB = Load(rawB);
            Assert.Equal(tfA.Live.InfoHashHex, tfB.Live.InfoHashHex);
            ts.Evaluate(Id, tfA, rawA, new FetchReport { Networks = 2, Peers = 2 }, 2, true);
            var d = ts.Evaluate(Id, tfB, rawB, new FetchReport { Networks = 1, Peers = 1 }, 2, true);
            Assert.NotEqual(TrustLevel.Changed, d.Level);
            Assert.Equal(Hex.ToHex(HashUtil.Sha1(rawB)), ts.Get(Id).Checksum);
        }

        [Fact]
        public void CorruptFileIsIgnoredAndStoreIsBounded()
        {
            var path = Tmp();
            try
            {
                File.WriteAllText(path, "{{{ no es json");
                Assert.Equal(0, new TrustStore(path).Count);
                File.WriteAllText(path, "[{\"id\":\"zz\",\"sum\":\"x\"},{\"id\":\"" + Id + "\",\"sum\":\"abc\"}]");
                Assert.Equal(1, new TrustStore(path).Count);
            }
            finally { File.Delete(path); }
        }
    }

    public class QuorumByNetworkTests
    {
        private static byte[] Good() { return TestUtil.Fixture("real/2940120bf034db79a7f5849846ccea0255172eae.acelive"); }
        private static byte[] Other() { return TestUtil.Fixture("real/54a4a28882d3cae1ca19de5d4b022ebea40351f9.acelive"); }
        private static readonly byte[] Id = TestUtil.FromHex("2940120bf034db79a7f5849846ccea0255172eae");

        private static AceEngine.P2P.EngineOptions Opt()
        {
            var o = new AceEngine.P2P.EngineOptions { EnableDht = false, EnableTrackers = false, EnableListener = false };
            o.Resolver.InactiveTimeout = TimeSpan.FromSeconds(5);
            return o;
        }

        [Fact]
        public async Task TwoPeersOfTheSameNetworkAreOneWitness()
        {
            var opt = Opt(); opt.Resolver.WeakFallbackSeconds = -1;
            using (var a = new FakeAceMetadataPeer("127.0.1.1") { FullData = Good() })
            using (var b = new FakeAceMetadataPeer("127.0.1.2") { FullData = Good() })     // same /24
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                var rep = new FetchReport();
                var got = await f.FetchAsync(Id, MetadataMode.AceFull, null, new[] { a.EndPoint, b.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None, null, rep);
                Assert.Null(got);                                                         // no network quorum and no deadline: it is not accepted
            }
        }

        [Fact]
        public async Task TwoNetworksFormAQuorumAndTheReportSaysSo()
        {
            var opt = Opt(); opt.Resolver.WeakFallbackSeconds = -1;
            using (var a = new FakeAceMetadataPeer("127.0.1.1") { FullData = Good() })
            using (var b = new FakeAceMetadataPeer("127.0.2.1") { FullData = Good() })
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                var rep = new FetchReport();
                var got = await f.FetchAsync(Id, MetadataMode.AceFull, null, new[] { a.EndPoint, b.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None, null, rep);
                Assert.Equal(Good(), got);
                Assert.Equal(2, rep.Networks);
                Assert.False(rep.ExactHash);
                Assert.False(rep.Conflict);
            }
        }

        [Fact]
        public async Task MajorityOfNetworksWinsOverALyingPeer()
        {
            var opt = Opt(); opt.Resolver.WeakFallbackSeconds = -1;
            using (var a = new FakeAceMetadataPeer("127.0.1.1") { FullData = Good() })
            using (var b = new FakeAceMetadataPeer("127.0.2.1") { FullData = Good() })
            using (var c = new FakeAceMetadataPeer("127.0.3.1") { FullData = Other() })
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                var rep = new FetchReport();
                var got = await f.FetchAsync(Id, MetadataMode.AceFull, null, new[] { c.EndPoint, a.EndPoint, b.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None, null, rep);
                Assert.Equal(Good(), got);
                Assert.True(rep.Networks >= 2);
            }
        }

        [Fact]
        public async Task TieBetweenTwoDifferentFilesAcceptsNothingEvenAfterTheFallback()
        {
            var opt = Opt(); opt.Resolver.WeakFallbackSeconds = 0.3;
            using (var a = new FakeAceMetadataPeer("127.0.1.1") { FullData = Good() })
            using (var b = new FakeAceMetadataPeer("127.0.2.1") { FullData = Other() })
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                Assert.Null(await f.FetchAsync(Id, MetadataMode.AceFull, null, new[] { a.EndPoint, b.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None));
            }
        }

        [Fact]
        public async Task SinglePeerFallbackIsReportedAsUnconfirmed()
        {
            var opt = Opt(); opt.Resolver.WeakFallbackSeconds = 0.3;
            using (var a = new FakeAceMetadataPeer("127.0.1.1") { FullData = Good() })
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                var rep = new FetchReport();
                var got = await f.FetchAsync(Id, MetadataMode.AceFull, null, new[] { a.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None, null, rep);
                Assert.Equal(Good(), got);
                Assert.Equal(1, rep.Networks);
            }
        }
    }
}
