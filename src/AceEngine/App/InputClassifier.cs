using System;

namespace AceEngine.App
{
    public enum InputKind { Invalid, Code, M3uUrl }

    /// <summary>Tells a mylinkspaste code from the URL of an M3U list (the two ways of configuring a list).</summary>
    public static class InputClassifier
    {
        public const int MaxCodeLength = 2048;

        /// <summary>A mylinkspaste code is a DNS label (24 hex or 50 alphanumeric characters); an http(s) URL is an M3U list.</summary>
        public static InputKind Classify(string text, out string normalized)
        {
            normalized = (text ?? "").Trim();
            const string scheme = "mylinkspaste://";
            if (normalized.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(scheme.Length).Trim().TrimEnd('/');
            if (normalized.StartsWith("acestream://", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(12).Trim();
            if (normalized.Length == 0 || normalized.Length > MaxCodeLength) return InputKind.Invalid;
            if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Uri u;
                if (Uri.TryCreate(normalized, UriKind.Absolute, out u) && !string.IsNullOrEmpty(u.Host)) return InputKind.M3uUrl;
                return InputKind.Invalid;
            }
            if (normalized.Length > 63) return InputKind.Invalid;
            foreach (char c in normalized)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-')) return InputKind.Invalid;
            if (normalized[0] == '-' || normalized[normalized.Length - 1] == '-') return InputKind.Invalid;
            return InputKind.Code;
        }
    }
}
