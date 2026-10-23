using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;

namespace AceEngine.Sim
{
    /// <summary>
    /// RSASSA-PKCS1-v1_5 / SHA-1 signer with BigInteger for the simulated sender (the real sender uses pycryptodome with 768-bit keys and e=3).
    /// Written separately from the engine's verification (src/AceEngine/Crypto/Rsa.cs) from RFC 8017 and checked
    /// against OpenSSL (the PKCS#1 v1.5 signature is deterministic: the bytes match).
    /// </summary>
    public sealed class RsaSigner
    {
        private static readonly byte[] DigestInfoSha1 = { 0x30, 0x21, 0x30, 0x09, 0x06, 0x05, 0x2B, 0x0E, 0x03, 0x02, 0x1A, 0x05, 0x00, 0x04, 0x14 };

        public BigInteger N { get; private set; }
        public BigInteger E { get; private set; }
        public BigInteger D { get; private set; }
        public int SignSize { get; private set; }

        public RsaSigner(BigInteger n, BigInteger e, BigInteger d)
        {
            N = n; E = e; D = d;
            int bits = 0;
            for (var t = n; t.Sign > 0; t >>= 1) bits++;
            SignSize = bits / 8;
        }

        public static RsaSigner FromHex(string nHex, string dHex, int e = 3)
        {
            return new RsaSigner(ParseHex(nHex), e, ParseHex(dHex));
        }

        private static BigInteger ParseHex(string hex)
        {
            return BigInteger.Parse("0" + hex, System.Globalization.NumberStyles.HexNumber);
        }

        /// <summary>Generates a key of 'bits' bits with e=3 (like createlivestream: 768 bits, e=3).</summary>
        public static RsaSigner Generate(int bits = 768, int seed = -1)
        {
            var rnd = seed >= 0 ? new Random(seed) : null;
            var e = new BigInteger(3);
            while (true)
            {
                var p = GeneratePrime(bits / 2, rnd);
                var q = GeneratePrime(bits - bits / 2, rnd);
                if (p == q) continue;
                var n = p * q;
                var phi = (p - 1) * (q - 1);
                if (BigInteger.GreatestCommonDivisor(e, phi) != 1) continue;
                int nb = 0;
                for (var t = n; t.Sign > 0; t >>= 1) nb++;
                if (nb != bits) continue;
                var d = ModInverse(e, phi);
                return new RsaSigner(n, e, d);
            }
        }

        private static BigInteger ModInverse(BigInteger a, BigInteger m)
        {
            BigInteger g = m, x = 0, y = 1, aa = a;
            BigInteger r0 = m, r1 = aa % m, s0 = 0, s1 = 1;
            while (r1 != 0)
            {
                var q = r0 / r1;
                var r2 = r0 - q * r1; r0 = r1; r1 = r2;
                var s2 = s0 - q * s1; s0 = s1; s1 = s2;
            }
            if (r0 != 1) throw new InvalidOperationException("no inverse");
            return ((s0 % m) + m) % m;
        }

        private static BigInteger GeneratePrime(int bits, Random rnd)
        {
            while (true)
            {
                var b = new byte[(bits + 7) / 8 + 1];
                if (rnd != null) rnd.NextBytes(b);
                else using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
                b[b.Length - 1] = 0;                                     // positive
                var c = new BigInteger(b);
                c &= (BigInteger.One << bits) - 1;
                c |= BigInteger.One << (bits - 1);                       // high bit
                c |= BigInteger.One << (bits - 2);                       // second high bit: p*q has exactly 'bits' bits
                c |= 1;
                if (c % 3 == 1) continue;                                // gcd(3, p-1) = 1
                if (IsProbablePrime(c, rnd)) return c;
            }
        }

        private static bool IsProbablePrime(BigInteger n, Random rnd)
        {
            foreach (int sp in new[] { 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 })
            {
                if (n == sp) return true;
                if (n % sp == 0) return false;
            }
            var d = n - 1;
            int s = 0;
            while (d.IsEven) { d >>= 1; s++; }
            for (int i = 0; i < 24; i++)
            {
                var ab = new byte[n.ToByteArray().Length];
                if (rnd != null) rnd.NextBytes(ab);
                else using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(ab);
                ab[ab.Length - 1] = 0;
                var a = new BigInteger(ab) % (n - 3) + 2;
                var x = BigInteger.ModPow(a, d, n);
                if (x == 1 || x == n - 1) continue;
                bool comp = true;
                for (int r = 1; r < s; r++)
                {
                    x = BigInteger.ModPow(x, 2, n);
                    if (x == n - 1) { comp = false; break; }
                }
                if (comp) return false;
            }
            return true;
        }

        /// <summary>SHA-1 PKCS#1 v1.5 signature of 'data' (SignSize bytes).</summary>
        public byte[] Sign(byte[] data, int offset, int count)
        {
            byte[] h;
            using (var sha = SHA1.Create()) h = sha.ComputeHash(data, offset, count);
            int k = SignSize;
            var em = new byte[k];
            em[1] = 0x01;
            int tLen = DigestInfoSha1.Length + h.Length;
            int ps = k - tLen - 3;
            for (int i = 0; i < ps; i++) em[2 + i] = 0xff;
            em[2 + ps] = 0x00;
            Buffer.BlockCopy(DigestInfoSha1, 0, em, 3 + ps, DigestInfoSha1.Length);
            Buffer.BlockCopy(h, 0, em, 3 + ps + DigestInfoSha1.Length, h.Length);
            var mBytes = new byte[k + 1];
            for (int i = 0; i < k; i++) mBytes[k - 1 - i] = em[i];
            var m = new BigInteger(mBytes);
            var s = BigInteger.ModPow(m, D, N);
            var sb = s.ToByteArray();
            var sig = new byte[k];
            for (int i = 0; i < sb.Length && i < k; i++) sig[k - 1 - i] = sb[i];
            return sig;
        }

        public byte[] Sign(byte[] data) { return Sign(data, 0, data.Length); }

        /// <summary>SubjectPublicKeyInfo DER (the descriptor's 'pubkey').</summary>
        public byte[] PublicKeySpki()
        {
            var rsaKey = Seq(Int(N), Int(E));
            var bitString = Tlv(0x03, Concat(new byte[] { 0 }, rsaKey));
            var alg = Seq(new byte[] { 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x01, 0x01 }, new byte[] { 0x05, 0x00 });
            return Seq(alg, bitString);
        }

        private static byte[] Int(BigInteger v)
        {
            var le = v.ToByteArray();
            int len = le.Length;
            var be = new byte[len];
            for (int i = 0; i < len; i++) be[len - 1 - i] = le[i];
            return Tlv(0x02, be);                                 // ToByteArray already includes the sign 0 if needed
        }

        private static byte[] Seq(params byte[][] parts) { return Tlv(0x30, Concat(parts)); }

        private static byte[] Tlv(int tag, byte[] body)
        {
            var l = new List<byte> { (byte)tag };
            if (body.Length < 0x80) l.Add((byte)body.Length);
            else if (body.Length < 0x100) { l.Add(0x81); l.Add((byte)body.Length); }
            else { l.Add(0x82); l.Add((byte)(body.Length >> 8)); l.Add((byte)body.Length); }
            l.AddRange(body);
            return l.ToArray();
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var l = new List<byte>();
            foreach (var p in parts) l.AddRange(p);
            return l.ToArray();
        }
    }
}
