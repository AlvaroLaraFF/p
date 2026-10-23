using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Bencode;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Sim;
using AceEngine.Sources;
using AceEngine.Tests.App;
using AceEngine.Tests.Live;
using AceEngine.Transport;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Resolve
{
    /// <summary>
    /// REAL vectors from the Ace network (2026-10-17): the transport file of a live stream as served by an Ace engine over ut_metadata 'full' when it is
    /// asked for the swarm's INFOHASH (the one given by the central search engine), not a content_id. The file name is the requested infohash. They were obtained with
    /// `ace play magnet:?xt=urn:btih:&lt;infohash&gt; --save` on real results of `ace search --ace`.
    /// </summary>
    internal static class InfoHashVectors
    {
        public static IEnumerable<object[]> All()
        {
            foreach (var f in Directory.GetFiles(TestUtil.FixturePath("real/infohash"), "*.acelive").OrderBy(x => x, StringComparer.Ordinal))
                yield return new object[] { Path.GetFileNameWithoutExtension(f) };
        }

        public static byte[] Bytes(string infohash) { return TestUtil.Fixture("real/infohash/" + infohash + ".acelive"); }
        public static byte[] Hash(string infohash) { return Hex.Parse(infohash); }

        /// <summary>Copy of the descriptor with one field changed (or removed if value is null) and re-encrypted with the same key: what a peer that tampers with the file would do.</summary>
        public static byte[] WithField(byte[] file, string key, BValue value)
        {
            var tf = TransportLoader.Load(file);
            var d = new BDict();
            foreach (var e in tf.Root.Entries)
            {
                if (e.Key.AsText() == key) { if (value != null) d.Add(key, value); }
                else d.Add(e.Key, e.Value);
            }
            if (value != null && !tf.Root.ContainsKey(key)) d.Add(key, value);
            return TransportLoader.Build(d, tf.Format, tf.IvUsed);
        }
    }

    public class InfoHashVerifierTests
    {
        public static IEnumerable<object[]> Vectors() { return InfoHashVectors.All(); }

        [Fact]
        public void ThereAreRealVectorsToTest()
        {
            Assert.True(InfoHashVectors.All().Count() >= 5, "real vectors are missing in Fixtures/real/infohash");
        }

        [Theory, MemberData(nameof(Vectors))]
        public void RealLiveDescriptorMatchesTheInfoHashItWasAskedFor(string infohash)
        {
            string why;
            var tf = InfoHashVerifier.Match(InfoHashVectors.Bytes(infohash), InfoHashVectors.Hash(infohash), true, out why);
            Assert.True(tf != null, why);
            Assert.Equal("bt", tf.Type);
            Assert.True(tf.IsLive);
            Assert.Equal(infohash, tf.InfoHashHex);
            Assert.Equal("RSA", tf.Live.AuthMethod);
            Assert.Equal(TransportFormat.AceStreamTransportV2, tf.Format);
            Assert.True(tf.PaddingWasValid);
            // the file's SHA-1 is NEITHER the infohash NOR a content_id: here the swarm's infohash was requested and that is the only datum that authenticates it
            Assert.NotEqual(infohash, tf.ContentId);
        }

        [Theory]
        [InlineData("0560234787945a17522e284c4c22bb4df29f33b0", "DAZN1", 1048576, 802401)]
        [InlineData("07473038aa16ed164f5989234fafd303eecf67dc", "M+ Wéstern HD Spain", 1048576, 937389)]       // the search-engine channel that would not open on the TV
        [InlineData("07793c990e158208c29207faecb10769595beeea", "Sky Cinema Special HD [DE]", 524288, 653988)]
        [InlineData("0c4fc5ecca41212e4c1a22654271d331cef49f13", "TVP3 [PL]", 262144, 304588)]
        [InlineData("0d4ad3f3d66b9124453ded032b1ddf2c0b4394ae", "RTL+ SPORT EVENT 6 [DE]", 131072, 99090)]
        [InlineData("13ddaf59c0f2d760fd3775acc254e13825206eac", "Premier Sports 1 [UK]", 262144, 213291)]
        [InlineData("47e35c1dc2f572229e37a4ec819df6c6ec47768d", "Sport 5 HD", 524288, 500000)]
        [InlineData("58b193890946b099a7d2c7dca37a48b36a12467f", "Polsat Sport Premium 1 [PL]", 1048576, 2039973)]
        [InlineData("8a25653a2f774f4ae1062d38a30dcd714d304a3a", "DAZN LaLiga", 1048576, 800000)]
        [InlineData("a0fd9380808627dc9dd061348caa6065e7b6037a", "M. Deportes 2 HD", 1048576, 800000)]
        public void RealVectorsHaveTheFieldsTheSearchResultAnnounced(string infohash, string name, int pieceLength, int bitrate)
        {
            var live = TransportLoader.Load(InfoHashVectors.Bytes(infohash)).Live;
            Assert.Equal(name, live.Name);
            Assert.Equal(pieceLength, live.PieceLength);
            Assert.Equal(bitrate, live.Bitrate);                            // bytes/s, like the search engine's 'b' field
            Assert.Equal(16384, live.ChunkLength);
            Assert.Equal(infohash, live.InfoHashHex);
            Assert.NotEmpty(live.Trackers);
        }

        [Theory, MemberData(nameof(Vectors))]
        public void ItAlsoWorksWithTheTolerantLoader(string infohash)
        {
            Assert.True(InfoHashVerifier.Matches(InfoHashVectors.Bytes(infohash), InfoHashVectors.Hash(infohash), false));
        }

        [Theory, MemberData(nameof(Vectors))]
        public void AnyOtherInfoHashIsRejected(string infohash)
        {
            var h = InfoHashVectors.Hash(infohash);
            for (int i = 0; i < 20; i += 7)
            {
                var other = (byte[])h.Clone();
                other[i] ^= 0x01;
                string why;
                Assert.Null(InfoHashVerifier.Match(InfoHashVectors.Bytes(infohash), other, true, out why));
                Assert.Contains("infohash", why);
            }
            string why2;
            Assert.Null(InfoHashVerifier.Match(InfoHashVectors.Bytes(infohash), new byte[20], true, out why2));
            Assert.Null(InfoHashVerifier.Match(InfoHashVectors.Bytes(infohash), null, true, out why2));
            Assert.Null(InfoHashVerifier.Match(InfoHashVectors.Bytes(infohash), new byte[19], true, out why2));
        }

        [Fact]
        public void TheDescriptorOfAnotherChannelIsRejected()
        {
            var all = InfoHashVectors.All().Select(x => (string)x[0]).ToList();
            foreach (var a in all)
                foreach (var b in all)
                {
                    if (a == b) continue;
                    Assert.False(InfoHashVerifier.Matches(InfoHashVectors.Bytes(a), InfoHashVectors.Hash(b), true), a + " cannot pass for " + b);
                }
        }

        [Theory, MemberData(nameof(Vectors))]
        public void ChangingAnyFieldCoveredByTheHashIsRejected(string infohash)
        {
            var file = InfoHashVectors.Bytes(infohash);
            var h = InfoHashVectors.Hash(infohash);
            var tf = TransportLoader.Load(file);
            Assert.True(InfoHashVerifier.Matches(file, h, true));
            // the name, the public key, the piece size and the bitrate are in hash_fields: a peer cannot change them
            Assert.False(InfoHashVerifier.Matches(InfoHashVectors.WithField(file, "name", BValue.Str(tf.Name + "x")), h, true));
            var pk = (byte[])tf.Live.PubKey.Clone(); pk[pk.Length - 3] ^= 0x10;
            Assert.False(InfoHashVerifier.Matches(InfoHashVectors.WithField(file, "pubkey", BValue.Bytes(pk)), h, true));
            Assert.False(InfoHashVerifier.Matches(InfoHashVectors.WithField(file, "piece_length", BValue.Int(tf.Live.PieceLength * 2)), h, true));
            Assert.False(InfoHashVerifier.Matches(InfoHashVectors.WithField(file, "bitrate", BValue.Int(tf.Live.Bitrate + 1)), h, true));
            Assert.False(InfoHashVerifier.Matches(InfoHashVectors.WithField(file, "authmethod", BValue.Str("None")), h, true));
            // removing a mandatory field: it is not even interpreted
            Assert.False(InfoHashVerifier.Matches(InfoHashVectors.WithField(file, "pubkey", null), h, true));
        }

        [Theory, MemberData(nameof(Vectors))]
        public void FieldsNotCoveredByTheHashDoNotChangeTheResult(string infohash)
        {
            // Known limit: trackers, meta_trackers, startup_nodes and title are NOT in the hash, so a peer can change them. What matters (the public key
            // that signs the pieces) is covered: a descriptor with foreign trackers can only send us to other peers, never unsigned data.
            var file = InfoHashVectors.Bytes(infohash);
            var h = InfoHashVectors.Hash(infohash);
            var changed = InfoHashVectors.WithField(file, "trackers", BValue.List(BValue.Str("udp://example.test:1/announce")));
            string why;
            var tf = InfoHashVerifier.Match(changed, h, true, out why);
            Assert.True(tf != null, why);
            Assert.Equal(new[] { "udp://example.test:1/announce" }, tf.Live.Trackers.ToArray());
            Assert.Equal(TransportLoader.Load(file).Live.PubKey, tf.Live.PubKey);
        }

        [Theory, MemberData(nameof(Vectors))]
        public void CorruptedOrTruncatedBytesNeverThrowAndNeverMatchAnotherChannel(string infohash)
        {
            var file = InfoHashVectors.Bytes(infohash);
            var h = InfoHashVectors.Hash(infohash);
            // every prefix shorter than the file: rejected without an exception
            for (int n = 0; n < file.Length; n += 5)
            {
                var cut = new byte[n]; Array.Copy(file, cut, n);
                string why;
                Assert.Null(InfoHashVerifier.Match(cut, h, true, out why));
            }
            // a byte flipped at any position: either it is rejected, or (if it falls in a field the hash does not cover, like the trackers) the infohash is still the requested one
            int rejected = 0, kept = 0;
            for (int i = 0; i < file.Length; i++)
            {
                var bad = (byte[])file.Clone();
                bad[i] ^= 0x40;
                string why;
                var tf = InfoHashVerifier.Match(bad, h, true, out why);
                if (tf == null) { rejected++; continue; }
                kept++;
                Assert.Equal(infohash, tf.InfoHashHex);
            }
            Assert.True(rejected > file.Length / 2, "only " + rejected + " of " + file.Length + " were rejected");
            Assert.True(rejected + kept == file.Length);
        }

        [Fact]
        public void VodTorrentIsVerifiedWithTheSha1OfItsInfo()
        {
            var sintel = TestUtil.Fixture("sintel.torrent");
            var t = TransportLoader.Load(sintel).Torrent;
            string why;
            var tf = InfoHashVerifier.Match(sintel, t.InfoHash, true, out why);
            Assert.True(tf != null, why);
            Assert.False(tf.IsLive);
            Assert.Equal("Sintel", tf.Name);
            var other = (byte[])t.InfoHash.Clone(); other[0] ^= 1;
            Assert.Null(InfoHashVerifier.Match(sintel, other, true, out why));
        }

        [Fact]
        public void MultiQualityGivesBackTheQualityThatMatches()
        {
            var multi = TestUtil.Fixture("multi_v2.acelive");
            var m = TransportLoader.Load(multi);
            Assert.True(m.IsMulti);
            foreach (var q in m.Qualities.Where(x => x.Data != null))
            {
                string why;
                var tf = InfoHashVerifier.Match(multi, q.Data.InfoHash, true, out why);
                Assert.True(tf != null, why);
                Assert.False(tf.IsMulti);                                  // the quality is returned as its own file
                Assert.Equal(q.Data.InfoHashHex, tf.InfoHashHex);
                Assert.Equal(q.Data.RawBytes, tf.RawBytes);
            }
            string why2;
            Assert.Null(InfoHashVerifier.Match(multi, new byte[20], true, out why2));
            Assert.Contains("qualities", why2);
        }

        [Fact]
        public void HlsDescriptorsHaveNoInfoHashToCheckAndAreRejected()
        {
            var hls = TestUtil.Fixture("hls_v1.acelive");
            string why;
            Assert.Null(InfoHashVerifier.Match(hls, new byte[20], true, out why));
            Assert.Contains("hls", why);
        }

        [Fact]
        public void GarbageAndEmptyInputsAreRejectedWithoutThrowing()
        {
            string why;
            Assert.Null(InfoHashVerifier.Match(null, new byte[20], true, out why));
            Assert.Null(InfoHashVerifier.Match(new byte[0], new byte[20], true, out why));
            Assert.Null(InfoHashVerifier.Match(new byte[] { 1, 2, 3 }, new byte[20], true, out why));
            Assert.Null(InfoHashVerifier.Match(Enumerable.Repeat((byte)'d', 2000).ToArray(), new byte[20], true, out why));
            Assert.Null(InfoHashVerifier.Match(TestUtil.Ascii("AceStreamTransport"), new byte[20], true, out why));
        }
    }

    /// <summary>MetadataFetcher in InfoHash mode: each peer chooses between BEP9 (VOD) and the full transport (Ace live stream without 'info').</summary>
    public class InfoHashFetchTests
    {
        private static EngineOptions Opt()
        {
            var o = new EngineOptions { EnableDht = false, EnableTrackers = false };
            o.Resolver.WeakFallbackSeconds = 0.5;
            return o;
        }

        private static string RealHash() { return InfoHashVectors.All().Select(x => (string)x[0]).First(); }

        [Fact]
        public async Task LiveSwarmPeerWithoutMetadataSizeServesTheWholeDescriptor()
        {
            string ih = RealHash();
            var file = InfoHashVectors.Bytes(ih);
            using (var peer = new FakeAceMetadataPeer { FullData = file, AdvertiseMetadataSize = false })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var report = new FetchReport();
                var got = await f.FetchAsync(InfoHashVectors.Hash(ih), MetadataMode.InfoHash, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None, null, report);
                Assert.Equal(file, got);
                Assert.Equal(MetadataPayload.Transport, report.Payload);
                Assert.True(report.ExactHash);
                Assert.Equal(1, peer.FullRequests);
                Assert.Equal(0, peer.PieceRequests);                  // BEP9 was never requested from a peer without metadata_size
            }
        }

        [Fact]
        public async Task TheDescriptorIsAcceptedWhateverRequestHashSaysBecauseTheInfoHashIsTheProof()
        {
            string ih = RealHash();
            foreach (var mode in new[] { FakeAceMetadataPeer.RequestHashMode.Echo, FakeAceMetadataPeer.RequestHashMode.Wrong, FakeAceMetadataPeer.RequestHashMode.None })
                using (var peer = new FakeAceMetadataPeer { FullData = InfoHashVectors.Bytes(ih), AdvertiseMetadataSize = false, HashMode = mode })
                {
                    var f = new MetadataFetcher(Opt(), null, new HttpClient());
                    var got = await f.FetchAsync(InfoHashVectors.Hash(ih), MetadataMode.InfoHash, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None);
                    Assert.NotNull(got);
                }
        }

        [Fact]
        public async Task ADescriptorOfAnotherChannelIsRefusedAndTheNextPeerIsTried()
        {
            var all = InfoHashVectors.All().Select(x => (string)x[0]).ToList();
            string wanted = all[0], other = all[1];
            using (var liar = new FakeAceMetadataPeer("127.0.1.1") { FullData = InfoHashVectors.Bytes(other), AdvertiseMetadataSize = false })
            using (var honest = new FakeAceMetadataPeer("127.0.2.1") { FullData = InfoHashVectors.Bytes(wanted), AdvertiseMetadataSize = false, HandshakeDelayMs = 400 })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var got = await f.FetchAsync(InfoHashVectors.Hash(wanted), MetadataMode.InfoHash, null, new[] { liar.EndPoint, honest.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(InfoHashVectors.Bytes(wanted), got);
                Assert.True(liar.FullRequests >= 1);
            }
        }

        [Fact]
        public async Task OnlyLiarsMeansNoResultInsteadOfAWrongChannel()
        {
            var all = InfoHashVectors.All().Select(x => (string)x[0]).ToList();
            using (var a = new FakeAceMetadataPeer("127.0.1.1") { FullData = InfoHashVectors.Bytes(all[1]), AdvertiseMetadataSize = false })
            using (var b = new FakeAceMetadataPeer("127.0.2.1") { FullData = InfoHashVectors.Bytes(all[1]), AdvertiseMetadataSize = false })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var got = await f.FetchAsync(InfoHashVectors.Hash(all[0]), MetadataMode.InfoHash, null, new[] { a.EndPoint, b.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None);
                Assert.Null(got);                                       // even if two peers from different networks say the same thing: the infohash rules, not the quorum
            }
        }

        [Fact]
        public async Task GarbageWithTheRightRequestHashIsRefused()
        {
            string ih = RealHash();
            using (var peer = new FakeAceMetadataPeer { FullData = Enumerable.Range(0, 700).Select(i => (byte)(i * 31)).ToArray(), AdvertiseMetadataSize = false })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                Assert.Null(await f.FetchAsync(InfoHashVectors.Hash(ih), MetadataMode.InfoHash, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None));
            }
        }

        [Fact]
        public async Task PeerThatIsNotAnAceEngineAndHasNoMetadataSizeIsDropped()
        {
            string ih = RealHash();
            using (var peer = new FakeAceMetadataPeer { FullData = InfoHashVectors.Bytes(ih), AdvertiseMetadataSize = false, AdvertiseAce = false })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                Assert.Null(await f.FetchAsync(InfoHashVectors.Hash(ih), MetadataMode.InfoHash, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(2), CancellationToken.None));
                Assert.Equal(0, peer.FullRequests);
            }
        }

        [Fact]
        public async Task PeerThatClosesOnTheFullRequestLikeARealSupportNodeDoesNotBreakTheOthers()
        {
            var all = InfoHashVectors.All().Select(x => (string)x[0]).ToList();
            // the real support nodes (nt=3) do the live-stream extended handshake and close on receiving 'full' (observed on 2026-10-17)
            using (var support = new FakeAceMetadataPeer("127.0.1.1") { FullData = InfoHashVectors.Bytes(all[0]), AdvertiseMetadataSize = false, CloseOnFullRequest = true })
            using (var client = new FakeAceMetadataPeer("127.0.2.1") { FullData = InfoHashVectors.Bytes(all[0]), AdvertiseMetadataSize = false, HandshakeDelayMs = 300 })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var got = await f.FetchAsync(InfoHashVectors.Hash(all[0]), MetadataMode.InfoHash, null, new[] { support.EndPoint, client.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(InfoHashVectors.Bytes(all[0]), got);
                Assert.True(support.FullRequests >= 1);
            }
        }

        [Fact]
        public async Task VodPeerWithMetadataSizeStillGetsTheInfoByBep9()
        {
            var t = TransportLoader.Load(TestUtil.Fixture("sintel.torrent")).Torrent;
            using (var peer = new FakeAceMetadataPeer { InfoData = t.InfoRaw, UseAceProtocolName = false, AdvertiseAce = false })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var report = new FetchReport();
                var got = await f.FetchAsync(t.InfoHash, MetadataMode.InfoHash, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None, null, report);
                Assert.Equal(t.InfoRaw, got);
                Assert.Equal(MetadataPayload.Info, report.Payload);
                Assert.Equal(0, peer.FullRequests);
                Assert.True(peer.PieceRequests >= 2);
            }
        }

        [Fact]
        public async Task AceVodPeerThatAdvertisesMetadataSizeNeverGetsAFullRequest()
        {
            var t = TransportLoader.Load(TestUtil.Fixture("sintel.torrent")).Torrent;
            using (var peer = new FakeAceMetadataPeer { InfoData = t.InfoRaw, AdvertiseAce = true })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                var got = await f.FetchAsync(t.InfoHash, MetadataMode.InfoHash, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(10), CancellationToken.None);
                Assert.Equal(t.InfoRaw, got);
                Assert.Equal(0, peer.FullRequests);
            }
        }

        [Fact]
        public async Task CorruptBep9InfoIsStillDiscardedInInfoHashMode()
        {
            var t = TransportLoader.Load(TestUtil.Fixture("sintel.torrent")).Torrent;
            var bad = (byte[])t.InfoRaw.Clone(); bad[bad.Length / 2] ^= 0xff;
            using (var peer = new FakeAceMetadataPeer { InfoData = bad })
            {
                var f = new MetadataFetcher(Opt(), null, new HttpClient());
                Assert.Null(await f.FetchAsync(t.InfoHash, MetadataMode.InfoHash, null, new[] { peer.EndPoint }, TimeSpan.FromSeconds(3), CancellationToken.None));
            }
        }
    }

    /// <summary>TransportResolver: infohash/magnet -> descriptor (live stream or VOD), with the transport cache.</summary>
    public class InfoHashResolverTests
    {
        private static EngineOptions Opt(params FakeAceMetadataPeer[] peers)
        {
            var o = new EngineOptions { EnableDht = false, EnableTrackers = false };
            o.Resolver.WeakFallbackSeconds = 0.3;
            o.Resolver.Timeout = TimeSpan.FromSeconds(8);
            o.Resolver.EnableTsService = false;
            foreach (var p in peers) o.StaticPeers.Add(p.EndPoint.ToString());
            return o;
        }

        private static string Hash0() { return InfoHashVectors.All().Select(x => (string)x[0]).First(); }

        [Fact]
        public async Task InfoHashOfALiveChannelResolvesToALiveDescriptor()
        {
            string ih = Hash0();
            using (var peer = new FakeAceMetadataPeer { FullData = InfoHashVectors.Bytes(ih), AdvertiseMetadataSize = false })
            using (var r = new TransportResolver(Opt(peer), null))
            {
                foreach (var link in new[] { AceLink.Parse(ih, AceLinkKind.InfoHash), AceLink.Parse("acestream:?infohash=" + ih), AceLink.Parse("magnet:?xt=urn:btih:" + ih) })
                {
                    r.Cache.Clear();
                    var res = await r.ResolveAsync(link, CancellationToken.None);
                    Assert.True(res.Transport.IsLive);
                    Assert.Equal("bt", res.Transport.Type);
                    Assert.Equal(ih, res.Transport.InfoHashHex);
                    Assert.Equal("magnet", res.Source);
                    Assert.False(res.HasContentId);
                    Assert.Null(res.RequestedContentId);
                    Assert.Equal(TrustLevel.Verified, res.Trust.Level);
                    var mf = MediaFiles.Get(res.Transport);
                    Assert.Equal("live", mf.Type);
                    Assert.Equal(ih, mf.Infohash);
                }
            }
        }

        [Fact]
        public async Task TheResolvedDescriptorGoesToTheTransportCacheAndTheSecondResolutionDoesNotAskThePeers()
        {
            string ih = Hash0();
            using (var peer = new FakeAceMetadataPeer { FullData = InfoHashVectors.Bytes(ih), AdvertiseMetadataSize = false })
            using (var r = new TransportResolver(Opt(peer), null))
            {
                var first = await r.ResolveAsync(AceLink.Parse("magnet:?xt=urn:btih:" + ih), CancellationToken.None);
                Assert.Equal("magnet", first.Source);
                Assert.Equal(1, r.Cache.Count);
                byte[] cached;
                Assert.True(r.Cache.TryGet(ih, out cached));
                Assert.Equal(InfoHashVectors.Bytes(ih), cached);                  // the bytes exactly as the peer gave them
                int asked = peer.FullRequests;
                var again = await r.ResolveAsync(AceLink.Parse("magnet:?xt=urn:btih:" + ih), CancellationToken.None);
                Assert.Equal("local_db", again.Source);
                Assert.True(again.Transport.IsLive);
                Assert.Equal(ih, again.Transport.InfoHashHex);
                Assert.Equal(asked, peer.FullRequests);
                // and get_media_files with only the local cache also works
                var local = await r.ResolveAsync(AceLink.Parse(ih, AceLinkKind.InfoHash), CancellationToken.None, true);
                Assert.Equal("local_db", local.Source);
            }
        }

        [Fact]
        public async Task LocalCacheOnlyWithoutEntryReportsMissingTransportFile()
        {
            using (var r = new TransportResolver(Opt(), null))
            {
                var ex = await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.Parse(Hash0(), AceLinkKind.InfoHash), CancellationToken.None, true));
                Assert.Equal("no_transport_file_in_local_cache", ex.Message);
            }
        }

        [Fact]
        public async Task ACacheEntryThatDoesNotMatchTheInfoHashIsIgnoredAndOverwritten()
        {
            var all = InfoHashVectors.All().Select(x => (string)x[0]).ToList();
            string ih = all[0];
            using (var peer = new FakeAceMetadataPeer { FullData = InfoHashVectors.Bytes(ih), AdvertiseMetadataSize = false })
            using (var r = new TransportResolver(Opt(peer), null))
            {
                r.Cache.Put(ih, InfoHashVectors.Bytes(all[1]));                       // the cache is poisoned with the neighboring channel
                var res = await r.ResolveAsync(AceLink.Parse("magnet:?xt=urn:btih:" + ih), CancellationToken.None);
                Assert.Equal("magnet", res.Source);
                Assert.Equal(ih, res.Transport.InfoHashHex);
                byte[] cached;
                Assert.True(r.Cache.TryGet(ih, out cached));
                Assert.Equal(InfoHashVectors.Bytes(ih), cached);
                // and with only the local cache the poisoned one is not accepted
                r.Cache.Put(all[2], InfoHashVectors.Bytes(all[1]));
                await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.Parse("magnet:?xt=urn:btih:" + all[2]), CancellationToken.None, true));
            }
        }

        [Fact]
        public async Task ADescriptorForAnotherInfoHashIsNeverReturned()
        {
            var all = InfoHashVectors.All().Select(x => (string)x[0]).ToList();
            using (var liar = new FakeAceMetadataPeer { FullData = InfoHashVectors.Bytes(all[1]), AdvertiseMetadataSize = false })
            {
                var o = Opt(liar);
                o.Resolver.Timeout = TimeSpan.FromSeconds(2);
                using (var r = new TransportResolver(o, null))
                {
                    var ex = await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.Parse(all[0], AceLinkKind.InfoHash), CancellationToken.None));
                    Assert.Contains(all[0], ex.Message);
                    Assert.Equal(0, r.Cache.Count);
                }
            }
        }

        [Fact]
        public async Task NobodyHasItGivesTheSameErrorAsAContentIdWithoutPeers()
        {
            var o = Opt();
            o.Resolver.Timeout = TimeSpan.FromSeconds(1);
            using (var r = new TransportResolver(o, null))
            {
                var ex = await Assert.ThrowsAsync<ResolveException>(() => r.ResolveAsync(AceLink.Parse(Hash0(), AceLinkKind.InfoHash), CancellationToken.None));
                Assert.Contains("cannot get transport file", ex.Message);
            }
        }

        [Fact]
        public async Task VodInfoHashStillGivesAMinimalTorrentWithTheTrackersOfTheLink()
        {
            var t = TransportLoader.Load(TestUtil.Fixture("sintel.torrent")).Torrent;
            using (var peer = new FakeAceMetadataPeer { InfoData = t.InfoRaw, UseAceProtocolName = false })
            using (var r = new TransportResolver(Opt(peer), null))
            {
                var link = AceLink.Parse("magnet:?xt=urn:btih:" + t.InfoHashHex + "&tr=udp%3A%2F%2Fexample.test%3A1%2Fannounce");
                var res = await r.ResolveAsync(link, CancellationToken.None);
                Assert.False(res.Transport.IsLive);
                Assert.Equal("magnet", res.Source);
                Assert.Equal(t.InfoHashHex, res.Transport.InfoHashHex);
                Assert.Contains("udp://example.test:1/announce", res.Transport.Torrent.GetTrackerTiers().SelectMany(x => x));
                // the cache stores the 'info' without trackers and each link puts its own
                byte[] cached;
                Assert.True(r.Cache.TryGet(t.InfoHashHex, out cached));
                Assert.Empty(TransportLoader.Load(cached).Torrent.GetTrackerTiers());
                var other = await r.ResolveAsync(AceLink.Parse("magnet:?xt=urn:btih:" + t.InfoHashHex + "&tr=udp%3A%2F%2Fotro.test%3A2%2Fannounce"), CancellationToken.None);
                Assert.Equal("local_db", other.Source);
                Assert.Contains("udp://otro.test:2/announce", other.Transport.Torrent.GetTrackerTiers().SelectMany(x => x));
                Assert.DoesNotContain("udp://example.test:1/announce", other.Transport.Torrent.GetTrackerTiers().SelectMany(x => x));
                Assert.Equal(t.InfoRaw, other.Transport.Torrent.InfoRaw);
            }
        }

        [Fact]
        public async Task RealVodInfoIsServedByBep9AndStaysAVod()
        {
            // REAL Sintel 'info' (20,242 bytes = 2 ut_metadata chunks) obtained over BEP9 from the network on 2026-10-17
            var info = TestUtil.Fixture("real/infohash/vod/08ada5a7a6183aae1e09d831df6748d566095a10.info");
            const string ih = "08ada5a7a6183aae1e09d831df6748d566095a10";
            Assert.Equal(ih, TestUtil.Sha1Hex(info));
            using (var peer = new FakeAceMetadataPeer { InfoData = info, UseAceProtocolName = false })
            using (var r = new TransportResolver(Opt(peer), null))
            {
                var res = await r.ResolveAsync(AceLink.Parse("magnet:?xt=urn:btih:" + ih + "&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce"), CancellationToken.None);
                Assert.False(res.Transport.IsLive);
                Assert.Equal("torrent", res.Transport.Type);
                Assert.Equal("Sintel", res.Transport.Name);
                Assert.Equal(ih, res.Transport.InfoHashHex);
                Assert.Equal(987, res.Transport.Torrent.PieceCount);
                Assert.Equal(131072, res.Transport.Torrent.PieceLength);
                var mf = MediaFiles.Get(res.Transport);
                Assert.Equal("vod", mf.Type);
                Assert.Equal("Sintel.mp4", mf.Files.Single().Filename);
                Assert.Equal(5, mf.Files.Single().Index);
                Assert.Equal(129241752, mf.Files.Single().Size);
                Assert.Equal(0, peer.FullRequests);
                Assert.True(peer.PieceRequests >= 2);
            }
        }

        [Fact]
        public async Task ProfileStatsAreFilledForInfoHashResolutionsToo()
        {
            string ih = Hash0();
            using (var peer = new FakeAceMetadataPeer { FullData = InfoHashVectors.Bytes(ih), AdvertiseMetadataSize = false })
            using (var r = new TransportResolver(Opt(peer), null))
            {
                var profile = new ResolveProfile();
                await r.ResolveAsync(AceLink.Parse("magnet:?xt=urn:btih:" + ih), CancellationToken.None, false, true, profile);
                Assert.True(profile.Stats.Candidates >= 1);
            }
        }
    }

    /// <summary>From the infohash link to playback, end to end on the local loopback.</summary>
    public class InfoHashEndToEndTests
    {
        private static LiveSimOptions Sim() { return new LiveSimOptions { TimeScale = 4 }; }

        private static void AssertPlays(SimStreamVerifier v)
        {
            Assert.True(v.Bytes > 300000, "bytes " + v.Bytes);
            Assert.Equal(0, v.SyncErrors); Assert.Equal(0, v.CorruptPackets); Assert.Empty(v.Gaps);
        }

        [Fact]
        public async Task AMagnetOfALiveChannelIsOpenedAsALiveStreamWithoutTouchingTheVodPath()
        {
            using (var h = new LiveHarness(Sim(), o => { o.Resolver.EnableTsService = false; }))
            {
                h.AddSource();
                using (var meta = new FakeAceMetadataPeer { FullData = h.Model.TransportBytes, AdvertiseMetadataSize = false })
                {
                    h.Options.StaticPeers.Add(meta.EndPoint.ToString());
                    await h.OpenAsync(AceLink.Parse("magnet:?xt=urn:btih:" + Hex.ToHex(h.Model.InfoHash)));
                    Assert.IsType<AceEngine.Live.LiveSession>(h.Opened.Session);            // not a VodSession over an infohash that is not a torrent's
                    Assert.True(h.Opened.Transport.IsLive);
                    Assert.Equal(TrustLevel.Verified, h.Opened.Trust == null ? TrustLevel.Verified : h.Opened.Trust.Level);
                    AssertPlays(await h.ReadAsync(6));
                    Assert.True(meta.FullRequests >= 1);
                    Assert.Equal(0, meta.PieceRequests);
                    // get_media_files of that infohash comes from the cache
                    var mf = await h.Host.GetMediaFilesAsync(AceLink.Parse(Hex.ToHex(h.Model.InfoHash), AceLinkKind.InfoHash), new MediaFilesOptions { UseLocalCacheOnly = true }, CancellationToken.None);
                    Assert.Equal("live", mf.Type);
                    Assert.Equal(Hex.ToHex(h.Model.InfoHash), mf.Infohash);
                }
            }
        }

        [Fact]
        public async Task ThePlaybackSessionPlaysAnInfoHashChannelAsLiveAndShowsResolvingFirst()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(Sim(), o => { o.Resolver.EnableTsService = false; }))
            {
                h.AddSource();
                using (var meta = new FakeAceMetadataPeer { FullData = h.Model.TransportBytes, AdvertiseMetadataSize = false, HandshakeDelayMs = 300 })
                {
                    h.Options.StaticPeers.Add(meta.EndPoint.ToString());
                    h.PrepareHost();
                    var factory = new FakePlayerFactory();
                    var po = new PlaybackOptions { RetryDelay = TimeSpan.FromMilliseconds(100), StatusInterval = TimeSpan.FromMilliseconds(100), ReadyTimeout = TimeSpan.FromSeconds(20), PrepareTimeout = TimeSpan.FromSeconds(3) };
                    using (var pb = new PlaybackSession(h.Host, factory, po))
                    {
                        var phases = new List<PlayPhase>();
                        pb.Changed += s => { lock (phases) { if (phases.Count == 0 || phases[phases.Count - 1] != s.Phase) phases.Add(s.Phase); } };
                        var entry = LinkEntry.FromUrl("Canal del buscador", "magnet:?xt=urn:btih:" + Hex.ToHex(h.Model.InfoHash));
                        var run = pb.RunAsync(entry, "Canal del buscador", CancellationToken.None);
                        var end = DateTime.UtcNow.AddSeconds(25);
                        while (DateTime.UtcNow < end && pb.Status.Phase != PlayPhase.Playing) await Task.Delay(25);
                        Assert.Equal(PlayPhase.Playing, pb.Status.Phase);
                        Assert.True(pb.Status.IsLive);
                        lock (phases) Assert.Equal(PlayPhase.Resolving, phases[0]);       // the "resolviendo" phase is the usual one
                        pb.Stop(); await run;
                        Assert.Empty(h.Host.Server.Sessions);
                    }
                }
            }
        }

        [Fact]
        public async Task UnresolvableInfoHashRetriesTheResolutionThenReportsAReadableError()
        {
            var opt = new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false };
            opt.Resolver.EnableTsService = false; opt.Resolver.Timeout = TimeSpan.FromSeconds(1);
            using (var host = new AceEngineHost(opt))
            {
                host.Start();
                var e = LinkEntry.FromUrl("Canal", "magnet:?xt=urn:btih:" + new string('e', 40));
                var factory = new FakePlayerFactory();
                var po = new PlaybackOptions { RetryDelay = TimeSpan.FromMilliseconds(100), StatusInterval = TimeSpan.FromMilliseconds(100), MaxResolveRetries = 2, KeepRetryingWithoutSource = false };
                using (var pb = new PlaybackSession(host, factory, po))
                {
                    var retries = new List<int>();
                    pb.Changed += s => { if (s.Phase == PlayPhase.Retrying) lock (retries) { if (!retries.Contains(s.Retry)) retries.Add(s.Retry); } };
                    await pb.RunAsync(e, "Canal", CancellationToken.None);
                    Assert.Equal(PlayPhase.Error, pb.Status.Phase);
                    Assert.Equal(PlaybackSession.NoSourceMessage, pb.Status.Error);       // no debug text: "Sin fuentes por ahora"
                    Assert.Equal(new[] { 1, 2 }, retries.OrderBy(x => x).ToArray());     // the same resolution retries as with a content_id
                    Assert.Empty(factory.Players);
                    Assert.Empty(host.Server.Sessions);
                }
            }
        }

        [Fact]
        public async Task ARetriedResolutionSucceedsWhenThePeerAppearsLater()
        {
            FakePlayerBackend.Created = 0;
            using (var h = new LiveHarness(Sim(), o => { o.Resolver.EnableTsService = false; o.Resolver.Timeout = TimeSpan.FromSeconds(1.5); }))
            {
                h.AddSource();
                // the peer closes the first two connections (the two that the first resolution attempt makes to a candidate): the first attempt fails and
                // PlaybackSession retries it, as with a content_id; the second attempt already finds it
                using (var meta = new FakeAceMetadataPeer { FullData = h.Model.TransportBytes, AdvertiseMetadataSize = false, DropFirstConnections = 2 })
                {
                    h.Options.StaticPeers.Add(meta.EndPoint.ToString());
                    h.PrepareHost();
                    var factory = new FakePlayerFactory();
                    var po = new PlaybackOptions { RetryDelay = TimeSpan.FromMilliseconds(100), StatusInterval = TimeSpan.FromMilliseconds(100), ReadyTimeout = TimeSpan.FromSeconds(20), PrepareTimeout = TimeSpan.FromSeconds(3), MaxResolveRetries = 3 };
                    using (var pb = new PlaybackSession(h.Host, factory, po))
                    {
                        var seenRetry = false;
                        pb.Changed += s => { if (s.Phase == PlayPhase.Retrying) seenRetry = true; };
                        var run = pb.RunAsync(LinkEntry.FromUrl("C", "magnet:?xt=urn:btih:" + Hex.ToHex(h.Model.InfoHash)), "C", CancellationToken.None);
                        var end = DateTime.UtcNow.AddSeconds(30);
                        while (DateTime.UtcNow < end && pb.Status.Phase != PlayPhase.Playing) await Task.Delay(25);
                        Assert.Equal(PlayPhase.Playing, pb.Status.Phase);
                        Assert.True(seenRetry, "the first attempt should have failed and been retried");
                        pb.Stop(); await run;
                    }
                }
            }
        }
    }
}
