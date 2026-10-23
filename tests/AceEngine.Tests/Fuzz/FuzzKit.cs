using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Xunit;

namespace AceEngine.Tests.Fuzz
{
    /// <summary>
    /// Fuzzing tests: random inputs and mutated valid inputs against every reader of network data. Rules: never an exception other than
    /// the reader's documented one (an IndexOutOfRange or a NullReference is a failure), never more than 2 s per input and memory cannot grow without limit.
    /// Everything is deterministic (fixed seed), so a failure can be reproduced; the message carries the input in hexadecimal.
    /// </summary>
    internal static class FuzzKit
    {
        /// <summary>
        /// Memory growth margin for the fuzzing tests. GC.GetTotalMemory measures the WHOLE process, and with more than a thousand tests running in parallel
        /// (probe, live with memory, UPnP...) others reserve tens of MiB at once: the margin detects unbounded growth (hundreds of MiB), not noise.
        /// </summary>
        public const long MemoryBudget = 256L * 1024 * 1024;

        public static int Iterations
        {
            get { int n; return int.TryParse(Environment.GetEnvironmentVariable("ACE_FUZZ_ITER"), out n) && n > 0 ? n : 6000; }
        }

        public static byte[] Random(Random r, int maxLen)
        {
            int len = r.Next(0, maxLen + 1);
            var b = new byte[len];
            r.NextBytes(b);
            return b;
        }

        /// <summary>Mutation of a valid input: byte and bit changes, truncations, insertions, duplicates, huge lengths and splices.</summary>
        public static byte[] Mutate(Random r, byte[] seed, IList<byte[]> pool)
        {
            var l = new List<byte>(seed);
            int ops = r.Next(1, 6);
            for (int i = 0; i < ops; i++)
            {
                switch (r.Next(0, 8))
                {
                    case 0: if (l.Count > 0) l[r.Next(l.Count)] = (byte)r.Next(256); break;
                    case 1: if (l.Count > 0) l[r.Next(l.Count)] ^= (byte)(1 << r.Next(8)); break;
                    case 2: if (l.Count > 0) { int from = r.Next(l.Count); l.RemoveRange(from, Math.Min(l.Count - from, r.Next(1, 20))); } break;
                    case 3: l.Insert(r.Next(l.Count + 1), (byte)r.Next(256)); break;
                    case 4: if (l.Count > 0) { int keep = r.Next(l.Count); l.RemoveRange(keep, l.Count - keep); } break;      // truncate
                    case 5:
                        {
                            // huge numbers (lengths and integers)
                            string[] nums = { "99999999999999999999", "2147483647", "4294967295", "-1", "0", "65536", "999999999" };
                            l.InsertRange(r.Next(l.Count + 1), Encoding.ASCII.GetBytes(nums[r.Next(nums.Length)]));
                            break;
                        }
                    case 6: if (l.Count > 0) { int from = r.Next(l.Count); int n = Math.Min(l.Count - from, r.Next(1, 64)); l.InsertRange(r.Next(l.Count + 1), l.GetRange(from, n)); } break;
                    case 7:
                        if (pool.Count > 0)
                        {
                            var o = pool[r.Next(pool.Count)];
                            if (o.Length > 0) { int from = r.Next(o.Length); int n = Math.Min(o.Length - from, r.Next(1, 64)); l.InsertRange(r.Next(l.Count + 1), o.Skip(from).Take(n)); }
                        }
                        break;
                }
                if (l.Count > 1 << 20) l.RemoveRange(1 << 20, l.Count - (1 << 20));
            }
            return l.ToArray();
        }

        public static IEnumerable<byte[]> Inputs(IList<byte[]> seeds, int seed, int randomMaxLen, int count)
        {
            var r = new Random(seed);
            foreach (var s in seeds) yield return s;
            for (int i = 0; i < count; i++)
            {
                if (seeds.Count == 0 || r.Next(4) == 0) yield return Random(r, randomMaxLen);
                else yield return Mutate(r, seeds[r.Next(seeds.Count)], seeds);
            }
        }

        public static string Hex(byte[] b, int max = 400)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(b.Length, max); i++) sb.Append(b[i].ToString("x2"));
            return sb.ToString() + (b.Length > max ? "...(" + b.Length + " bytes)" : "");
        }

        /// <summary>Runs the target with all the inputs. 'allowed' are the documented exceptions; any other is a failure.</summary>
        public static void Run(string name, IEnumerable<byte[]> inputs, Action<byte[]> target, params Type[] allowed)
        {
            GC.Collect();
            long before = GC.GetTotalMemory(true);
            long n = 0, rejected = 0;
            var worst = TimeSpan.Zero;
            foreach (var input in inputs)
            {
                n++;
                var sw = Stopwatch.StartNew();
                try { target(input); }
                catch (Exception ex)
                {
                    rejected++;
                    if (!allowed.Any(t => t.IsInstanceOfType(ex)))
                        Assert.Fail(name + ": undocumented exception " + ex.GetType().Name + ": " + ex.Message + "\n input: " + Hex(input) + "\n" + ex.StackTrace);
                }
                sw.Stop();
                if (sw.Elapsed > worst) worst = sw.Elapsed;
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), name + ": took " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s with input " + Hex(input));
            }
            GC.Collect();
            long after = GC.GetTotalMemory(true);
            Assert.True(after - before < MemoryBudget, name + ": memory grew " + (after - before) / 1024 / 1024 + " MiB");
            Console.WriteLine("[fuzz] " + name + ": " + n + " inputs (" + rejected + " rejected), worst case " + worst.TotalMilliseconds.ToString("0") + " ms, memory +" + (after - before) / 1024 + " KiB");
        }
    }
}
