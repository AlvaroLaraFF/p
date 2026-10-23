using System;
using System.Text;

namespace AceEngine.Tests
{
    internal static class TestUtil
    {
        public static string FixturePath(string name)
        {
            return System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        }

        public static byte[] Fixture(string name)
        {
            return System.IO.File.ReadAllBytes(FixturePath(name));
        }

        public static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }
        public static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s); }

        public static byte[] FromHex(string hex)
        {
            hex = hex.Replace(" ", "").Replace("\n", "");
            var r = new byte[hex.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return r;
        }

        public static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }

        public static string Sha1Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return ToHex(sha.ComputeHash(data));
        }
    }
}
