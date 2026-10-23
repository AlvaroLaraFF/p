using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AceEngine.App.Tv;
using AceEngine.Resolve;
using AceEngine.Tests.Fuzz;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Tv2
{
    /// <summary>Fuzzing of what arrives from search providers and from the settings file: never an undocumented exception and everything bounded.</summary>
    public class SearchFuzzTests
    {
        private const string Ih = "0123456789abcdef0123456789abcdef01234567";

        private static List<byte[]> Seeds()
        {
            var texts = new[]
            {
                "{\"result\":{\"total\":2,\"time\":0.1,\"results\":[{\"name\":\"BBC\",\"items\":[{\"name\":\"BBC One\",\"infohash\":\"" + Ih + "\",\"categories\":[\"general\"],\"disabled\":false,\"peers\":4}],\"epg\":[{\"start\":1,\"stop\":2,\"name\":\"x\"}],\"icons\":[{\"type\":0,\"url\":\"http://x/y.png\"}]}]}}",
                "{\"total\":1,\"time\":1,\"results\":[{\"name\":\"C\",\"items\":[{\"n\":\"Canal\",\"i\":\"" + Ih + "\",\"c\":[\"sport\"],\"a\":0.9,\"au\":1,\"ci\":3,\"s\":2,\"d\":0},{\"n\":\"Flujo\",\"u\":\"http://x.test/a.m3u8\"}]}]}",
                "[{\"name\":\"Uno\",\"url\":\"acestream://" + Ih + "\"},{\"name\":\"Dos\",\"content_id\":\"" + Ih + "\"},3,null,\"x\",{\"error\":true}]",
                "{\"result\":null,\"error\":{\"code\":0,\"message\":\"search is not available\"}}",
                "#EXTM3U\n#EXTINF:-1 tvg-id=\"1\" tvg-name=\"BBC\" group-title=\"general\",BBC One HD\nhttp://127.0.0.1:6878/ace/getstream?infohash=" + Ih + "\n#EXTINF:-1,x\nfile:///etc/passwd\n#EXTINF:-1,Y\nacestream://" + Ih + "\nhttp://a.test/b.ts\n"
            };
            return texts.Select(t => Encoding.UTF8.GetBytes(t)).ToList();
        }

        [Fact]
        public void TheResultParserIsBoundedAndOnlyThrowsItsOwnError()
        {
            FuzzKit.Run("busqueda-resultados", FuzzKit.Inputs(Seeds(), 31, 2000, FuzzKit.Iterations), b =>
            {
                var list = RemoteResultParser.ParseAuto(Encoding.UTF8.GetString(b), 50);
                Assert.True(list.Count <= 50);
                foreach (var r in list)
                {
                    Assert.True(r.Name.Length > 0 && r.Name.Length <= RemoteResultParser.MaxNameLength);
                    Assert.True(r.Category.Length <= RemoteResultParser.MaxCategoryLength);
                    Assert.True(r.Url.Length <= RemoteResultParser.MaxUrlLength);
                    Assert.True(r.Url.StartsWith("acestream:", StringComparison.OrdinalIgnoreCase) || r.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                        || r.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || r.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase), r.Url);
                    Assert.DoesNotContain(r.Name, c => char.IsControl(c));
                    Assert.True(r.Peers >= -1 && r.Peers <= 1000000 && r.Seeds >= -1 && r.Seeds <= 1000000);
                }
            }, typeof(RemoteFetchException));
        }

        [Fact]
        public void TheAceCentralResponseParserOnlyThrowsItsOwnError()
        {
            // valid envelope with a correct signature + mutated content, and mutated envelopes
            var content = Encoding.UTF8.GetBytes("{\"total\":1,\"time\":1,\"results\":[{\"name\":\"C\",\"items\":[{\"n\":\"Canal\",\"i\":\"" + Ih + "\",\"c\":[\"sport\"]}]}]}");
            var seeds = new List<byte[]> { content };
            FuzzKit.Run("ace-search-content", FuzzKit.Inputs(seeds, 32, 500, FuzzKit.Iterations), b =>
            {
                string data = Convert.ToBase64String(b);
                var dataBytes = Encoding.UTF8.GetBytes(data);
                var buf = dataBytes.Concat(Encoding.ASCII.GetBytes("42")).Concat(Encoding.ASCII.GetBytes("FEv5QHTKzoTZG5oWTXUpmetdjf4cfSYVs8sZDkbzBdupR6la7xaH3")).ToArray();
                string sign = Hex.ToHex(HashUtil.Sha1(buf));
                int g; long t;
                var list = AceCentralSearchProvider.ParseResponse("{\"data\":\"" + data + "\",\"sign\":\"" + sign + "\"}", "42", 1 << 20, 30, out g, out t);
                Assert.True(list.Count <= 30);
            }, typeof(RemoteFetchException));
            var envelope = new List<byte[]> { Encoding.UTF8.GetBytes("{\"data\":\"eyJ0b3RhbCI6IDB9\",\"sign\":\"f597f44c17625e038a24556f1fa0a648a789cd6c\"}") };
            FuzzKit.Run("ace-search-envelope", FuzzKit.Inputs(envelope, 33, 300, FuzzKit.Iterations), b =>
            {
                int g; long t;
                AceCentralSearchProvider.ParseResponse(Encoding.UTF8.GetString(b), "123456789", 1 << 20, 30, out g, out t);
            }, typeof(RemoteFetchException));
        }

        [Fact]
        public void TheSettingsParserNeverThrowsAndAlwaysReturnsAValidObject()
        {
            var seeds = new List<byte[]>
            {
                Encoding.UTF8.GetBytes("{\"onlyLive\":true,\"uploadKBps\":512,\"upnpEnabled\":true,\"tsServiceEnabled\":true,\"aceSearchEnabled\":false}"),
                Encoding.UTF8.GetBytes("{\"uploadEnabled\":false,\"uploadMaxBytesPerSec\":262144,\"uploadSlots\":2}")
            };
            FuzzKit.Run("ajustes", FuzzKit.Inputs(seeds, 34, 400, FuzzKit.Iterations * 2), b =>
            {
                var s = UserSettingsStore.Parse(Encoding.UTF8.GetString(b));
                Assert.Contains(s.UploadKBps, UserSettings.UploadStepsKBps);
            });
        }

        [Fact]
        public void TheTsServiceResponseClassifierNeverThrows()
        {
            var seeds = new List<byte[]>
            {
                Encoding.UTF8.GetBytes("<response r=\"1\" sig=\"abc\"><torrent>AQIDBA==</torrent><checksum>00</checksum></response>"),
                Encoding.UTF8.GetBytes("<response><status>error</status></response>"),
                Encoding.UTF8.GetBytes("<html>HTTP 451</html>")
            };
            FuzzKit.Run("tsservice-clasificacion", FuzzKit.Inputs(seeds, 35, 300, FuzzKit.Iterations), b =>
            {
                string err; bool wf;
                TsServiceClient.ParseResponseCore(b, "c8c33b2673efe7f386790c5966643820578f3535", 7, new ResolverOptions(), out err, out wf);
            });
        }
    }
}
