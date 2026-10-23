using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AceEngine.Util;

namespace AceEngine.App
{
    /// <summary>
    /// Sends log lines over UDP, one datagram per line: "HH:mm:ss.fff L [tag] text". A long line, or one with line breaks, is split into several
    /// datagrams (each one fits in an unfragmented UDP packet). Received with tools/udplog.py. Never throws.
    /// </summary>
    public sealed class UdpLogWriter : IDisposable
    {
        /// <summary>Largest datagram, prefix included.</summary>
        public const int MaxDatagram = 1200;

        private readonly UdpClient _udp;
        private readonly IPEndPoint _target;

        /// <summary>Resolves the host (an IPv4 address or a name) and opens the socket. Throws if the host does not resolve to an IPv4 address.</summary>
        public UdpLogWriter(string host, int port)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                var addrs = Dns.GetHostAddresses(host);
                ip = null;
                foreach (var a in addrs) if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
                if (ip == null) throw new ArgumentException("no IPv4 address for " + host);
            }
            _target = new IPEndPoint(ip, port);
            _udp = new UdpClient();
        }

        public void Write(string level, string tag, string msg)
        {
            if (msg == null) msg = "";
            try
            {
                string prefix = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " [" + tag + "] ";
                foreach (var line in msg.Split('\n'))
                {
                    string l = line.TrimEnd('\r');
                    int pos = 0;
                    do
                    {
                        int take = Math.Min(l.Length - pos, MaxDatagram - prefix.Length);
                        if (take < 0) take = 0;
                        var b = Encoding.UTF8.GetBytes(prefix + l.Substring(pos, take));
                        _udp.Send(b, b.Length, _target);
                        pos += take;
                    } while (pos < l.Length);
                }
            }
            catch { }
        }

        public void Dispose() { try { _udp.Dispose(); } catch { } }
    }

    /// <summary>
    /// App log shared by every shell. dlog is blocked on the TV, so every line can also go over UDP to the development PC (destination configurable and can be
    /// disabled in AppConfig). LocalSink is the platform's own log (dlog on Tizen, logcat on Android): each line goes there first, whatever the UDP state.
    /// </summary>
    public static class UdpLog
    {
        private static readonly object Gate = new object();
        private static UdpLogWriter _writer;

        /// <summary>The platform's own log: (level letter "D"/"I"/"W"/"E", tag, text). Null = none. Set it before the first line.</summary>
        public static volatile Action<string, string, string> LocalSink;

        /// <summary>Turns the UDP destination on or off; any previous one is closed. If the host cannot be resolved, the UDP log stays off.</summary>
        public static void Configure(bool enabled, string host, int port)
        {
            lock (Gate)
            {
                var old = _writer; _writer = null;
                if (old != null) old.Dispose();
                if (!enabled) return;
                try { _writer = new UdpLogWriter(host, port); }
                catch { _writer = null; }
            }
        }

        /// <summary>true while lines are being sent over UDP.</summary>
        public static bool UdpEnabled { get { lock (Gate) return _writer != null; } }

        public static void Info(string tag, string msg) { Write("I", tag, msg); }
        public static void Warn(string tag, string msg) { Write("W", tag, msg); }
        public static void Error(string tag, string msg) { Write("E", tag, msg); }

        /// <summary>The engine's log sink (AceLog.Sink = UdpLog.Engine).</summary>
        public static void Engine(LogLevel lvl, string tag, string msg)
        {
            Write(lvl == LogLevel.Debug ? "D" : lvl == LogLevel.Info ? "I" : lvl == LogLevel.Warn ? "W" : "E", tag, msg);
        }

        private static void Write(string level, string tag, string msg)
        {
            if (msg == null) msg = "";
            try { var local = LocalSink; if (local != null) local(level, tag, msg); } catch { }
            UdpLogWriter w;
            lock (Gate) w = _writer;
            if (w != null) w.Write(level, tag, msg);
        }
    }
}
