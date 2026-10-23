using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Sources;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>Downloads the links of ONE configured list. Seam for the catalog tests: in the app it is <see cref="ListLoader"/>.</summary>
    public interface IListLoader
    {
        /// <summary>All the links of the list (with captions and web pages: the catalog keeps the playable ones). Throws if the list cannot be loaded.</summary>
        Task<List<LinkEntry>> LoadAsync(ListSource source, CancellationToken ct);
    }

    /// <summary>
    /// Real load: a mylinkspaste code is resolved recursively through DNS TXT (MyLinksPasteClient + DnsTxtClient) and a URL is downloaded as an
    /// M3U list (M3uParser), with the same network limits as the engine. It does not need the engine started: it carries its own HttpClient.
    /// </summary>
    public sealed class ListLoader : IListLoader, IDisposable
    {
        private const string Tag = "lists";
        private readonly DnsOptions _dns;
        private readonly object _gate = new object();
        private HttpClient _http;
        private readonly bool _ownsHttp;
        private bool _disposed;

        public ListLoader(DnsOptions dns = null, HttpClient http = null)
        {
            _dns = dns ?? new DnsOptions();
            _http = http;
            _ownsHttp = http == null;
        }

        private HttpClient Http()
        {
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException("ListLoader");
                if (_http == null)
                {
                    var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate };
                    _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = HttpLimits.ClientBuffer };
                }
                return _http;
            }
        }

        public async Task<List<LinkEntry>> LoadAsync(ListSource source, CancellationToken ct)
        {
            if (source == null) throw new ArgumentNullException("source");
            string normalized;
            switch (InputClassifier.Classify(source.Code, out normalized))
            {
                case InputKind.M3uUrl:
                    return LinkList.Dedupe(await M3uParser.LoadAsync(normalized, Http(), ct).ConfigureAwait(false));
                case InputKind.Code:
                    var client = new MyLinksPasteClient(new DnsTxtClient(_dns, Http()));
                    var tree = await client.FetchAsync(normalized, ct).ConfigureAwait(false);
                    // a failed subcode does not bring the list down, but it is noted in the log
                    if (tree.Errors.Count > 0) AceLog.Warn(Tag, "list '" + source.Name + "': " + tree.Errors.Count + " warnings while walking the tree (" + tree.Errors[0] + ")");
                    return LinkList.Dedupe(tree.Entries);
                default:
                    throw new FormatException("invalid list code or address");
            }
        }

        public void Dispose()
        {
            HttpClient h = null;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_ownsHttp) { h = _http; _http = null; }
            }
            if (h != null) h.Dispose();
        }
    }

    internal static class AsyncUtil
    {
        /// <summary>
        /// Runs an operation with a deadline even if it does not honor cancellation: when it expires, it cancels it, stops waiting for it and throws TimeoutException.
        /// If the caller cancels, it throws OperationCanceledException. Late failures of the abandoned operation are observed so that they do not
        /// surface as unobserved exceptions.
        /// </summary>
        public static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> start, TimeSpan timeout, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                Task<T> work = Task.Run(() => start(cts.Token));
                Task delay = Task.Delay(timeout, cts.Token);
                Task done = await Task.WhenAny(work, delay).ConfigureAwait(false);
                if (done == work)
                {
                    cts.Cancel();                                            // stops the deadline timer
                    return await work.ConfigureAwait(false);
                }
                cts.Cancel();
                var observed = work.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("deadline of " + (int)timeout.TotalSeconds + " s exceeded");
            }
        }
    }
}
