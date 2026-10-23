using System;
using System.Linq;
using System.Numerics;
using System.Text;
using AceEngine.Crypto;
using AceEngine.Sim;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Live
{
    public class Ed25519Tests
    {
        // RFC 8032 7.1 (vectors 1-3) and own vectors computed separately with Python/cryptography (the EXTEND signature is over SHA-256(bencode)).
        [Theory]
        [InlineData("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
        [InlineData("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "72",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
        [InlineData("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "af82",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a")]
        [InlineData("52f22665a60c12d289185d950ee8813609166f6b113d178d6c0fd3901ff239a1", "97a956a26813d927004bcbe1bf87b0014b4990ec04e3b5e1aca19c776a4e0ddf",
            "9e6f0842b168bbd1690c89ca0033eb89ac819ac27aa2e077524f2d6bceca26b8",
            "6bcf56b451004dcfd665aa0a7e37b594cb8c7c2a6e6be9b0773f426d3ff35e1ed2ae39f05396300e34a06d4587004880a726570674a830ea0052a7d7b5d6ed04")]
        [InlineData("a095f20f9395650cf9380b8edb224a6b248a1e924e8fd0ae2e1a9492a3305f18", "4a7e7c83064f8491bc8c6b078788c870f35f913453c17acf0553675b8f72e054",
            "2080edad1b0914bd21c54c55f8a2928a095e61b798dfebaba21922adfdbf3a10",
            "dfcfd496e4f2601f84804d95202701007b8b75e5c31dc0d5d6f0f1c13a9585af2a49ac7f9ff0cb077bd46ff2d49200163157e18ca7659bbc9e5aaf4654d3fa04")]
        [InlineData("8cb610900f9e347fae886dc6507795ec745c4c3fcb2eb2c73e14934c867ee057", "525e2d57e397271f030b07ee5250e973f76a93d7b8ef0f82e8e12255b353f917",
            "85d6ab8abfcdf4954c2998163ada23dff0122274d63852b249242c670e4934f8",
            "d7ccb80df5f5ff5ada649266b7dd6a48f3c19bfa29799fc2d4f77297dd10e930c97a58912f6f2d0d516e3a866dcc77ef997a9bcd84ba5b04cd5abd64e0d17a07")]
        public void MatchesRfc8032AndPythonVectors(string seedHex, string msgHex, string pubHex, string sigHex)
        {
            var seed = TestUtil.FromHex(seedHex);
            var msg = TestUtil.FromHex(msgHex);
            Assert.Equal(pubHex, Hex.ToHex(Ed25519.PublicKey(seed)));
            var sig = Ed25519.Sign(seed, msg);
            Assert.Equal(sigHex, Hex.ToHex(sig));
            Assert.True(Ed25519.Verify(TestUtil.FromHex(pubHex), msg, sig));
        }

        [Fact]
        public void RejectsTamperedMessagesKeysAndSignatures()
        {
            byte[] seed, pub;
            Ed25519.GenerateKeyPair(out seed, out pub);
            var msg = Encoding.ASCII.GetBytes("hola directo");
            var sig = Ed25519.Sign(seed, msg);
            Assert.True(Ed25519.Verify(pub, msg, sig));
            Assert.False(Ed25519.Verify(pub, Encoding.ASCII.GetBytes("hola directO"), sig));
            var bad = (byte[])sig.Clone(); bad[10] ^= 1;
            Assert.False(Ed25519.Verify(pub, msg, bad));
            bad = (byte[])sig.Clone(); bad[40] ^= 0x80;
            Assert.False(Ed25519.Verify(pub, msg, bad));
            byte[] seed2, pub2;
            Ed25519.GenerateKeyPair(out seed2, out pub2);
            Assert.False(Ed25519.Verify(pub2, msg, sig));
            Assert.False(Ed25519.Verify(pub, msg, new byte[10]));
            Assert.False(Ed25519.Verify(new byte[5], msg, sig));
            // S >= L is not a valid signature
            var big = (byte[])sig.Clone();
            for (int i = 32; i < 64; i++) big[i] = 0xff;
            Assert.False(Ed25519.Verify(pub, msg, big));
        }

        [Fact]
        public void SignsAndVerifiesQuicklyEnoughForOneMessagePerConnection()
        {
            byte[] seed, pub;
            Ed25519.GenerateKeyPair(out seed, out pub);
            var msg = new byte[32];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) Assert.True(Ed25519.Verify(pub, msg, Ed25519.Sign(seed, msg)));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        }
    }

    public class RsaTests
    {
        private static readonly byte[] SpkiFromOpenSsl = TestUtil.Fixture("crypto/rsa768_pub.der");
        private static readonly byte[] Data = TestUtil.Fixture("crypto/rsa_data.bin");
        private static readonly byte[] SigFromOpenSsl = TestUtil.Fixture("crypto/rsa_data.sig");

        [Fact]
        public void ParsesSpkiAndExposesSizes()
        {
            var k = RsaPublicKey.FromSpki(SpkiFromOpenSsl);
            Assert.Equal(768, k.Bits);
            Assert.Equal(96, k.SignSize);
            Assert.Equal(3, (int)k.E);
            Assert.Equal("e3a7129c0a485e3583cd7850d8b5080c992ddf4e562117552844816af42026eda776211f8dbb5ce16b8bada014e9f4588ecc8d7d3de08c01520e816c0ecb3495734302b37cee9ec02770650967de4aa92f676f0802a0c5004624b3d628d381d3",
                k.N.ToString("x").TrimStart('0'));
            Assert.Equal(k.N, RsaPublicKey.Parse(SpkiFromOpenSsl).N);
        }

        [Fact]
        public void VerifiesAnOpenSslSignature()
        {
            // Signature generated by `openssl dgst -sha1 -sign` with a 768-bit key and e=3 (independent oracle).
            var k = RsaPublicKey.FromSpki(SpkiFromOpenSsl);
            Assert.True(k.VerifySha1(Data, SigFromOpenSsl));
            var bad = (byte[])Data.Clone(); bad[1000] ^= 1;
            Assert.False(k.VerifySha1(bad, SigFromOpenSsl));
            var badSig = (byte[])SigFromOpenSsl.Clone(); badSig[50] ^= 1;
            Assert.False(k.VerifySha1(Data, badSig));
            Assert.False(k.VerifySha1(Data, new byte[95]));
            Assert.False(k.VerifySha1(Data, new byte[96]));
        }

        [Fact]
        public void TestSignerProducesTheSameBytesAsOpenSsl()
        {
            var json = Encoding.UTF8.GetString(TestUtil.Fixture("crypto/rsa768_priv.json"));
            var o = MiniJson.AsObject(MiniJson.Parse(json));
            var signer = RsaSigner.FromHex(MiniJson.GetString(o, "n"), MiniJson.GetString(o, "d"));
            Assert.Equal(96, signer.SignSize);
            Assert.Equal(SigFromOpenSsl, signer.Sign(Data));                        // PKCS#1 v1.5 is deterministic
            Assert.Equal(SpkiFromOpenSsl, signer.PublicKeySpki());
        }

        [Fact]
        public void VerifiesSignaturesOfGeneratedKeysOfSeveralSizes()
        {
            foreach (int bits in new[] { 512, 768, 1024 })
            {
                var signer = RsaSigner.Generate(bits, seed: bits);
                var key = RsaPublicKey.FromSpki(signer.PublicKeySpki());
                Assert.Equal(bits, key.Bits);
                Assert.Equal(bits / 8, key.SignSize);
                for (int i = 0; i < 3; i++)
                {
                    var data = new byte[1000 + i * 4096];
                    new Random(i).NextBytes(data);
                    var sig = signer.Sign(data);
                    Assert.True(key.VerifySha1(data, sig));
                    // the signature covers only the content: other content does not verify
                    data[0] ^= 1;
                    Assert.False(key.VerifySha1(data, sig));
                }
            }
        }

        [Fact]
        public void RejectsSignaturesThatAreNotPkcs1V15()
        {
            var signer = RsaSigner.Generate(768, 5);
            var key = RsaPublicKey.FromSpki(signer.PublicKeySpki());
            var data = new byte[100];
            // an arbitrary 96-byte value (less than n) is not a valid EMSA block
            var junk = new byte[96]; junk[95] = 7;
            Assert.False(key.VerifySha1(data, junk));
            // signature >= n
            var all = Enumerable.Repeat((byte)0xff, 96).ToArray();
            Assert.False(key.VerifySha1(data, all));
        }

        [Fact]
        public void MalformedDerIsRejected()
        {
            Assert.Throws<FormatException>(() => RsaPublicKey.FromSpki(new byte[] { 0x30, 0x03, 1, 2, 3 }));
            Assert.Throws<FormatException>(() => RsaPublicKey.FromSpki(SpkiFromOpenSsl.Take(60).ToArray()));
            Assert.Throws<FormatException>(() => RsaPublicKey.Parse(new byte[0]));
        }
    }
}
