using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Sim;
using AceEngine.Transport;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Live
{
    public class LiveTransportTests
    {
        [Fact]
        public async Task MultiQualityTransportPlaysTheChosenQuality()
        {
            using (var h = new LiveHarness(new LiveSimOptions { TimeScale = 4 }))
            {
                h.AddSource();
                // multi-quality: qualities = [{name, data: <nested BT transport>}, ...]
                var other = new LiveStreamModel(new LiveSimOptions { Name = "otra", Bitrate = 100000, RsaSeed = 3 });
                var root = new BDict();
                root.Add("name", BValue.Str("Canal multicalidad"));
                var q0 = new BDict(); q0.Add("name", BValue.Str("HD")); q0.Add("data", BValue.Bytes(h.Model.TransportBytes));
                var q1 = new BDict(); q1.Add("name", BValue.Str("SD")); q1.Add("data", BValue.Bytes(other.TransportBytes));
                root.Add("qualities", BValue.List(q0, q1));
                var bytes = TransportLoader.Build(root, TransportFormat.AceStreamTransportV2, IvMode.Sha1OfMd5Key);
                var path = Path.Combine(Path.GetTempPath(), "multi-" + Guid.NewGuid().ToString("N") + ".acelive");
                File.WriteAllBytes(path, bytes);
                try
                {
                    var tf = TransportLoader.Load(bytes);
                    Assert.True(tf.IsMulti); Assert.Equal(2, tf.Qualities.Count);
                    var mf = MediaFiles.Get(tf);
                    Assert.Equal(new[] { "HD", "SD" }, mf.Files.Select(f => f.Filename).ToArray());
                    Assert.Equal(new[] { Hex.ToHex(h.Model.InfoHash), Hex.ToHex(other.InfoHash) }, mf.Files.Select(f => f.Infohash).ToArray());

                    h.Options.StaticPeers.Add(h.Sources[0].EndPoint.ToString());
                    h.Host = new AceEngineHost(h.Options);
                    h.Host.Start();
                    h.Opened = await h.Host.OpenStreamAsync(AceLink.Parse(path), new OpenOptions { QualityIndex = 0 }, CancellationToken.None);
                    var v = await h.ReadAsync(5);
                    Assert.True(v.Bytes > 300000); Assert.Equal(0, v.SyncErrors); Assert.Equal(0, v.CorruptPackets);
                    Assert.Equal("Canal simulado", h.Session.Name);
                    await Assert.ThrowsAsync<ArgumentException>(() => h.Host.OpenStreamAsync(AceLink.Parse(path), new OpenOptions { QualityIndex = 5 }, CancellationToken.None));
                }
                finally { File.Delete(path); }
            }
        }

        private static string Build(BDict root)
        {
            var path = Path.Combine(Path.GetTempPath(), "d-" + Guid.NewGuid().ToString("N") + ".acelive");
            File.WriteAllBytes(path, TransportLoader.Build(root, TransportFormat.AceStreamTransportV2, IvMode.Sha1OfMd5Key));
            return path;
        }

        [Fact]
        public async Task HlsAndWrapperDescriptorsPlayDirectlyFromTheirManifestUrl()
        {
            using (var host = new AceEngineHost(new EngineOptions { EnableListener = false, EnableDht = false, EnableTrackers = false }))
            {
                host.Start();
                var root = new BDict();
                root.Add("type", BValue.Str("hls")); root.Add("name", BValue.Str("Canal HLS")); root.Add("manifest_url", BValue.Str("https://ejemplo.test/live/index.m3u8"));
                var path = Build(root);
                try
                {
                    var opened = await host.OpenStreamAsync(AceLink.Parse(path), new OpenOptions(), CancellationToken.None);
                    Assert.True(opened.IsDirect); Assert.Null(opened.Session);
                    Assert.Equal("https://ejemplo.test/live/index.m3u8", opened.Url);
                }
                finally { File.Delete(path); }

                var w = new BDict();
                w.Add("type", BValue.Str("wrapper")); w.Add("name", BValue.Str("W")); w.Add("hidden_manifest_url", BValue.Str("http://h.test/a.m3u8"));
                path = Build(w);
                try { Assert.Equal("http://h.test/a.m3u8", (await host.OpenStreamAsync(AceLink.Parse(path), new OpenOptions(), CancellationToken.None)).Url); }
                finally { File.Delete(path); }

                var noUrl = new BDict(); noUrl.Add("type", BValue.Str("hls")); noUrl.Add("name", BValue.Str("sin url"));
                path = Build(noUrl);
                try { await Assert.ThrowsAsync<NotSupportedException>(() => host.OpenStreamAsync(AceLink.Parse(path), new OpenOptions(), CancellationToken.None)); }
                finally { File.Delete(path); }

                var dash = new BDict(); dash.Add("type", BValue.Str("dash")); dash.Add("name", BValue.Str("d")); dash.Add("manifest_url", BValue.Str("http://d.test/a.mpd"));
                path = Build(dash);
                try { await Assert.ThrowsAsync<NotSupportedException>(() => host.OpenStreamAsync(AceLink.Parse(path), new OpenOptions(), CancellationToken.None)); }
                finally { File.Delete(path); }
            }
        }

        [Fact]
        public void OldFormatLiveTorrentMapsToADescriptor()
        {
            var sim = new LiveStreamModel(new LiveSimOptions());
            var live = new BDict();
            live.Add("authmethod", BValue.Str("RSA")); live.Add("pubkey", BValue.Bytes(sim.Descriptor.PubKey)); live.Add("bps", BValue.Int(262144));
            var info = new BDict();
            info.Add("name", BValue.Str("directo antiguo")); info.Add("piece length", BValue.Int(65536)); info.Add("live", live);
            var root = new BDict(); root.Add("announce", BValue.Str("udp://t.test:1/announce")); root.Add("info", info);
            var tf = TransportLoader.Load(BencodeWriter.Encode(root));
            Assert.True(tf.IsLive); Assert.Null(tf.Live);
            var d = LiveDescriptor.FromLiveTorrent(tf.Torrent);
            Assert.Equal("RSA", d.AuthMethod); Assert.Equal(65536, d.PieceLength); Assert.Equal(262144, d.Bitrate); Assert.Equal(65536, d.ChunkLength);
            Assert.Equal(tf.Torrent.InfoHash, d.InfoHash);                        // a torrent's infohash is the SHA-1 of info
            Assert.Equal(new[] { "udp://t.test:1/announce" }, d.Trackers.ToArray());
            var noBps = new BDict(); var info2 = new BDict(); info2.Add("name", BValue.Str("x")); info2.Add("piece length", BValue.Int(1)); info2.Add("live", new BDict());
            noBps.Add("info", info2);
            var tf2 = TransportLoader.Load(BencodeWriter.Encode(noBps));
            Assert.Throws<TransportException>(() => LiveDescriptor.FromLiveTorrent(tf2.Torrent));
        }

        [Fact]
        public void DescriptorListsAcceptSpaceSeparatedUrlsAndNodePairs()
        {
            var d = new BDict();
            d.Add("name", BValue.Str("x")); d.Add("authmethod", BValue.Str("None")); d.Add("pubkey", BValue.Bytes(new byte[32])); d.Add("piece_length", BValue.Int(65536)); d.Add("bitrate", BValue.Int(100000));
            d.Add("trackers", BValue.List(BValue.Str("udp://a:1/announce udp://b:2/announce"), BValue.Str("http://c/announce")));
            d.Add("startup_nodes", BValue.List(BValue.Str("1.2.3.4:5"), BValue.List(BValue.Str("5.6.7.8"), BValue.Int(9)), BValue.Str("9.9.9.9:1 8.8.8.8:2")));
            d.Add("meta_trackers", BValue.List(BValue.Str("http://mt/x")));
            var live = LiveDescriptor.FromMeta(d);
            Assert.Equal(new[] { "udp://a:1/announce", "udp://b:2/announce", "http://c/announce" }, live.Trackers.ToArray());
            Assert.Equal(new[] { "1.2.3.4:5", "5.6.7.8:9", "9.9.9.9:1", "8.8.8.8:2" }, live.StartupNodes.ToArray());
            Assert.Equal(new[] { "http://mt/x" }, live.MetaTrackers.ToArray());
            Assert.True(live.AllowPublicTrackers);
        }
    }
}
