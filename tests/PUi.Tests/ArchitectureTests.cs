using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PUi.Tests
{
    /// <summary>
    /// The layering of the solution as tests (plan of layers, F1 step 10): the engine knows nothing of Skia, the interface knows nothing of any platform, the shells do not depend on
    /// each other, conditional compilation lives in one file, and the TV keeps loading its own SkiaSharp. They read the project files and sources of the repository.
    /// </summary>
    public class ArchitectureTests
    {
        private static readonly string[] Shells = { "PTizen", "PAndroid", "PDesktop" };
        private static readonly string[] PlatformNamespaces = { "Tizen", "ElmSharp", "Android", "Java", "Xamarin", "Mono.Android" };

        // ------------------------------------------------------------------ helpers

        private static IEnumerable<string> Sources(params string[] under)
        {
            foreach (var dir in under)
                foreach (var f in Directory.GetFiles(RepoPaths.Combine(dir), "*.cs", SearchOption.AllDirectories))
                {
                    string n = f.Replace('\\', '/');
                    if (n.Contains("/obj/") || n.Contains("/bin/")) continue;
                    yield return f;
                }
        }

        private static IEnumerable<string> Projects()
        {
            foreach (var dir in new[] { "src", "tests" })
                foreach (var f in Directory.GetFiles(RepoPaths.Combine(dir), "*.csproj", SearchOption.AllDirectories)) yield return f;
        }

        private static XDocument Load(string csproj) { return XDocument.Load(csproj); }
        private static IEnumerable<XElement> Items(XDocument d, string name) { return d.Descendants().Where(e => e.Name.LocalName == name); }
        private static string Attr(XElement e, string a) { var x = e.Attribute(a); return x == null ? "" : x.Value; }
        private static string Proj(string name) { return Directory.GetFiles(RepoPaths.Combine("src", name), "*.csproj").Single(); }

        /// <summary>Source text without comments (// and /* */) so that a mention of a platform in a comment is not a use of it.</summary>
        private static string Code(string file)
        {
            string s = File.ReadAllText(file);
            s = Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline);
            s = Regex.Replace(s, @"(?<!:)//.*$", "", RegexOptions.Multiline);
            return s;
        }

        private static IEnumerable<string> ReferencedProjects(string csproj)
        {
            foreach (var r in Items(Load(csproj), "ProjectReference"))
                yield return Path.GetFileNameWithoutExtension(Attr(r, "Include").Replace('\\', '/'));
        }

        private static IEnumerable<string> AssemblyRefs(Assembly a) { return a.GetReferencedAssemblies().Select(n => n.Name); }

        // ------------------------------------------------------------------ the engine does not know Skia

        [Fact]
        public void AceEngineDoesNotReferenceSkia()
        {
            var doc = Load(Proj("AceEngine"));
            Assert.Empty(Items(doc, "PackageReference"));                                            // no NuGet at all
            Assert.Empty(Items(doc, "ProjectReference"));                                            // and no project: the engine is the bottom layer
            Assert.DoesNotContain(AssemblyRefs(typeof(AceEngine.App.AppConfig).Assembly), n => n.StartsWith("SkiaSharp") || n == "PUi");
            foreach (var f in Sources("src/AceEngine"))
            {
                string code = Code(f);
                Assert.False(Regex.IsMatch(code, @"\bSkiaSharp\b"), f + " uses SkiaSharp");
                Assert.False(Regex.IsMatch(code, @"\busing\s+PUi\b"), f + " uses PUi");
            }
        }

        [Fact]
        public void TheEngineHasNoPlatformCodeEither()
        {
            foreach (var f in Sources("src/AceEngine"))
                foreach (var ns in PlatformNamespaces)
                    Assert.False(Regex.IsMatch(Code(f), @"\busing\s+" + Regex.Escape(ns) + @"\b"), f + " uses " + ns);
            Assert.DoesNotContain(AssemblyRefs(typeof(AceEngine.App.AppConfig).Assembly), n => PlatformNamespaces.Any(p => n.StartsWith(p)));
        }

        // ------------------------------------------------------------------ the interface does not know the platforms

        [Fact]
        public void PUiDoesNotUseTizenElmSharpOrAndroid()
        {
            foreach (var f in Sources("src/PUi"))
            {
                string code = Code(f);
                foreach (var ns in PlatformNamespaces)
                {
                    Assert.False(Regex.IsMatch(code, @"\busing\s+" + Regex.Escape(ns) + @"\b"), f + " uses " + ns);
                    Assert.False(Regex.IsMatch(code, @"\b" + Regex.Escape(ns) + @"\.[A-Z]"), f + " mentions " + ns + " in code");
                }
            }
            Assert.DoesNotContain(AssemblyRefs(typeof(PUi.App.PApp).Assembly), n => PlatformNamespaces.Any(p => n.StartsWith(p)));
            var doc = Load(Proj("PUi"));
            foreach (var r in ReferencedProjects(Proj("PUi"))) Assert.DoesNotContain(r, Shells);
            foreach (var p in Items(doc, "PackageReference")) Assert.False(PlatformNamespaces.Any(ns => Attr(p, "Include").StartsWith(ns)), "PUi references the package " + Attr(p, "Include"));
        }

        [Fact]
        public void PUiAndTheDemoDataOnlyDependOnTheLayersBelowThem()
        {
            Assert.Equal(new[] { "AceEngine" }, ReferencedProjects(Proj("PUi")).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { "AceEngine" }, ReferencedProjects(Proj("PUi.Demo")).OrderBy(x => x).ToArray());
            Assert.DoesNotContain(AssemblyRefs(typeof(PUi.Demo.DemoMode).Assembly), n => n.StartsWith("SkiaSharp") || n == "PUi");
            Assert.DoesNotContain(AssemblyRefs(typeof(AceEngine.App.AppConfig).Assembly), n => n == "PUi.Demo");
        }

        // ------------------------------------------------------------------ the shells

        [Fact]
        public void NoShellReferencesAnotherShellOrPDesktop()
        {
            foreach (var shell in Shells)
            {
                string csproj = Proj(shell);
                foreach (var r in ReferencedProjects(csproj)) Assert.False(Shells.Contains(r), shell + " references the shell " + r);
                // nor does it compile another shell's files in
                foreach (var c in Items(Load(csproj), "Compile"))
                {
                    string inc = Attr(c, "Include").Replace('\\', '/');
                    foreach (var other in Shells) Assert.False(other != shell && inc.Contains("/" + other + "/"), shell + " compiles " + inc);
                }
            }
        }

        [Fact]
        public void NoLibraryOrTestProjectReferencesAShellAndNoDeviceShellReferencesTheTestSupport()
        {
            foreach (var csproj in Projects())
            {
                string name = Path.GetFileNameWithoutExtension(csproj);
                if (Shells.Contains(name)) continue;
                foreach (var r in ReferencedProjects(csproj)) Assert.False(Shells.Contains(r), name + " references the shell " + r);
            }
            foreach (var device in new[] { "PTizen", "PAndroid" })
                foreach (var r in ReferencedProjects(Proj(device))) Assert.NotEqual("PUi.Testing", r);
        }

        [Fact]
        public void TheOnlyShellFilesTheTestsLinkArePureTables()
        {
            // PTizen's key table and everything in PAndroid/Pure (code that uses no Android API, see the next test): the rest of a shell cannot run on the PC
            foreach (var csproj in Projects().Where(p => p.Replace('\\', '/').Contains("/tests/")))
                foreach (var c in Items(Load(csproj), "Compile"))
                {
                    string inc = Attr(c, "Include").Replace('\\', '/');
                    foreach (var shell in Shells)
                        if (inc.Contains("/" + shell + "/"))
                            Assert.True(inc.EndsWith("PTizen/Platform/TizenKeys.cs") || inc.Contains("/PAndroid/Pure/"), "a test project links " + inc);
                }
        }

        [Fact]
        public void TheAndroidPureSourcesUseNoAndroidApi()
        {
            var files = Directory.GetFiles(RepoPaths.Combine("src", "PAndroid", "Pure"), "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
            foreach (var f in files)
            {
                string code = Code(f);
                foreach (var ns in PlatformNamespaces)
                {
                    Assert.False(Regex.IsMatch(code, @"\busing\s+" + Regex.Escape(ns) + @"\b"), f + " uses " + ns);
                    Assert.False(Regex.IsMatch(code, @"\b" + Regex.Escape(ns) + @"\.[A-Z]"), f + " mentions " + ns + " in code");
                }
            }
        }

        // ------------------------------------------------------------------ conditional compilation

        [Fact]
        public void ConditionalCompilationLivesOnlyInSkiaCompat()
        {
            var offenders = new List<string>();
            foreach (var f in Sources("src", "tests"))
            {
                string n = f.Replace('\\', '/');
                if (n.EndsWith("src/PUi/Gfx/SkiaCompat.cs")) continue;
                if (Regex.IsMatch(File.ReadAllText(f), @"^\s*#\s*(if|elif|else|endif)\b", RegexOptions.Multiline)) offenders.Add(n);
            }
            Assert.True(offenders.Count == 0, "#if outside Gfx/SkiaCompat.cs: " + string.Join(", ", offenders));
            Assert.True(File.Exists(RepoPaths.Combine("src", "PUi", "Gfx", "SkiaCompat.cs")));
        }

        [Fact]
        public void NoProjectDefinesItsOwnCompilationSymbols()
        {
            foreach (var csproj in Projects())
                Assert.Empty(Items(Load(csproj), "DefineConstants"));
        }

        // ------------------------------------------------------------------ the TV keeps its own SkiaSharp

        [Fact]
        public void TheTvKeepsLoadingItsOwnSkiaSharpAndNothingBundlesAnother()
        {
            // PTizen: SkiaSharp 1.60.3 only to compile against; no runtime, native or build assets from the package
            var tizen = Items(Load(Proj("PTizen")), "PackageReference").Single(p => Attr(p, "Include") == "SkiaSharp");
            Assert.Equal("1.60.3", Attr(tizen, "Version"));
            var excluded = Attr(tizen, "ExcludeAssets").Split(';');
            Assert.Contains("runtime", excluded); Assert.Contains("native", excluded); Assert.Contains("build", excluded);

            // PUi's netstandard2.0 target: the same, and private (it does not flow to the consumers)
            var pui = Load(Proj("PUi"));
            var group = Items(pui, "ItemGroup").Single(g => Attr(g, "Condition").Contains("netstandard2.0"));
            var skia = group.Elements().Single(e => e.Name.LocalName == "PackageReference" && Attr(e, "Include") == "SkiaSharp");
            Assert.Equal("1.60.3", Attr(skia, "Version"));
            Assert.Equal("all", Attr(skia, "PrivateAssets"));
            foreach (var a in new[] { "runtime", "native", "build" }) Assert.Contains(a, Attr(skia, "ExcludeAssets").Split(';'));

            // no native SkiaSharp package in anything that goes to the TV, and no SkiaSharp 3.x in the repository while the TV is a target
            foreach (var proj in new[] { "PTizen", "PUi", "PUi.Demo", "AceEngine" })
                Assert.DoesNotContain(Items(Load(Proj(proj)), "PackageReference"), p => Attr(p, "Include").StartsWith("SkiaSharp.NativeAssets"));
            foreach (var csproj in Projects())
                foreach (var p in Items(Load(csproj), "PackageReference").Where(p => Attr(p, "Include").StartsWith("SkiaSharp")))
                    Assert.False(Attr(p, "Version").StartsWith("3."), Path.GetFileName(csproj) + " uses SkiaSharp 3.x");
        }

        [Fact]
        public void PUiTargetsExactlyNetStandard20ForTheTvAndNet8ForTheRest()
        {
            var tf = Items(Load(Proj("PUi")), "TargetFrameworks").Single().Value.Split(';').OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "net8.0", "netstandard2.0" }, tf);
            Assert.Equal("netstandard2.0", Items(Load(Proj("AceEngine")), "TargetFramework").Single().Value);
            Assert.Equal("netstandard2.0", Items(Load(Proj("PUi.Demo")), "TargetFramework").Single().Value);
        }

        [Fact]
        public void TheTvShellReferencesPUiAndTheEngineNotTheDesktopSources()
        {
            var refs = ReferencedProjects(Proj("PTizen")).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "AceEngine", "PUi", "PUi.Demo" }, refs);
        }
    }
}
