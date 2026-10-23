using System;
using System.Numerics;
using AceEngine.Util;

namespace AceEngine.Crypto
{
    /// <summary>
    /// RSA public key and RSASSA-PKCS1-v1_5 verification with SHA-1 using System.Numerics.BigInteger. Does not use the platform's RSA:
    /// Ace's live streams sign with 768-bit keys and e=3, which some backends (OpenSSL 3) reject.
    /// </summary>
    public sealed class RsaPublicKey
    {
        // SHA-1 DigestInfo (RFC 8017 9.2): 30 21 30 09 06 05 2B 0E 03 02 1A 05 00 04 14
        private static readonly byte[] Sha1DigestInfo = { 0x30, 0x21, 0x30, 0x09, 0x06, 0x05, 0x2B, 0x0E, 0x03, 0x02, 0x1A, 0x05, 0x00, 0x04, 0x14 };

        public BigInteger N { get; private set; }
        public BigInteger E { get; private set; }

        /// <summary>pycryptodome's size_in_bits(): bits of n.</summary>
        public int Bits { get; private set; }

        /// <summary>Signature size in bytes = bits // 8. If n had 767 bits it would be 95, not 96.</summary>
        public int SignSize { get { return Bits / 8; } }

        public RsaPublicKey(BigInteger n, BigInteger e)
        {
            if (n.Sign <= 0 || e.Sign <= 0) throw new ArgumentException("invalid RSA key");
            N = n; E = e;
            Bits = BitLength(n);
        }

        private static int BitLength(BigInteger v)
        {
            var b = v.ToByteArray();
            int len = b.Length;
            while (len > 1 && b[len - 1] == 0) len--;
            int bits = (len - 1) * 8;
            int top = b[len - 1];
            while (top != 0) { bits++; top >>= 1; }
            return bits;
        }

        /// <summary>Reads a DER SubjectPublicKeyInfo (SEQUENCE { AlgorithmIdentifier, BIT STRING { SEQUENCE { n, e } } }).</summary>
        public static RsaPublicKey FromSpki(byte[] der)
        {
            try
            {
                int p = 0;
                var spki = Asn1.Read(der, ref p, 0x30);
                int q = 0;
                Asn1.Read(spki, ref q, 0x30);                         // AlgorithmIdentifier (ignored)
                var bits = Asn1.Read(spki, ref q, 0x03);
                if (bits.Length < 1 || bits[0] != 0) throw new FormatException("BIT STRING with unused bits");
                var inner = new byte[bits.Length - 1];
                Buffer.BlockCopy(bits, 1, inner, 0, inner.Length);
                return FromPkcs1(inner);
            }
            catch (IndexOutOfRangeException) { throw new FormatException("truncated DER public key"); }
        }

        /// <summary>Reads a PKCS#1 RSAPublicKey (SEQUENCE { n, e }).</summary>
        public static RsaPublicKey FromPkcs1(byte[] der)
        {
            try
            {
                int p = 0;
                var seq = Asn1.Read(der, ref p, 0x30);
                int q = 0;
                var n = Asn1.Read(seq, ref q, 0x02);
                var e = Asn1.Read(seq, ref q, 0x02);
                return new RsaPublicKey(Asn1.UnsignedInteger(n), Asn1.UnsignedInteger(e));
            }
            catch (IndexOutOfRangeException) { throw new FormatException("truncated DER RSA key"); }
        }

        /// <summary>Accepts SPKI or PKCS#1 (a descriptor's 'pubkey' should be SPKI).</summary>
        public static RsaPublicKey Parse(byte[] der)
        {
            try { return FromSpki(der); }
            catch (FormatException) { return FromPkcs1(der); }
        }

        /// <summary>
        /// Verifies 'signature' (SignSize bytes) over 'data' [offset, offset+count): m = s^e mod n, and the block
        /// 00 01 FF..FF 00 DigestInfo || SHA1(data) must be exactly m with k = SignSize bytes.
        /// </summary>
        public bool VerifySha1(byte[] data, int offset, int count, byte[] signature, int sigOffset)
        {
            int k = SignSize;
            // s (as an unsigned integer) must be < n
            var sb = new byte[k + 1];
            for (int i = 0; i < k; i++) sb[k - 1 - i] = signature[sigOffset + i];
            var s = new BigInteger(sb);
            if (s >= N) return false;
            var m = BigInteger.ModPow(s, E, N);
            var mb = m.ToByteArray();                                  // little-endian
            var em = new byte[k];
            for (int i = 0; i < mb.Length && i < k; i++) em[k - 1 - i] = mb[i];
            for (int i = k; i < mb.Length; i++) if (mb[i] != 0) return false;

            byte[] hash = HashUtil.Sha1(data, offset, count);
            int tLen = Sha1DigestInfo.Length + hash.Length;
            if (k < tLen + 11) return false;
            int psLen = k - tLen - 3;
            if (em[0] != 0x00 || em[1] != 0x01) return false;
            for (int i = 0; i < psLen; i++) if (em[2 + i] != 0xff) return false;
            if (em[2 + psLen] != 0x00) return false;
            int o = 3 + psLen;
            for (int i = 0; i < Sha1DigestInfo.Length; i++) if (em[o + i] != Sha1DigestInfo[i]) return false;
            o += Sha1DigestInfo.Length;
            int diff = 0;
            for (int i = 0; i < hash.Length; i++) diff |= em[o + i] ^ hash[i];
            return diff == 0;
        }

        public bool VerifySha1(byte[] data, byte[] signature)
        {
            if (signature == null || signature.Length != SignSize) return false;
            return VerifySha1(data, 0, data.Length, signature, 0);
        }
    }

    /// <summary>Minimal DER reader (definite lengths only).</summary>
    internal static class Asn1
    {
        /// <summary>Reads an element with the expected tag at buf[pos..] and returns its content; advances pos.</summary>
        public static byte[] Read(byte[] buf, ref int pos, int expectedTag)
        {
            if (pos + 2 > buf.Length) throw new FormatException("truncated DER");
            int tag = buf[pos++];
            if (tag != expectedTag) throw new FormatException("unexpected DER tag 0x" + tag.ToString("x2") + " (expected 0x" + expectedTag.ToString("x2") + ")");
            long len = buf[pos++];
            if ((len & 0x80) != 0)
            {
                int n = (int)(len & 0x7f);
                if (n == 0 || n > 4 || pos + n > buf.Length) throw new FormatException("invalid DER length");
                len = 0;
                for (int i = 0; i < n; i++) len = (len << 8) | buf[pos++];
            }
            if (len < 0 || pos + len > buf.Length) throw new FormatException("truncated DER");
            var r = new byte[len];
            Buffer.BlockCopy(buf, pos, r, 0, (int)len);
            pos += (int)len;
            return r;
        }

        public static BigInteger UnsignedInteger(byte[] big)
        {
            var t = new byte[big.Length + 1];
            for (int i = 0; i < big.Length; i++) t[big.Length - 1 - i] = big[i];
            return new BigInteger(t);
        }
    }
}
