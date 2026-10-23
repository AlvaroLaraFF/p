using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AceEngine.App;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.App
{
    /// <summary>A UDP socket on the loopback that collects what the log sends.</summary>
    internal sealed class UdpSink : IDisposable
    {
        private readonly UdpClient _rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        public int Port { get { return ((IPEndPoint)_rx.Client.LocalEndPoint).Port; } }

        public List<string> Receive(int count, int timeoutMs = 3000)
        {
            var res = new List<string>();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (res.Count < count && DateTime.UtcNow < deadline)
            {
                var t = _rx.ReceiveAsync();
                if (!t.Wait(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds))) break;
                res.Add(Encoding.UTF8.GetString(t.Result.Buffer));
            }
            return res;
        }

        public void Dispose() { _rx.Dispose(); }
    }

    public class UdpLogWriterTests
    {
        [Fact]
        public void SendsOneDatagramPerLineWithTimeLevelAndTag()
        {
            using (var sink = new UdpSink())
            using (var w = new UdpLogWriter("127.0.0.1", sink.Port))
            {
                w.Write("W", "net", "hello");
                var got = sink.Receive(1);
                Assert.Single(got);
                Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} W \[net\] hello$", got[0]);
            }
        }

        [Fact]
        public void LinesWithBreaksAreSplitAndCarriageReturnsDropped()
        {
            using (var sink = new UdpSink())
            using (var w = new UdpLogWriter("127.0.0.1", sink.Port))
            {
                w.Write("I", "t", "one\r\ntwo\nthree");
                var got = sink.Receive(3);
                Assert.Equal(3, got.Count);
                Assert.EndsWith("[t] one", got[0]); Assert.EndsWith("[t] two", got[1]); Assert.EndsWith("[t] three", got[2]);
            }
        }

        [Fact]
        public void ALongLineIsSplitIntoDatagramsThatFitAndRebuildsExactly()
        {
            using (var sink = new UdpSink())
            using (var w = new UdpLogWriter("127.0.0.1", sink.Port))
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 700; i++) sb.Append(i % 10);
                string msg = sb.ToString();                                  // 700 chars
                string longMsg = msg + msg + msg;                            // 2100 chars: needs 2 datagrams of at most 1200 bytes
                w.Write("I", "big", longMsg);
                var got = sink.Receive(2);
                Assert.Equal(2, got.Count);
                var rebuilt = new StringBuilder();
                foreach (var d in got)
                {
                    Assert.True(Encoding.UTF8.GetByteCount(d) <= UdpLogWriter.MaxDatagram);
                    var m = Regex.Match(d, @"^\d\d:\d\d:\d\d\.\d{3} I \[big\] (.*)$");
                    Assert.True(m.Success);
                    rebuilt.Append(m.Groups[1].Value);
                }
                Assert.Equal(longMsg, rebuilt.ToString());
            }
        }

        [Fact]
        public void AnEmptyOrNullMessageStillSendsTheLine()
        {
            using (var sink = new UdpSink())
            using (var w = new UdpLogWriter("127.0.0.1", sink.Port))
            {
                w.Write("I", "e", null);
                var got = sink.Receive(1);
                Assert.Single(got);
                Assert.EndsWith("[e] ", got[0]);
            }
        }

        [Fact]
        public void ANameThatDoesNotResolveThrows()
        {
            Assert.ThrowsAny<Exception>(() => new UdpLogWriter("nonexistent-host.invalid", 9999));
        }

        [Fact]
        public void WritingAfterDisposeNeverThrows()
        {
            var w = new UdpLogWriter("127.0.0.1", 9);
            w.Dispose();
            w.Write("E", "x", "after dispose");
        }
    }

    /// <summary>UdpLog is static: these tests do not run in parallel with each other.</summary>
    [Collection("UdpLog")]
    public class UdpLogTests
    {
        private static void Reset() { UdpLog.Configure(false, "", 0); UdpLog.LocalSink = null; }

        [Fact]
        public void TheLocalSinkReceivesEveryLineEvenWithUdpOff()
        {
            Reset();
            var seen = new List<string>();
            UdpLog.LocalSink = (lvl, tag, msg) => seen.Add(lvl + "|" + tag + "|" + msg);
            try
            {
                Assert.False(UdpLog.UdpEnabled);
                UdpLog.Info("a", "one"); UdpLog.Warn("b", "two"); UdpLog.Error("c", "three"); UdpLog.Info("d", null);
                Assert.Equal(new[] { "I|a|one", "W|b|two", "E|c|three", "I|d|" }, seen.ToArray());
            }
            finally { Reset(); }
        }

        [Fact]
        public void EngineSinkMapsLevelsToLetters()
        {
            Reset();
            var seen = new List<string>();
            UdpLog.LocalSink = (lvl, tag, msg) => seen.Add(lvl);
            try
            {
                UdpLog.Engine(LogLevel.Debug, "t", "m"); UdpLog.Engine(LogLevel.Info, "t", "m"); UdpLog.Engine(LogLevel.Warn, "t", "m"); UdpLog.Engine(LogLevel.Error, "t", "m");
                Assert.Equal(new[] { "D", "I", "W", "E" }, seen.ToArray());
            }
            finally { Reset(); }
        }

        [Fact]
        public void ConfiguredUdpSendsAndDisablingStopsIt()
        {
            Reset();
            using (var sink = new UdpSink())
            {
                try
                {
                    UdpLog.Configure(true, "127.0.0.1", sink.Port);
                    Assert.True(UdpLog.UdpEnabled);
                    UdpLog.Info("net", "over udp");
                    var got = sink.Receive(1);
                    Assert.Single(got);
                    Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} I \[net\] over udp$", got[0]);
                    UdpLog.Configure(false, "", 0);
                    Assert.False(UdpLog.UdpEnabled);
                    UdpLog.Info("net", "not sent");
                    Assert.Empty(sink.Receive(1, 300));
                }
                finally { Reset(); }
            }
        }

        [Fact]
        public void AHostThatDoesNotResolveLeavesUdpOffAndNothingBreaks()
        {
            Reset();
            UdpLog.Configure(true, "nonexistent-host.invalid", 9999);
            Assert.False(UdpLog.UdpEnabled);
            UdpLog.Info("x", "still fine");
        }

        [Fact]
        public void AFailingLocalSinkNeverBreaksTheLog()
        {
            Reset();
            UdpLog.LocalSink = (l, t, m) => { throw new InvalidOperationException("sink down"); };
            try { UdpLog.Warn("x", "survives"); }
            finally { Reset(); }
        }
    }
}
