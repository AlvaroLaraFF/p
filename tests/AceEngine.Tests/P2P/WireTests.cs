using System.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using AceEngine.Bencode;
using AceEngine.P2P;
using Xunit;

namespace AceEngine.Tests.P2P
{
    /// <summary>Byte format of the wire protocol. The vectors are written by hand from the protocol description.</summary>
    public class WireTests
    {
        private static readonly byte[] Ih = TestUtil.FromHex("000102030405060708090a0b0c0d0e0f10111213");
        private static readonly byte[] Pid = Encoding.ASCII.GetBytes("R30------abcdefghijk");

        [Fact]
        public void HandshakeLayoutMatchesSpec()
        {
            var reserved = new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 };
            var hs = Wire.BuildHandshake(reserved, Ih, Pid);
            Assert.Equal(68, hs.Length);
            Assert.Equal(19, hs[0]);
            Assert.Equal("BitTorrent protocol", Encoding.ASCII.GetString(hs, 1, 19));
            Assert.Equal("0000000000300000", TestUtil.ToHex(hs.AsSpan(20, 8).ToArray()));
            Assert.Equal(Ih, hs.AsSpan(28, 20).ToArray());
            Assert.Equal(Pid, hs.AsSpan(48, 20).ToArray());
            Assert.Equal(48, Wire.BuildHandshakeHead(reserved, Ih).Length);
        }

        [Fact]
        public void HandshakeParseRoundTripAndFlags()
        {
            var reserved = new byte[] { 0, 0, 0, 0, 0, 0x30, 0, 0 };
            var hs = Wire.BuildHandshake(reserved, Ih, Pid);
            HandshakeInfo info;
            Assert.True(Wire.TryParseHandshake(hs, 68, out info));
            Assert.Equal(Ih, info.InfoHash);
            Assert.Equal(Pid, info.PeerId);
            Assert.True(info.SupportsLtep);
            Assert.False(info.SupportsDht);          // Ace does not announce DHT or Fast
            Assert.False(info.SupportsFast);

            Assert.True(Wire.TryParseHandshake(hs, 48, out info));
            Assert.Null(info.PeerId);
            Assert.False(Wire.TryParseHandshake(hs, 47, out info));

            var std = (byte[])hs.Clone();
            std[27] = 0x05;     // DHT + Fast
            Assert.True(Wire.TryParseHandshake(std, 68, out info));
            Assert.True(info.SupportsDht);
            Assert.True(info.SupportsFast);
        }

        [Fact]
        public void HandshakeRejectsOtherProtocols()
        {
            var hs = Wire.BuildHandshake(new byte[8], Ih, Pid);
            hs[0] = 17;                   // AceStreamProtocol is the live-stream handshake, not the VOD one
            HandshakeInfo info;
            Assert.False(Wire.TryParseHandshake(hs, 68, out info));
            hs = Wire.BuildHandshake(new byte[8], Ih, Pid);
            hs[5] = (byte)'X';
            Assert.False(Wire.TryParseHandshake(hs, 68, out info));
        }

        [Fact]
        public void BadHandshakeArgumentsThrow()
        {
            Assert.Throws<ArgumentException>(() => Wire.BuildHandshake(new byte[7], Ih, Pid));
            Assert.Throws<ArgumentException>(() => Wire.BuildHandshake(new byte[8], new byte[19], Pid));
        }

        [Fact]
        public void SimpleMessages()
        {
            Assert.Equal("0000000100", TestUtil.ToHex(Wire.Choke()));
            Assert.Equal("0000000101", TestUtil.ToHex(Wire.Unchoke()));
            Assert.Equal("0000000102", TestUtil.ToHex(Wire.Interested()));
            Assert.Equal("0000000103", TestUtil.ToHex(Wire.NotInterested()));
            Assert.Equal("00000000", TestUtil.ToHex(Wire.KeepAlive));
        }

        [Fact]
        public void HaveRequestCancelPieceBitfield()
        {
            Assert.Equal("00000005040102 0304".Replace(" ", ""), TestUtil.ToHex(Wire.Have(0x01020304)));
            Assert.Equal("0000000d06" + "00000007" + "00004000" + "00004000", TestUtil.ToHex(Wire.Request(7, 0x4000, 0x4000)));
            Assert.Equal("0000000d08" + "00000007" + "00004000" + "00004000", TestUtil.ToHex(Wire.Cancel(7, 0x4000, 0x4000)));
            var data = new byte[] { 0xaa, 0xbb, 0xcc };
            Assert.Equal("0000000c07" + "00000002" + "00000010" + "aabbcc", TestUtil.ToHex(Wire.Piece(2, 16, data, 0, 3)));
            Assert.Equal("0000000b07" + "00000002" + "00000010" + "bbcc", TestUtil.ToHex(Wire.Piece(2, 16, data, 1, 2)));
            Assert.Equal("00000003" + "05" + "a080", TestUtil.ToHex(Wire.Bitfield(new byte[] { 0xa0, 0x80 })));
        }

        [Fact]
        public void PieceLengthPrefixCountsIdAndHeader()
        {
            var block = new byte[16384];
            var m = Wire.Piece(5, 32768, block, 0, block.Length);
            Assert.Equal(4 + 9 + 16384, m.Length);
            Assert.Equal(9u + 16384, Wire.GetU32(m, 0));
            Assert.Equal(WireId.Piece, m[4]);
            Assert.Equal(5u, Wire.GetU32(m, 5));
            Assert.Equal(32768u, Wire.GetU32(m, 9));
        }

        [Fact]
        public void ExtendedWrapper()
        {
            var m = Wire.Extended(0, new byte[] { 0x64, 0x65 });
            Assert.Equal("00000004" + "14" + "00" + "6465", TestUtil.ToHex(m));
            Assert.Equal("00000004" + "14" + "02" + "6465", TestUtil.ToHex(Wire.Extended(2, new byte[] { 0x64, 0x65 })));
        }

        [Fact]
        public void NumericHelpers()
        {
            var b = new byte[8];
            Wire.PutU32(b, 0, 0xdeadbeef);
            Assert.Equal(0xdeadbeefu, Wire.GetU32(b, 0));
            Wire.PutU16(b, 0, 0xabcd);
            Assert.Equal((ushort)0xabcd, Wire.GetU16(b, 0));
            Wire.PutU64(b, 0, 0x0102030405060708UL);
            Assert.Equal("0102030405060708", TestUtil.ToHex(b));
            Assert.Equal(0x0102030405060708UL, Wire.GetU64(b, 0));
        }

        [Fact]
        public void FastExtensionAndPortIdsAreNeverProducedByTheBuilders()
        {
            // PORT (9) and the Fast ones (13-17) close the connection in Ace: there are no constructors for them.
            var all = new List<byte[]>
            {
                Wire.Choke(), Wire.Unchoke(), Wire.Interested(), Wire.NotInterested(), Wire.Have(1), Wire.Bitfield(new byte[1]),
                Wire.Request(1, 0, 1), Wire.Cancel(1, 0, 1), Wire.Piece(1, 0, new byte[1], 0, 1), Wire.Extended(0, new byte[1])
            };
            foreach (var m in all)
            {
                byte id = m[4];
                Assert.NotEqual(WireId.Port, id);
                Assert.False(id >= 13 && id <= 17);
            }
        }
    }

    public class BitfieldTests
    {
        [Fact]
        public void MsbFirstEncoding()
        {
            var bf = new Bitfield(10);
            bf.Set(0); bf.Set(7); bf.Set(8);
            Assert.Equal(new byte[] { 0x81, 0x80 }, bf.ToBytes());
            Assert.Equal(3, bf.Count);
        }

        [Fact]
        public void ParseRoundTrip()
        {
            var data = new byte[] { 0xff, 0xc0 };
            var bf = Bitfield.FromBytes(data, 0, 2, 10);
            Assert.NotNull(bf);
            Assert.Equal(10, bf.Count);
            Assert.True(bf.IsComplete);
            Assert.Equal(data, bf.ToBytes());
        }

        [Fact]
        public void RejectsWrongLengthAndNonZeroPadding()
        {
            Assert.Null(Bitfield.FromBytes(new byte[] { 0xff }, 0, 1, 10));
            Assert.Null(Bitfield.FromBytes(new byte[] { 0xff, 0xc1 }, 0, 2, 10));          // padding other than 0
            Assert.NotNull(Bitfield.FromBytes(new byte[] { 0xff, 0xc1 }, 0, 2, 10, false));
        }

        [Fact]
        public void SetIsIdempotentAndIgnoresOutOfRange()
        {
            var bf = new Bitfield(4);
            Assert.True(bf.Set(1));
            Assert.False(bf.Set(1));
            Assert.False(bf.Set(9));
            Assert.Equal(1, bf.Count);
            bf.Set(1, false);
            Assert.Equal(0, bf.Count);
            bf.SetAll();
            Assert.True(bf.IsComplete);
            bf.Clear();
            Assert.Equal(0, bf.Count);
        }

        [Fact]
        public void ByteLength()
        {
            Assert.Equal(0, Bitfield.ByteLength(0));
            Assert.Equal(1, Bitfield.ByteLength(1));
            Assert.Equal(1, Bitfield.ByteLength(8));
            Assert.Equal(2, Bitfield.ByteLength(9));
        }
    }

    public class PeerIdTests
    {
        [Fact]
        public void FormatIsR30Dashes11Chars()
        {
            var id = PeerId.Generate();
            Assert.Equal(20, id.Length);
            Assert.Equal("R30------", Encoding.ASCII.GetString(id, 0, 9));
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz.-";
            for (int i = 9; i < 20; i++) Assert.Contains((char)id[i], alphabet);
            Assert.NotEqual(Encoding.ASCII.GetString(id), Encoding.ASCII.GetString(PeerId.Generate()));
        }
    }

    public class ExtensionMessagesTests
    {
        [Fact]
        public void ExtendedHandshakeHasAceCompatibleKeys()
        {
            var payload = ExtensionMessages.BuildExtendedHandshake("ACEStream 2.0", 8621, true, true, 12345, new byte[] { 1, 2, 3, 4 });
            var d = (BDict)BencodeParser.Parse(payload);
            var m = d.GetDict("m");
            Assert.Equal(1, m.GetInt("ut_pex"));
            Assert.Equal(2, m.GetInt("ut_metadata"));
            Assert.Equal(2, m.Count);                       // only extensions that Ace handles
            Assert.Equal(8621, d.GetInt("p"));
            Assert.Equal("ACEStream 2.0", d.GetString("v"));
            Assert.Equal(0, d.GetInt("e"));
            Assert.Equal(12345, d.GetInt("metadata_size"));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, d.GetBytes("yourip"));
            Assert.False(d.ContainsKey("ace_metadata_version"));
        }

        [Fact]
        public void ExtendedHandshakeOmitsOptionalFields()
        {
            var payload = ExtensionMessages.BuildExtendedHandshake("x", 0, false, false, null, null);
            var d = (BDict)BencodeParser.Parse(payload);
            Assert.False(d.ContainsKey("p"));
            Assert.False(d.ContainsKey("metadata_size"));
            Assert.False(d.ContainsKey("yourip"));
            Assert.Equal(0, d.GetDict("m").Count);
        }

        [Fact]
        public void ParsesRemoteExtendedHandshake()
        {
            // What an Ace engine would send: m with several extensions, v, metadata_size, ace_metadata_version.
            var text = "d1:md5:NS_CSi227e15:Tr_OVERLAYSWARMi253e6:ut_pexi1e11:ut_metadatai224e10:Tr_LIVE_v1i254ee"
                     + "13:metadata_sizei999e20:ace_metadata_versioni1e1:pi8621e1:v13:ACEStream 2.01:ei0e6:yourip4:\u0001\u0002\u0003\u0004e";
            var bytes = Encoding.Latin1.GetBytes(text);
            ExtendedHandshakeInfo h;
            Assert.True(ExtensionMessages.TryParseExtendedHandshake(bytes, 0, bytes.Length, out h));
            Assert.Equal(224, h.UtMetadataId);
            Assert.Equal(1, h.UtPexId);
            Assert.Equal(254, h.M["Tr_LIVE_v1"]);
            Assert.Equal(999, h.MetadataSize);
            Assert.Equal(1, h.AceMetadataVersion);
            Assert.Equal(8621, h.ListenPort);
            Assert.Equal("ACEStream 2.0", h.Client);
            Assert.True(h.LooksLikeAce);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, h.YourIp);
        }

        [Fact]
        public void ParsesSingleByteValuesInMLikeOrd()
        {
            var bytes = Encoding.ASCII.GetBytes("d1:md6:ut_pex1:\u0001ee");
            ExtendedHandshakeInfo h;
            Assert.True(ExtensionMessages.TryParseExtendedHandshake(bytes, 0, bytes.Length, out h));
            Assert.Equal(1, h.UtPexId);
        }

        [Fact]
        public void RejectsGarbageHandshake()
        {
            ExtendedHandshakeInfo h;
            Assert.False(ExtensionMessages.TryParseExtendedHandshake(new byte[] { 1, 2, 3 }, 0, 3, out h));
            var list = Encoding.ASCII.GetBytes("li1ee");
            Assert.False(ExtensionMessages.TryParseExtendedHandshake(list, 0, list.Length, out h));
        }

        [Fact]
        public void MetadataRequestFormats()
        {
            Assert.Equal("d8:msg_typei0e5:piecei3ee", Encoding.ASCII.GetString(ExtensionMessages.BuildMetadataRequest(3)));
            // Ace extension: {'msg_type':0,'version':1,'full':1}
            Assert.Equal("d4:fulli1e8:msg_typei0e7:versioni1ee", Encoding.ASCII.GetString(ExtensionMessages.BuildAceFullMetadataRequest()));
            Assert.Equal("d8:msg_typei2e5:piecei3ee", Encoding.ASCII.GetString(ExtensionMessages.BuildMetadataReject(3)));
        }

        [Fact]
        public void MetadataDataMessageRoundTrip()
        {
            var data = new byte[] { 9, 8, 7, 6, 5 };
            var msg = ExtensionMessages.BuildMetadataData(1, 32000, data, 1, 3);
            MetadataMessage m;
            Assert.True(ExtensionMessages.TryParseMetadataMessage(msg, 0, msg.Length, out m));
            Assert.Equal(1, m.MsgType);
            Assert.Equal(1, m.Piece);
            Assert.Equal(32000, m.TotalSize);
            Assert.Equal(3, m.DataLength);
            Assert.Equal(new byte[] { 8, 7, 6 }, new ArraySegment<byte>(msg, m.DataOffset, m.DataLength).ToArray());
        }

        [Fact]
        public void MetadataParseHonoursOffset()
        {
            var inner = ExtensionMessages.BuildMetadataData(0, 10, new byte[] { 1, 2 }, 0, 2);
            var framed = new byte[inner.Length + 6];
            Array.Copy(inner, 0, framed, 6, inner.Length);
            MetadataMessage m;
            Assert.True(ExtensionMessages.TryParseMetadataMessage(framed, 6, inner.Length, out m));
            Assert.Equal(framed.Length - 2, m.DataOffset);
            Assert.Equal(2, m.DataLength);
        }

        [Fact]
        public void PexParseSkipsLoopbackAndZeroPort()
        {
            // 3 entries: 10.0.0.1:6881, 127.0.0.1:80 (discarded), 10.0.0.2:0 (discarded)
            var added = new byte[] { 10, 0, 0, 1, 0x1a, 0xe1, 127, 0, 0, 1, 0, 80, 10, 0, 0, 2, 0, 0 };
            var d = new BDict();
            d.Add("added", BValue.Bytes(added));
            var enc = BencodeWriter.Encode(d);
            var list = new List<IPEndPoint>();
            Assert.True(ExtensionMessages.TryParsePex(enc, 0, enc.Length, list));
            Assert.Single(list);
            Assert.Equal("10.0.0.1:6881", list[0].ToString());
        }

        [Fact]
        public void PexMalformedIsRejected()
        {
            var d = new BDict();
            d.Add("added", BValue.Bytes(new byte[7]));
            var enc = BencodeWriter.Encode(d);
            Assert.False(ExtensionMessages.TryParsePex(enc, 0, enc.Length, new List<IPEndPoint>()));
        }

        [Fact]
        public void PexBuildRoundTrip()
        {
            var eps = new List<IPEndPoint> { new IPEndPoint(IPAddress.Parse("1.2.3.4"), 5000), new IPEndPoint(IPAddress.Parse("5.6.7.8"), 6000) };
            var enc = ExtensionMessages.BuildPex(eps);
            var back = new List<IPEndPoint>();
            Assert.True(ExtensionMessages.TryParsePex(enc, 0, enc.Length, back));
            Assert.Equal(eps.Select(e => e.ToString()), back.Select(e => e.ToString()));
        }
    }
}
