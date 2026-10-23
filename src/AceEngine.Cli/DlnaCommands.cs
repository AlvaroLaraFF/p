using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Cast;
using AceEngine.Cast.Dlna;

namespace AceEngine.Cli
{
    /// <summary>`ace dlna-scan`: finds the UPnP MediaRenderers (Smart TVs) of the network and asks them READ-ONLY questions (what formats they accept, what state they are in). It never plays anything.</summary>
    internal static class DlnaCommands
    {
        public static async Task<int> ScanAsync(Func<string, string, string> get, Func<string, bool> has, CancellationToken ct)
        {
            var o = new DlnaDiscoveryOptions();
            if (has("--seconds")) o.Ssdp.Window = TimeSpan.FromSeconds(double.Parse(get("--seconds", "4"), CultureInfo.InvariantCulture));
            var d = new DlnaDiscovery(null, null, o);
            var res = await d.DiscoverAsync(ct).ConfigureAwait(false);
            Console.WriteLine("SSDP: " + res.Responses + " responses, " + res.Renderers.Count + " MediaRenderer(s) in " + res.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
            foreach (var n in res.Notes) Console.WriteLine("  note: " + n);
            string only = get("--only", null);
            using (var client = new DlnaClient())
            {
                foreach (var r in res.Renderers)
                {
                    if (only != null && !r.Location.Host.Equals(only)) continue;
                    Console.WriteLine();
                    Console.WriteLine("* " + r.FriendlyName);
                    Console.WriteLine("    address:      " + r.Location.Host + "   description: " + r.Location);
                    Console.WriteLine("    manufacturer: " + r.Manufacturer + "   model: " + r.ModelName + " " + r.ModelNumber + "   UDN: " + r.Udn);
                    Console.WriteLine("    AVTransport:       " + (r.AvTransport == null ? "-" : r.AvTransport.ServiceType + " @ " + r.AvTransport.ControlUrl));
                    Console.WriteLine("    RenderingControl:  " + (r.RenderingControl == null ? "-" : r.RenderingControl.ControlUrl.ToString()));
                    Console.WriteLine("    ConnectionManager: " + (r.ConnectionManager == null ? "-" : r.ConnectionManager.ControlUrl.ToString()));
                    var variants = DlnaQuirks.VariantsFor(r.Manufacturer, r.ModelName);
                    try
                    {
                        var sink = await client.GetProtocolInfoAsync(r, ct).ConfigureAwait(false);
                        var video = sink.Where(s => s.ContentFormat.StartsWith("video/", StringComparison.Ordinal)).ToList();
                        Console.WriteLine("    GetProtocolInfo: " + sink.Count + " formats; " + video.Count + " video");
                        foreach (var g in sink.GroupBy(s => s.ContentFormat).OrderBy(g => g.Key)) Console.WriteLine("        " + g.Key + "  x" + g.Count() + (g.Key == "video/mp2t" || g.Key == "video/mpeg" || g.Key == "video/vnd.dlna.mpeg-tts" ? "   <-- TS candidate" : ""));
                        if (has("--video")) foreach (var v in video) Console.WriteLine("        " + v);
                        variants = DlnaQuirks.Prefer(variants, sink);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { Console.WriteLine("    GetProtocolInfo: failed: " + ex.Message); }
                    try
                    {
                        var t = await client.GetTransportInfoAsync(r, ct).ConfigureAwait(false);
                        Console.WriteLine("    GetTransportInfo: state=" + t.RawState + " status=" + t.Status + " speed=" + t.Speed);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException)) { Console.WriteLine("    GetTransportInfo: failed: " + ex.Message); }
                    Console.WriteLine("    variants to try, in order: " + string.Join("  >  ", variants.Select(v => v.ToString())));
                }
            }
            return res.Renderers.Count > 0 ? 0 : 1;
        }
    }
}
