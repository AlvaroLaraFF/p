using System;
using System.Security.Cryptography;
using AceEngine.Util;

namespace AceEngine.Transport
{
    /// <summary>
    /// How the AES-CBC IV is obtained in m2_AES_decrypt. The sources disagreed; CONFIRMED with real AceStreamTransport v2 files
    /// from the Ace network (16-10-2026, also by an independent check in Python): IV = SHA1(MD5(key))[0:16] (Sha1OfMd5Key).
    /// SHA1(key)[0:16] and zeros give garbage in the first block. It is left configurable and Auto tries the three variants.
    /// </summary>
    public enum IvMode
    {
        /// <summary>Tries the variants in order and keeps the first whose result is valid bencode.</summary>
        Auto = 0,
        /// <summary>16 zero bytes.</summary>
        Zero = 1,
        /// <summary>SHA1(key)[0:16] (REFUTED by the real files).</summary>
        Sha1OfKey = 2,
        /// <summary>SHA1(MD5(key))[0:16] (alternative; CONFIRMED with real files).</summary>
        Sha1OfMd5Key = 3
    }

    public static class AceCrypto
    {
        public static byte[] DeriveAesKey(byte[] key)
        {
            return HashUtil.Md5(key);
        }

        public static byte[] DeriveIv(byte[] key, IvMode mode)
        {
            switch (mode)
            {
                case IvMode.Zero:
                    return new byte[16];
                case IvMode.Sha1OfKey:
                    return Take16(HashUtil.Sha1(key));
                case IvMode.Sha1OfMd5Key:
                    return Take16(HashUtil.Sha1(HashUtil.Md5(key)));
                default:
                    throw new ArgumentException("concrete IvMode required (not Auto)", "mode");
            }
        }

        private static byte[] Take16(byte[] h)
        {
            var r = new byte[16];
            Buffer.BlockCopy(h, 0, r, 0, 16);
            return r;
        }

        /// <summary>
        /// m2_AES_decrypt: AES-128-CBC with key MD5(key). Removes the padding like the original (s[:-s[-1]]).
        /// paddingValid says whether the padding was correct PKCS#7 (the original does not validate it).
        /// </summary>
        public static byte[] M2AesDecrypt(byte[] data, byte[] key, IvMode iv, out bool paddingValid)
        {
            if (data == null) throw new ArgumentNullException("data");
            if (key == null) throw new ArgumentNullException("key");
            if (data.Length == 0 || data.Length % 16 != 0)
                throw new CryptographicException("encrypted length not a multiple of 16: " + data.Length);
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = DeriveAesKey(key);
                aes.IV = DeriveIv(key, iv);
                byte[] plain;
                using (var dec = aes.CreateDecryptor())
                    plain = dec.TransformFinalBlock(data, 0, data.Length);
                return Unpad(plain, out paddingValid);
            }
        }

        public static byte[] M2AesEncrypt(byte[] data, byte[] key, IvMode iv)
        {
            if (iv == IvMode.Auto) throw new ArgumentException("concrete IvMode required (not Auto)", "iv");
            var padded = Pad(data, 16);
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.KeySize = 128;
                aes.Key = DeriveAesKey(key);
                aes.IV = DeriveIv(key, iv);
                using (var enc = aes.CreateEncryptor())
                    return enc.TransformFinalBlock(padded, 0, padded.Length);
            }
        }

        /// <summary>pad(data, bs) = data + bytes([n])*n with n = bs - len % bs (always adds padding).</summary>
        public static byte[] Pad(byte[] data, int blockSize)
        {
            int n = blockSize - data.Length % blockSize;
            var r = new byte[data.Length + n];
            Buffer.BlockCopy(data, 0, r, 0, data.Length);
            for (int i = data.Length; i < r.Length; i++) r[i] = (byte)n;
            return r;
        }

        /// <summary>
        /// unpad of the original: pt[:-pt[-1]]. Tolerant: if the last byte is not in 1..16 or exceeds the length
        /// nothing is removed and paddingValid=false.
        /// </summary>
        public static byte[] Unpad(byte[] plain, out bool paddingValid)
        {
            paddingValid = false;
            if (plain.Length == 0) return plain;
            int n = plain[plain.Length - 1];
            if (n < 1 || n > 16 || n > plain.Length) return plain;
            bool ok = true;
            for (int i = plain.Length - n; i < plain.Length; i++) if (plain[i] != n) { ok = false; break; }
            paddingValid = ok;
            var r = new byte[plain.Length - n];
            Buffer.BlockCopy(plain, 0, r, 0, r.Length);
            return r;
        }
    }
}
