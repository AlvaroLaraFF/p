using System.Linq;
using AceEngine.Transport;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Transport
{
    public class MediaFilesTests
    {
        [Fact]
        public void VodListsOnlyMediaFilesKeepingOriginalIndices()
        {
            var tf = TransportLoader.Load(TestUtil.Fixture("plain.torrent"));
            var r = MediaFiles.Get(tf);
            Assert.Equal("vod", r.Type);
            Assert.Equal("bt", r.TransportType);
            Assert.Equal("Pack de prueba", r.Name);
            Assert.Equal(tf.InfoHashHex, r.Infohash);
            // subs.srt (index 1) is filtered out; mkv keeps index 2.
            Assert.Equal(new[] { 0, 2 }, r.Files.Select(f => f.Index).ToArray());
            Assert.Equal("video/mp4", r.Files[0].Mime);
            Assert.Equal("video/x-matroska", r.Files[1].Mime);
            Assert.Equal(40000, r.Files[0].Size);
            Assert.Equal(20000, r.Files[1].Size);
            Assert.Equal(40037, r.Files[1].Offset);
            Assert.All(r.Files, f => Assert.Equal("vod", f.Type));
        }

        [Fact]
        public void LiveBtDescriptorGivesOneTsFileAtIndexZero()
        {
            var tf = TransportLoader.Load(TestUtil.Fixture("live_v2.acelive"));
            var r = MediaFiles.Get(tf);
            Assert.Equal("live", r.Type);
            Assert.Single(r.Files);
            Assert.Equal(0, r.Files[0].Index);
            Assert.Equal("video/mp2t", r.Files[0].Mime);
            Assert.Equal(0, r.Files[0].Size);
            Assert.Equal("bt", r.TransportType);
        }

        [Fact]
        public void HlsDescriptorUsesHlsMime()
        {
            var tf = TransportLoader.Load(TestUtil.Fixture("hls_v1.acelive"));
            var r = MediaFiles.Get(tf);
            Assert.Equal("hls", r.TransportType);
            Assert.Equal(MediaFiles.HlsMime, r.Files[0].Mime);
            Assert.Equal("", r.Infohash);
        }

        [Fact]
        public void MimeTypesFollowTable()
        {
            Assert.Equal("video/mp2t", MimeTypes.GetMimeType("a.ts"));
            Assert.Equal("video/mp2t", MimeTypes.GetMimeType("a.m2ts"));
            Assert.Equal("video/mp4", MimeTypes.GetMimeType("A.MP4"));
            Assert.Equal("video/mp4", MimeTypes.GetMimeType("a.m4v"));
            Assert.Equal("video/mp4", MimeTypes.GetMimeType("a.mov"));
            Assert.Equal("video/x-matroska", MimeTypes.GetMimeType("a.mkv"));
            Assert.Equal("video/x-msvideo", MimeTypes.GetMimeType("a.avi"));
            Assert.Equal("video/webm", MimeTypes.GetMimeType("a.webm"));
            Assert.Equal("audio/mpeg", MimeTypes.GetMimeType("a.mp3"));
            Assert.Equal("audio/aac", MimeTypes.GetMimeType("a.aac"));
            Assert.Equal("audio/flac", MimeTypes.GetMimeType("a.flac"));
            Assert.Equal("application/octet-stream", MimeTypes.GetMimeType("a.xyz"));
            Assert.Equal("application/octet-stream", MimeTypes.GetMimeType("noext"));
            Assert.True(MimeTypes.IsVideoExtension("x/y.mkv"));
            Assert.False(MimeTypes.IsVideoExtension("x/y.srt"));
        }
    }
}
