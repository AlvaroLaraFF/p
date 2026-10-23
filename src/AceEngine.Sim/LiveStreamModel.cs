using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AceEngine.Bencode;
using AceEngine.Live;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Sim
{
    /// <summary>Parameters of the simulated live stream (createlivestream; piece_size, chunk_length, bitrate...).</summary>
    public sealed class LiveSimOptions
    {
        public string Name = "Canal simulado";
        public int PieceLength = 65536;
        public int ChunkLength = 16384;
        /// <summary>Bytes per second of the stream (the descriptor's bitrate).</summary>
        public int Bitrate = 262144;
        /// <summary>Speed of the source clock relative to real time (10 = 10 s of live per real second).</summary>
        public double TimeScale = 1.0;
        public long FirstIndex = 1000000037;
        public int RsaBits = 768;
        public int RsaSeed = -1;
        /// <summary>Garbage at the start of the stream (the sender cuts the TS wherever it falls): the first piece does not start aligned.</summary>
        public int StartMisalignment = 77;
        public int Version = 3022200;
        /// <summary>Pieces that are kept (window announced in the BITFIELD).</summary>
        public int WindowPieces = 300;
        public string Trackers = "";
        public List<string> MetaTrackers = new List<string>();
        public List<string> StartupNodes = new List<string>();
        public bool Private;
        /// <summary>The live stream has already been broadcasting this long when the model is created (a real channel has been on for hours).</summary>
        public double StartOffsetSeconds = 60;
        /// <summary>Signs the pieces with a key different from the descriptor's (test of the tolerant mode: our reading of the format would be wrong).</summary>
        public bool SignWithWrongKey = false;
        /// <summary>The content is not TS (test of the unaligned delivery mode).</summary>
        public bool NonTsContent = false;
        /// <summary>TS stream with a timeline (PCR, GOP with random access and IDR) instead of SimTs's flat one: tests of live with memory. Its Bitrate must match Bitrate.</summary>
        public SimTsProfile MediaProfile = null;
        /// <summary>Content of the stream by byte offset (offset, count), instead of the generators above: tests that need real H.264/AAC elementary streams (HLS). Its bytes per second must match Bitrate.</summary>
        public Func<long, int, byte[]> ContentProvider = null;
    }

    /// <summary>
    /// Simulated live stream shared by the fake peers: clock, verifiable MPEG-TS content, RSA-signed pieces (768 bits, e=3)
    /// and the transport file (.acelive, encrypted AceStreamTransport v2 with the IV confirmed on the real network).
    /// </summary>
    public sealed class LiveStreamModel
    {
        public readonly LiveSimOptions Options;
        public readonly RsaSigner Signer;
        private RsaSigner _wrongSigner;
        public byte[] TransportBytes { get; private set; }
        public TransportFile Transport { get; private set; }
        public LiveDescriptor Descriptor { get; private set; }
        public readonly int PieceLength, ChunkLength, ContentSize, SignSize, Chunks;
        public readonly double PiecesPerSecond;
        public readonly double BaseUnixTime;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _startOffsetSeconds;
        private readonly object _gate = new object();
        private readonly Dictionary<long, byte[]> _cache = new Dictionary<long, byte[]>();
        public int SignaturesComputed;

        public LiveStreamModel(LiveSimOptions options, double startOffsetSeconds = 0)
        {
            Options = options;
            _startOffsetSeconds = options.StartOffsetSeconds + startOffsetSeconds;
            Signer = RsaSigner.Generate(options.RsaBits, options.RsaSeed);
            SignSize = Signer.SignSize;
            PieceLength = options.PieceLength;
            ChunkLength = options.ChunkLength;
            ContentSize = PieceLength - SignSize;
            Chunks = (PieceLength + ChunkLength - 1) / ChunkLength;
            PiecesPerSecond = (double)options.Bitrate / PieceLength;
            BaseUnixTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            var built = BuildTransport(options);
            TransportBytes = built.Item1;
            Transport = built.Item2;
            Descriptor = Transport.Live;
        }

        private Tuple<byte[], TransportFile> BuildTransport(LiveSimOptions options)
        {
            var d = new BDict();
            d.Add("authmethod", BValue.Str("RSA"));
            d.Add("bitrate", BValue.Int(options.Bitrate));
            d.Add("chunk_length", BValue.Int(options.ChunkLength));
            d.Add("name", BValue.Str(options.Name));
            d.Add("piece_length", BValue.Int(options.PieceLength));
            d.Add("pubkey", BValue.Bytes(Signer.PublicKeySpki()));
            if (!string.IsNullOrEmpty(options.Trackers)) d.Add("trackers", BValue.List(BValue.Str(options.Trackers)));
            d.Add("allow_public_trackers", BValue.Int(1));
            if (options.MetaTrackers.Count > 0) d.Add("meta_trackers", BValue.List(options.MetaTrackers.Select(x => (BValue)BValue.Str(x)).ToArray()));
            if (options.StartupNodes.Count > 0) d.Add("startup_nodes", BValue.List(options.StartupNodes.Select(x => (BValue)BValue.Str(x)).ToArray()));
            if (options.Private) d.Add("private", BValue.Int(1));
            var bytes = TransportLoader.Build(d, TransportFormat.AceStreamTransportV2, IvMode.Sha1OfMd5Key);
            return Tuple.Create(bytes, TransportLoader.Load(bytes));
        }

        /// <summary>
        /// Changes the descriptor's discovery fields (trackers, meta_trackers, startup_nodes). The infohash does not change: those fields are not
        /// in hash_fields. It has to be done before opening the link.
        /// </summary>
        public void SetDiscovery(string trackers, IEnumerable<string> metaTrackers, IEnumerable<string> startupNodes)
        {
            Options.Trackers = trackers ?? "";
            Options.MetaTrackers = (metaTrackers ?? new string[0]).ToList();
            Options.StartupNodes = (startupNodes ?? new string[0]).ToList();
            var built = BuildTransport(Options);
            TransportBytes = built.Item1;
            Transport = built.Item2;
            Descriptor = Transport.Live;
        }

        public byte[] InfoHash { get { return Descriptor.InfoHash; } }

        /// <summary>Seconds of live elapsed (with the speed factor).</summary>
        public double Elapsed { get { return _startOffsetSeconds + _clock.Elapsed.TotalSeconds * Options.TimeScale; } }

        /// <summary>Last piece produced (sender's index).</summary>
        public long Head
        {
            get { return Options.FirstIndex + (long)Math.Floor(Elapsed * PiecesPerSecond); }
        }

        public long FirstIndex { get { return Options.FirstIndex; } }

        /// <summary>Sender restart without a .restart file: the indices go back and the stream starts over.</summary>
        public void Restart(long newFirstIndex)
        {
            lock (_gate)
            {
                Options.FirstIndex = newFirstIndex;
                _cache.Clear();
                _startOffsetSeconds = 0;
                _clock.Restart();
            }
        }

        public double Timestamp(long index)
        {
            return BaseUnixTime + (index - Options.FirstIndex) / PiecesPerSecond;
        }

        /// <summary>Complete piece = content (PieceLength - SignSize) || RSA PKCS#1 v1.5 SHA-1 signature of the content.</summary>
        public byte[] GetPiece(long index)
        {
            lock (_gate)
            {
                byte[] p;
                if (_cache.TryGetValue(index, out p)) return p;
            }
            long contentOffset = (index - Options.FirstIndex) * ContentSize;
            var content = Options.ContentProvider != null ? Options.ContentProvider(contentOffset, ContentSize)
                : Options.NonTsContent ? SimTs.Raw(contentOffset, ContentSize)
                : Options.MediaProfile != null ? Options.MediaProfile.Content(Options.StartMisalignment, contentOffset, ContentSize)
                : SimTs.Content(Options.StartMisalignment, contentOffset, ContentSize);
            var signer = Signer;
            if (Options.SignWithWrongKey) { if (_wrongSigner == null) _wrongSigner = RsaSigner.Generate(Options.RsaBits, Options.RsaSeed + 1000); signer = _wrongSigner; }
            var sig = signer.Sign(content);
            var piece = new byte[PieceLength];
            Buffer.BlockCopy(content, 0, piece, 0, ContentSize);
            Buffer.BlockCopy(sig, 0, piece, ContentSize, SignSize);
            lock (_gate)
            {
                SignaturesComputed++;
                _cache[index] = piece;
                if (_cache.Count > Options.WindowPieces * 2)
                    foreach (var k in _cache.Keys.Where(k => k < index - Options.WindowPieces * 2).ToList()) _cache.Remove(k);
            }
            return piece;
        }
    }
}
