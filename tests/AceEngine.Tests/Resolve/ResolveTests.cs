using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Tests.P2P;
using AceEngine.Transport;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Resolve
{
    public class MiniJsonTests
    {
        [Fact]
        public void ParsesAllTypes()
        {
            var o = MiniJson.AsObject(MiniJson.Parse("{\"a\":1,\"b\":-2.5e1,\"c\":[true,false,null,\"x\"],\"d\":{\"e\":\"f\"},\"g\":12345678901234}"));
            Assert.Equal(1L, o["a"]);
            Assert.Equal(-25.0, o["b"]);
            var c = (List<object>)o["c"];
            Assert.Equal(new object[] { true, false, null, "x" }, c.ToArray());
            Assert.Equal("f", MiniJson.GetString(MiniJson.GetObject(o, "d"), "e"));
            Assert.Equal(12345678901234L, MiniJson.GetLong(o, "g"));
        }

        [Fact]
        public void HandlesEscapesBomAndSurrogates()
        {
            Assert.Equal("a\"b\\c/d\b\f\n\r\t", MiniJson.Parse("\"a\\\"b\\\\c\\/d\\b\\f\\n\\r\\t\""));
            Assert.Equal("\u00e9\u20ac", MiniJson.Parse("\"\\u00e9\\u20AC\""));
            Assert.Equal("\U0001F600", MiniJson.Parse("\"\\ud83d\\ude00\""));
            Assert.Equal("ok", MiniJson.Parse("\uFEFF \"ok\" "));
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("[1,]")]
        [InlineData("{\"a\" 1}")]
        [InlineData("\"abc")]
        [InlineData("tru")]
        [InlineData("1 2")]
        [InlineData("\"\\x\"")]
        public void RejectsMalformed(string text)
        {
            object v;
            Assert.False(MiniJson.TryParse(text, out v));
        }

        [Fact]
        public void RejectsExcessiveNesting()
        {
            object v;
            Assert.False(MiniJson.TryParse(new string('[', 200) + new string(']', 200), out v));
        }

        [Fact]
        public void StringifyRoundTrips()
        {
            var d = new Dictionary<string, object>();
            d["s"] = "a\"\n\u0001é"; d["n"] = 5; d["l"] = new List<object> { 1L, "x", null, true }; d["f"] = 1.5;
            var json = MiniJson.Stringify(d);
            Assert.Equal("{\"s\":\"a\\\"\\n\\u0001é\",\"n\":5,\"l\":[1,\"x\",null,true],\"f\":1.5}", json);
            var back = MiniJson.AsObject(MiniJson.Parse(json));
            Assert.Equal("a\"\n\u0001é", back["s"]);
            Assert.Contains("\n  \"n\": 5", MiniJson.Stringify(d, true));
        }
    }

    public class ResolutionCacheTests
    {
        private static byte[] Data(int seed)
        {
            var b = new byte[100 + seed]; new Random(seed).NextBytes(b); return b;
        }

        [Fact]
        public void StoresAndExpires()
        {
            var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            var cache = new ResolutionCache(TimeSpan.FromHours(1), utcNow: () => now);
            var id = "2940120bf034db79a7f5849846ccea0255172eae";
            Assert.True(cache.Put(id.ToUpperInvariant(), Data(1)));
            byte[] got;
            Assert.True(cache.TryGet(id, out got));
            Assert.Equal(Data(1), got);
            now = now.AddMinutes(59);
            Assert.True(cache.TryGet(id, out got));
            now = now.AddMinutes(2);
            Assert.False(cache.TryGet(id, out got));
            Assert.Equal(0, cache.Count);
        }

        [Fact]
        public void RejectsBadKeysAndEmptyData()
        {
            var cache = new ResolutionCache(TimeSpan.FromHours(1));
            Assert.False(cache.Put("zz", Data(1)));
            Assert.False(cache.Put("2940120bf034db79a7f5849846ccea0255172eae", new byte[0]));
        }

        [Fact]
        public void EvictsOldestWhenFull()
        {
            var now = DateTime.UtcNow;
            var cache = new ResolutionCache(TimeSpan.FromHours(1), maxEntries: 3, utcNow: () => now);
            for (int i = 0; i < 5; i++)
            {
                cache.Put(Hex.ToHex(HashUtil.Sha1(new[] { (byte)i })), Data(i));
                now = now.AddSeconds(1);
            }
            Assert.Equal(3, cache.Count);
            byte[] x;
            Assert.False(cache.TryGet(Hex.ToHex(HashUtil.Sha1(new[] { (byte)0 })), out x));
            Assert.True(cache.TryGet(Hex.ToHex(HashUtil.Sha1(new[] { (byte)4 })), out x));
        }

        [Fact]
        public void PersistsToJsonAndSkipsExpiredOrCorruptEntries()
        {
            var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            var now = t0;
            var a = new ResolutionCache(TimeSpan.FromHours(1), utcNow: () => now);
            a.Put("2940120bf034db79a7f5849846ccea0255172eae", Data(1));
            now = t0.AddMinutes(50);
            a.Put("38f7b2044e549df2039ff26cefa6f9a60c854d5e", Data(2));
            string json = a.ToJson();

            now = t0.AddMinutes(70);                       // the first entry already expired
            var b = new ResolutionCache(TimeSpan.FromHours(1), utcNow: () => now);
            Assert.Equal(1, b.LoadJson(json));
            byte[] got;
            Assert.True(b.TryGet("38f7b2044e549df2039ff26cefa6f9a60c854d5e", out got));
            Assert.Equal(Data(2), got);
            Assert.False(b.TryGet("2940120bf034db79a7f5849846ccea0255172eae", out got));
            Assert.Equal(0, b.LoadJson("esto no es json"));
            Assert.Equal(0, b.LoadJson("{\"v\":1,\"entries\":[{\"id\":\"x\",\"t\":1,\"d\":\"!!\"}]}"));
        }

        [Fact]
        public void SavesAndLoadsFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "ace-cache-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var a = new ResolutionCache(TimeSpan.FromHours(1));
                a.Put("2940120bf034db79a7f5849846ccea0255172eae", Data(3));
                a.Save(path);
                var b = new ResolutionCache(TimeSpan.FromHours(1));
                Assert.Equal(1, b.Load(path));
                Assert.Equal(0, new ResolutionCache(TimeSpan.FromHours(1)).Load(path + ".nope"));
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }

    public class RealTransportFileTests
    {
        // Transport files obtained from the Ace network on 2026-10-16.
        private static TransportFile Load(string contentId)
        {
            return TransportLoader.Load(TestUtil.Fixture("real/" + contentId + ".acelive"));
        }

        [Theory]
        [InlineData("2940120bf034db79a7f5849846ccea0255172eae", "#VAMOS FHD --> NEW ERA", 524288, 697587, "20db14f6e9cd048c12006e7f9e5e8326390eaff1", "f0599fbf71f409c88d370312baf80159138431b9")]
        [InlineData("38f7b2044e549df2039ff26cefa6f9a60c854d5e", null, 1048576, 902408, "c8424e272e15aa4c6e3f636126d490a46903902c", "6d6d6b6ea6a08d3d7494bf8b6f31764f35c1bdcc")]
        [InlineData("54a4a28882d3cae1ca19de5d4b022ebea40351f9", "Eleven Sports 2 HD [PL]", 1048576, 948563, "ae964a3fb5009d462ceb6e60dd56b3cfa8159620", "40397ebb3549ea95e7623fa11e4fc18aa80ca742")]
        public void LoadsRealLiveDescriptors(string requestedId, string name, int pieceLength, int bitrate, string infohash, string fileSha1)
        {
            var tf = Load(requestedId);
            Assert.Equal(TransportFormat.AceStreamTransportV2, tf.Format);
            Assert.Equal(IvMode.Sha1OfMd5Key, tf.IvUsed);              // CONFIRMED with real data: the IV is SHA1(MD5(key))[:16]
            Assert.True(tf.PaddingWasValid);
            Assert.Equal("bt", tf.Type);
            Assert.True(tf.IsLive);
            if (name != null) Assert.Equal(name, tf.Name);
            Assert.Equal("RSA", tf.Live.AuthMethod);
            Assert.Equal(pieceLength, tf.Live.PieceLength);
            Assert.Equal(bitrate, tf.Live.Bitrate);
            Assert.Equal(infohash, tf.InfoHashHex);
            // The link's content_id is NOT the SHA-1 of the file that the peers serve.
            Assert.Equal(fileSha1, tf.ContentId);
            Assert.NotEqual(requestedId, tf.ContentId);
        }

        [Fact]
        public void RealDescriptorFieldsAreParsedLikeThePeersSendThem()
        {
            var live = Load("2940120bf034db79a7f5849846ccea0255172eae").Live;
            Assert.Equal(16384, live.ChunkLength);
            Assert.Equal(96, SignSizeOf(live.PubKey));
            // 'trackers' arrives as a single string with three URLs separated by spaces
            Assert.Equal(new[] { "udp://tracker.opentrackr.org:1337/announce", "udp://93.158.213.92:1337/announce", "udp://186.10.170.198:1337/announce" }, live.Trackers.ToArray());
        }

        private static int SignSizeOf(byte[] spki)
        {
            // 768-bit SPKI: the length of the modulus n is 96 bytes (+ 1 sign byte in DER)
            return spki.Length >= 122 ? 96 : -1;
        }

        [Fact]
        public void MediaFilesOfARealLiveChannel()
        {
            var tf = Load("2940120bf034db79a7f5849846ccea0255172eae");
            var res = MediaFiles.Get(tf, new MediaFilesOptions { DumpTransportFile = true });
            Assert.Equal("live", res.Type);
            Assert.Equal("bt", res.TransportType);
            Assert.Equal("20db14f6e9cd048c12006e7f9e5e8326390eaff1", res.Infohash);
            var f = res.Files.Single();
            Assert.Equal(0, f.Index); Assert.Equal("video/mp2t", f.Mime); Assert.Equal(0, f.Size);
            Assert.Equal(Convert.ToBase64String(tf.RawBytes), res.TransportFileData);
        }
    }

    public class TsServiceTests
    {
        private static readonly string Cid = "c8c33b2673efe7f386790c5966643820578f3535";

        [Fact]
        public void SignatureMatchesIndependentComputation()
        {
            // Vector computed separately with Python: sha1(b'#'.join(sorted('k=v')) + SECRET)
            var opt = new ResolverOptions();
            var ps = new Dictionary<string, string> { { "pid", Cid }, { "x", "a b&c" } };
            var url = TsServiceClient.BuildUrl("http://s1.test/", "gettorrent", ps, opt, 1792177534, 123456789);
            Assert.StartsWith("http://s1.test/gettorrent?_r=123456789&_t=1792177534&_v=3.2.22&pid=" + Cid + "&x=a+b%26c&_s=", url);
            Assert.EndsWith("&_s=14e90a16cd46a2a90b5b9591292504cb4f4509b9", url);
        }

        [Fact]
        public void ReservedParametersAreRejected()
        {
            Assert.Throws<ArgumentException>(() => TsServiceClient.BuildUrl("http://s", "m", new Dictionary<string, string> { { "_r", "1" } }, new ResolverOptions(), 1, 1));
        }

        private static string Xml(byte[] torrent, string extra = "")
        {
            return "<?xml version=\"1.0\"?><response r=\"7\" sig=\"abc\"><result><torrent>" + Convert.ToBase64String(torrent) + "</torrent><checksum>" + Hex.ToHex(HashUtil.Sha1(torrent)) + "</checksum>" + extra + "</result></response>";
        }

        [Fact]
        public void ParsesXmlResponses()
        {
            var t = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
            string err;
            var got = TsServiceClient.ParseResponse(Encoding.UTF8.GetBytes(Xml(t)), Hex.ToHex(HashUtil.Sha1(t)), 7, new ResolverOptions(), out err);
            Assert.Equal(t, got);
            // base64 with line breaks and an uppercase label
            var b64 = Convert.ToBase64String(t, Base64FormattingOptions.InsertLineBreaks);
            got = TsServiceClient.ParseResponse(Encoding.UTF8.GetBytes("<r><TORRENT>" + b64 + "</TORRENT></r>"), "x", 0, new ResolverOptions(), out err);
            Assert.Equal(t, got);
        }

        [Fact]
        public void ReportsErrorsWithoutTorrent()
        {
            string err;
            Assert.Null(TsServiceClient.ParseResponse(new byte[0], Cid, 1, new ResolverOptions(), out err));
            Assert.Equal("Empty response", err);
            Assert.Null(TsServiceClient.ParseResponse(Encoding.UTF8.GetBytes("<response><status>error</status></response>"), Cid, 1, new ResolverOptions(), out err));
            Assert.Contains("error", err);
            Assert.Null(TsServiceClient.ParseResponse(Encoding.UTF8.GetBytes("<a><b></a>"), Cid, 1, new ResolverOptions(), out err));
            Assert.Null(TsServiceClient.ParseResponse(Encoding.UTF8.GetBytes("<!DOCTYPE html PUBLIC \"-//W3C//DTD HTML 4.01//EN\"><html>HTTP 451</html>"), Cid, 1, new ResolverOptions(), out err));
        }

        [Fact]
        public void StrictModeChecksRandomAndSignature()
        {
            var opt = new ResolverOptions { TsVerifyResponseSignature = true };
            var t = new byte[] { 1, 2, 3, 4 };
            string err;
            Assert.Null(TsServiceClient.ParseResponse(Encoding.UTF8.GetBytes(Xml(t)), Cid, 8, opt, out err));          // different r
            Assert.Equal("Bad random", err);
            Assert.Null(TsServiceClient.ParseResponse(Encoding.UTF8.GetBytes(Xml(t)), Cid, 7, opt, out err));          // wrong sig
            Assert.Equal("Bad signature", err);
            // with the correct signature (sha1 of the text without the sig attribute + secret)
            string body = "<response r=\"7\"><torrent>AQIDBA==</torrent></response>";
            var payload = Encoding.UTF8.GetBytes(body).Concat(opt.TsSecret).ToArray();
            string sig = Hex.ToHex(HashUtil.Sha1(payload));
            string signed = "<response r=\"7\" sig=\"" + sig + "\"><torrent>AQIDBA==</torrent></response>";
            Assert.Equal(t, TsServiceClient.ParseResponse(Encoding.UTF8.GetBytes(signed), Cid, 7, opt, out err));
        }

        [Fact]
        public async Task EndToEndWithSignedRequestAgainstFakeServer()
        {
            var opt = new ResolverOptions { TsMaxHosts = 2 };
            var transport = Enumerable.Range(0, 200).Select(i => (byte)(i * 3)).ToArray();
            using (var srv = new FakeTsService(opt.TsSecret))
            using (var http = new HttpClient())
            {
                srv.Handler = q => q.ContainsKey("pid") && q["pid"] == Cid ? Xml(transport) : "";
                opt.TsHosts = new List<string> { srv.Url };
                var client = new TsServiceClient(opt, http);
                var got = await client.GetTorrentAsync(Cid, false, CancellationToken.None);
                Assert.Equal(transport, got);
                var req = srv.Requests.Single();
                Assert.Contains("/gettorrent?", req);
                Assert.Contains("pid=" + Cid, req);
                Assert.Contains("_v=3.2.22", req);
                Assert.Contains("_s=", req);
                // an unknown pid: the server answers empty -> null
                Assert.Null(await client.GetTorrentAsync("0000000000000000000000000000000000000001", false, CancellationToken.None));
                // infohash
                srv.Handler = q => q.ContainsKey("infohash") ? Xml(transport) : "";
                Assert.Equal(transport, await client.GetTorrentAsync(Cid, true, CancellationToken.None));
            }
        }

        [Fact]
        public async Task UnsignedOrWrongSecretIsAnsweredWithNothing()
        {
            var opt = new ResolverOptions { TsSecret = new byte[] { 1, 2, 3 } };
            using (var srv = new FakeTsService(new ResolverOptions().TsSecret))
            using (var http = new HttpClient())
            {
                srv.Handler = q => Xml(new byte[] { 9 });
                opt.TsHosts = new List<string> { srv.Url };
                Assert.Null(await new TsServiceClient(opt, http).GetTorrentAsync(Cid, false, CancellationToken.None));
            }
        }

        [Fact]
        public async Task HostErrorsAndTimeoutsFallThroughToNextHost()
        {
            var opt = new ResolverOptions { TsRequestTimeout = TimeSpan.FromSeconds(2), TsMaxHosts = 5 };
            var transport = new byte[] { 5, 6, 7, 8, 9 };
            using (var bad = new FakeTsService(opt.TsSecret) { Status = 451 })
            using (var good = new FakeTsService(opt.TsSecret))
            using (var http = new HttpClient())
            {
                bad.Handler = q => "<html>451</html>";
                good.Handler = q => Xml(transport);
                opt.TsHosts = new List<string> { "http://127.0.0.1:9", bad.Url, good.Url };       // the first one refuses the connection
                // the order is shuffled: it does not matter which one is first, up to 5 hosts are tried
                var got = await new TsServiceClient(opt, http).GetTorrentAsync(Cid, false, CancellationToken.None);
                Assert.Equal(transport, got);
            }
        }
    }

    public class MetadataFetcherTests
    {
        private static byte[] LiveTransportBytes() { return TestUtil.Fixture("real/2940120bf034db79a7f5849846ccea0255172eae.acelive"); }

        private static EngineOptions Opt()
        {
            var o = new EngineOptions { EnableDht = false, EnableTrackers = false };
            o.Resolver.WeakFallbackSeconds = 0.5;
            return o;
        }

        [Fact]
        public async Task FullMetadataWithShaMatchIsAcceptedFromOnePeer()
        {
            var data = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
            var id = HashUtil.Sha1(data);
            using (var peer = new FakeAceMetadataPeer { FullData = data })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var got = await f.FetchAsync(id, MetadataMode.AceFull, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(data, got);
                Assert.Equal(1, peer.FullRequests);
                Assert.StartsWith("-ST0100-", peer.LastClientPeerId);                    // MiniBitTorrent's peer_id
                // the client announces ut_pex=1, ut_metadata=2 and, since the peer answers AceStreamProtocol, pv=2 and ct=metadata
                Assert.Contains("2:pvi2e", peer.LastClientHandshakeDict);
                Assert.Contains("2:ct8:metadata", peer.LastClientHandshakeDict);
                Assert.Contains("11:ut_metadatai2e", peer.LastClientHandshakeDict);
            }
        }

        [Fact]
        public async Task WrongDataWithoutMatchingRequestHashIsRejected()
        {
            var data = LiveTransportBytes();
            var id = new byte[20]; id[0] = 1;                   // any content_id: the SHA-1 does not match
            using (var peer = new FakeAceMetadataPeer { FullData = data, HashMode = FakeAceMetadataPeer.RequestHashMode.Wrong })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var got = await f.FetchAsync(id, MetadataMode.AceFull, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None);
                Assert.Null(got);
            }
        }

        [Fact]
        public async Task GarbageWithMatchingRequestHashIsRejectedBecauseItIsNotATransportFile()
        {
            var id = new byte[20]; id[0] = 2;
            using (var peer = new FakeAceMetadataPeer { FullData = new byte[400] })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                Assert.Null(await f.FetchAsync(id, MetadataMode.AceFull, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None));
            }
        }

        [Fact]
        public async Task RequestHashMatchIsAcceptedAfterFallbackWhenOnlyOnePeerAnswers()
        {
            // Real case: the link's content_id is not the SHA-1 of the file and the peer answers with request_hash = content_id.
            var data = LiveTransportBytes();
            var id = TestUtil.FromHex("2940120bf034db79a7f5849846ccea0255172eae");
            using (var peer = new FakeAceMetadataPeer { FullData = data })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var got = await f.FetchAsync(id, MetadataMode.AceFull, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(data, got);
                Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(400), "it must wait for the quorum deadline");
                Assert.Equal(1, peer.FullRequests);                  // the same peer is not asked again
            }
        }

        [Fact]
        public async Task QuorumOfTwoPeersAcceptsImmediately()
        {
            var data = LiveTransportBytes();
            var id = TestUtil.FromHex("2940120bf034db79a7f5849846ccea0255172eae");
            var opt = Opt();
            opt.Resolver.WeakFallbackSeconds = 60;
            using (var a = new FakeAceMetadataPeer("127.0.1.1") { FullData = data })
            using (var b = new FakeAceMetadataPeer("127.0.2.1") { FullData = data })
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var got = await f.FetchAsync(id, MetadataMode.AceFull, null, new[] { a.EndPoint, b.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(data, got);
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task TwoPeersWithDifferentFilesDoNotFormAQuorum()
        {
            var good = LiveTransportBytes();
            var other = TestUtil.Fixture("real/54a4a28882d3cae1ca19de5d4b022ebea40351f9.acelive");
            var id = TestUtil.FromHex("2940120bf034db79a7f5849846ccea0255172eae");
            var opt = Opt();
            opt.Resolver.WeakFallbackSeconds = -1;           // no deadline: only the quorum counts
            using (var a = new FakeAceMetadataPeer { FullData = good })
            using (var b = new FakeAceMetadataPeer { FullData = other })
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                Assert.Null(await f.FetchAsync(id, MetadataMode.AceFull, null, new[] { a.EndPoint, b.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None));
            }
        }

        [Fact]
        public async Task RequestHashAcceptanceCanBeDisabled()
        {
            var data = LiveTransportBytes();
            var id = TestUtil.FromHex("2940120bf034db79a7f5849846ccea0255172eae");
            var opt = Opt();
            opt.Resolver.AcceptRequestHash = false;
            using (var peer = new FakeAceMetadataPeer { FullData = data })
            {
                var f = new MetadataFetcher(opt, null, new HttpClient());
                Assert.Null(await f.FetchAsync(id, MetadataMode.AceFull, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None));
            }
        }

        [Fact]
        public async Task NonAcePeerCannotServeFullMetadata()
        {
            var data = new byte[] { 1, 2, 3 };
            using (var peer = new FakeAceMetadataPeer { FullData = data, AdvertiseAce = false, UseAceProtocolName = false })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                Assert.Null(await f.FetchAsync(HashUtil.Sha1(data), MetadataMode.AceFull, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(2), CancellationToken.None));
            }
        }

        [Fact]
        public async Task Bep9InfoIsFetchedByPiecesWithAceTotalSizeQuirk()
        {
            var info = TestUtil.Fixture("sintel.torrent");
            // the 'info' of a real ~20 KB torrent takes two 16 KiB pieces
            var t = TransportLoader.Load(info).Torrent;
            using (var peer = new FakeAceMetadataPeer { InfoData = t.InfoRaw, UseAceProtocolName = false })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var got = await f.FetchAsync(t.InfoHash, MetadataMode.Bep9, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(t.InfoRaw, got);
                Assert.Equal((t.InfoRaw.Length + 16383) / 16384, peer.PieceRequests);
            }
        }

        [Fact]
        public async Task CorruptBep9InfoIsDiscarded()
        {
            var t = TransportLoader.Load(TestUtil.Fixture("sintel.torrent")).Torrent;
            var bad = (byte[])t.InfoRaw.Clone(); bad[bad.Length / 2] ^= 0xff;
            using (var peer = new FakeAceMetadataPeer { InfoData = bad, UseAceProtocolName = false })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                Assert.Null(await f.FetchAsync(t.InfoHash, MetadataMode.Bep9, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None));
            }
        }

        [Fact]
        public async Task DeadPeersAndTimeoutReturnNullWithoutThrowing()
        {
            var f = new MetadataFetcher(Opt(), null, new HttpClient());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var got = await f.FetchAsync(new byte[20], MetadataMode.AceFull, null, new[] { new IPEndPoint(IPAddress.Loopback, 9) }, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Null(got);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task CancellationThrows()
        {
            var f = new MetadataFetcher(Opt(), null, new HttpClient());
            using (var cts = new CancellationTokenSource(200))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.FetchAsync(new byte[20], MetadataMode.AceFull, null, null, TimeSpan.FromSeconds(30), cts.Token));
        }

        [Fact]
        public async Task FindsPeersThroughTheDhtAndTrackers()
        {
            var data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 13)).ToArray();
            var id = HashUtil.Sha1(data);
            using (var peer = new FakeAceMetadataPeer { FullData = data })
            using (var dhtNode = new FakeDhtNode())
            {
                dhtNode.Peers.Add(peer.EndPoint);
                var opt = Opt();
                opt.EnableDht = true;
                opt.DhtBootstrap = new List<string> { "127.0.0.1:" + dhtNode.Port };
                using (var dht = new DhtClient(opt))
                {
                    dht.Start();
                    var f = new MetadataFetcher(opt, dht, new HttpClient());
                    var got = await f.FetchAsync(id, MetadataMode.AceFull, null, null, TimeSpan.FromSeconds(15), CancellationToken.None);
                    Assert.Equal(data, got);
                    Assert.True(dhtNode.GetPeersReceived >= 1);
                }
            }
            using (var peer = new FakeAceMetadataPeer { FullData = data })
            using (var tracker = new FakeHttpTracker())
            {
                tracker.Peers.Add(peer.EndPoint);
                var opt = Opt();
                opt.EnableTrackers = true;
                var f = new MetadataFetcher(opt, null, new HttpClient());
                var got = await f.FetchAsync(id, MetadataMode.AceFull, new[] { tracker.AnnounceUrl }, null, TimeSpan.FromSeconds(15), CancellationToken.None);
                Assert.Equal(data, got);
            }
        }
    }

    public class TransportResolverTests
    {
        private static byte[] RealBytes() { return TestUtil.Fixture("real/2940120bf034db79a7f5849846ccea0255172eae.acelive"); }
        private const string RealId = "2940120bf034db79a7f5849846ccea0255172eae";

        private static EngineOptions Opt(FakeAceMetadataPeer peer = null, FakeTsService ts = null)
        {
            var o = new EngineOptions { EnableDht = false, EnableTrackers = false };
            o.Resolver.WeakFallbackSeconds = 0.3;
            o.Resolver.Timeout = TimeSpan.FromSeconds(10);
            o.Resolver.EnableP2p = peer != null;
            o.Resolver.EnableTsService = ts != null;
            if (peer != null) o.StaticPeers.Add(peer.EndPoint.ToString());
            if (ts != null) o.Resolver.TsHosts = new List<string> { ts.Url };
            return o;
        }

        private static string Xml(byte[] torrent)
        {
            return "<response r=\"1\"><torrent>" + Convert.ToBase64String(torrent) + "</torrent></response>";
        }

        [Fact]
        public async Task ContentIdResolvedByP2pEndToEnd()
        {
            using (var peer = new FakeAceMetadataPeer { FullData = RealBytes() })
            using (var r = new TransportResolver(Opt(peer), null))
            {
                var res = await r.ResolveAsync(AceLink.Parse(RealId), CancellationToken.None);
                Assert.Equal("magnet", res.Source);
                Assert.Equal(RealId, res.RequestedContentId);
                Assert.Equal("20db14f6e9cd048c12006e7f9e5e8326390eaff1", res.Transport.InfoHashHex);
                var mf = MediaFiles.Get(res.Transport);
                Assert.Equal("live", mf.Type);
                // second time: it comes from the cache and the peer is not asked again
                int before = peer.FullRequests;
                var again = await r.ResolveAsync(AceLink.Parse("acestream://" + RealId), CancellationToken.None);
                Assert.Equal("local_db", again.Source);
                Assert.Equal(before, peer.FullRequests);
            }
        }

        [Fact]
        public async Task ContentIdResolvedByTsServiceWhenP2pIsOff()
        {
            using (var ts = new FakeTsService(new ResolverOptions().TsSecret))
            {
                var t = TestUtil.Fixture("real/38f7b2044e549df2039ff26cefa6f9a60c854d5e.acelive");
                string id = Hex.ToHex(HashUtil.Sha1(t));        // here the server does return a file whose SHA-1 is the id
                ts.Handler = q => q["pid"] == id ? Xml(t) : "";
                using (var r = new TransportResolver(Opt(null, ts), null))
                {
                    var res = await r.ResolveAsync(AceLink.ContentId(id), CancellationToken.None);
                    Assert.Equal("server", res.Source);
                    Assert.Equal(1048576, res.Transport.Live.PieceLength);
                }
            }
        }

        [Fact]
        public async Task TsServiceDataWithWrongShaIsDiscarded()
        {
            using (var ts = new FakeTsService(new ResolverOptions().TsSecret))
            {
                ts.Handler = q => Xml(RealBytes());          // its SHA-1 is not the requested content_id: the server gives no request_hash, it is not accepted
                using (var r = new TransportResolver(Opt(null, ts), null))
                    await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.ContentId(RealId), CancellationToken.None));
            }
        }

        [Fact]
        public async Task FirstViaToAnswerWinsAndTheOtherIsCancelled()
        {
            var t = TestUtil.Fixture("real/54a4a28882d3cae1ca19de5d4b022ebea40351f9.acelive");
            string id = Hex.ToHex(HashUtil.Sha1(t));
            using (var slowPeer = new FakeAceMetadataPeer { FullData = t, HandshakeDelayMs = 3000 })
            using (var ts = new FakeTsService(new ResolverOptions().TsSecret))
            {
                ts.Handler = q => Xml(t);
                using (var r = new TransportResolver(Opt(slowPeer, ts), null))
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var res = await r.ResolveAsync(AceLink.ContentId(id), CancellationToken.None);
                    Assert.Equal("server", res.Source);
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5));
                }
            }
        }

        [Fact]
        public async Task FailsWithAceMessageWhenNobodyHasIt()
        {
            var o = Opt(null, null);
            o.Resolver.Timeout = TimeSpan.FromSeconds(2);
            using (var r = new TransportResolver(o, null))
            {
                var ex = await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.ContentId(RealId), CancellationToken.None));
                Assert.Contains("cannot get transport file", ex.Message);
            }
            var o2 = Opt(new FakeAceMetadataPeer { RejectAll = true });
            o2.Resolver.Timeout = TimeSpan.FromSeconds(2);
            using (var r = new TransportResolver(o2, null))
            {
                var ex = await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.ContentId(RealId), CancellationToken.None));
                Assert.Contains("cannot get transport file", ex.Message);
            }
        }

        [Fact]
        public async Task LocalCacheOnlyReportsMissingTransportFile()
        {
            using (var r = new TransportResolver(Opt(), null))
            {
                var ex = await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.ContentId(RealId), CancellationToken.None, true));
                Assert.Equal("no_transport_file_in_local_cache", ex.Message);
                r.Cache.Put(RealId, RealBytes());
                var res = await r.ResolveAsync(AceLink.ContentId(RealId), CancellationToken.None, true);
                Assert.Equal("local_db", res.Source);
            }
        }

        [Fact]
        public async Task FileAndUrlAreLoadedDirectly()
        {
            var path = Path.Combine(Path.GetTempPath(), "x-" + Guid.NewGuid().ToString("N") + ".acelive");
            File.WriteAllBytes(path, RealBytes());
            try
            {
                using (var r = new TransportResolver(Opt(), null))
                {
                    var res = await r.ResolveAsync(AceLink.Parse(path), CancellationToken.None);
                    Assert.Equal("file", res.Source);
                    Assert.Equal("20db14f6e9cd048c12006e7f9e5e8326390eaff1", res.Transport.InfoHashHex);
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task InfoHashIsResolvedByBep9IntoAMinimalTorrent()
        {
            var t = TransportLoader.Load(TestUtil.Fixture("sintel.torrent")).Torrent;
            using (var peer = new FakeAceMetadataPeer { InfoData = t.InfoRaw, UseAceProtocolName = false })
            using (var r = new TransportResolver(Opt(peer), null))
            {
                var link = AceLink.Parse("magnet:?xt=urn:btih:" + t.InfoHashHex + "&tr=udp%3A%2F%2Fexample.test%3A1%2Fannounce");
                var res = await r.ResolveAsync(link, CancellationToken.None);
                Assert.False(res.HasContentId);
                Assert.Equal(t.InfoHashHex, res.Transport.InfoHashHex);
                Assert.Equal("Sintel", res.Transport.Name);
                Assert.Contains("udp://example.test:1/announce", res.Transport.Torrent.GetTrackerTiers().SelectMany(x => x));
                var mf = MediaFiles.Get(res.Transport);
                Assert.Equal("vod", mf.Type);
            }
        }

        [Fact]
        public void MinimalTorrentKeepsTheInfoBytesVerbatim()
        {
            var t = TransportLoader.Load(TestUtil.Fixture("sintel.torrent")).Torrent;
            var bytes = TransportResolver.BuildMinimalTorrent(t.InfoRaw, new[] { "udp://a:1/announce", "http://b/announce" });
            var back = TransportLoader.Load(bytes).Torrent;
            Assert.Equal(t.InfoHash, back.InfoHash);
            Assert.Equal("udp://a:1/announce", back.Announce);
            Assert.Equal(2, back.GetTrackerTiers().Count);
        }
    }

    public class MediaFilesOptionsTests
    {
        [Fact]
        public void ShortModeOnlyHasFilenameAndIndex()
        {
            var tf = TransportLoader.Load(TestUtil.Fixture("sintel.torrent"));
            var json = MiniJson.AsObject(MiniJson.Parse(MediaFiles.Get(tf, new MediaFilesOptions { Full = false }).ToJson()));
            var f = MiniJson.AsObject(MiniJson.AsArray(json["files"])[0]);
            Assert.Equal(new[] { "filename", "index" }, f.Keys.OrderBy(x => x).ToArray());
            Assert.False(json.ContainsKey("transport_file_data"));
        }

        [Fact]
        public void FullModeJsonHasTheMediaFileResponseFields()
        {
            var tf = TransportLoader.Load(TestUtil.Fixture("sintel.torrent"));
            var res = MediaFiles.Get(tf, new MediaFilesOptions { DumpTransportFile = true });
            var json = MiniJson.AsObject(MiniJson.Parse(res.ToJson()));
            Assert.Equal("vod", json["type"]); Assert.Equal("bt", json["transport_type"]); Assert.Equal("Sintel", json["name"]);
            Assert.Equal("08ada5a7a6183aae1e09d831df6748d566095a10", json["infohash"]);
            var f = MiniJson.AsObject(MiniJson.AsArray(json["files"])[0]);
            Assert.Equal(new[] { "filename", "index", "infohash", "mime", "size", "transport_type", "type" }, f.Keys.OrderBy(x => x).ToArray());
            Assert.Equal("video/mp4", f["mime"]);
            Assert.True((long)f["size"] > 100000000);
            Assert.Equal(tf.RawBytes, Convert.FromBase64String((string)json["transport_file_data"]));
        }

        [Fact]
        public void MultiQualityListsOneItemPerQuality()
        {
            var tf = TransportLoader.Load(TestUtil.Fixture("multi_v2.acelive"));
            var res = MediaFiles.Get(tf);
            Assert.Equal(tf.Qualities.Count, res.Files.Count);
            Assert.All(res.Files, f => Assert.Equal("live", f.Type));
            Assert.Equal(Enumerable.Range(0, tf.Qualities.Count).ToArray(), res.Files.Select(f => f.Index).ToArray());
        }

        [Fact]
        public void WrapperExpansionUsesItsMetadata()
        {
            var root = new BDict();
            root.Add("type", BValue.Str("wrapper")); root.Add("name", BValue.Str("Mi canal")); root.Add("mime", BValue.Str("video/mp4"));
            var bytes = TransportLoader.Build(root, TransportFormat.AceStreamTransportV2, IvMode.Sha1OfMd5Key);
            var tf = TransportLoader.Load(bytes);
            var res = MediaFiles.Get(tf);
            Assert.Equal("wrapper", res.TransportType);
            Assert.Equal("video/mp4", res.Files[0].Mime);
            Assert.Equal("Mi canal", res.WrapperData["title"]);
            Assert.Null(MediaFiles.Get(tf, new MediaFilesOptions { ExpandWrapper = false }).WrapperData);
        }
    }
}
