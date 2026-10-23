using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Net
{
    internal sealed class HttpReply
    {
        public int Status;
        public byte[] Body = new byte[0];
    }

    /// <summary>
    /// HTTP client for talking to the router: no proxy, no redirects, no cookies, Connection: close, total timeout per request (headers + body) and
    /// maximum body size (whatever a LAN device replies is untrusted input).
    /// </summary>
    internal sealed class BoundedHttp : IDisposable
    {
        private readonly HttpClient _http;

        public BoundedHttp(HttpMessageHandler handler)
        {
            if (handler == null) handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = System.Net.DecompressionMethods.None };
            _http = new HttpClient(handler, true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        }

        public async Task<HttpReply> SendAsync(HttpRequestMessage request, int maxBytes, TimeSpan timeout, CancellationToken ct)
        {
            using (request)
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(timeout);
                try
                {
                    request.Headers.ConnectionClose = true;
                    using (var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    {
                        long? len = resp.Content.Headers.ContentLength;
                        if (len.HasValue && len.Value > maxBytes) throw new UpnpException(UpnpErrorKind.TooLarge, "response of " + len.Value + " bytes (maximum " + maxBytes + ")");
                        var reply = new HttpReply { Status = (int)resp.StatusCode };
                        using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var ms = new MemoryStream())
                        {
                            var buf = new byte[4096];
                            while (true)
                            {
                                int n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                                if (n <= 0) break;
                                if (ms.Length + n > maxBytes) throw new UpnpException(UpnpErrorKind.TooLarge, "response of more than " + maxBytes + " bytes");
                                ms.Write(buf, 0, n);
                            }
                            reply.Body = ms.ToArray();
                        }
                        return reply;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) throw;
                    throw new UpnpException(UpnpErrorKind.Timeout, "no response in " + timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s");
                }
                catch (UpnpException) { throw; }
                catch (HttpRequestException ex) { throw new UpnpException(UpnpErrorKind.Transport, Describe(ex)); }
                catch (IOException ex) { throw new UpnpException(UpnpErrorKind.Transport, ex.Message); }
                catch (ObjectDisposedException) { throw new OperationCanceledException(ct); }
            }
        }

        private static string Describe(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        public void Dispose() { _http.Dispose(); }
    }
}
