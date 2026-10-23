using System;
using System.Collections.Generic;

namespace PAndroid
{
    /// <summary>One Amazon Fire TV model as Amazon's device specifications describe it.</summary>
    public sealed class FireTvModel
    {
        /// <summary>android.os.Build.MODEL ("AFTSSS").</summary>
        public readonly string Code;
        public readonly string Name;
        /// <summary>Fire OS major version (5, 6, 7, 8...).</summary>
        public readonly int FireOs;
        /// <summary>android.os.Build.VERSION.SDK_INT the model ships with.</summary>
        public readonly int ApiLevel;
        /// <summary>Nominal RAM in MiB (what the specifications say; the system reports somewhat less).</summary>
        public readonly int RamMiB;
        public readonly string Gpu;
        /// <summary>The GPU is one of the weaker ones (IMG GE8300, Mali-450): the interface is drawn at 720p there.</summary>
        public readonly bool WeakGpu;

        public FireTvModel(string code, string name, int fireOs, int apiLevel, int ramMiB, string gpu, bool weakGpu = false)
        {
            Code = code; Name = name; FireOs = fireOs; ApiLevel = apiLevel; RamMiB = ramMiB; Gpu = gpu; WeakGpu = weakGpu;
        }

        /// <summary>The apk can be installed: minSdk is 25 (Fire OS 6), and Fire OS 5 is API 22.</summary>
        public bool CanInstall { get { return ApiLevel >= 25; } }

        public override string ToString() { return Name + " (" + Code + ", Fire OS " + FireOs + ", API " + ApiLevel + ", " + RamMiB + " MiB, " + Gpu + ")"; }
    }

    /// <summary>
    /// The Fire TV models P! knows by name, from Amazon's "Fire TV Device Specifications" and "Identify Fire TV Devices" (developer.amazon.com/docs/device-specs). Used for the log, for the memory a
    /// device has when the system does not say, and for the GPU class. Smart TVs (Fire TV Edition) are not listed: they report their own memory and are not as weak. Amazon documents every one of these devices as
    /// 32-bit (ABI "32-bit"): the apk's armeabi-v7a libraries are the ones that run on a stick.
    ///
    /// Fire TV Stick 4K Select (2025: AFTCA002), Fire TV Stick HD (2026: AFTCL001) and Fire TV Stick 4K 3rd Gen (2026: AFTCR001) run Vega OS, which does not run Android apps at all: no apk can be
    /// installed on them, so there is nothing to detect.
    /// </summary>
    public static class FireTvModels
    {
        private static readonly Dictionary<string, FireTvModel> Models = Build();

        private static Dictionary<string, FireTvModel> Build()
        {
            var m = new Dictionary<string, FireTvModel>(StringComparer.Ordinal);
            void Add(FireTvModel x) { m[x.Code] = x; }
            // Fire OS 5 (API 22): below the app's minSdk 25, they cannot install it
            Add(new FireTvModel("AFTB", "Fire TV (1st gen, 2014)", 5, 22, 2048, "Adreno 320"));
            Add(new FireTvModel("AFTS", "Fire TV (2nd gen, 2015)", 5, 22, 2048, "PowerVR Rogue GX6250"));
            Add(new FireTvModel("AFTM", "Fire TV Stick (1st gen, 2014)", 5, 22, 1024, "Broadcom VideoCore IV", true));
            Add(new FireTvModel("AFTT", "Fire TV Stick (2nd gen 2016-2019 / Basic Edition 2017)", 5, 22, 1024, "Mali-450 MP4", true));
            // Fire OS 6 (API 25)
            Add(new FireTvModel("AFTN", "Fire TV (3rd gen pendant, 2017)", 6, 25, 2048, "Mali-450 MP3", true));
            Add(new FireTvModel("AFTA", "Fire TV Cube (1st gen, 2018)", 6, 25, 2048, "Mali-450 MP3", true));
            Add(new FireTvModel("AFTMM", "Fire TV Stick 4K (1st gen, 2018)", 6, 25, 1536, "IMG GE8300", true));
            // Fire OS 7 (API 28)
            Add(new FireTvModel("AFTR", "Fire TV Cube (2nd gen, 2019)", 7, 28, 2048, "Mali G52-MP2"));
            Add(new FireTvModel("AFTSSS", "Fire TV Stick (3rd gen, 2020)", 7, 28, 1024, "IMG GE8300", true));
            Add(new FireTvModel("AFTSS", "Fire TV Stick Lite (2020) / Stick HD (2024)", 7, 28, 1024, "IMG GE8300", true));
            Add(new FireTvModel("AFTKA", "Fire TV Stick 4K Max (1st gen, 2021)", 7, 28, 2048, "IMG GE9215"));
            Add(new FireTvModel("AFTGAZL", "Fire TV Cube (3rd gen, 2022)", 7, 28, 2048, "ARM G52 MP8"));
            // Fire OS 8 (API 30)
            Add(new FireTvModel("AFTKM", "Fire TV Stick 4K (2nd gen, 2023)", 8, 30, 2048, "IMG GE9215"));
            Add(new FireTvModel("AFTKRT", "Fire TV Stick 4K Max (2nd gen, 2023)", 8, 30, 2048, "IMG GE9215"));
            Add(new FireTvModel("AFTMA08C15", "Fire TV Stick 4K Plus (2025)", 8, 30, 2048, "IMG GE9215"));
            return m;
        }

        public static IEnumerable<FireTvModel> All { get { return Models.Values; } }

        /// <summary>The model Amazon documents for this Build.MODEL, or null (a smart TV, a model that is not listed).</summary>
        public static FireTvModel Lookup(string buildModel)
        {
            FireTvModel m;
            return buildModel != null && Models.TryGetValue(buildModel.Trim(), out m) ? m : null;
        }

        /// <summary>
        /// A Fire TV by its model code: Amazon's streaming devices, Cubes and the Fire TV Edition televisions all report an "AFT..." model (all capitals and digits). The system feature
        /// amazon.hardware.fire_tv is the primary check; this is the second one, for a device that does not report it.
        /// </summary>
        public static bool IsFireTv(string manufacturer, string model)
        {
            if (string.IsNullOrEmpty(model) || model.Length < 4 || model.Length > 16 || !model.StartsWith("AFT", StringComparison.Ordinal)) return false;
            foreach (char c in model) if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
            return true;
        }
    }
}
