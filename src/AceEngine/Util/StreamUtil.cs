using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AceEngine.Util
{
    public static class StreamUtil
    {
        private const int Step = 64 * 1024;

        /// <summary>
        /// Reads exactly len bytes. Up to 64 KiB it allocates the buffer at once; above that it grows as data arrives, so a peer that
        /// announces a huge message and never sends it consumes no memory (the length comes from the peer and cannot be trusted).
        /// </summary>
        public static async Task<byte[]> ReadMessageAsync(Stream s, int len, CancellationToken ct)
        {
            if (len < 0) throw new InvalidDataException("negative length");
            if (len <= Step)
            {
                var b = new byte[len];
                await ReadExactAsync(s, b, len, ct).ConfigureAwait(false);
                return b;
            }
            var buf = new byte[Step];
            int got = 0;
            while (got < len)
            {
                if (got == buf.Length)
                {
                    var bigger = new byte[(int)Math.Min((long)len, (long)buf.Length * 2)];
                    Buffer.BlockCopy(buf, 0, bigger, 0, got);
                    buf = bigger;
                }
                int n = await s.ReadAsync(buf, got, Math.Min(buf.Length - got, len - got), ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("the peer closed the connection");
                got += n;
            }
            return buf;
        }

        public static async Task ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
        {
            int got = 0;
            while (got < count)
            {
                int n = await s.ReadAsync(buf, got, count - got, ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("the peer closed the connection");
                got += n;
            }
        }
    }
}
