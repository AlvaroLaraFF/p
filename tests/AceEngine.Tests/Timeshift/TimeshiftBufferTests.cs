using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Live;
using AceEngine.Sim;
using Xunit;

namespace AceEngine.Tests.Timeshift
{
    /// <summary>The live stream's in-memory circular buffer, isolated: eviction, window, time index and random access, PAT/PMT, alignment, concurrency.</summary>
    public class TimeshiftBufferTests
    {
        private static readonly SimTsProfile P = TsFeed.Profile();
        private static long PacketsFor(double seconds) { return P.PacketAt(seconds); }

        [Fact]
        public void EvictsTheOldestByBudgetAndNeverExceedsIt()
        {
            using (var b = TsFeed.NewBuffer(40 * 188 * 100, 188 * 100))
            {
                Assert.Equal(40, b.MaxBlocks);
                TsFeed.Feed(b, P, 0, 30000);                                  // 5.6 MB for a 752 KB buffer
                Assert.Equal(40, b.AllocatedBlocks);
                Assert.True(b.RecycledBlocks > 100);
                Assert.True(b.WriteOffset - b.OldestOffset <= b.CapacityBytes, "ocupacion " + (b.WriteOffset - b.OldestOffset));
                Assert.True(b.WriteOffset - b.OldestOffset >= b.CapacityBytes - 188 * 100, "it must be almost full");
                Assert.Equal(0, b.OldestOffset % 188);
                var snap = b.Snapshot();
                Assert.Equal(b.WriteOffset - b.OldestOffset, snap.BufferedBytes);
            }
        }

        [Theory]
        [InlineData(100 * 1000)]
        [InlineData(1024 * 1024)]
        [InlineData(5 * 1024 * 1024 + 12345)]
        public void OccupancyNeverExceedsTheBudgetEvenWhenTheBudgetIsSmallOrNotAMultipleOfTheBlock(long budget)
        {
            using (var b = new TimeshiftBuffer(new TimeshiftOptions { BudgetBytes = budget, BlockBytes = 256 * 1024 }))
            {
                Assert.True(b.CapacityBytes <= budget, "capacidad " + b.CapacityBytes + " para " + budget);
                Assert.True(b.MaxBlocks >= 2);
                TsFeed.Feed(b, P, 0, 30000);
                Assert.True(b.WriteOffset - b.OldestOffset <= budget);
                Assert.True(b.AllocatedBlocks * b.BlockBytes <= budget);
            }
        }

        [Fact]
        public void WindowIsWhatIsStoredBehindTheLivePointAndGrowsUntilTheBudget()
        {
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                TsFeed.Feed(b, P, 0, PacketsFor(5));
                var s = b.Snapshot();
                Assert.Equal(0, s.Window.TotalSeconds);                       // 5 s stored < 10 s of live delay: nothing behind
                Assert.InRange(s.Buffered.TotalSeconds, 4.8, 5.2);
                TsFeed.Feed(b, P, PacketsFor(5), PacketsFor(30));
                s = b.Snapshot();
                Assert.InRange(s.Buffered.TotalSeconds, 29.8, 30.2);
                Assert.InRange(s.Window.TotalSeconds, 19.8, 20.2);            // 30 s stored - 10 s of delay
                Assert.Equal(TimeSpan.FromSeconds(10), s.LiveDelay);
            }
            // with a budget: the window stays at what fits (2 MiB at 256 KiB/s = 8 s... + the 10 s delay does not fit: window 0)
            using (var b = TsFeed.NewBuffer(6L * 1024 * 1024, 256 * 1024))
            {
                TsFeed.Feed(b, P, 0, PacketsFor(60));
                var s = b.Snapshot();
                Assert.InRange(s.Buffered.TotalSeconds, 21.0, 24.5);          // 6 MiB / 256 KiB/s = 24 s
                Assert.InRange(s.Window.TotalSeconds, 11.0, 14.5);
            }
        }

        [Fact]
        public void MediaClockFollowsThePcrAndIgnoresTheirOrigin()
        {
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                TsFeed.Feed(b, P, 0, PacketsFor(20));
                // the buffer's clock advances as much as the PCR (whose origin is 10 s): packet n is at n*188/bitrate
                long n = PacketsFor(12);
                Assert.InRange(b.MsAt(n * 188) - b.MsAt(0), 11950, 12050);
                Assert.True(b.IndexSamples > 100);
                Assert.True(b.IndexForTests.PcrCount > 400);
                Assert.Equal(0, b.IndexForTests.ClockJumps);
            }
        }

        [Fact]
        public void WithoutPcrTheClockFallsBackToTheNominalBitrate()
        {
            var noPcr = TsFeed.Profile(pcr: false);
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                TsFeed.Feed(b, noPcr, 0, PacketsFor(30));
                Assert.Equal(0, b.IndexSamples);
                var s = b.Snapshot();
                Assert.InRange(s.Buffered.TotalSeconds, 29.9, 30.1);
                Assert.InRange(s.Window.TotalSeconds, 19.9, 20.1);
                using (var r = b.OpenReader(TimeSpan.FromSeconds(5)))
                {
                    var data = TsFeed.Drain(r);
                    var first = TsFeed.VideoNumbers(data)[0];
                    // the random access signal is still there even without PCR: it starts at a GOP, 5 s (+ up to one GOP) before the live point
                    Assert.True(noPcr.IsGopStart(first));
                    double behind = 20 - noPcr.TimeOf(first);
                    Assert.InRange(behind, 5.0, 6.1);
                }
            }
        }

        [Fact]
        public void ClockSurvivesPcrWraparoundAndDiscontinuities()
        {
            // PCR near the 33-bit overflow (26.5 h): the clock keeps growing
            var wrap = new SimTsProfile { Bitrate = 262144, PcrOriginSeconds = 95443.0 - 2 };
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                TsFeed.Feed(b, wrap, 0, PacketsFor(8));
                Assert.InRange(b.MsAt(PacketsFor(8) * 188) - b.MsAt(0), 7900, 8100);
                Assert.Equal(0, b.IndexForTests.ClockJumps);
            }
            // a stream cut with a PCR jump does not put a jump in the clock
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                TsFeed.Feed(b, P, 0, PacketsFor(6));
                b.Discontinuity();
                long jumpTo = PacketsFor(500);                                 // the PCR jumps ~8 minutes
                TsFeed.Feed(b, P, jumpTo, jumpTo + PacketsFor(6));
                Assert.InRange(b.Snapshot().Buffered.TotalSeconds, 11.5, 12.6);   // 12 s of data, not 500 s
                Assert.True(b.IndexForTests.ClockJumps >= 1);
            }
        }

        [Fact]
        public void BehindReaderStartsAtAnEarlierRandomAccessPointWithPatAndPmtInFront()
        {
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                long total = PacketsFor(40);
                TsFeed.Feed(b, P, 0, total);
                double liveSec = P.TimeOf(total) - 10;                         // the buffer's live point
                using (var r = b.OpenReader(TimeSpan.FromSeconds(10)))
                {
                    Assert.Equal(2 * 188, r.PrefixLength);
                    var data = TsFeed.Drain(r);
                    Assert.Equal(0, data.Length % 188);
                    for (int i = 0; i < data.Length; i += 188) Assert.Equal(0x47, data[i]);
                    // PAT (PID 0) and PMT (PID 0x1000) in front
                    Assert.Equal(0, ((data[1] & 0x1F) << 8) | data[2]);
                    Assert.Equal(SimTs.PmtPid, ((data[189] & 0x1F) << 8) | data[190]);
                    // and then, a video packet with PUSI + random_access (start of GOP)
                    Assert.True((data[376 + 1] & 0x40) != 0);
                    Assert.True((data[376 + 5] & 0x40) != 0, "random_access_indicator");
                    var nums = TsFeed.VideoNumbers(data);
                    Assert.True(P.IsGopStart(nums[0]));
                    double startSec = P.TimeOf(nums[0]);
                    Assert.InRange(liveSec - startSec, 10.0, 11.1);             // 10 s behind the live stream, aligned to the previous GOP (1 s)
                    // the stream is continuous up to the edge
                    for (int i = 1; i < nums.Count; i++) Assert.Equal(nums[i - 1] + 1 + (SimTsProfile.IsPsi(nums[i - 1] + 1) ? 1 : 0), nums[i]);
                    Assert.Equal(total - 1, nums[nums.Count - 1] + (SimTsProfile.IsPsi(total - 1) ? 1 : 0));
                    Assert.InRange(r.StartBehindMs, 10000, 11100);
                }
            }
        }

        [Fact]
        public void LiveReaderStartsAtTheLivePointAtOrAfterIt()
        {
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                long total = PacketsFor(40);
                TsFeed.Feed(b, P, 0, total);
                double liveSec = P.TimeOf(total) - 10;
                using (var r = b.OpenReader(null))
                {
                    var nums = TsFeed.VideoNumbers(TsFeed.Drain(r));
                    Assert.True(P.IsGopStart(nums[0]));
                    Assert.InRange(P.TimeOf(nums[0]) - liveSec, -0.05, 1.1);    // at the live point or up to one GOP later
                    Assert.True(r.StartBehindMs <= 50);
                }
            }
        }

        [Fact]
        public void BackBeyondTheWindowIsClippedToWhatIsStored()
        {
            using (var b = TsFeed.NewBuffer(3L * 1024 * 1024, 256 * 1024))     // ~12 s stored, ~2 s of window
            {
                TsFeed.Feed(b, P, 0, PacketsFor(60));
                var s = b.Snapshot();
                using (var r = b.OpenReader(TimeSpan.FromMinutes(30)))
                {
                    Assert.True(r.StartOffset >= b.OldestOffset);
                    var data = TsFeed.Drain(r);
                    var nums = TsFeed.VideoNumbers(data);
                    double behind = P.TimeOf(PacketsFor(60)) - 10 - P.TimeOf(nums[0]);
                    Assert.InRange(behind, -1.1, s.Window.TotalSeconds + 1.2);                // with a window of ~2 s there is no random access before the margin: it starts at the next one
                }
            }
        }

        [Fact]
        public void WithoutRandomAccessMarksItFallsBackToThePesStartAndStillAligns()
        {
            // broadcaster that does not mark random_access: the PES's IDR (SPS/IDR) is detected anyway
            var p = TsFeed.Profile(rai: false);
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                TsFeed.Feed(b, p, 0, PacketsFor(40));
                using (var r = b.OpenReader(TimeSpan.FromSeconds(10)))
                {
                    var nums = TsFeed.VideoNumbers(TsFeed.Drain(r));
                    Assert.True(p.IsGopStart(nums[0]));
                }
            }
        }

        [Fact]
        public void ReaderAtAnAbsoluteOffsetIsClippedAndAlignedToAPacket()
        {
            using (var b = TsFeed.NewBuffer(40 * 188 * 100, 188 * 100))
            {
                TsFeed.Feed(b, P, 0, 30000);                                   // the buffer has wrapped around many times: the old data is no longer there
                long oldest = b.OldestOffset, write = b.WriteOffset;
                using (var r = b.OpenReaderAt(write - 188 * 50 - 77))              // in the middle of a packet: it is aligned downwards
                {
                    Assert.Equal(0, r.StartOffset % 188);
                    Assert.Equal(write - 188 * 51, r.StartOffset);
                    Assert.Equal(2 * 188, r.PrefixLength);                           // PAT/PMT in front
                    var data = TsFeed.Drain(r);
                    Assert.Equal(2 * 188 + 188 * 51, data.Length);
                    for (int i = 0; i < data.Length; i += 188) Assert.Equal(0x47, data[i]);
                }
                using (var r = b.OpenReaderAt(0)) Assert.Equal(oldest, r.StartOffset);                       // before what is stored: the oldest
                using (var r = b.OpenReaderAt(long.MaxValue)) Assert.Equal(write, r.StartOffset);           // after the edge: the edge
            }
        }

        [Fact]
        public void AnyChunkingProducesTheSameStoredStreamAndKeepsPacketsAligned()
        {
            var reference = TsFeed.Packets(P, 0, 3000);
            foreach (int maxChunk in new[] { 1, 7, 187, 189, 1000, 70000 })
            {
                using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 188 * 50))
                {
                    TsFeed.Feed(b, P, 0, 3000, seed: maxChunk, maxChunk: maxChunk);
                    Assert.Equal(reference.Length, b.WriteOffset);
                    var got = new byte[reference.Length];
                    Assert.Equal(got.Length, b.Peek(0, got, 0, got.Length));
                    Assert.Equal(reference, got);
                }
            }
        }

        [Fact]
        public void DiscontinuityDropsTheHalfPacketAndTheStoredStreamStaysWhole()
        {
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 188 * 50))
            {
                var a = TsFeed.Packets(P, 0, 10);
                b.Append(a, 0, 10 * 188 - 100);                                // the last packet arrives cut...
                b.Discontinuity();                                             // ...and the stream is cut: that packet is discarded
                var c = TsFeed.Packets(P, 500, 510);
                b.Append(c, 0, c.Length);
                Assert.Equal(19 * 188, b.WriteOffset);                                 // 9 whole packets (the 10th, cut, is thrown away) + 10 of the new stretch
                var got = new byte[19 * 188];
                b.Peek(0, got, 0, got.Length);
                var nums = TsFeed.VideoNumbers(got);
                Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 500, 501, 502, 503, 504, 505, 506, 507, 508, 509 }, nums.ToArray());
                Assert.True(b.DroppedBytes >= 88);
            }
        }

        [Fact]
        public void GarbageIsNotStoredAndTheStreamResynchronises()
        {
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 188 * 50))
            {
                var junk = new byte[1500];
                new Random(3).NextBytes(junk);
                for (int i = 0; i < junk.Length; i++) if (junk[i] == 0x47) junk[i] = 0x48;
                b.Append(junk, 0, junk.Length);
                Assert.Equal(0, b.WriteOffset);                                // without sync nothing is stored
                var good = TsFeed.Packets(P, 0, 40);
                b.Append(good, 0, 301);                                        // and then a valid stream arrives, cut in the middle of a packet
                b.Append(good, 301, good.Length - 301);
                Assert.True(b.WriteOffset > 0 && b.WriteOffset % 188 == 0);
                var got = new byte[b.WriteOffset];
                b.Peek(0, got, 0, got.Length);
                for (int i = 0; i < got.Length; i += 188) Assert.Equal(0x47, got[i]);
                // a valid chunk in the middle of the stream that loses sync midway: it recovers at the next one
                var mid = TsFeed.Packets(P, 100, 130);
                mid[188 * 10] = 0x00;                                          // packet 10 without sync
                long before = b.WriteOffset;
                b.Append(mid, 0, mid.Length);
                Assert.True(b.LostSyncCount >= 1);
                Assert.True(b.WriteOffset > before && b.WriteOffset % 188 == 0);
            }
        }

        [Fact]
        public void ARawNonTsStreamIsStoredAsIsWithoutAnIndex()
        {
            using (var b = TsFeed.NewBuffer(1L * 1024 * 1024, 4096, bitrate: 100000))
            {
                b.SetRawMode();
                var raw = SimTs.Raw(0, 300000);
                for (int i = 0; i < raw.Length; i += 7001) b.Append(raw, i, Math.Min(7001, raw.Length - i));
                Assert.Equal(300000, b.WriteOffset);
                Assert.Equal(0, b.IndexSamples);
                using (var r = b.OpenReader(TimeSpan.FromSeconds(1)))
                {
                    var data = TsFeed.Drain(r);
                    Assert.Equal(0, r.PrefixLength);
                    Assert.Equal(raw.Skip((int)r.StartOffset).ToArray(), data);
                }
            }
        }

        [Fact]
        public void AReaderWhoseCursorIsEvictedEndsCleanlyAndKnowsIt()
        {
            using (var b = TsFeed.NewBuffer(20 * 188 * 100, 188 * 100))
            {
                TsFeed.Feed(b, P, 0, 3000);
                var r = b.OpenReader(TimeSpan.FromSeconds(1));
                var buf = new byte[1000];
                Assert.True(r.TryRead(buf, 0, buf.Length) > 0);
                Assert.False(r.Evicted);
                TsFeed.Feed(b, P, 3000, 20000);                                // the reader does not read while the buffer wraps around many times
                Assert.True(b.Snapshot().ReaderEvicted);                       // it is known without the reader having tried to read
                Assert.Equal(-1, r.TryRead(buf, 0, buf.Length));
                Assert.True(r.Evicted);
                Assert.Equal(0, r.ReadAsync(buf, 0, buf.Length, CancellationToken.None).Result);
                r.Dispose();
                Assert.Equal(0, b.OpenReaders);
            }
        }

        [Fact]
        public async Task AReaderAtTheEdgeWaitsForDataAndEndsOnComplete()
        {
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 188 * 100))
            {
                TsFeed.Feed(b, P, 0, 500);
                var r = b.OpenReader(null);
                var all = new List<byte>();
                var buf = new byte[4000];
                var task = Task.Run(async () =>
                {
                    while (true)
                    {
                        int n = await r.ReadAsync(buf, 0, buf.Length, CancellationToken.None);
                        if (n == 0) break;
                        lock (all) all.AddRange(buf.Take(n));
                    }
                });
                await Task.Delay(100);
                Assert.False(task.IsCompleted);                                // waiting
                long before; lock (all) before = all.Count;
                TsFeed.Feed(b, P, 500, 700);
                await Task.Delay(100);
                lock (all) Assert.True(all.Count > before);                    // it wakes up with each piece of data
                b.Complete();
                await task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, all.Count % 188);
                r.Dispose();
            }
        }

        [Fact]
        public async Task ReadAsyncIsCancellable()
        {
            using (var b = TsFeed.NewBuffer(1024 * 1024, 188 * 100))
            using (var r = b.OpenReader(null))
            using (var cts = new CancellationTokenSource(150))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.ReadAsync(new byte[100], 0, 100, cts.Token));
            }
        }

        [Fact]
        public void ConcurrentReadersSeeAnUninterruptedStreamUntilTheyAreEvicted()
        {
            using (var b = TsFeed.NewBuffer(120 * 188 * 100, 188 * 100))
            {
                long total = 60000;
                var errors = new List<string>();
                var done = 0;
                Action reader = () =>
                {
                    try
                    {
                        // waits for something to exist and opens; reads at its own pace (the slow ones end up evicted, the fast ones reach the end)
                        while (b.WriteOffset < 188 * 400) Thread.Sleep(1);
                        var r = b.OpenReader(TimeSpan.FromMilliseconds(500));
                        var buf = new byte[3001 + (Environment.CurrentManagedThreadId % 5) * 1000];
                        long last = -1;
                        var carry = new List<byte>();
                        while (true)
                        {
                            int n = r.TryRead(buf, 0, buf.Length);
                            if (n < 0) { if (!r.Evicted && Volatile.Read(ref done) == 0) lock (errors) errors.Add("unexpected end"); break; }
                            if (n == 0) { if (Volatile.Read(ref done) != 0) break; Thread.Sleep(0); continue; }
                            carry.AddRange(buf.Take(n));
                            int whole = carry.Count / 188 * 188;
                            var arr = carry.Take(whole).ToArray();
                            carry.RemoveRange(0, whole);
                            foreach (var v in TsFeed.VideoNumbers(arr))
                            {
                                if (last >= 0 && v != last + 1 + (SimTsProfile.IsPsi(last + 1) ? 1 : 0)) lock (errors) errors.Add("salto " + last + " -> " + v);
                                last = v;
                            }
                        }
                        r.Dispose();
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex.ToString()); }
                };
                var threads = Enumerable.Range(0, 4).Select(i => new Thread(() => reader())).ToList();
                foreach (var t in threads) t.Start();
                for (long n = 0; n < total; n += 100) TsFeed.Feed(b, P, n, n + 100, seed: (int)n);
                Volatile.Write(ref done, 1);
                foreach (var t in threads) Assert.True(t.Join(20000));
                Assert.Empty(errors);
                Assert.Equal(0, b.OpenReaders);
            }
        }

        [Fact]
        public void MemoryStaysFlatAfterManyLapsOfTheRing()
        {
            using (var b = TsFeed.NewBuffer(48 * 188 * 100, 188 * 100))
            {
                var chunk = TsFeed.Packets(P, 0, 200);
                for (int i = 0; i < 1000; i++) b.Append(chunk, 0, chunk.Length);       // warm up: the buffer fills and is reused
                long blocks = b.AllocatedBlocks;
                Assert.Equal(48, blocks);
                // The process memory is shared by the tests running in parallel: what THIS thread allocates is measured (the data goes to the
                // blocks already reserved; the only things allocated are the wake-up signals, about a hundred bytes per call, and without going through the large-object heap)
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 20000; i++) b.Append(chunk, 0, chunk.Length);      // ~750 MB of stream = 1000 laps of the ring
                long allocated = GC.GetAllocatedBytesForCurrentThread() - a0;
                Assert.Equal(blocks, b.AllocatedBlocks);                               // not a single new block
                Assert.True(b.RecycledBlocks > 5000);
                Assert.True(allocated < 20000L * 400, "asignado " + allocated + " B en 750 MB de flujo");
                Assert.True(b.IndexSamples < 2000 && b.IndexAccessPoints < 2000, "indice " + b.IndexSamples + "/" + b.IndexAccessPoints);
            }
        }

        [Theory]
        [InlineData(4, 8)]
        [InlineData(8, 4)]
        [InlineData(12, 6)]
        [InlineData(16, 4)]
        [InlineData(16, 6)]
        [InlineData(16, 8)]
        public void WindowInSecondsFollowsBudgetAndBitrate(int budgetMiB, int mbit)
        {
            // (the formula is linear in the budget: the figures for 32, 64 and 128 MiB were measured with those budgets in a separate run;
            // here it is checked with small budgets so as not to load the memory of the tests running in parallel)
            // window (s) = budget / bitrate - 10 s of live delay; with 4, 6 and 8 Mbit/s (500, 750 and 1000 kB/s)
            int bytesPerSec = mbit * 1000000 / 8;
            var p = TsFeed.Profile(bitrate: bytesPerSec);
            long budget = budgetMiB * 1024L * 1024;
            using (var b = TsFeed.NewBuffer(budget, 256 * 1024, bitrate: bytesPerSec))
            {
                long packets = p.PacketAt(budget * 2.0 / bytesPerSec);               // double what fits
                for (long n = 0; n < packets; n += 5000) TsFeed.Feed(b, p, n, Math.Min(packets, n + 5000), seed: (int)n, maxChunk: 60000);
                var s = b.Snapshot();
                double expected = Math.Max(0, (double)budget / bytesPerSec - 10);              // with less than 10 s stored there is nothing behind the live point
                Assert.InRange(s.Window.TotalSeconds, expected - 1.5, expected + 0.2);   // the newest block is half full: up to ~0.5 s less, plus the eviction by blocks
                Assert.True(b.WriteOffset - b.OldestOffset <= budget);
                Assert.Equal(b.MaxBlocks, b.AllocatedBlocks);
                Assert.True(b.CapacityBytes <= budget);
                Console.WriteLine("budget " + budgetMiB + " MiB at " + mbit + " Mbit/s: window " + s.Window.TotalSeconds.ToString("0.0") + " s (stored " + s.Buffered.TotalSeconds.ToString("0.0") + " s)");
            }
        }

        [Fact]
        public void AHostileStreamCannotInflateTheIndexBeyondALittleOverTheBudget()
        {
            // all packets with PCR, random_access and video PUSI on the PCR PID: the index stays bounded by bytes, not by packets
            using (var b = TsFeed.NewBuffer(64 * 188 * 100, 188 * 100))
            {
                var pkt = new byte[188];
                long pcr = 0;
                var rnd = new Random(9);
                for (int n = 0; n < 200000; n++)
                {
                    for (int i = 0; i < 188; i++) pkt[i] = 0xff;
                    pkt[0] = 0x47; pkt[1] = 0x41; pkt[2] = 0x00; pkt[3] = 0x30;
                    pkt[4] = 7; pkt[5] = 0xD0;                                    // discontinuity + random_access + PCR
                    pcr += rnd.Next(1, 27000000 * 3);                              // jumps of up to 3 s between PCRs
                    long bas = pcr / 300, ext = pcr % 300;
                    pkt[6] = (byte)(bas >> 25); pkt[7] = (byte)(bas >> 17); pkt[8] = (byte)(bas >> 9); pkt[9] = (byte)(bas >> 1);
                    pkt[10] = (byte)(((bas & 1) << 7) | 0x7e | (ext >> 8)); pkt[11] = (byte)ext;
                    b.Append(pkt, 0, 188);
                }
                Assert.True(b.WriteOffset - b.OldestOffset <= b.CapacityBytes);
                Assert.True(b.IndexSamples <= b.CapacityBytes / (10 * 188) + 2, "muestras " + b.IndexSamples);
                Assert.True(b.IndexAccessPoints <= b.CapacityBytes / 4096 + 2, "accesos " + b.IndexAccessPoints);
            }
        }

        [Fact]
        public void LastPatAndPmtAreTheOnesInFrontOfLateReaders()
        {
            using (var b = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
            {
                TsFeed.Feed(b, P, 0, PacketsFor(30));
                Assert.True(b.HasPsi);
                Assert.Equal(0, b.IndexForTests.PmtPid - SimTs.PmtPid);
                Assert.Equal(SimTs.VideoPid, b.IndexForTests.PcrPid);
                using (var r = b.OpenReader(TimeSpan.FromSeconds(3)))
                {
                    var data = TsFeed.Drain(r, 188 * 2);
                    Assert.Equal(188 * 2, data.Length);
                }
                // a reader that starts at the first stored byte does not need prepended data (the stream itself brings it)
                using (var b2 = TsFeed.NewBuffer(64L * 1024 * 1024, 256 * 1024))
                {
                    TsFeed.Feed(b2, P, 0, 1000);
                    using (var r = b2.OpenReader(TimeSpan.FromHours(1))) Assert.Equal(0, r.PrefixLength);
                }
            }
        }

        [Fact]
        public void DisposeReleasesTheBlocksAndEndsReaders()
        {
            var b = TsFeed.NewBuffer(1024 * 1024, 188 * 100);
            TsFeed.Feed(b, P, 0, 3000);
            var r = b.OpenReader(null);
            b.Dispose();
            Assert.Equal(-1, r.TryRead(new byte[100], 0, 100));
            b.Append(new byte[188], 0, 188);                                   // it does not blow up nor store anything
            Assert.Throws<ObjectDisposedException>(() => b.OpenReader(null));
        }
    }
}
