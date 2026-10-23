using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace AceEngine.App
{
    /// <summary>Uploads a PNG capture of the surface to the development PC (HTTP POST to port 9998 by default) to compare it with the desktop ones. Tests only.</summary>
    public static class SnapUploader
    {
        public const int DefaultPort = 9998;

        public static async Task PutAsync(string host, string name, byte[] data, int port = DefaultPort)
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                using (var content = new ByteArrayContent(data))
                {
                    var r = await http.PostAsync("http://" + host + ":" + port + "/put/" + name, content);
                    UdpLog.Info("snap", name + " (" + data.Length + " bytes) -> " + (int)r.StatusCode);
                }
            }
            catch (Exception e) { UdpLog.Warn("snap", name + ": " + e.Message); }
        }
    }
}
