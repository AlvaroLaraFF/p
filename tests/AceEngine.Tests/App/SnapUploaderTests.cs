using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Tests.NetTests;
using Xunit;

namespace AceEngine.Tests.App
{
    public class SnapUploaderTests
    {
        [Fact]
        public async Task PostsTheCaptureToThePutPath()
        {
            using (var srv = new FakeHttpServer())
            {
                srv.Handler = r => new FakeHttpResponse { Status = 200 };
                var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3 };
                await SnapUploader.PutAsync("127.0.0.1", "snap_main.png", png, srv.Port);
                FakeHttpRequest req;
                Assert.True(srv.Requests.TryDequeue(out req));
                Assert.Equal("POST", req.Method);
                Assert.Equal("/put/snap_main.png", req.Path);
                Assert.Equal(png, req.Body);
            }
        }

        [Fact]
        public async Task AnUnreachableHostIsLoggedNotThrown()
        {
            int free;
            using (var srv = new FakeHttpServer()) free = srv.Port;               // closed again: nobody listens on it
            await SnapUploader.PutAsync("127.0.0.1", "snap_x.png", new byte[] { 1 }, free);
        }

        [Fact]
        public void DefaultPortIs9998()
        {
            Assert.Equal(9998, SnapUploader.DefaultPort);
        }
    }
}
