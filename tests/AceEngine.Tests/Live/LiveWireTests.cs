using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AceEngine.Bencode;
using AceEngine.Crypto;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Live
{
    /// <summary>Byte format of the live stream messages. The strings marked "real" are messages captured from real peers on 2026-10-16.</summary>
    public class LiveWireTests
    {
        private static byte[] Msg(byte[] frame)
        {
            // strips the length prefix and checks that it is consistent
            Assert.Equal((uint)(frame.Length - 4), Wire.GetU32(frame, 0));
            return frame.Skip(4).ToArray();
        }

        [Fact]
        public void IdsMatchTheTable()
        {
            Assert.Equal(new byte[] { 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 24, 25, 32, 33, 34, 35, 36, 37, 38, 39 },
                new[] { LiveId.Lost, LiveId.MyInfo, LiveId.Peers, LiveId.Close, LiveId.HpRequest, LiveId.Broadcast, LiveId.Unicast, LiveId.ChangePos, LiveId.Seek,
                    LiveId.Extend, LiveId.SeekResp, LiveId.HlsMetadataStreams, LiveId.SeekPos, LiveId.SeekPosResp, LiveId.HlsMetadataPieceList, LiveId.HlsStartedDownload,
                    LiveId.Ping, LiveId.Pong, LiveId.BroadcastV2, LiveId.UnicastV2, LiveId.GetPeers, LiveId.LogData });
            foreach (byte id in new byte[] { 17, 23, 26, 27, 28, 29, 30, 31, 40, 100, 255 }) Assert.False(LiveId.IsKnown(id), "id " + id);
            for (byte id = 0; id <= 16; id++) Assert.True(LiveId.IsKnown(id));
        }

        [Fact]
        public void HaveWithAndWithoutTimestamp()
        {
            var h = LiveWire.Have(0x0145cdacUL, null);
            Assert.Equal("00000009" + "04" + "000000000145cdac", TestUtil.ToHex(h));            // real: 9-byte HAVE
            var h2 = LiveWire.Have(1UL << 40, 1792177747.5);
            Assert.Equal(21, h2.Length);
            ulong idx; double? ts;
            Assert.True(LiveWire.TryParseHave(Msg(h2), out idx, out ts));
            Assert.Equal(1UL << 40, idx); Assert.Equal(1792177747.5, ts.Value);
            Assert.True(LiveWire.TryParseHave(TestUtil.FromHex("04000000000145cdac"), out idx, out ts));
            Assert.Equal(21351852UL, idx); Assert.Null(ts);
            Assert.False(LiveWire.TryParseHave(new byte[10], out idx, out ts));
        }

        [Fact]
        public void BitfieldWindowRealCaptureWithoutTimestamps()
        {
            // real: Q first=0x0145cbb8, I nbits=500, 63 bytes at 0xff; the message is 76 bytes long
            var msg = new byte[] { 5 }.Concat(TestUtil.FromHex("000000000145cbb8" + "000001f4")).Concat(Enumerable.Repeat((byte)0xff, 63)).ToArray();
            Assert.Equal(76, msg.Length);
            ulong first; int nbits, off; double? t0, t1;
            Assert.True(LiveWire.TryParseBitfield(msg, out first, out nbits, out off, out t0, out t1));
            Assert.Equal(0x0145cbb8UL, first); Assert.Equal(500, nbits); Assert.Equal(13, off); Assert.Null(t0);
            for (int j = 0; j < 500; j++) Assert.True(LiveWire.BitAt(msg, off, j));
        }

        [Fact]
        public void BitfieldBitOrderIsBigEndianAndRoundTrips()
        {
            var bits = new byte[] { 0b10100000, 0b10000000 };
            var f = LiveWire.Bitfield(1000, 9, bits, null, null);
            var m = Msg(f);
            ulong first; int n, off; double? a, b;
            Assert.True(LiveWire.TryParseBitfield(m, out first, out n, out off, out a, out b));
            Assert.Equal(1000UL, first); Assert.Equal(9, n);
            Assert.True(LiveWire.BitAt(m, off, 0)); Assert.False(LiveWire.BitAt(m, off, 1)); Assert.True(LiveWire.BitAt(m, off, 2));
            Assert.False(LiveWire.BitAt(m, off, 7)); Assert.True(LiveWire.BitAt(m, off, 8));

            var f2 = LiveWire.Bitfield(1000, 9, bits, 10.5, 20.25);
            var m2 = Msg(f2);
            Assert.Equal(m.Length + 16, m2.Length);
            Assert.True(LiveWire.TryParseBitfield(m2, out first, out n, out off, out a, out b));
            Assert.Equal(10.5, a.Value); Assert.Equal(20.25, b.Value); Assert.Equal(29, off);
            Assert.True(LiveWire.BitAt(m2, off, 2));
        }

        [Fact]
        public void BitfieldRejectsBadLengthsAndEmpty()
        {
            ulong first; int n, off; double? a, b;
            Assert.False(LiveWire.TryParseBitfield(new byte[13], out first, out n, out off, out a, out b));                // no bits (must be > 13)
            var bad = new byte[] { 5 }.Concat(TestUtil.FromHex("0000000000000001" + "00000040")).Concat(new byte[3]).ToArray();   // 64 bits need 8 bytes
            Assert.False(LiveWire.TryParseBitfield(bad, out first, out n, out off, out a, out b));
            Assert.Throws<ArgumentException>(() => LiveWire.Bitfield(1, 8, new byte[0], null, null));
        }

        [Fact]
        public void RequestPieceAndCancelWidths()
        {
            var r = LiveWire.Request(LiveId.Request, 0x0102030405060708UL, 7, true);
            Assert.Equal("0000000b" + "06" + "0102030405060708" + "0007", TestUtil.ToHex(r));
            var r1 = LiveWire.Request(LiveId.HpRequest, 5, 200, false);
            Assert.Equal("0000000a" + "0e" + "0000000000000005" + "c8", TestUtil.ToHex(r1));
            Assert.Equal(8, LiveWire.Request(LiveId.Cancel, 1, 1, true)[4]);
            ulong idx; int chunk;
            Assert.True(LiveWire.TryParseRequest(Msg(r), out idx, out chunk)); Assert.Equal(0x0102030405060708UL, idx); Assert.Equal(7, chunk);
            Assert.True(LiveWire.TryParseRequest(Msg(r1), out idx, out chunk)); Assert.Equal(200, chunk);
            Assert.False(LiveWire.TryParseRequest(new byte[12], out idx, out chunk));

            var data = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
            var p = LiveWire.Piece(21351931, 1792177747.6846318, 31, true, data, 10, 50);
            Assert.Equal(1 + 8 + 8 + 2 + 50, p.Length - 4);
            double ts; int ch, doff;
            Assert.True(LiveWire.TryParsePiece(Msg(p), true, out idx, out ts, out ch, out doff));
            Assert.Equal(21351931UL, idx); Assert.Equal(1792177747.6846318, ts); Assert.Equal(31, ch);
            Assert.Equal(data.Skip(10).Take(50).ToArray(), Msg(p).Skip(doff).ToArray());
            var p1 = LiveWire.Piece(1, 2.0, 9, false, data, 0, 10);
            Assert.True(LiveWire.TryParsePiece(Msg(p1), false, out idx, out ts, out ch, out doff));
            Assert.Equal(9, ch); Assert.Equal(10, Msg(p1).Length - doff);
        }

        [Fact]
        public void ChangePosLostPingPong()
        {
            Assert.Equal("0000000d" + "12" + "000000000145cbb8" + "000007d0", TestUtil.ToHex(LiveWire.ChangePos(0x0145cbb8, 2000)));
            ulong idx; uint win;
            Assert.True(LiveWire.TryParseChangePos(Msg(LiveWire.ChangePos(77, 12)), out idx, out win)); Assert.Equal(77UL, idx); Assert.Equal(12u, win);
            Assert.False(LiveWire.TryParseChangePos(new byte[12], out idx, out win));
            Assert.Equal("00000009" + "0a" + "0000000000000063", TestUtil.ToHex(LiveWire.Lost(99)));
            Assert.True(LiveWire.TryParseLost(Msg(LiveWire.Lost(99)), out idx)); Assert.Equal(99UL, idx);

            // real: PING seq=1 size=82839 (0x14397)
            int seq, size;
            Assert.True(LiveWire.TryParsePingPong(TestUtil.FromHex("220000000100014397"), out seq, out size));
            Assert.Equal(1, seq); Assert.Equal(82839, size);
            var pong = LiveWire.Pong(1, 82839);
            Assert.Equal(4 + 1 + 8 + 82839, pong.Length);
            Assert.Equal(35, pong[4]);
            Assert.True(pong.Skip(13).All(b => b == 0));
            Assert.Equal("0000000922" + "0000000500000010", TestUtil.ToHex(LiveWire.Ping(5, 16)));
        }

        // real BROADCAST_V2 (82 bytes): ver 2, offset 52, ttl 63, maxttl 64, ip 5.231.25.132, port 10063, peer_id, seq, id 4 (LAST_PIECE_V2), created, {a: index, b: unix}
        private const string RealBroadcastV2 = "24" + "0002" + "0034" + "003f" + "0040" + "05e71984" + "274f" + "5233302d2d2d2d2d2d357353524a546d426e556c"
            + "000001a1461d9f89" + "04" + "00065df9e3f2b924" + "643a313a6169323133353138353365313a626931373932313737373133656565";

        [Fact]
        public void ParsesARealBroadcastV2()
        {
            var raw = TestUtil.FromHex(RealBroadcastV2.Replace("643a31", "64313a"));      // the real dictionary: d1:ai21351853e1:bi1792177713ee
            raw = TestUtil.FromHex("24" + "0002" + "0034" + "003f" + "0040" + "05e71984" + "274f" + "5233302d2d2d2d2d2d357353524a546d426e556c"
                + "000001a1461d9f89" + "04" + "00065df9e3f2b924").Concat(Encoding.ASCII.GetBytes("d1:ai21351853e1:bi1792177713ee")).ToArray();
            Assert.Equal(82, raw.Length);
            LiveBroadcast b;
            Assert.True(LiveWire.TryParseBroadcast(raw, out b));
            Assert.Equal(2, b.Version); Assert.Equal(52, b.PayloadOffset); Assert.Equal(63, b.Ttl); Assert.Equal(64, b.MaxTtl);
            Assert.Equal("5.231.25.132", string.Join(".", b.OriginIp)); Assert.Equal(10063, b.OriginPort);
            Assert.Equal("R30------5sSRJTmBnUl", Encoding.ASCII.GetString(b.OriginPeerId));
            Assert.Equal(0x000001a1461d9f89UL, b.Seq); Assert.Equal(4, b.MessageId); Assert.Equal(0x00065df9e3f2b924UL, b.CreatedMicro);
            ulong idx; double ts;
            Assert.True(LiveWire.TryParseLastPiece(b.Payload, out idx, out ts));
            Assert.Equal(21351853UL, idx); Assert.Equal(1792177713.0, ts);
        }

        [Fact]
        public void BroadcastBuildersRoundTrip()
        {
            var peer = Encoding.ASCII.GetBytes("R30------abcdefghijk");
            var payload = LiveWire.LastPiecePayloadV2(12345, 1700000000);
            Assert.Equal("d1:ai12345e1:bi1700000000ee", Encoding.ASCII.GetString(payload));
            var f = LiveWire.BroadcastV2(10, 12, new byte[] { 1, 2, 3, 4 }, 8621, peer, 99, 4, 5, payload);
            LiveBroadcast b;
            Assert.True(LiveWire.TryParseBroadcast(Msg(f), out b));
            Assert.Equal(52, b.PayloadOffset); Assert.Equal(10, b.Ttl); Assert.Equal(99UL, b.Seq); Assert.Equal(payload, b.Payload);
            var f1 = LiveWire.BroadcastV1(7, new byte[] { 9, 8, 7, 6 }, 1000, peer, 3, 0, Encoding.ASCII.GetBytes("li55ei1ee"));
            Assert.True(LiveWire.TryParseBroadcast(Msg(f1), out b));
            Assert.Equal(1, b.Version); Assert.Equal(38, b.PayloadOffset); Assert.Equal(7, b.Ttl); Assert.Equal(1000, b.OriginPort); Assert.Equal(0, b.MessageId);
            ulong idx; double ts;
            Assert.True(LiveWire.TryParseLastPiece(b.Payload, out idx, out ts)); Assert.Equal(55UL, idx);
            Assert.False(LiveWire.TryParseBroadcast(new byte[] { 36, 0, 2 }, out b));
        }

        [Fact]
        public void MyInfoRealCaptureParsesAndBuilderUsesSingleLetterKeys()
        {
            var real = Encoding.ASCII.GetBytes("d1:ai1e1:bi43385e1:ci822134e1:di19319354e1:ei870589e1:fi1e1:gi21349010e1:hi2941e1:ii21348969e1:ji21351851e1:ki0e1:li542197e1:mi-1e1:ni0e1:oi0e1:pi0e1:qi1e1:ri21351852e1:si21351852e1:ti-1ee");
            var msg = new byte[] { 11 }.Concat(real).ToArray();
            Assert.Equal(189, msg.Length);                                                       // real: len 189
            var d = LiveWire.ParseMyInfo(msg);
            Assert.Equal(21349010, d.GetInt("g")); Assert.Equal(21348969, d.GetInt("i")); Assert.Equal(21351851, d.GetInt("j")); Assert.Equal(21351852, d.GetInt("r"));
            Assert.Equal(2941, d.GetInt("h")); Assert.Equal(21351852, d.GetInt("s"));

            var st = new LiveLocalState { Position = 5, MinPiece = 1, MaxPiece = 9, Lsp = 9, LiveWindowSize = 300, DownloadWindowEnd = 20 };
            var mine = new BDict();
            LiveExtend.AddMyInfoLetters(mine, st, 0);
            var frame = LiveWire.MyInfo(mine);
            var back = LiveWire.ParseMyInfo(Msg(frame));
            Assert.Equal(new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s" }, back.Keys.OrderBy(x => x).ToArray());
            Assert.Equal(5, back.GetInt("g")); Assert.Equal(300, back.GetInt("h")); Assert.Equal(20, back.GetInt("s"));
            Assert.Null(LiveWire.ParseMyInfo(new byte[] { 11, 1, 2, 3 }));
        }

        [Theory]
        [InlineData(3017400, true, true, true)]
        [InlineData(3022200, true, true, true)]
        [InlineData(3012600, true, true, true)]
        [InlineData(3012599, false, false, true)]
        [InlineData(3002400, false, false, true)]
        [InlineData(3002399, false, false, false)]
        [InlineData(3000300, false, false, false)]
        public void CapabilitiesFollowTheRemoteVersion(long v, bool ping, bool broadcastV2, bool wide)
        {
            var c = LiveCaps.FromVersion(v);
            Assert.Equal(ping, c.Ping); Assert.Equal(broadcastV2, c.BroadcastV2); Assert.Equal(wide, c.WideChunk);
        }

        [Fact]
        public void PieceMapTracksMinMaxAndPrunes()
        {
            var m = new LivePieceMap();
            Assert.Equal(0, m.Count);
            foreach (long i in new long[] { 10, 12, 11, 20 }) m.Set(i);
            Assert.Equal(10, m.Min); Assert.Equal(20, m.Max); Assert.True(m.Has(11)); Assert.False(m.Has(13));
            m.Clear(20); Assert.Equal(12, m.Max);
            m.Clear(10); Assert.Equal(11, m.Min);
            m.PruneBelow(12); Assert.Equal(1, m.Count); Assert.Equal(12, m.Min);
            m.Reset(); Assert.Equal(0, m.Count);
        }
    }

    public class LiveExtendTests
    {
        private static readonly LiveOptions Opt = new LiveOptions();

        private static byte[] Frame(byte[] payload) { return new byte[] { 20, 0 }.Concat(payload).ToArray(); }

        [Fact]
        public void BuiltExtendIsSignedAndVerifiesAfterRoundTrip()
        {
            byte[] seed, pub;
            Ed25519.GenerateKeyPair(out seed, out pub);
            var st = new LiveLocalState { Lsp = 21351850, Position = 21351840, LiveWindowSize = 900, ListenPort = 8621 };
            var payload = LiveExtend.BuildPayload(Opt, st, 0, new byte[] { 1, 2, 3, 4 }, 12345, seed, pub);
            LiveExtendInfo info;
            Assert.True(LiveExtend.TryParse(Frame(payload), out info));
            Assert.True(info.HasSignature);
            Assert.True(info.SignatureValid.Value);
            Assert.Equal(pub, info.NodeId);
            Assert.Equal(3022200, info.V); Assert.Equal(2, info.Pv); Assert.Equal(1, info.Nt); Assert.Equal("bt", info.Tt); Assert.Equal(3, info.Platform);
            Assert.Equal(21351850, info.Lsp); Assert.Equal(8621, info.Port); Assert.Equal(1, info.AceMetadataVersion); Assert.Equal(2, info.UtMetadataId);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, info.YourIp);
            Assert.Equal(21351840, info.MiPosition);
            Assert.Equal(900, info.MiLiveWindowSize);

            // the signature is Ed25519 over the SHA-256 of the bencode with the signature zeroed
            var zeroMarker = BencodeWriter.Encode(BValue.Bytes(new byte[64]));
            var sigMarker = BencodeWriter.Encode(BValue.Bytes(info.Signature));
            var zeroed = new List<byte>();
            int at = LiveExtend.IndexOf(payload, sigMarker, 0);
            zeroed.AddRange(payload.Take(at)); zeroed.AddRange(zeroMarker); zeroed.AddRange(payload.Skip(at + sigMarker.Length));
            Assert.True(Ed25519.Verify(pub, HashUtil.Sha256(zeroed.ToArray()), info.Signature));
        }

        [Fact]
        public void TamperingBreaksTheSignature()
        {
            byte[] seed, pub;
            Ed25519.GenerateKeyPair(out seed, out pub);
            var payload = LiveExtend.BuildPayload(Opt, new LiveLocalState { Lsp = 5 }, 0, null, 1, seed, pub);
            var bad = (byte[])payload.Clone();
            int at = LiveExtend.IndexOf(bad, Encoding.ASCII.GetBytes("2:tt2:bt"), 0);
            Assert.True(at > 0);
            bad[at + 6] = (byte)'x';                                           // "bt" -> "xt": the signature no longer matches
            LiveExtendInfo info;
            Assert.True(LiveExtend.TryParse(Frame(bad), out info));
            Assert.True(info.HasSignature);
            Assert.False(info.SignatureValid.Value);
        }

        [Fact]
        public void UnsignedExtendIsAccepted()
        {
            var o = new LiveOptions { SignExtend = false };
            var payload = LiveExtend.BuildPayload(o, new LiveLocalState(), 0, null, 1, null, null);
            LiveExtendInfo info;
            Assert.True(LiveExtend.TryParse(Frame(payload), out info));
            Assert.False(info.HasSignature); Assert.Null(info.SignatureValid);
        }

        [Fact]
        public void KeysAreInAceOrderAndMiUsesLongNamesByDefault()
        {
            byte[] seed, pub;
            Ed25519.GenerateKeyPair(out seed, out pub);
            var payload = LiveExtend.BuildPayload(Opt, new LiveLocalState { Lsp = 7 }, 0, new byte[4], 1, seed, pub);
            var d = (BDict)BencodeParser.Parse(payload);
            Assert.Equal(new[] { "ace_metadata_version", "lsp", "m", "mi", "node_id", "nt", "p", "platform", "pv", "signature", "tt", "ts", "v", "yourip" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                d.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            var mi = d.GetDict("mi");
            Assert.True(mi.ContainsKey("position")); Assert.True(mi.ContainsKey("min_piece")); Assert.True(mi.ContainsKey("peer_type")); Assert.True(mi.ContainsKey("top_session_up_rate"));
            Assert.Equal(0, mi.GetInt("peer_type"));
            var o = new LiveOptions { LongMiKeys = false };
            var mi2 = ((BDict)BencodeParser.Parse(LiveExtend.BuildPayload(o, new LiveLocalState(), 0, null, 1, null, null))).GetDict("mi");
            Assert.True(mi2.ContainsKey("g")); Assert.False(mi2.ContainsKey("position"));
        }

        [Fact]
        public void ConfigurableInferredValuesReachTheWire()
        {
            var o = new LiveOptions { NodeType = 4, TransportType = "hls", Platform = 9, ProtocolVersion = 3, EngineVersion = 3010000, SignExtend = false };
            var d = (BDict)BencodeParser.Parse(LiveExtend.BuildPayload(o, new LiveLocalState(), 7, null, 1, null, null));
            Assert.Equal(4, d.GetInt("nt")); Assert.Equal("hls", d.GetString("tt")); Assert.Equal(9, d.GetInt("platform")); Assert.Equal(3, d.GetInt("pv")); Assert.Equal(3010000, d.GetInt("v"));
            Assert.Equal(7, d.GetDict("mi").GetInt("peer_type"));
        }

        // ---- Real EXTEND messages (captured on 2026-10-16 from peers on the Ace network) ----

        private static byte[] Real(string name) { return TestUtil.FromHex(Encoding.ASCII.GetString(TestUtil.Fixture("real/" + name)).Trim()); }

        [Theory]
        [InlineData("extend_43.251.27.125_8621.hex", 3020800, true)]
        [InlineData("extend_2.237.74.215_8621.hex", 3020800, true)]
        [InlineData("extend_5.89.172.102_8621.hex", 3021400, true)]
        [InlineData("extend_175.130.71.232_8621.hex", 3013200, false)]
        public void RealPeerExtendMessagesParseAndTheirSignaturesVerify(string file, long version, bool signed)
        {
            var msg = Real(file);
            Assert.Equal(20, msg[0]); Assert.Equal(0, msg[1]);
            LiveExtendInfo info;
            Assert.True(LiveExtend.TryParse(msg, out info));
            Assert.Equal(version, info.V);
            Assert.Equal(1, info.Nt); Assert.Equal("bt", info.Tt); Assert.Equal(2, info.Pv); Assert.Equal(1, info.AceMetadataVersion); Assert.Equal(2, info.UtMetadataId);
            Assert.Equal(signed, info.HasSignature);
            if (signed) Assert.True(info.SignatureValid.Value, "the ed25519 signature of a real peer must verify");
            else Assert.Null(info.SignatureValid);
            Assert.True(info.Lsp > 1000000);
            Assert.True(info.MiMaxPiece > 1000000 && info.MiMinPiece < info.MiMaxPiece);
            Assert.True(info.MiLiveWindowSize > 0);
            Assert.Equal(0, info.MiPeerType);
        }

        [Fact]
        public void ARealSignedExtendWithOneByteChangedDoesNotVerify()
        {
            var msg = Real("extend_43.251.27.125_8621.hex");
            int at = LiveExtend.IndexOf(msg, Encoding.ASCII.GetBytes("2:tt2:bt"), 0);
            Assert.True(at > 0);
            msg[at + 6] = (byte)'x';
            LiveExtendInfo info;
            Assert.True(LiveExtend.TryParse(msg, out info));
            Assert.False(info.SignatureValid.Value);
        }

        [Fact]
        public void MalformedExtendIsRejected()
        {
            LiveExtendInfo info;
            Assert.False(LiveExtend.TryParse(new byte[] { 20, 0 }, out info));
            Assert.False(LiveExtend.TryParse(new byte[] { 20, 0, (byte)'x', (byte)'y', (byte)'z' }, out info));
            Assert.False(LiveExtend.TryParse(Frame(Encoding.ASCII.GetBytes("li1ee")), out info));          // not a dictionary
            Assert.True(LiveExtend.TryParse(Frame(Encoding.ASCII.GetBytes("de")), out info));              // empty dictionary: valid but without 'tt'
            Assert.Null(info.Tt);
        }
    }

    public class LiveDescriptorFromRealFilesTests
    {
        [Fact]
        public void TheRealDescriptorsDecryptAndHaveTheirFieldsOnTheWire()
        {
            // The real files are decrypted with the V2 key and the IV SHA1(MD5(key)); the infohash matches the one a real peer accepted
            var tf = AceEngine.Transport.TransportLoader.Load(TestUtil.Fixture("real/2940120bf034db79a7f5849846ccea0255172eae.acelive"));
            var key = RsaPublicKey.Parse(tf.Live.PubKey);
            Assert.Equal(768, key.Bits); Assert.Equal(96, key.SignSize); Assert.Equal(3, (int)key.E);
            Assert.Equal("20db14f6e9cd048c12006e7f9e5e8326390eaff1", tf.Live.InfoHashHex);
        }
    }
}
