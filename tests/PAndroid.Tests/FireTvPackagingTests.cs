using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PAndroid.Tests
{
    /// <summary>
    /// What makes the one apk installable and visible on a Fire TV (and everything else), checked on the sources: the manifest, the activity's attributes, the banner, the ABIs the project builds, the
    /// absence of anything that needs Google Play services, and the lifecycle rules that keep an Alexa overlay from cutting the channel. tools/apkcheck.py does the same on the built apk (aapt2
    /// and apksigner need the SDK, which these tests do not).
    /// </summary>
    public class FireTvPackagingTests
    {
        private static readonly string Root = FindRoot();

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "P.sln"))) dir = dir.Parent;
            if (dir == null) throw new InvalidOperationException("P.sln not found above " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static string Src(params string[] parts) { return File.ReadAllText(Path.Combine(new[] { Root, "src", "PAndroid" }.Concat(parts).ToArray())); }

        private static readonly XNamespace A = "http://schemas.android.com/apk/res/android";

        // ------------------------------------------------------------------ the manifest

        [Fact]
        public void TheManifestAsksForMinSdk25WhichIsFireOs6()
        {
            var uses = XDocument.Parse(Src("AndroidManifest.xml")).Root.Element("uses-sdk");
            Assert.Equal("25", (string)uses.Attribute(A + "minSdkVersion"));
            Assert.Contains("<SupportedOSPlatformVersion>25</SupportedOSPlatformVersion>", Src("PAndroid.csproj"));
        }

        [Fact]
        public void TouchScreenAndLeanbackAreNotRequiredSoAFireTvAndAPhoneBothInstallIt()
        {
            var features = XDocument.Parse(Src("AndroidManifest.xml")).Root.Elements("uses-feature").ToList();
            foreach (var name in new[] { "android.hardware.touchscreen", "android.software.leanback", "android.hardware.wifi" })
            {
                var f = features.SingleOrDefault(x => (string)x.Attribute(A + "name") == name);
                Assert.NotNull(f);
                Assert.Equal("false", (string)f.Attribute(A + "required"));
            }
        }

        [Fact]
        public void NoFeatureOrPermissionThatAStickLacksIsRequired()
        {
            var root = XDocument.Parse(Src("AndroidManifest.xml")).Root;
            // no hardware a Fire TV stick does not have (a "required" feature defaults to true)
            foreach (var f in root.Elements("uses-feature"))
            {
                string name = (string)f.Attribute(A + "name");
                bool required = (string)f.Attribute(A + "required") != "false";
                Assert.False(required, name + " is required");
            }
            var permissions = root.Elements("uses-permission").Select(p => (string)p.Attribute(A + "name")).ToList();
            foreach (var p in permissions) Assert.DoesNotMatch("CAMERA|RECORD_AUDIO|LOCATION|BLUETOOTH|READ_PHONE|EXTERNAL_STORAGE|NEARBY", p);
            Assert.Contains("android.permission.INTERNET", permissions);
            Assert.Contains("android.permission.CHANGE_WIFI_MULTICAST_STATE", permissions);
        }

        [Fact]
        public void TheActivityIsInTheLauncherOfAPhoneAndOfAFireTvAndHasItsBanner()
        {
            string a = Src("MainActivity.cs");
            Assert.Contains("MainLauncher = true", a);
            Assert.Contains("Exported = true", a);
            Assert.Contains("Banner = \"@drawable/banner\"", a);
            Assert.Contains("Icon = \"@mipmap/ic_launcher\"", a);
            Assert.Contains("android.intent.category.LEANBACK_LAUNCHER", a);
            Assert.Contains("LaunchMode = LaunchMode.SingleTask", a);
            string app = Src("MainApplication.cs");
            Assert.Contains("Banner = \"@drawable/banner\"", app);
            Assert.Contains("Icon = \"@mipmap/ic_launcher\"", app);
        }

        [Fact]
        public void TheBannerIs320By180InTheXhdpiFolderWhichIsTheDensityOfAFireTv()
        {
            // Amazon's "Display and Layout": a Fire TV is xhdpi whatever the output resolution
            var png = File.ReadAllBytes(Path.Combine(Root, "src", "PAndroid", "Resources", "drawable-xhdpi", "banner.png"));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            Assert.Equal(320, w); Assert.Equal(180, h);
        }

        [Fact]
        public void TheLauncherIconExistsAsAnAdaptiveIconAndAsPlainBitmapsForTheSystemsThatDoNotDrawAdaptiveOnes()
        {
            // Fire OS 7 does not support adaptive icons (Amazon, "Developing for Fire OS 7"): the PNG launcher icons are there for it
            string res = Path.Combine(Root, "src", "PAndroid", "Resources");
            Assert.True(File.Exists(Path.Combine(res, "mipmap-anydpi-v26", "ic_launcher.xml")));
            foreach (var d in new[] { "mdpi", "hdpi", "xhdpi", "xxhdpi", "xxxhdpi" }) Assert.True(File.Exists(Path.Combine(res, "mipmap-" + d, "ic_launcher.png")), d);
        }

        // ------------------------------------------------------------------ the build

        [Fact]
        public void TheProjectBuildsTheThreeAbisSoOneApkServesEverythingAndTheStickGetsItsThirtyTwoBitLibraries()
        {
            string csproj = Src("PAndroid.csproj");
            var m = Regex.Match(csproj, "<RuntimeIdentifiers>([^<]*)</RuntimeIdentifiers>");
            Assert.True(m.Success);
            var rids = m.Groups[1].Value.Split(';');
            Assert.Contains("android-arm", rids);            // armeabi-v7a: Amazon documents every Fire TV as 32-bit
            Assert.Contains("android-arm64", rids);
            Assert.Contains("android-x64", rids);
        }

        [Fact]
        public void TheOnlyGoogleCodeIsTheCastSenderAndItStaysInItsOwnFolderBehindTheGate()
        {
            string csproj = Regex.Replace(Src("PAndroid.csproj"), "<!--.*?-->", "", RegexOptions.Singleline);
            // the one package of Google's is the Cast sender's binding (it brings Play services' client libraries with it); nothing of Firebase, ads, maps, sign-in...
            var packages = Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
            Assert.Contains("Xamarin.GooglePlayServices.Cast.Framework", packages);
            Assert.DoesNotContain(packages, p => Regex.IsMatch(p, "(?i)Firebase|Xamarin\\.Google\\.(Android|Play)|GooglePlayServices\\.(?!Cast\\.Framework$)"));
            string srcDir = Path.Combine(Root, "src", "PAndroid"), sep = Path.DirectorySeparatorChar.ToString();
            foreach (var file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep)))
            {
                string text = File.ReadAllText(file);
                string code = Regex.Replace(Regex.Replace(text, "(?s)/\\*.*?\\*/", ""), "(?m)//.*$", "");
                bool inCastFolder = file.Contains(sep + "GoogleCast" + sep);
                bool usesGoogle = Regex.IsMatch(code, "using\\s+(Google\\.Android|Android\\.Gms|Xamarin\\.Google\\.Android|Google\\.Cast|AndroidX\\.MediaRouter)");
                if (!inCastFolder) Assert.False(usesGoogle, Path.GetFileName(file) + " uses the Google Cast SDK outside src/PAndroid/GoogleCast");
                // and nobody but the gate (AndroidCast) names the bridge: the activity, the platform and the factory only see IGoogleCastBridge
                string name = Path.GetFileName(file);
                if (!inCastFolder && name != "AndroidCast.cs") Assert.False(Regex.IsMatch(code, "GoogleCastBridge|PAndroid\\.GoogleCast|GoogleCast\\."), name + " names the Cast bridge");
            }
            // the gate: the bridge is only made when the policy says so, in a method that is never inlined into a caller
            string gate = Src(Path.Combine("Platform", "AndroidCast.cs"));
            Assert.Matches("(?s)NoInlining\\)\\]\\s*public static IGoogleCastBridge CreateBridge.*?if \\(!IsEnabled\\) return null;", gate);
        }

        [Fact]
        public void TheSendingServiceIsAMediaPlaybackForegroundServiceAndTheManifestHasItsPermissions()
        {
            string service = Src(Path.Combine("Cast", "CastSendingService.cs"));
            Assert.Contains("ForegroundServiceType = ForegroundService.TypeMediaPlayback", service);
            Assert.Contains("Exported = false", service);
            string manifest = Src("AndroidManifest.xml");
            foreach (var perm in new[] { "FOREGROUND_SERVICE", "FOREGROUND_SERVICE_MEDIA_PLAYBACK", "POST_NOTIFICATIONS", "WAKE_LOCK" })
                Assert.Contains("android.permission." + perm + "\"", manifest);
            // only a phone or a tablet builds the sender: the factory asks the policy first
            Assert.Matches("(?s)SendPolicy\\.Offers\\(profile\\.Kind\\)\\) return null", Src(Path.Combine("Cast", "AndroidCastFactory.cs")));
        }

        [Fact]
        public void TheFireTvAndEveryReceiverNeverStartAnythingOfTheSender()
        {
            // the activity builds the sender through the factory only, and the factory returns null on a TV, a Fire TV or a box
            string activity = Src("MainActivity.cs");
            Assert.Contains("AndroidCastFactory.Create(", activity);
            Assert.DoesNotContain("new CastController", activity);
            Assert.DoesNotContain("GoogleCastBridge", activity);
            string policy = Src(Path.Combine("Pure", "TvPolicies.cs"));
            Assert.Matches("(?s)public static bool Offers\\(AndroidDeviceKind kind\\) \\{ return kind == AndroidDeviceKind\\.Phone \\|\\| kind == AndroidDeviceKind\\.Tablet; \\}", policy);
        }

        // ------------------------------------------------------------------ the lifecycle: the Alexa overlay must not cut the channel

        private static string Method(string source, string signature)
        {
            int i = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(i >= 0, signature + " not found");
            int open = source.IndexOf('{', i), depth = 0, j = open;
            for (; j < source.Length; j++)
            {
                if (source[j] == '{') depth++;
                else if (source[j] == '}' && --depth == 0) break;
            }
            return source.Substring(open, j - open + 1);
        }

        [Fact]
        public void OnPauseDoesNothingToTheChannelBecauseAlexaAndTheQuickSettingsOverlayTheAppWithoutStoppingIt()
        {
            string a = Src("MainActivity.cs");
            string onPause = Method(a, "protected override void OnPause()");
            Assert.DoesNotContain("_host", onPause);
            Assert.DoesNotContain("_view", onPause);
            Assert.DoesNotContain("Pause()", onPause.Replace("base.OnPause()", ""));
        }

        [Fact]
        public void OnStopPausesTheAppAndOnStartResumesIt()
        {
            string a = Src("MainActivity.cs");
            Assert.Contains("_host.Pause()", Method(a, "protected override void OnStop()"));
            Assert.Contains("_host.Resume()", Method(a, "protected override void OnStart()"));
            Assert.Contains("OnFocusLost", Method(a, "public override void OnWindowFocusChanged(bool hasFocus)"));
        }

        [Fact]
        public void TheAudioFocusLossOfAnOverlayPausesTheChannelAndGainResumesIt()
        {
            string b = Src("Player", "PlayerBackendBase.cs");
            string handler = Method(b, "private void OnFocusChanged(AudioFocusChange change)");
            Assert.Contains("AudioFocusChange.Duck", handler);
            Assert.Contains("AudioFocusChange.LostTransient", handler);
            Assert.Contains("AudioFocusChange.Gained", handler);
            Assert.Contains("SetPlaying(false)", handler);
            Assert.Contains("SetPlaying(true)", handler);
        }
    }
}
