using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>What a host of the central search answered to a raw request (diagnostic tool: ace search --ace --raw). The app does not use any of this.</summary>
    public sealed class AceRawResponse
    {
        public string Host = "", Url = "", Nonce = "";
        /// <summary>0 if there was no HTTP response (network, DNS, deadline).</summary>
        public int HttpStatus;
        public long ElapsedMs;
        /// <summary>Body as it arrived (cut to the configured maximum).</summary>
        public string Body = "";
        /// <summary>Network or read error (null if an HTTP response arrived).</summary>
        public string Error;
        /// <summary>Variant of the response signature that matched ("nonce", "plain", ...), or null if none.</summary>
        public string SignVariant;
        /// <summary>JSON already decoded from base64 (null if it could not be).</summary>
        public string Json;
        /// <summary>Why it could not be decoded (malformed envelope, signature, base64...).</summary>
        public string ParseProblem;
    }

    public sealed partial class AceCentralSearchProvider
    {
        /// <summary>The signed URL of a request (without sending it): to try it from another network with another tool.</summary>
        public string SignedUrl(string host, string query, string category, int page, int pageSize)
        {
            return host.TrimEnd('/') + BuildPath("a", BuildParams(query, category, page, pageSize), NewNonce(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        /// <summary>
        /// A signed request to ONE host, without cache or retries, returning everything that is known (HTTP status, body, signature, JSON). To debug the protocol from the
        /// command line. It does not check IsEnabled. With allVariants, response signature variants that the app does not use are also tried (in case the service changed).
        /// </summary>
        public async Task<AceRawResponse> RawRequestAsync(string host, string query, string category, int page, int pageSize, bool allVariants, CancellationToken ct)
        {
            var res = new AceRawResponse { Host = host };
            var p = BuildParams(query, category, page, pageSize);
            string nonce = NewNonce();
            res.Nonce = nonce;
            res.Url = host.TrimEnd('/') + BuildPath("a", p, nonce, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Uri uri;
            if (!Uri.TryCreate(res.Url, UriKind.Absolute, out uri)) { res.Error = "invalid URL"; return res; }
            var sw = Stopwatch.StartNew();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(_opt.HostTimeout);
                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                    {
                        req.Headers.TryAddWithoutValidation("User-Agent", RemoteHttp.UserAgent);
                        req.Headers.TryAddWithoutValidation("Accept", "application/json");
                        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            res.HttpStatus = (int)resp.StatusCode;
                            try
                            {
                                var body = await HttpLimits.ReadBodyAsync(resp.Content, _opt.MaxBytes, cts.Token).ConfigureAwait(false);
                                res.Body = Encoding.UTF8.GetString(body);
                            }
                            catch (System.IO.InvalidDataException) { res.Error = "body too large (maximum " + _opt.MaxBytes + " bytes)"; }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { res.Error = "timed out (" + (int)_opt.HostTimeout.TotalSeconds + " s)"; }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    var inner = ex;
                    while (inner.InnerException != null) inner = inner.InnerException;
                    res.Error = ex.GetType().Name + ": " + ex.Message + (inner != ex ? " <- " + inner.GetType().Name + ": " + inner.Message : "");
                }
            }
            res.ElapsedMs = sw.ElapsedMilliseconds;
            if (res.HttpStatus != 200 || res.Body.Length == 0) return res;
            Decode(res, nonce, allVariants);
            return res;
        }

        private void Decode(AceRawResponse res, string nonce, bool allVariants)
        {
            object env;
            if (!MiniJson.TryParse(res.Body.Trim(), out env)) { res.ParseProblem = "the body is not JSON"; return; }
            var o = MiniJson.AsObject(env);
            var data = o == null || !o.ContainsKey("data") ? null : o["data"] as string;
            var sign = o == null || !o.ContainsKey("sign") ? null : o["sign"] as string;
            if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(sign)) { res.ParseProblem = "data or sign missing"; return; }
            var d = Encoding.UTF8.GetBytes(data);
            var n = Encoding.ASCII.GetBytes(nonce);
            var variants = new List<KeyValuePair<string, byte[]>>();
            variants.Add(new KeyValuePair<string, byte[]>("nonce", Concat(d, n, ResponseKey)));
            variants.Add(new KeyValuePair<string, byte[]>("plain", Concat(d, ResponseKey)));
            if (allVariants)
            {
                byte[] raw = null;
                try { raw = Convert.FromBase64String(data); } catch (FormatException) { }
                var hash = Encoding.ASCII.GetBytes("#");
                variants.Add(new KeyValuePair<string, byte[]>("key+data", Concat(ResponseKey, d)));
                variants.Add(new KeyValuePair<string, byte[]>("nonce+data+key", Concat(n, d, ResponseKey)));
                variants.Add(new KeyValuePair<string, byte[]>("data#nonce#key", Concat(d, hash, n, hash, ResponseKey)));
                variants.Add(new KeyValuePair<string, byte[]>("data#key", Concat(d, hash, ResponseKey)));
                variants.Add(new KeyValuePair<string, byte[]>("data+key+nonce", Concat(d, ResponseKey, n)));
                variants.Add(new KeyValuePair<string, byte[]>("request-key", Concat(d, n, RequestKey)));
                if (raw != null)
                {
                    variants.Add(new KeyValuePair<string, byte[]>("decoded+nonce+key", Concat(raw, n, ResponseKey)));
                    variants.Add(new KeyValuePair<string, byte[]>("decoded+key", Concat(raw, ResponseKey)));
                }
            }
            foreach (var v in variants)
                if (SameHex(HashUtil.Sha1(v.Value), sign)) { res.SignVariant = v.Key; break; }
            try { res.Json = Encoding.UTF8.GetString(Convert.FromBase64String(data)); }
            catch (FormatException) { res.ParseProblem = "data is not valid base64"; }
            if (res.SignVariant == null) res.ParseProblem = (res.ParseProblem == null ? "" : res.ParseProblem + "; ") + "no variant of the response signature matches (bad sign)";
        }

        private static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
    }
}
