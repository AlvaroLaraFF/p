using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Util;

namespace AceEngine.Cli
{
    /// <summary>
    /// `ace peer-test host:port[,...] infohash`: connects to specific peers of a live stream with a custom EXTEND and counts what they do (messages, closes). It is the tool
    /// to try variants of nt, peer_type, tt, pv, v, platform, is_accessible, window and signature against the peers that close on us.
    /// </summary>
    internal static class PeerTest
    {
        public static async Task<int> RunAsync(List<string> pos, EngineOptions opt, Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            if (pos.Count < 2) { Console.Error.WriteLine("usage: ace peer-test host:port[,host:port...] <live-infohash> [--seconds 8] [--accessible 1] [--live-window N] [--port N] [--no-yourip] [--unsigned] + --nt --peer-type --platform --pv --engine-version --tt"); return 2; }
            var inv = CultureInfo.InvariantCulture;
            var infoHash = Hex.Parse(pos[1]);
            int seconds = int.Parse(get("--seconds", "8"), inv);
            var keys = NodeKeys.Generate();
            var state = new LiveLocalState { ListenPort = int.Parse(get("--port", "0"), inv), LiveWindowSize = int.Parse(get("--live-window", "100"), inv), Accessible = get("--accessible", "0") == "1" };
            if (has("--lsp")) state.Lsp = long.Parse(get("--lsp", "-1"), inv);
            if (has("--up-rate")) { state.UpRate = long.Parse(get("--up-rate", "0"), inv); state.TopUpRate = Math.Max(state.UpRate, long.Parse(get("--top-up-rate", "0"), inv)); state.TopSessionUpRate = state.TopUpRate; }
            if (has("--down-rate")) state.DownRate = long.Parse(get("--down-rate", "0"), inv);
            if (has("--upload-rating")) state.UploadRating = int.Parse(get("--upload-rating", "0"), inv);
            if (has("--distance")) state.DistanceFromSource = int.Parse(get("--distance", "-1"), inv);
            if (has("--unsigned")) opt.Live.SignExtend = false;
            var lo = opt.Live;
            Console.WriteLine("variant: nt=" + lo.NodeType + " peer_type=" + lo.PeerType + " platform=" + lo.Platform + " pv=" + lo.ProtocolVersion + " v=" + lo.EngineVersion + " tt=" + lo.TransportType
                + " signature=" + (lo.SignExtend ? "yes" : "no") + " accessible=" + state.Accessible + " window=" + state.LiveWindowSize + " port=" + state.ListenPort + " yourip=" + (!has("--no-yourip")) + " up_rate=" + state.UpRate + " upload_rating=" + state.UploadRating + " distance=" + state.DistanceFromSource);
            var tasks = new List<Task<string>>();
            foreach (var hp in pos[0].Split(','))
            {
                var parts = hp.Trim().Split(':');
                IPAddress ipa; int port;
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out ipa) || !int.TryParse(parts[1], out port)) continue;
                var ep = new IPEndPoint(ipa, port);
                tasks.Add(OneAsync(ep, infoHash, opt, state, keys, seconds, has("--no-yourip"), ct));
            }
            var res = await Task.WhenAll(tasks);
            foreach (var r in res) Console.WriteLine(r);
            return 0;
        }

        private static async Task<string> OneAsync(IPEndPoint ep, byte[] infoHash, EngineOptions opt, LiveLocalState state, NodeKeys keys, int seconds, bool noYourIp, CancellationToken ct)
        {
            var log = new StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Action<string> L = s => { lock (log) log.AppendLine("  " + ep + " +" + sw.ElapsedMilliseconds.ToString("0000", CultureInfo.InvariantCulture) + " ms " + s); };
            string end = "?";
            try
            {
                using (var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true })
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(seconds));
                    var c = tcp.ConnectAsync(ep.Address, ep.Port);
                    if (await Task.WhenAny(c, Task.Delay(4000, cts.Token)) != c) return ep + ": tcp timeout";
                    await c;
                    L("TCP ok");
                    var s = tcp.GetStream();
                    var hs = Wire.BuildHandshake(opt.HandshakeReserved, infoHash, PeerId.Generate(opt.PeerIdPrefix));
                    await s.WriteAsync(hs, 0, hs.Length, cts.Token);
                    var one = new byte[1];
                    await StreamUtil.ReadExactAsync(s, one, 1, cts.Token);
                    var rest = new byte[one[0] + 8 + 20 + 20];
                    await StreamUtil.ReadExactAsync(s, rest, rest.Length, cts.Token);
                    L("handshake: " + Encoding.ASCII.GetString(rest, 0, one[0]) + " peer_id " + Encoding.ASCII.GetString(rest, one[0] + 8 + 20, 20).Replace('\0', '.'));
                    long ts = (long)(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency);
                    var ip = noYourIp ? null : ep.Address.GetAddressBytes();
                    var ext = LiveExtend.BuildFrame(LiveExtend.BuildPayload(opt.Live, state, opt.Live.PeerType, ip, ts, keys.Seed, keys.PublicKey));
                    await s.WriteAsync(ext, 0, ext.Length, cts.Token);
                    var lenBuf = new byte[4];
                    int n = 0;
                    var counts = new Dictionary<string, int>();
                    while (!cts.IsCancellationRequested)
                    {
                        await StreamUtil.ReadExactAsync(s, lenBuf, 4, cts.Token);
                        uint len = Wire.GetU32(lenBuf, 0);
                        if (len == 0) { L("keepalive"); continue; }
                        var msg = await StreamUtil.ReadMessageAsync(s, (int)len, cts.Token);
                        string name = LiveId.Name(msg[0]);
                        int cnt; counts.TryGetValue(name, out cnt); counts[name] = cnt + 1;
                        if (msg[0] == LiveId.Extend)
                        {
                            LiveExtendInfo info;
                            if (LiveExtend.TryParse(msg, out info, false))
                            {
                                L("EXTEND v=" + info.V + " nt=" + info.Nt + " pv=" + info.Pv + " platform=" + info.Platform + " tt=" + info.Tt + " signature=" + info.HasSignature + " lsp=" + info.Lsp + " mi.max_piece=" + info.MiMaxPiece + " mi.position=" + info.MiPosition + " accessible=" + (info.Mi != null ? (info.Mi.GetInt("is_accessible") ?? -1).ToString() : "?"));
                                if (n++ == 0) L("   " + LiveExtend.Describe(info.Raw));
                            }
                            else L("EXTEND not interpretable");
                        }
                        else if (cnt < 2 || msg[0] == LiveId.Close || msg[0] == LiveId.Peers) L(name + " " + len + " B");
                        if (msg[0] == LiveId.Close) { end = "CLOSE from the peer"; break; }
                        if (msg[0] == LiveId.Ping) { int seq, size; if (LiveWire.TryParsePingPong(msg, out seq, out size)) { var pong = LiveWire.Pong(seq, Math.Min(size, 4096)); await s.WriteAsync(pong, 0, pong.Length, cts.Token); } }
                    }
                    if (end == "?") end = "end of time";
                    L("summary: " + string.Join(", ", counts.Select(k => k.Key + "x" + k.Value)));
                }
            }
            catch (OperationCanceledException) { end = "end of time"; }
            catch (Exception ex) { end = ex.GetType().Name + ": " + ex.Message; }
            lock (log) return ep + " => " + end + "\n" + log.ToString().TrimEnd();
        }
    }
}
