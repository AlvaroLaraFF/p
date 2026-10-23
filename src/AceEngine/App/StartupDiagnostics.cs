using System;
using AceEngine.Util;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Bencode;
using AceEngine.Crypto;
using AceEngine.P2P;
using AceEngine.Sources;
using AceEngine.Transport;

namespace AceEngine.App
{
    /// <summary>
    /// Self-diagnosis at startup: runtime, memory, that the engine loads and works (bencode, crypto with timings, local HTTP server,
    /// DNS TXT over UDP and over DoH). Everything goes to the log; the summary is also returned so it can be shown on screen.
    /// Shared by every shell.
    /// </summary>
    public static class StartupDiagnostics
    {
        private const string Tag = "diag";

        public static string RuntimeLine()
        {
            return "runtime=" + RuntimeInformation.FrameworkDescription + " Environment.Version=" + Environment.Version + " os=" + RuntimeInformation.OSDescription
                + " process=" + RuntimeInformation.ProcessArchitecture + " 64bit=" + Environment.Is64BitProcess + " cpus=" + Environment.ProcessorCount;
        }

        public static void LogMemory()
        {
            var mi = ProcMonitor.ReadKb("/proc/meminfo");
            var st = ProcMonitor.ReadKb("/proc/self/status");
            Func<System.Collections.Generic.Dictionary<string, long>, string, string> g = (d, k) => { long v; return d.TryGetValue(k, out v) ? (v / 1024) + "MiB" : "-"; };
            UdpLog.Info(Tag, "system memory: total=" + g(mi, "MemTotal") + " free=" + g(mi, "MemFree") + " available=" + g(mi, "MemAvailable") + " cache=" + g(mi, "Cached") + " swapTotal=" + g(mi, "SwapTotal"));
            UdpLog.Info(Tag, "process memory: rss=" + g(st, "VmRSS") + " peak=" + g(st, "VmHWM") + " vm=" + g(st, "VmSize") + " data=" + g(st, "VmData"));
            UdpLog.Info(Tag, "GC: server=" + GCSettings.IsServerGC + " latency=" + GCSettings.LatencyMode + " heap=" + (GC.GetTotalMemory(false) / (1024 * 1024)) + "MiB generations=" + (GC.MaxGeneration + 1));
            foreach (var f in new[] { "/proc/self/limits", "/sys/fs/cgroup/memory/memory.limit_in_bytes", "/sys/fs/cgroup/memory.max" })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    foreach (var line in File.ReadAllLines(f))
                        if (f.EndsWith("limits") ? (line.StartsWith("Max address space") || line.StartsWith("Max resident set") || line.StartsWith("Max open files") || line.StartsWith("Max processes")) : true)
                            UdpLog.Info(Tag, f + ": " + line.Trim());
                }
                catch { }
            }
        }

        private static string Ms(Stopwatch sw) { return sw.Elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms"; }

        /// <summary>Engine tests. Returns true if everything went well.</summary>
        public static async Task<bool> RunEngineSelfTestAsync(AppConfig cfg, Action<string> screen)
        {
            bool ok = true;
            Action<string> say = s => { UdpLog.Info(Tag, s); if (screen != null) screen(s); };
            try
            {
                var asm = typeof(AceEngineHost).Assembly;
                say("engine: " + asm.GetName().Name + " " + asm.GetName().Version + " (" + asm.ImageRuntimeVersion + ")");

                // Bencode with raw bytes
                var sw = Stopwatch.StartNew();
                var bv = BencodeParser.Parse(Encoding.ASCII.GetBytes("d3:cow3:moo4:spaml1:a1:bee"));
                say("bencode: OK (" + Ms(sw) + ")" + (bv.GetRawBytes().Length == 26 ? "" : " UNEXPECTED RAW"));

                // Own cryptography: timings on the TV (Ed25519 per live connection; RSA per piece; SHA-1 and AES per file/piece)
                byte[] seed, pub;
                sw.Restart(); Ed25519.GenerateKeyPair(out seed, out pub); var tGen = Ms(sw);
                var msg = new byte[32];
                sw.Restart(); var sig = Ed25519.Sign(seed, msg); var tSign = Ms(sw);
                sw.Restart(); bool v = Ed25519.Verify(pub, msg, sig); var tVer = Ms(sw);
                sw.Restart(); sig = Ed25519.Sign(seed, msg); var tSign2 = Ms(sw);
                sw.Restart(); v &= Ed25519.Verify(pub, msg, sig); var tVer2 = Ms(sw);
                say("ed25519: " + (v ? "OK" : "FAIL") + " key=" + tGen + " sign=" + tSign + "/" + tSign2 + " verify=" + tVer + "/" + tVer2);
                ok &= v;

                var rnd = new Random(1);
                var nb = new byte[97]; rnd.NextBytes(nb); nb[0] |= 0x80; nb[96] = 0; // positive 768-bit little-endian BigInteger
                var rsa = new RsaPublicKey(new BigInteger(nb), 3);
                var piece = new byte[512 * 1024]; rnd.NextBytes(piece);
                var fakeSig = new byte[rsa.SignSize]; rnd.NextBytes(fakeSig); fakeSig[0] = 0;
                sw.Restart(); bool rv = rsa.VerifySha1(piece, 0, piece.Length - rsa.SignSize, fakeSig, 0); var tRsa = Ms(sw);
                sw.Restart(); for (int i = 0; i < 5; i++) rsa.VerifySha1(piece, 0, piece.Length - rsa.SignSize, fakeSig, 0); var tRsa5 = sw.Elapsed.TotalMilliseconds / 5;
                say("rsa768/e=3 + sha1 of 512 KiB: " + tRsa + " (average of 5 = " + tRsa5.ToString("0.0", CultureInfo.InvariantCulture) + " ms; fake signature, result=" + rv + ")");
                var mb = new byte[1024 * 1024]; rnd.NextBytes(mb);
                sw.Restart(); var sha = System.Security.Cryptography.SHA1.Create().ComputeHash(mb); say("sha1 of 1 MiB: " + Ms(sw));
                sw.Restart(); bool pv; var enc = AceCrypto.M2AesEncrypt(mb, new byte[] { 1, 2, 3, 4 }, IvMode.Sha1OfMd5Key); var dec = AceCrypto.M2AesDecrypt(enc, new byte[] { 1, 2, 3, 4 }, IvMode.Sha1OfMd5Key, out pv);
                say("aes-cbc encrypt+decrypt 1 MiB: " + Ms(sw) + " " + (dec.Length == mb.Length && pv ? "OK" : "FAIL"));
                ok &= dec.Length == mb.Length;

                // Local HTTP server + TCP sockets on the loopback
                using (var host = new AceEngineHost(new EngineOptions { EnableListener = false, EnableDht = false }))
                {
                    host.Start();
                    say("local HTTP server: 127.0.0.1:" + host.Server.Port);
                    using (var tcp = new TcpClient())
                    {
                        var conn = tcp.ConnectAsync(IPAddress.Loopback, host.Server.Port).Observed();
                        if (await Task.WhenAny(conn, Task.Delay(3000)) != conn) { say("local HTTP server: cannot connect"); ok = false; }
                        else
                        {
                            await conn;
                            var ns = tcp.GetStream();
                            var req = Encoding.ASCII.GetBytes("GET /stream/nope HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                            await ns.WriteAsync(req, 0, req.Length);
                            var buf = new byte[256];
                            var rd = ns.ReadAsync(buf, 0, buf.Length);
                            if (await Task.WhenAny(rd, Task.Delay(3000)) != rd) { say("local HTTP server: no response"); ok = false; }
                            else
                            {
                                int n = await rd;
                                var first = Encoding.ASCII.GetString(buf, 0, n).Split('\r')[0];
                                say("local HTTP server responds: " + first);
                                ok &= first.Contains("404");
                            }
                        }
                    }
                }

                // Network: DNS TXT over UDP 53 and over DoH (HttpClient/TLS)
                if (cfg.DiagnosticsNetwork)
                {
                    var o = new DnsOptions { Timeout = TimeSpan.FromSeconds(5) };
                    sw.Restart();
                    try
                    {
                        var recs = await new DnsTxtClient(o).QueryTxtAsync("google.com", CancellationToken.None);
                        say("DNS TXT (UDP 8.8.8.8): " + recs.Count + " records in " + Ms(sw));
                    }
                    catch (Exception ex) { say("DNS TXT (UDP) FAIL: " + ex.GetType().Name + " " + ex.Message); ok = false; }
                    var o2 = new DnsOptions { Servers = new System.Collections.Generic.List<string>(), UseDohFallback = true, Timeout = TimeSpan.FromSeconds(8) };
                    sw.Restart();
                    try
                    {
                        var recs = await new DnsTxtClient(o2).QueryTxtAsync("google.com", CancellationToken.None);
                        say("DNS TXT (DoH, HttpClient+TLS): " + recs.Count + " records in " + Ms(sw));
                    }
                    catch (Exception ex) { say("DNS TXT (DoH) FAIL: " + ex.GetType().Name + " " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                say("SELF-DIAGNOSIS: exception " + ex.GetType().Name + ": " + ex.Message);
                UdpLog.Error(Tag, ex.ToString());
                ok = false;
            }
            say("self-diagnosis " + (ok ? "OK" : "WITH FAILURES"));
            return ok;
        }
    }
}
