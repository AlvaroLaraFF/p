using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Util
{
    /// <summary>Limits for everything that arrives over HTTP: a hostile response must not exhaust the TV's memory.</summary>
    public static class HttpLimits
    {
        /// <summary>Responses from HTTP trackers (announce with compact peers, scrape).</summary>
        public const int TrackerBody = 256 * 1024;
        public const int DohBody = 256 * 1024;
        public const int TsServiceBody = 1024 * 1024;
        public const int JsonBody = 256 * 1024;
        public const int M3uBody = 4 * 1024 * 1024;
        /// <summary>Global buffer cap for HttpClient (HttpCompletionOption.ResponseContentRead).</summary>
        public const long ClientBuffer = 4 * 1024 * 1024;

        /// <summary>
        /// Reads a response body up to maxBytes. If the server announces more (Content-Length) or sends more, it throws InvalidDataException without
        /// having kept more than maxBytes (plus one block) in memory. Also works with Transfer-Encoding: chunked, which declares no length.
        /// </summary>
        public static async Task<byte[]> ReadBodyAsync(HttpContent content, int maxBytes, CancellationToken ct)
        {
            var len = content.Headers.ContentLength;
            if (len.HasValue && len.Value > maxBytes) throw new InvalidDataException("response too large (" + len.Value + " bytes, maximum " + maxBytes + ")");
            using (var s = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var ms = new MemoryStream(len.HasValue ? (int)Math.Min(len.Value, maxBytes) : 4096))
            {
                var buf = new byte[8192];
                long total = 0;
                while (true)
                {
                    int n = await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;
                    if (total > maxBytes) throw new InvalidDataException("response too large (more than " + maxBytes + " bytes)");
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
        }

        public static async Task<string> ReadTextAsync(HttpContent content, int maxBytes, CancellationToken ct)
        {
            return System.Text.Encoding.UTF8.GetString(await ReadBodyAsync(content, maxBytes, ct).ConfigureAwait(false));
        }
    }
}
