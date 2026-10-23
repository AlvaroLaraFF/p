using System;
using System.Collections.Generic;
using System.Linq;
using AceEngine.Cast;
using Xunit;

namespace AceEngine.Tests.Cast
{
    public class AnnexBFixTests
    {
        /// <summary>NAL types of each video access unit of a transport stream (up to the first slice and including it).</summary>
        internal static List<int[]> AuNalOrders(byte[] data, int videoPid = 0x100)
        {
            var aus = new List<List<byte>>();
            List<byte> cur = null;
            for (int i = 0; i + 188 <= data.Length; i += 188)
            {
                if (((data[i + 1] & 0x1F) << 8 | data[i + 2]) != videoPid) continue;
                int afc = (data[i + 3] >> 4) & 3, ps = i + 4;
                if ((afc & 2) != 0) ps = i + 5 + data[i + 4];
                if ((afc & 1) == 0) continue;
                if ((data[i + 1] & 0x40) != 0)
                {
                    cur = new List<byte>();
                    aus.Add(cur);
                    ps += 9 + data[ps + 8];
                }
                if (cur != null) for (int k = ps; k < i + 188; k++) cur.Add(data[k]);
            }
            var res = new List<int[]>();
            foreach (var a in aus)
            {
                var nals = new List<int>();
                for (int j = 0; j + 3 < a.Count; j++)
                    if (a[j] == 0 && a[j + 1] == 0 && a[j + 2] == 1)
                    {
                        int t = a[j + 3] & 0x1F;
                        nals.Add(t);
                        if (t >= 1 && t <= 5) break;
                    }
                res.Add(nals.ToArray());
            }
            return res;
        }

        private static byte[] SegmentWith(TestNalOrder order, bool normalize)
        {
            using (var rig = new SegRig(g => { g.NalOrder = order; }, o => o.NormalizeNalOrder = normalize))
            {
                rig.Feed(30);
                var pl = rig.Playlist();
                return rig.Fetch(pl.Segments[1].Uri);
            }
        }

        [Theory]
        [InlineData(TestNalOrder.SeiFirst)]
        [InlineData(TestNalOrder.SpsFirst)]
        public void EncodersThatOrderTheNalUnitsDifferentlyAreNormalizedInEverySegment(TestNalOrder order)
        {
            using (var rig = new SegRig(g => { g.NalOrder = order; g.GopFrames = 25; }))
            {
                rig.Feed(40);
                var pl = rig.Playlist();
                Assert.True(pl.Segments.Count >= 4);
                int keyAus = 0;
                foreach (var s in pl.Segments)
                {
                    foreach (var nals in AuNalOrders(rig.Fetch(s.Uri)))
                    {
                        if (!nals.Contains(5)) continue;
                        keyAus++;
                        Assert.Equal(new[] { 9, 7, 8, 6, 5 }, nals);
                    }
                }
                Assert.True(keyAus >= 8, "several key frames per segment are checked, not only the first");
                Assert.True(rig.Seg.GetStats().NalOrderFixes >= keyAus);
            }
        }

        [Theory]
        [InlineData(TestNalOrder.SeiFirst)]
        [InlineData(TestNalOrder.SpsFirst)]
        public void TheNormalizerOnlyPermutesBytesInsideTheAccessUnit(TestNalOrder order)
        {
            var raw = SegmentWith(order, false);
            var fixedSeg = SegmentWith(order, true);
            Assert.Equal(raw.Length, fixedSeg.Length);
            // every TS packet keeps its header (PID, flags, continuity counter) and the multiset of payload bytes of the whole segment is the same
            for (int i = 0; i < raw.Length; i += 188)
                for (int k = 0; k < 4; k++) Assert.Equal(raw[i + k], fixedSeg[i + k]);
            Assert.NotEqual(raw, fixedSeg);
            Assert.Equal(raw.OrderBy(b => b).ToArray(), fixedSeg.OrderBy(b => b).ToArray());
            // and what was already fine in the raw segment is untouched: the packets that carry no NAL prefix are byte-identical
            int identical = 0;
            for (int i = 0; i < raw.Length; i += 188) if (raw.Skip(i).Take(188).SequenceEqual(fixedSeg.Skip(i).Take(188))) identical++;
            Assert.True(identical > raw.Length / 188 * 0.9);
        }

        [Fact]
        public void AStreamAlreadyInTheStandardOrderIsLeftByteForByte()
        {
            var a = SegmentWith(TestNalOrder.Canonical, false);
            var b = SegmentWith(TestNalOrder.Canonical, true);
            Assert.Equal(a, b);
            Assert.Equal(new[] { 9, 7, 8, 5 }, AuNalOrders(a).First(x => x.Contains(5)));
        }

        // ---------- the function on hand-built packets ----------

        private static byte[] Packets(byte[] pesPayload, bool withDts = false, int extraAfBytes = 0)
        {
            var pes = new List<byte>(new byte[] { 0, 0, 1, 0xE0, 0, 0, 0x80, (byte)(withDts ? 0xC0 : 0x80), (byte)(withDts ? 10 : 5) });
            pes.AddRange(new byte[] { 0x21, 0, 1, 0, 1 });
            if (withDts) pes.AddRange(new byte[] { 0x11, 0, 1, 0, 1 });
            pes.AddRange(pesPayload);
            var res = new List<byte>();
            int pos = 0; bool first = true; int cc = 0;
            while (pos < pes.Count)
            {
                var p = new byte[188];
                p[0] = 0x47; p[1] = (byte)((first ? 0x40 : 0) | 1); p[2] = 0x00;                       // pid 0x100
                int n = Math.Min(184 - (first ? extraAfBytes : 0), pes.Count - pos);
                int stuff = 184 - n - (first ? extraAfBytes : 0);
                int afLen = (first && extraAfBytes > 0 ? extraAfBytes - 1 : -1);
                if (stuff > 0) afLen = (afLen < 0 ? 0 : afLen + 1) + stuff - (afLen < 0 ? 1 : 0);
                int hdr = 4;
                if (afLen >= 0) { p[3] = (byte)(0x30 | (cc & 15)); p[4] = (byte)afLen; for (int q = 5; q < 5 + afLen; q++) p[q] = 0xFF; if (afLen > 0) p[5] = 0; hdr = 5 + afLen; }
                else p[3] = (byte)(0x10 | (cc & 15));
                for (int q = 0; q < n; q++) p[hdr + q] = pes[pos + q];
                pos += n; first = false; cc++;
                res.AddRange(p);
            }
            return res.ToArray();
        }

        private static byte[] Nal(int type, int size, bool four = true, byte fill = 0x55)
        {
            var l = new List<byte>();
            if (four) l.Add(0);
            l.AddRange(new byte[] { 0, 0, 1, (byte)(0x60 | type) });
            for (int i = 0; i < size; i++) l.Add(fill);
            return l.ToArray();
        }

        private static byte[] Cat(params byte[][] parts) { return parts.SelectMany(x => x).ToArray(); }

        [Fact]
        public void ASeiThatSpansSeveralPacketsIsMovedAfterThePpsAndTheRestIsUntouched()
        {
            var slice = Nal(5, 3000, true, 0x77);
            var payload = Cat(Nal(9, 1), Nal(6, 500, true, 0x33), Nal(7, 20, true, 0x11), Nal(8, 4, true, 0x22), slice);
            var ts = Packets(payload);
            Assert.True(ts.Length > 188 * 5);
            var before = (byte[])ts.Clone();
            int n = AnnexBFix.NormalizeParameterSets(ts, 0, ts.Length, 0x100);
            Assert.Equal(1, n);
            Assert.Equal(new[] { 9, 7, 8, 6, 5 }, AuNalOrders(ts)[0]);
            // reassemble the payload and compare with the expected permutation
            var expected = Cat(Nal(9, 1), Nal(7, 20, true, 0x11), Nal(8, 4, true, 0x22), Nal(6, 500, true, 0x33), slice);
            Assert.Equal(expected, Reassemble(ts, payload.Length));
            Assert.Equal(before.Length, ts.Length);
            // the packets after the prefix are identical
            for (int i = 188 * 4; i < ts.Length; i++) Assert.Equal(before[i], ts[i]);
        }

        private static byte[] Reassemble(byte[] ts, int length)
        {
            var l = new List<byte>(); bool first = true;
            for (int i = 0; i + 188 <= ts.Length; i += 188)
            {
                int afc = (ts[i + 3] >> 4) & 3, ps = i + 4;
                if ((afc & 2) != 0) ps = i + 5 + ts[i + 4];
                if (first) ps += 9 + ts[ps + 8];
                first = false;
                for (int k = ps; k < i + 188; k++) l.Add(ts[k]);
            }
            return l.Take(length).ToArray();
        }

        [Fact]
        public void MixedThreeAndFourByteStartCodesAndAPesWithDtsAreHandled()
        {
            var payload = Cat(Nal(6, 30, false, 0x33), Nal(9, 1, true), Nal(7, 12, false, 0x11), Nal(8, 4, true, 0x22), Nal(1, 40, false, 0x77));
            var ts = Packets(payload, withDts: true);
            Assert.Equal(1, AnnexBFix.NormalizeParameterSets(ts, 0, ts.Length, 0x100));
            Assert.Equal(new[] { 9, 7, 8, 6, 1 }, AuNalOrders(ts)[0]);
            Assert.Equal(payload.Length, Reassemble(ts, payload.Length).Length);
            Assert.Equal(payload.OrderBy(b => b).ToArray(), Reassemble(ts, payload.Length).OrderBy(b => b).ToArray());
        }

        [Fact]
        public void AnAdaptationFieldInFrontOfThePayloadIsRespected()
        {
            var payload = Cat(Nal(6, 60, true, 0x33), Nal(7, 12, true, 0x11), Nal(8, 4, true, 0x22), Nal(5, 400, true, 0x77), Nal(9, 1));
            var ts = Packets(payload, extraAfBytes: 8);
            Assert.Equal(0x30, ts[3] & 0x30);                                                      // the first packet has an adaptation field in front of the payload
            int n = AnnexBFix.NormalizeParameterSets(ts, 0, ts.Length, 0x100);
            Assert.Equal(1, n);
            Assert.Equal(new[] { 7, 8, 6, 5 }, AuNalOrders(ts)[0]);
        }

        [Theory]
        [InlineData(new[] { 9, 7, 8, 6, 5 })]
        [InlineData(new[] { 9, 6, 5 })]
        [InlineData(new[] { 9, 1 })]
        [InlineData(new[] { 7, 8, 5 })]
        public void OrdersThatAreAlreadyRightAreNotTouched(int[] order)
        {
            var parts = order.Select(t => Nal(t, 10 + t)).ToArray();
            var ts = Packets(Cat(parts));
            var before = (byte[])ts.Clone();
            Assert.Equal(0, AnnexBFix.NormalizeParameterSets(ts, 0, ts.Length, 0x100));
            Assert.Equal(before, ts);
        }

        [Fact]
        public void UnknownNalUnitsInFrontOfTheSliceMakeItLeaveTheAccessUnitAlone()
        {
            var ts = Packets(Cat(Nal(6, 10), Nal(14, 4), Nal(7, 10), Nal(5, 50)));                // a prefix NAL (14) the code does not know
            var before = (byte[])ts.Clone();
            Assert.Equal(0, AnnexBFix.NormalizeParameterSets(ts, 0, ts.Length, 0x100));
            Assert.Equal(before, ts);
            var noSlice = Packets(Cat(Nal(6, 10), Nal(7, 10), Nal(8, 10)));                       // no slice at all
            before = (byte[])noSlice.Clone();
            Assert.Equal(0, AnnexBFix.NormalizeParameterSets(noSlice, 0, noSlice.Length, 0x100));
            Assert.Equal(before, noSlice);
        }

        [Fact]
        public void OtherPidsAndGarbageAreIgnoredAndRandomBytesNeverThrow()
        {
            var rnd = new Random(77);
            for (int round = 0; round < 300; round++)
            {
                int packets = 1 + rnd.Next(30);
                var d = new byte[packets * 188];
                rnd.NextBytes(d);
                for (int i = 0; i < d.Length; i += 188) { d[i] = (byte)(rnd.Next(10) == 0 ? rnd.Next(256) : 0x47); if (rnd.Next(2) == 0) { d[i + 1] = (byte)(0x40 | 1); d[i + 2] = 0; } }
                if (round % 3 == 0)
                {
                    // a plausible PES start with SEI-first and random after it
                    var pay = Cat(Nal(9, 1), Nal(6, rnd.Next(5, 300)), Nal(7, rnd.Next(5, 60)), Nal(8, 4), Nal(5, rnd.Next(1, 800)));
                    var ts = Packets(pay);
                    Array.Copy(ts, d, Math.Min(ts.Length, d.Length));
                    if (rnd.Next(2) == 0) d[rnd.Next(d.Length)] ^= (byte)(1 << rnd.Next(8));
                }
                var before = d.Length;
                AnnexBFix.NormalizeParameterSets(d, 0, d.Length, 0x100);
                Assert.Equal(before, d.Length);
            }
        }

        [Fact]
        public void ADisabledNormalizerLeavesTheSegmentsAsTheEncoderMadeThem()
        {
            var raw = SegmentWith(TestNalOrder.SeiFirst, false);
            Assert.Equal(new[] { 9, 6, 7, 8, 5 }, AuNalOrders(raw).First(x => x.Contains(5)));
        }
    }
}
