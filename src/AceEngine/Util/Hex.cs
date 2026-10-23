using System;
using System.Text;

namespace AceEngine.Util
{
    public static class Hex
    {
        private static readonly char[] Digits = "0123456789abcdef".ToCharArray();

        public static string ToHex(byte[] data)
        {
            return ToHex(data, 0, data.Length);
        }

        public static string ToHex(byte[] data, int offset, int count)
        {
            var c = new char[count * 2];
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                c[i * 2] = Digits[b >> 4];
                c[i * 2 + 1] = Digits[b & 15];
            }
            return new string(c);
        }

        public static bool TryParse(string hex, out byte[] data)
        {
            data = null;
            if (hex == null || hex.Length % 2 != 0) return false;
            var r = new byte[hex.Length / 2];
            for (int i = 0; i < r.Length; i++)
            {
                int hi = Val(hex[i * 2]);
                int lo = Val(hex[i * 2 + 1]);
                if (hi < 0 || lo < 0) return false;
                r[i] = (byte)((hi << 4) | lo);
            }
            data = r;
            return true;
        }

        public static byte[] Parse(string hex)
        {
            byte[] d;
            if (!TryParse(hex, out d)) throw new FormatException("invalid hexadecimal string: " + hex);
            return d;
        }

        /// <summary>^[0-9a-fA-F]{40}$</summary>
        public static bool IsHex40(string s)
        {
            if (s == null || s.Length != 40) return false;
            foreach (var ch in s) if (Val(ch) < 0) return false;
            return true;
        }

        private static int Val(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }

    public static class HashUtil
    {
        public static byte[] Sha1(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create()) return sha.ComputeHash(data);
        }

        public static byte[] Sha1(byte[] data, int offset, int count)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create()) return sha.ComputeHash(data, offset, count);
        }

        public static byte[] Md5(byte[] data)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create()) return md5.ComputeHash(data);
        }

        public static byte[] Sha256(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) return sha.ComputeHash(data);
        }

        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
