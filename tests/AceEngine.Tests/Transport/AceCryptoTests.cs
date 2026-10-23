using System;
using System.Security.Cryptography;
using System.Text;
using AceEngine.Transport;
using Xunit;

namespace AceEngine.Tests.Transport
{
    public class AceCryptoTests
    {
        // Vectors computed separately with Python/cryptography (AES-128-CBC, key = MD5("tslive_key")):
        //   text "d3:fooi1ee" + PKCS#7 padding.
        private static readonly byte[] Key = Encoding.ASCII.GetBytes("tslive_key");
        private static readonly byte[] Plain = Encoding.ASCII.GetBytes("d3:fooi1ee");

        [Fact]
        public void AesKeyIsMd5OfKey()
        {
            Assert.Equal("586e8436c10fd4454ebfda983566b67d", TestUtil.ToHex(AceCrypto.DeriveAesKey(Key)));
        }

        [Theory]
        [InlineData(IvMode.Zero, "00000000000000000000000000000000", "e61d5013ed38e98d40ab9bc341e0c932")]
        [InlineData(IvMode.Sha1OfKey, "10f289b799a9f7f2e4bdd8651a312be0", "ac4fff2f7e17599c308bb5e3a903b815")]
        [InlineData(IvMode.Sha1OfMd5Key, "730928055fa615b788ddb4cca047acc8", "9dd363fbf92e7913dffaa2cd04619337")]
        public void MatchesExternalVectors(IvMode mode, string ivHex, string cipherHex)
        {
            Assert.Equal(ivHex, TestUtil.ToHex(AceCrypto.DeriveIv(Key, mode)));
            var enc = AceCrypto.M2AesEncrypt(Plain, Key, mode);
            Assert.Equal(cipherHex, TestUtil.ToHex(enc));
            bool padOk;
            var dec = AceCrypto.M2AesDecrypt(TestUtil.FromHex(cipherHex), Key, mode, out padOk);
            Assert.True(padOk);
            Assert.Equal(Plain, dec);
        }

        [Fact]
        public void WrongIvOnlyCorruptsFirstBlock()
        {
            // CBC property that IvMode.Auto relies on: with a wrong IV, from the 2nd block on everything is correct.
            var longPlain = Encoding.ASCII.GetBytes("d4:name32:0123456789abcdef0123456789abcdef3:zzzi5ee");
            var enc = AceCrypto.M2AesEncrypt(longPlain, Key, IvMode.Zero);
            bool padOk;
            var dec = AceCrypto.M2AesDecrypt(enc, Key, IvMode.Sha1OfKey, out padOk);
            Assert.True(padOk);
            Assert.Equal(longPlain.Length, dec.Length);
            for (int i = 16; i < dec.Length; i++) Assert.Equal(longPlain[i], dec[i]);
            Assert.NotEqual(longPlain[0], dec[0]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(15)]
        [InlineData(16)]
        [InlineData(17)]
        [InlineData(1000)]
        public void RoundTripAnyLength(int length)
        {
            var data = new byte[length];
            new Random(length).NextBytes(data);
            foreach (var mode in new[] { IvMode.Zero, IvMode.Sha1OfKey, IvMode.Sha1OfMd5Key })
            {
                var enc = AceCrypto.M2AesEncrypt(data, AceKeys.KeyTransportV2, mode);
                Assert.Equal(0, enc.Length % 16);
                Assert.True(enc.Length > length);                 // there is always padding
                bool ok;
                Assert.Equal(data, AceCrypto.M2AesDecrypt(enc, AceKeys.KeyTransportV2, mode, out ok));
                Assert.True(ok);
            }
        }

        [Fact]
        public void PadAlwaysAddsPaddingAndUnpadStripsIt()
        {
            var p = AceCrypto.Pad(new byte[16], 16);
            Assert.Equal(32, p.Length);
            Assert.Equal(16, p[31]);
            bool ok;
            Assert.Equal(16, AceCrypto.Unpad(p, out ok).Length);
            Assert.True(ok);
        }

        [Fact]
        public void UnpadIsTolerantLikeTheOriginal()
        {
            // The original does pt[:-pt[-1]] without validating: here the padding is stripped even if it is not correct PKCS#7.
            var bad = new byte[] { 1, 2, 3, 9, 9, 9, 4, 4 };
            bool ok;
            var r = AceCrypto.Unpad(bad, out ok);
            Assert.False(ok);
            Assert.Equal(new byte[] { 1, 2, 3, 9 }, r);
            // Last byte outside 1..16: left intact.
            var weird = new byte[] { 1, 2, 3, 200 };
            Assert.Equal(weird, AceCrypto.Unpad(weird, out ok));
            Assert.False(ok);
        }

        [Fact]
        public void RejectsNonBlockMultiples()
        {
            bool ok;
            Assert.Throws<CryptographicException>(() => AceCrypto.M2AesDecrypt(new byte[15], Key, IvMode.Zero, out ok));
            Assert.Throws<CryptographicException>(() => AceCrypto.M2AesDecrypt(new byte[0], Key, IvMode.Zero, out ok));
        }

        [Fact]
        public void AutoIsNotAConcreteMode()
        {
            Assert.Throws<ArgumentException>(() => AceCrypto.DeriveIv(Key, IvMode.Auto));
            Assert.Throws<ArgumentException>(() => AceCrypto.M2AesEncrypt(Plain, Key, IvMode.Auto));
        }
    }
}
