using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PUi.Screens;
using Xunit;

namespace PUi.Tests
{
    /// <summary>The version is written once (AppVersion in Directory.Build.props); the places that cannot read it must agree with it.</summary>
    public class VersionTests
    {
        private static string PropsVersion()
        {
            var m = Regex.Match(File.ReadAllText(RepoPaths.Combine("Directory.Build.props")), @"<AppVersion>([^<]+)</AppVersion>");
            Assert.True(m.Success, "AppVersion is missing from Directory.Build.props");
            return m.Groups[1].Value.Trim();
        }

        [Fact]
        public void AppInfoReadsTheAssemblyVersionSetInDirectoryBuildProps()
        {
            Assert.Equal(PropsVersion(), AppInfo.Version);
            Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
        }

        [Fact]
        public void TizenManifestHasTheSameVersion()
        {
            var doc = XDocument.Load(RepoPaths.Combine("src", "PTizen", "tizen-manifest.xml"));
            Assert.Equal(AppInfo.Version, (string)doc.Root.Attribute("version"));
        }

        [Fact]
        public void AndroidProjectDerivesItsVersionFromTheSingleSource()
        {
            string csproj = File.ReadAllText(RepoPaths.Combine("src", "PAndroid", "PAndroid.csproj"));
            Assert.Contains("<ApplicationDisplayVersion>$(AppVersion)</ApplicationDisplayVersion>", csproj);
            Assert.Contains("<ApplicationVersion>$(AppVersionCode)</ApplicationVersion>", csproj);
        }

        [Fact]
        public void NoOtherProjectHardcodesAVersion()
        {
            foreach (var f in Directory.GetFiles(RepoPaths.Combine("src"), "*.csproj", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(f);
                Assert.DoesNotMatch(@"<(Version|AssemblyVersion|FileVersion|VersionPrefix)>", text);
            }
        }
    }
}
