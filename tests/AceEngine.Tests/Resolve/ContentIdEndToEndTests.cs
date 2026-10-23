using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Links;
using AceEngine.P2P;
using AceEngine.Resolve;
using AceEngine.Tests.P2P;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Resolve
{
    /// <summary>`ace play acestream://&lt;content_id&gt;` end to end (VOD) on the local loopback: resolution over P2P and download through the local URL.</summary>
    public class ContentIdEndToEndTests
    {
        [Fact]
        public async Task PlayVodByContentIdResolvesTheTorrentAndStreamsIt()
        {
            var t = TestTorrent.Create(16 * 32768 + 77, 32768, name: "pelicula.mp4");
            string contentId = Hex.ToHex(HashUtil.Sha1(t.TorrentBytes));       // for an UNencrypted .torrent the content_id is indeed the SHA-1 of the file
            using (var meta = new FakeAceMetadataPeer { FullData = t.TorrentBytes })
            using (var seeder = new FakeSeeder(t))
            using (var host = new AceEngineHost(new EngineOptions
            {
                EnableListener = false, EnableDht = false, EnableTrackers = false,
                ConnectTimeout = TimeSpan.FromSeconds(3), HandshakeTimeout = TimeSpan.FromSeconds(3), MetadataTimeout = TimeSpan.FromSeconds(20),
                StaticPeers = new List<string> { meta.EndPoint.ToString(), seeder.EndPoint.ToString() },
                Resolver = { EnableTsService = false, Timeout = TimeSpan.FromSeconds(15) }
            }))
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            {
                host.Start();
                // get_media_files first (resolves and leaves the file in the cache)
                var mf = await host.GetMediaFilesAsync(AceLink.Parse("acestream://" + contentId), new AceEngine.Transport.MediaFilesOptions(), CancellationToken.None);
                Assert.Equal("vod", mf.Type);
                Assert.Equal("pelicula.mp4", mf.Files.Single().Filename);
                Assert.Equal(t.Content.Length, mf.Files.Single().Size);
                Assert.Equal(1, meta.FullRequests);

                var opened = await host.OpenStreamAsync(AceLink.Parse("acestream://" + contentId), new OpenOptions(), CancellationToken.None);
                Assert.Equal(1, meta.FullRequests);                  // the second resolution comes from the cache
                var body = await http.GetByteArrayAsync(opened.Url);
                Assert.Equal(TestUtil.Sha1Hex(t.Content), TestUtil.Sha1Hex(body));
            }
        }

        [Fact]
        public async Task PlainHexIsAContentIdByDefaultAndAnInfohashOnRequest()
        {
            var h = "2940120bf034db79a7f5849846ccea0255172eae";
            Assert.Equal(AceLinkKind.ContentId, AceLink.Parse(h).Kind);
            Assert.Equal(AceLinkKind.ContentId, AceLink.Parse("acestream://" + h).Kind);
            Assert.Equal(AceLinkKind.InfoHash, AceLink.Parse(h, AceLinkKind.InfoHash).Kind);
            Assert.Equal(AceLinkKind.InfoHash, AceLink.Parse("acestream:?infohash=" + h, AceLinkKind.ContentId).Kind);
            Assert.Equal(AceLinkKind.Magnet, AceLink.Parse("magnet:?xt=urn:btih:" + h, AceLinkKind.ContentId).Kind);
            await Task.CompletedTask;
        }
    }
}
