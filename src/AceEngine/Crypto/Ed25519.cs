using System;
using System.Numerics;
using System.Security.Cryptography;

namespace AceEngine.Crypto
{
    /// <summary>
    /// Managed, dependency-free Ed25519 (RFC 8032, pure "Ed25519", not Ed25519ph) on top of System.Numerics.BigInteger.
    /// Written by hand instead of using BouncyCastle: live EXTEND only signs/verifies one message per connection
    /// (about 10-20 ms with BigInteger), the code is ~200 lines checkable against the RFC vectors, and it avoids shipping
    /// a ~4 MB library and its reflection surface to the TV. It is not constant-time: the key is ephemeral and only used to
    /// identify ourselves to peers, it protects no long-lived secret.
    /// </summary>
    public static class Ed25519
    {
        public const int SeedLength = 32;
        public const int PublicKeyLength = 32;
        public const int SignatureLength = 64;

        private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
        private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
        private static readonly BigInteger D = Mod(-121665 * ModInverse(121666));
        private static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
        private static readonly Point Base = MakeBase();

        // Point in extended coordinates (X, Y, Z, T) with T = XY/Z
        private struct Point
        {
            public BigInteger X, Y, Z, T;
        }

        private static BigInteger Mod(BigInteger a)
        {
            var r = BigInteger.Remainder(a, P);
            return r.Sign < 0 ? r + P : r;
        }

        private static BigInteger ModInverse(BigInteger a)
        {
            return BigInteger.ModPow(Mod(a), P - 2, P);
        }

        private static Point MakeBase()
        {
            var y = Mod(4 * ModInverse(5));
            var x = RecoverX(y, 0);
            return new Point { X = x, Y = y, Z = BigInteger.One, T = Mod(x * y) };
        }

        private static BigInteger RecoverX(BigInteger y, int sign)
        {
            var y2 = Mod(y * y);
            var x2 = Mod((y2 - 1) * ModInverse(D * y2 + 1));
            if (x2.IsZero)
            {
                if (sign != 0) throw new ArgumentException("invalid point");
                return BigInteger.Zero;
            }
            var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
            if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtM1);
            if (!Mod(x * x - x2).IsZero) throw new ArgumentException("invalid point");
            if ((int)(x & 1) != sign) x = P - x;
            return x;
        }

        private static Point Add(Point a, Point b)
        {
            var A = Mod((a.Y - a.X) * (b.Y - b.X));
            var B = Mod((a.Y + a.X) * (b.Y + b.X));
            var C = Mod(a.T * 2 * D * b.T);
            var DD = Mod(a.Z * 2 * b.Z);
            BigInteger E = B - A, F = DD - C, G = DD + C, H = B + A;
            return new Point { X = Mod(E * F), Y = Mod(G * H), T = Mod(E * H), Z = Mod(F * G) };
        }

        private static Point ScalarMult(Point p, BigInteger s)
        {
            var q = new Point { X = BigInteger.Zero, Y = BigInteger.One, Z = BigInteger.One, T = BigInteger.Zero };
            while (s.Sign > 0)
            {
                if (!s.IsEven) q = Add(q, p);
                p = Add(p, p);
                s >>= 1;
            }
            return q;
        }

        private static bool Equal(Point a, Point b)
        {
            return Mod(a.X * b.Z - b.X * a.Z).IsZero && Mod(a.Y * b.Z - b.Y * a.Z).IsZero;
        }

        private static byte[] Compress(Point p)
        {
            var zi = ModInverse(p.Z);
            var x = Mod(p.X * zi);
            var y = Mod(p.Y * zi);
            var r = ToLe(y | ((x & 1) << 255), 32);
            return r;
        }

        private static Point Decompress(byte[] s)
        {
            if (s == null || s.Length != 32) throw new ArgumentException("32-byte point required");
            var y = FromLe(s);
            int sign = (int)(y >> 255);
            y &= (BigInteger.One << 255) - 1;
            if (y >= P) throw new ArgumentException("invalid point");
            var x = RecoverX(y, sign);
            return new Point { X = x, Y = y, Z = BigInteger.One, T = Mod(x * y) };
        }

        private static BigInteger FromLe(byte[] b)
        {
            var t = new byte[b.Length + 1];
            Buffer.BlockCopy(b, 0, t, 0, b.Length);
            return new BigInteger(t);
        }

        private static byte[] ToLe(BigInteger v, int len)
        {
            var b = v.ToByteArray();                 // little-endian, with a sign byte if needed
            var r = new byte[len];
            Buffer.BlockCopy(b, 0, r, 0, Math.Min(len, b.Length));
            return r;
        }

        private static byte[] Sha512(params byte[][] parts)
        {
            using (var sha = SHA512.Create())
            {
                int n = 0;
                foreach (var p in parts) n += p.Length;
                var all = new byte[n];
                int o = 0;
                foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, o, p.Length); o += p.Length; }
                return sha.ComputeHash(all);
            }
        }

        // Cache of the last expanded seed: signing many EXTENDs with the same key repeats neither SHA-512 nor the multiplication A = a*B
        private static readonly object CacheGate = new object();
        private static string _cacheSeed;
        private static BigInteger _cacheA;
        private static byte[] _cachePrefix, _cachePub;

        private static void Expanded(byte[] seed, out BigInteger a, out byte[] prefix, out byte[] pub)
        {
            string key = Convert.ToBase64String(seed);
            lock (CacheGate)
            {
                if (_cacheSeed == key) { a = _cacheA; prefix = _cachePrefix; pub = _cachePub; return; }
            }
            ExpandSeed(seed, out a, out prefix);
            pub = Compress(ScalarMult(Base, a));
            lock (CacheGate) { _cacheSeed = key; _cacheA = a; _cachePrefix = prefix; _cachePub = pub; }
        }

        private static void ExpandSeed(byte[] seed, out BigInteger a, out byte[] prefix)
        {
            var h = Sha512(seed);
            var lo = new byte[32];
            Buffer.BlockCopy(h, 0, lo, 0, 32);
            lo[0] &= 248; lo[31] &= 127; lo[31] |= 64;
            a = FromLe(lo);
            prefix = new byte[32];
            Buffer.BlockCopy(h, 32, prefix, 0, 32);
        }

        /// <summary>Public key (32 bytes) of a 32-byte seed.</summary>
        public static byte[] PublicKey(byte[] seed)
        {
            if (seed == null || seed.Length != SeedLength) throw new ArgumentException("the seed must be 32 bytes", "seed");
            BigInteger a; byte[] prefix, pubKey;
            Expanded(seed, out a, out prefix, out pubKey);
            return pubKey;
        }

        public static void GenerateKeyPair(out byte[] seed, out byte[] publicKey)
        {
            seed = new byte[SeedLength];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(seed);
            publicKey = PublicKey(seed);
        }

        /// <summary>Signs 'message' (pure Ed25519). Returns 64 bytes R||S.</summary>
        public static byte[] Sign(byte[] seed, byte[] message)
        {
            if (seed == null || seed.Length != SeedLength) throw new ArgumentException("the seed must be 32 bytes", "seed");
            BigInteger a; byte[] prefix, pub;
            Expanded(seed, out a, out prefix, out pub);
            var r = FromLe(Sha512(prefix, message)) % L;
            var rEnc = Compress(ScalarMult(Base, r));
            var k = FromLe(Sha512(rEnc, pub, message)) % L;
            var s = (r + k * a) % L;
            var sig = new byte[SignatureLength];
            Buffer.BlockCopy(rEnc, 0, sig, 0, 32);
            Buffer.BlockCopy(ToLe(s, 32), 0, sig, 32, 32);
            return sig;
        }

        /// <summary>Verifies a signature. Returns false on any malformed input.</summary>
        public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            try
            {
                if (publicKey == null || publicKey.Length != PublicKeyLength || signature == null || signature.Length != SignatureLength) return false;
                var a = Decompress(publicKey);
                var rBytes = new byte[32];
                Buffer.BlockCopy(signature, 0, rBytes, 0, 32);
                var r = Decompress(rBytes);
                var sBytes = new byte[32];
                Buffer.BlockCopy(signature, 32, sBytes, 0, 32);
                var s = FromLe(sBytes);
                if (s >= L) return false;
                var k = FromLe(Sha512(rBytes, publicKey, message)) % L;
                var lhs = ScalarMult(Base, s);
                var rhs = Add(r, ScalarMult(a, k));
                return Equal(lhs, rhs);
            }
            catch (ArgumentException) { return false; }
        }
    }
}
