using System;
using System.IO;
using System.Linq;
using AceEngine.P2P;
using Xunit;

namespace AceEngine.Tests.Peers
{
    /// <summary>DHT node table saved between runs (the TV recreates the engine every time the app is paused).</summary>
    public class DhtNodesTests
    {
        private static string Tmp() { return Path.Combine(Path.GetTempPath(), "dhtnodes-" + Guid.NewGuid().ToString("N") + ".json"); }

        private static string Json(params string[] nodes)
        {
            return "{\"v\":1,\"nodes\":[" + string.Join(",", nodes.Select(n => "[\"" + n.Split('@')[0] + "\",\"" + n.Split('@')[1] + "\"]")) + "]}";
        }

        private const string IdA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", IdB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Fact]
        public void LoadsValidNodesAndSavesThemBack()
        {
            var path = Tmp();
            try
            {
                File.WriteAllText(path, Json(IdA + "@85.54.18.1:6881", IdB + "@2.237.74.215:51413"));
                using (var d = new DhtClient(new EngineOptions()))
                {
                    Assert.Equal(2, d.LoadNodes(path));
                    Assert.Equal(2, d.KnownNodes);
                    Assert.Equal(0, d.LoadNodes(path));                       // they are already there: not repeated
                    var path2 = Tmp();
                    try
                    {
                        d.SaveNodes(path2);
                        using (var e = new DhtClient(new EngineOptions()))
                        {
                            Assert.Equal(2, e.LoadNodes(path2));
                            Assert.Equal(2, e.KnownNodes);
                        }
                    }
                    finally { File.Delete(path2); }
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void GarbageAndHostileFilesAreIgnored()
        {
            var path = Tmp();
            try
            {
                using (var d = new DhtClient(new EngineOptions()))
                {
                    Assert.Equal(0, d.LoadNodes(path));                       // it does not exist
                    File.WriteAllText(path, "{esto no es json");
                    Assert.Equal(0, d.LoadNodes(path));
                    File.WriteAllText(path, Json("zz@85.54.18.1:6881", IdA + "@no-es-un-par", IdA + "@85.54.18.1:0", IdA + "@[::1]:6881"));
                    Assert.Equal(0, d.LoadNodes(path));
                    File.WriteAllText(path, new string(' ', 300 * 1024));
                    Assert.Equal(0, d.LoadNodes(path));
                    Assert.Equal(0, d.KnownNodes);
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void TheTableIsBounded()
        {
            var path = Tmp();
            try
            {
                var nodes = Enumerable.Range(0, 1000).Select(i => IdA + "@10." + (i / 250) + "." + (i % 250) + ".1:6881").ToArray();
                File.WriteAllText(path, Json(nodes));
                using (var d = new DhtClient(new EngineOptions()))
                {
                    Assert.Equal(400, d.LoadNodes(path));
                    Assert.Equal(400, d.KnownNodes);
                }
            }
            finally { File.Delete(path); }
        }
    }
}
