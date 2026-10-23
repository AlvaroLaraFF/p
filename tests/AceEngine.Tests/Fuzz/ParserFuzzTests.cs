using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using AceEngine.Bencode;
using AceEngine.Links;
using AceEngine.Live;
using AceEngine.P2P;
using AceEngine.Probe;
using AceEngine.Resolve;
using AceEngine.Sources;
using AceEngine.Streaming;
using AceEngine.Transport;
using AceEngine.Util;
using Xunit;

namespace AceEngine.Tests.Fuzz
{
    public class BencodeFuzzTests
    {
        private static List<byte[]> Seeds()
        {
            var s = new List<byte[]>();
            s.Add(TestUtil.Ascii("d3:cow3:moo4:spaml1:a1:bei42ee"));
            s.Add(TestUtil.Ascii("d8:announce20:udp://t.example:6969/a4:infod6:lengthi1000e4:name3:abc12:piece lengthi16384e6:pieces20:01234567890123456789ee"));
            s.Add(TestUtil.Fixture("sintel.torrent"));
            foreach (var f in new[] { "2940120bf034db79a7f5849846ccea0255172eae", "38f7b2044e549df2039ff26cefa6f9a60c854d5e", "54a4a28882d3cae1ca19de5d4b022ebea40351f9" })
                s.Add(TransportLoader.Load(TestUtil.Fixture("real/" + f + ".acelive")).Payload);
            return s;
        }

        [Fact]
        public void RandomAndMutatedInputsOnlyRaiseBencodeException()
        {
            FuzzKit.Run("bencode", FuzzKit.Inputs(Seeds(), 1, 600, FuzzKit.Iterations * 3), b =>
            {
                var v = BencodeParser.Parse(b);
                BencodeWriter.Encode(v);
                if (v is BDict) { var d = (BDict)v; foreach (var k in d.Keys) d.Get(k); }
            }, typeof(BencodeException));
        }

        [Fact]
        public void HostileShapesAreRejectedQuicklyWithoutExhaustingMemoryOrStack()
        {
            var cases = new List<byte[]>();
            cases.Add(Enumerable.Repeat((byte)'l', 1000000).ToArray());                                     // nesting of a million
            cases.Add(Enumerable.Repeat((byte)'d', 1000000).ToArray());
            cases.Add(Encoding.ASCII.GetBytes("999999999999:abc"));                                         // string longer than the data
            cases.Add(Encoding.ASCII.GetBytes("4294967296:abc"));
            cases.Add(Encoding.ASCII.GetBytes("i" + new string('9', 5000) + "e"));
            cases.Add(Encoding.ASCII.GetBytes("l" + string.Concat(Enumerable.Repeat("i0e", 2000000)) + "e"));  // 2M integers: exceeds the element cap
            cases.Add(Encoding.ASCII.GetBytes("d" + string.Concat(Enumerable.Range(0, 300000).Select(i => "3:k" + (i % 10) + "i0e")) + "e"));
            FuzzKit.Run("bencode-hostile", cases, b => BencodeParser.Parse(b), typeof(BencodeException));
            // the element cap really protects
            Assert.Throws<BencodeException>(() => BencodeParser.Parse(cases[5]));
            // but a normal file with thousands of elements passes
            var ok = Encoding.ASCII.GetBytes("l" + string.Concat(Enumerable.Repeat("i0e", 50000)) + "e");
            Assert.Equal(50000, ((BList)BencodeParser.Parse(ok)).Count);
        }
    }

    public class TransportFuzzTests
    {
        private static List<byte[]> Seeds()
        {
            var s = new List<byte[]>();
            foreach (var f in new[] { "2940120bf034db79a7f5849846ccea0255172eae", "38f7b2044e549df2039ff26cefa6f9a60c854d5e", "54a4a28882d3cae1ca19de5d4b022ebea40351f9" })
                s.Add(TestUtil.Fixture("real/" + f + ".acelive"));
            s.Add(TestUtil.Fixture("sintel.torrent"));
            foreach (var n in new[] { "live_v2.acelive", "live_v1.acelive", "legacy_v3.acetorrent", "legacy_v2.acetorrent", "legacy_tslive.acetorrent", "multi_v2.acelive", "hls_v1.acelive", "plain.torrent", "live_v2_ivzero.acelive", "legacy_v3_ivsha1md5.acetorrent" })
            {
                try { s.Add(TestUtil.Fixture(n)); } catch (IOException) { }
            }
            // a file valid by format, generated
            var d = new BDict(); d.Add("name", BValue.Str("x")); d.Add("authmethod", BValue.Str("RSA")); d.Add("pubkey", BValue.Bytes(new byte[40])); d.Add("piece_length", BValue.Int(65536)); d.Add("bitrate", BValue.Int(100000));
            foreach (var fmt in new[] { TransportFormat.AceStreamTransportV1, TransportFormat.AceStreamTransportV2, TransportFormat.PlainBencode })
                s.Add(TransportLoader.Build(d, fmt, IvMode.Sha1OfMd5Key));
            return s;
        }

        [Fact]
        public void LoaderOnlyRaisesTransportException()
        {
            FuzzKit.Run("transport", FuzzKit.Inputs(Seeds(), 2, 900, FuzzKit.Iterations), b => TransportLoader.Load(b), typeof(TransportException));
        }

        [Fact]
        public void StrictLoaderOnlyRaisesTransportException()
        {
            var strict = new TransportLoadOptions { Strict = true };
            FuzzKit.Run("transport-strict", FuzzKit.Inputs(Seeds(), 3, 900, FuzzKit.Iterations), b => TransportLoader.Load(b, strict), typeof(TransportException));
        }

        [Fact]
        public void InfoHashVerifierNeverThrowsAndOnlyAcceptsTheInfoHashItWasAskedFor()
        {
            // inputs: real descriptors (2026-10-17) and the usual ones, mutated; each one is checked against the infohash of all the seeds
            var seeds = new List<byte[]>();
            foreach (var f in Directory.GetFiles(TestUtil.FixturePath("real/infohash"), "*.acelive")) seeds.Add(File.ReadAllBytes(f));
            seeds.AddRange(Seeds());
            var hashes = new List<byte[]>();
            foreach (var s in seeds)
            {
                try { var ih = TransportLoader.Load(s).InfoHash; if (ih != null) hashes.Add(ih); } catch (TransportException) { }
            }
            Assert.True(hashes.Count >= 12);
            int accepted = 0;
            FuzzKit.Run("infohash-verifier", FuzzKit.Inputs(seeds, 5, 900, Math.Max(500, FuzzKit.Iterations / 4)), b =>
            {
                foreach (var h in hashes)
                {
                    string why;
                    var tf = AceEngine.Resolve.InfoHashVerifier.Match(b, h, true, out why);
                    if (tf == null) continue;
                    accepted++;
                    Assert.Equal(h, tf.InfoHash);                  // the only thing that comes out is a transport whose infohash is the requested one
                }
            });
            Assert.True(accepted >= hashes.Count);                  // unmutated seeds are accepted
        }

        [Fact]
        public void MutatedDescriptorsThatStillLoadHaveSaneValues()
        {
            int loaded = 0;
            FuzzKit.Run("transport-valores", FuzzKit.Inputs(Seeds(), 4, 900, FuzzKit.Iterations), b =>
            {
                var tf = TransportLoader.Load(b, new TransportLoadOptions { Strict = false });
                loaded++;
                if (tf.Live != null)
                {
                    Assert.InRange(tf.Live.PieceLength, LiveDescriptor.MinPieceLength, LiveDescriptor.MaxPieceLength);
                    Assert.InRange(tf.Live.ChunkLength, 1024, tf.Live.PieceLength);
                    Assert.InRange(tf.Live.Bitrate, LiveDescriptor.MinBitrate, LiveDescriptor.MaxBitrate);
                    Assert.True(tf.Live.Trackers.Count <= LiveDescriptor.MaxListEntries);
                    Assert.True(tf.Live.PubKey.Length <= LiveDescriptor.MaxPubKeyLength);
                }
                if (tf.Torrent != null && !tf.Torrent.IsLive)
                {
                    Assert.InRange(tf.Torrent.PieceLength, TorrentMeta.MinPieceLength, TorrentMeta.MaxPieceLength);
                    Assert.True(tf.Torrent.TotalLength >= 0 && tf.Torrent.TotalLength <= TorrentMeta.MaxTotalLength);
                    foreach (var f in tf.Torrent.Files) { Assert.DoesNotContain("..", f.Path.Split('/')); Assert.DoesNotContain(f.Path, c => c < ' '); }
                }
            }, typeof(TransportException));
            Assert.True(loaded > 0);
        }

        [Fact]
        public void NestedQualitiesAreBounded()
        {
            // a multi-quality transport inside another, many times: without a cap the stack would overflow
            byte[] inner = TransportLoader.Build(MakeMulti(new byte[0]), TransportFormat.AceStreamTransportV2, IvMode.Sha1OfMd5Key);
            for (int i = 0; i < 400; i++) inner = TransportLoader.Build(MakeMulti(inner), TransportFormat.AceStreamTransportV2, IvMode.Sha1OfMd5Key);
            var tf = TransportLoader.Load(inner);                       // the stack does not overflow and nothing is thrown outside TransportException
            Assert.NotNull(tf);
        }

        private static BDict MakeMulti(byte[] data)
        {
            var q = new BDict(); q.Add("name", BValue.Str("q")); q.Add("data", BValue.Bytes(data));
            var d = new BDict(); d.Add("name", BValue.Str("m")); d.Add("qualities", BValue.List(q));
            return d;
        }

        [Fact]
        public void TooManyQualitiesAreRejected()
        {
            var qs = new BList();
            for (int i = 0; i < 100; i++) { var q = new BDict(); q.Add("name", BValue.Str("q" + i)); q.Add("data", BValue.Bytes(new byte[3])); qs.Add(q); }
            var d = new BDict(); d.Add("name", BValue.Str("m")); d.Add("qualities", qs);
            Assert.Throws<TransportException>(() => TransportLoader.Load(TransportLoader.Build(d, TransportFormat.AceStreamTransportV2, IvMode.Sha1OfMd5Key)));
        }
    }

    public class WireParserFuzzTests
    {
        private static List<byte[]> ExtSeeds()
        {
            var s = new List<byte[]>();
            s.Add(ExtensionMessages.BuildExtendedHandshake("ACEStream 2.0", 8621, true, true, 1234, new byte[] { 1, 2, 3, 4 }));
            s.Add(ExtensionMessages.BuildMetadataRequest(3));
            s.Add(ExtensionMessages.BuildAceFullMetadataRequest());
            s.Add(ExtensionMessages.BuildMetadataData(0, 500, new byte[500], 0, 500));
            s.Add(ExtensionMessages.BuildPex(new List<IPEndPoint> { new IPEndPoint(IPAddress.Parse("1.2.3.4"), 80), new IPEndPoint(IPAddress.Parse("5.6.7.8"), 6881) }));
            foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "real"), "extend_*.hex"))
            {
                try { s.Add(TestUtil.FromHex(File.ReadAllText(f).Trim())); } catch (FormatException) { }
            }
            return s;
        }

        [Fact]
        public void ExtensionMessageParsersNeverThrow()
        {
            var seeds = ExtSeeds();
            FuzzKit.Run("extension-messages", FuzzKit.Inputs(seeds, 5, 400, FuzzKit.Iterations * 2), b =>
            {
                ExtendedHandshakeInfo h; ExtensionMessages.TryParseExtendedHandshake(b, 0, b.Length, out h);
                MetadataMessage m; ExtensionMessages.TryParseMetadataMessage(b, 0, b.Length, out m);
                ExtensionMessages.TryParsePex(b, 0, b.Length, new List<IPEndPoint>());
                if (b.Length > 3) { ExtensionMessages.TryParseExtendedHandshake(b, 2, b.Length - 2, out h); ExtensionMessages.TryParseMetadataMessage(b, 2, b.Length - 2, out m); }
            });
        }

        [Fact]
        public void LiveWireParsersNeverThrow()
        {
            var framed = new List<byte[]>();
            var seeds = new List<byte[]>();
            framed.Add(LiveWire.Have(123456, 1700000000.5));
            framed.Add(LiveWire.Have(7, null));
            framed.Add(LiveWire.Bitfield(1000, 40, new byte[5], 1.5, 2.5));
            framed.Add(LiveWire.Bitfield(1000, 40, new byte[5], null, null));
            framed.Add(LiveWire.Request(LiveId.Request, 5, 3, false)); framed.Add(LiveWire.Request(LiveId.Request, 5, 3, true));
            framed.Add(LiveWire.Piece(5, 1.5, 3, true, new byte[100], 0, 100));
            framed.Add(LiveWire.Piece(5, 1.5, 3, false, new byte[100], 0, 100));
            framed.Add(LiveWire.Simple(30));
            var d = new BDict(); d.Add("a", BValue.Int(1)); d.Add("t", BValue.Int(2)); framed.Add(LiveWire.MyInfo(d));
            foreach (var f in framed) seeds.Add(f.Skip(4).ToArray());        // the parsers receive the message without the length prefix: id + payload
            foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "real"), "extend_*.hex"))
            {
                try { var raw = TestUtil.FromHex(File.ReadAllText(f).Trim()); seeds.Add(raw); } catch (FormatException) { }
            }
            FuzzKit.Run("live-wire", FuzzKit.Inputs(seeds, 6, 300, FuzzKit.Iterations * 2), b =>
            {
                ulong idx; double? ts; double ts2; int chunk, off, nbits, seq, size; ulong first; uint win; double? t0, t1; int bitsOff;
                LiveWire.TryParseHave(b, out idx, out ts);
                LiveWire.TryParseLost(b, out idx);
                LiveWire.TryParseBitfield(b, out first, out nbits, out bitsOff, out t0, out t1);
                LiveWire.TryParseRequest(b, out idx, out chunk);
                LiveWire.TryParsePiece(b, true, out idx, out ts2, out chunk, out off);
                LiveWire.TryParsePiece(b, false, out idx, out ts2, out chunk, out off);
                LiveWire.TryParseChangePos(b, out idx, out win);
                LiveWire.TryParsePingPong(b, out seq, out size);
                LiveWire.TryParseLastPiece(b, out idx, out ts2);
                LiveBroadcast bc; LiveWire.TryParseBroadcast(b, out bc);
                try { LiveWire.ParseMyInfo(b); } catch (BencodeException) { }
                LiveExtendInfo ei; LiveExtend.TryParse(b, out ei, false);
                LiveExtend.TryParse(b, out ei, true);
            });
        }

        [Fact]
        public void LiveBitfieldWithAbsurdSizesIsRejectedOrBounded()
        {
            // huge nbits in a tiny message: memory must not be reserved for the declared value
            var msg = LiveWire.Bitfield(1, 1000, new byte[125], null, null).Skip(4).ToArray();
            ulong first; int nbits, off; double? a, b;
            Assert.True(LiveWire.TryParseBitfield(msg, out first, out nbits, out off, out a, out b));
            Assert.Equal(1000, nbits);
            // header with nbits = 2^31-1
            var bad = (byte[])msg.Clone();
            for (int i = 9; i < 13; i++) bad[i] = 0x7f;
            int n2; 
            bool ok = LiveWire.TryParseBitfield(bad, out first, out n2, out off, out a, out b);
            Assert.True(!ok || (n2 >= 0 && off + (n2 + 7) / 8 <= bad.Length));
        }
    }

    public class TrackerAndDnsFuzzTests
    {
        [Fact]
        public void TrackerResponsesNeverThrow()
        {
            var seeds = new List<byte[]>();
            seeds.Add(TestUtil.Ascii("d8:completei5e10:incompletei7e8:intervali1800e5:peers12:" + "\x01\x02\x03\x04\x1a\xe1\x05\x06\x07\x08\x1a\xe2" + "e"));
            seeds.Add(TestUtil.Ascii("d5:filesd20:12345678901234567890d8:completei1e10:incompletei2e10:downloadedi3eee5:flagsd20:min_request_intervali60eee"));
            seeds.Add(TestUtil.Ascii("d14:failure reason4:nopee"));
            seeds.Add(TestUtil.Ascii("d5:peersld2:ip7:1.2.3.44:porti80eeee"));
            var ih = TestUtil.Ascii("12345678901234567890");
            FuzzKit.Run("tracker-http", FuzzKit.Inputs(seeds, 7, 300, FuzzKit.Iterations * 2), b =>
            {
                HttpTracker.ParseResponse(b);
                ScrapeClientParse(b, ih);
                var l = new List<IPEndPoint>(); HttpTracker.ParseCompact(b, l);
            });
        }

        private static void ScrapeClientParse(byte[] b, byte[] ih) { HttpScrape.ParseResponse(b, ih); }

        [Fact]
        public void UdpTrackerPacketsNeverThrow()
        {
            var seeds = new List<byte[]>();
            var conn = new byte[16]; conn[3] = 0; seeds.Add(conn);
            var ann = new byte[20 + 6 * 10]; ann[3] = 1; seeds.Add(ann);
            var scr = new byte[8 + 12 * 3]; scr[3] = 2; seeds.Add(scr);
            FuzzKit.Run("tracker-udp", FuzzKit.Inputs(seeds, 8, 200, FuzzKit.Iterations * 2), b =>
            {
                ulong cid; string err; UdpTracker.ParseConnect(b, 0, out cid, out err);
                UdpTracker.ParseAnnounce(b, 0, new AnnounceResult(), out err);
                List<ScrapeResult> res; UdpScrape.ParseScrape(b, 0, 3, out res, out err);
                UdpScrape.ParseScrape(b, 0, 1000, out res, out err);
            });
        }

        [Fact]
        public void DnsResponsesNeverThrow()
        {
            var q = DnsTxtClient.BuildQuery("a1b2c3d4e5f60718293a4b5c.elcano.top", 0x1234);
            var seeds = new List<byte[]>();
            // a valid response with a TXT of 2 strings
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0 }, 0, 12);
            ms.Write(q, 12, q.Length - 12);
            ms.Write(new byte[] { 0xc0, 0x0c, 0, 16, 0, 1, 0, 0, 0, 60, 0, 12, 5 }, 0, 13);
            ms.Write(TestUtil.Ascii("hello"), 0, 5); ms.WriteByte(5); ms.Write(TestUtil.Ascii("world"), 0, 5);
            seeds.Add(ms.ToArray());
            seeds.Add(q);
            FuzzKit.Run("dns", FuzzKit.Inputs(seeds, 9, 300, FuzzKit.Iterations * 3), b => DnsTxtClient.ParseResponse(b));
            // a compression pointer that points to itself must not hang
            var loop = new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0, 0xc0, 0x0c, 0, 16, 0, 1, 0xc0, 0x0c, 0, 16, 0, 1, 0, 0, 0, 1, 0, 1, 0 };
            DnsTxtClient.ParseResponse(loop);
        }

        [Fact]
        public void DohJsonOnlyRaisesDocumentedExceptions()
        {
            var seeds = new List<byte[]>();
            seeds.Add(Encoding.UTF8.GetBytes("{\"Status\":0,\"Answer\":[{\"name\":\"a\",\"type\":16,\"data\":\"\\\"abc\\\" \\\"def\\\"\"}]}"));
            seeds.Add(Encoding.UTF8.GetBytes("{\"Status\":3}"));
            FuzzKit.Run("doh-json", FuzzKit.Inputs(seeds, 10, 300, FuzzKit.Iterations), b => DnsTxtClient.ParseDohJson(b), typeof(JsonException), typeof(DnsException));
        }

        [Fact]
        public void DnsTxtLimitsAreEnforced()
        {
            // 100 TXT records: only MaxTxtRecords are read
            var q = DnsTxtClient.BuildQuery("a.elcano.top", 1);
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0, 1, 0x81, 0x80, 0, 1, 0, 100, 0, 0, 0, 0 }, 0, 12);
            ms.Write(q, 12, q.Length - 12);
            for (int i = 0; i < 100; i++) { ms.Write(new byte[] { 0xc0, 0x0c, 0, 16, 0, 1, 0, 0, 0, 60, 0, 4, 3 }, 0, 13); ms.Write(TestUtil.Ascii("abc"), 0, 3); }
            var r = DnsTxtClient.ParseResponse(ms.ToArray());
            Assert.NotNull(r);
            Assert.Equal(DnsTxtClient.MaxTxtRecords, r.Txt.Count);
        }
    }

    public class TextFuzzTests
    {
        private static IEnumerable<byte[]> TextInputs(IList<string> seeds, int seed, int n)
        {
            return FuzzKit.Inputs(seeds.Select(s => Encoding.UTF8.GetBytes(s)).ToList(), seed, 300, n);
        }

        [Fact]
        public void JsonOnlyRaisesJsonException()
        {
            var seeds = new[] { "{\"a\":[1,2.5e3,-4,{\"b\":null,\"c\":true}],\"d\":\"x\\u00e9\\n\"}", "[]", "\"\\ud83d\\ude00\"", "{\"name\":\"n\",\"url\":\"acestream://" + new string('a', 40) + "\",\"subLinks\":[{\"name\":\"s\"}]}" };
            FuzzKit.Run("json", TextInputs(seeds, 11, FuzzKit.Iterations * 3), b => MiniJson.Parse(b), typeof(JsonException));
        }

        [Fact]
        public void PersistedFilesNeverThrowWhenCorruptedOrManipulated()
        {
            var cache = new ResolutionCache(TimeSpan.FromHours(1), 16, 1 << 20);
            cache.Put(new string('a', 40), new byte[] { 1, 2, 3, 4 });
            var seeds = new[]
            {
                cache.ToJson(),
                "{\"v\":1,\"entries\":[{\"id\":\"" + new string('b', 40) + "\",\"t\":99999999999999999,\"d\":\"AAAA\"}]}",
                "[{\"code\":\"abc\",\"name\":\"n\",\"favorite\":true,\"last\":5}]",
                "[{\"id\":\"" + new string('c', 40) + "\",\"sum\":\"x\",\"ih\":\"y\",\"pk\":\"z\",\"nets\":3,\"ok\":true,\"first\":1,\"last\":2}]",
                "{\"log\":{\"enabled\":false,\"host\":\"1.2.3.4\",\"port\":99999},\"autoPlay\":\"x\",\"uploadSlots\":-5,\"keyScript\":\"Right wait:999999999999\"}"
            };
            var dir = Path.Combine(Path.GetTempPath(), "p-fuzzfiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                FuzzKit.Run("ficheros-persistidos", TextInputs(seeds, 17, FuzzKit.Iterations / 3), b =>
                {
                    var text = Encoding.UTF8.GetString(b);
                    new ResolutionCache(TimeSpan.FromHours(1), 16, 1 << 20).LoadJson(text);
                    var cfg = AceEngine.App.AppConfig.Parse(text);
                    AceEngine.App.AppConfig.Parse(cfg.ToJson());
                    string f = Path.Combine(dir, "x.json");
                    File.WriteAllBytes(f, b);
                    new TrustStore(f);
                    var rc = new ResolutionCache(TimeSpan.FromHours(1), 16, 1 << 20); rc.Load(f);
                });
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void JsonHostileShapesAreBounded()
        {
            Assert.Throws<JsonException>(() => MiniJson.Parse(new string('[', 1000000)));
            Assert.Throws<JsonException>(() => MiniJson.Parse("[" + string.Concat(Enumerable.Repeat("1,", 500000)) + "1]"));
            Assert.Throws<JsonException>(() => MiniJson.Parse(new string(' ', MiniJson.MaxLength + 1)));
            Assert.NotNull(MiniJson.Parse("[" + string.Concat(Enumerable.Repeat("1,", 20000)) + "1]"));
        }

        [Fact]
        public void M3uParserNeverThrowsAndIsBounded()
        {
            var seeds = new[]
            {
                "#EXTM3U\n#EXTINF:-1 group-title=\"A, B\" tvg-id=\"x\",Canal, uno\nacestream://" + new string('a', 40) + "\n#EXTGRP:G\nhttp://example.org/a.m3u8\nmagnet:?xt=urn:btih:" + new string('b', 40) + "\n",
                "#EXTM3U\r\n#EXTINF:5,Nombre\r\nfile:///etc/passwd\r\n/etc/passwd\r\n../../x\r\n",
                "﻿#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nlow.m3u8\n"
            };
            FuzzKit.Run("m3u", TextInputs(seeds, 12, FuzzKit.Iterations * 2), b =>
            {
                var text = M3uParser.DecodeText(b);
                var l1 = M3uParser.Parse(text);
                var l2 = M3uParser.Parse(text, "http://example.org/lists/x.m3u");
                Assert.True(l1.Count <= M3uParser.MaxEntries && l2.Count <= M3uParser.MaxEntries);
                foreach (var e in l1) { Assert.True(e.Name.Length <= M3uParser.MaxNameLength); Assert.NotEqual(AceLinkKind.TransportFile, e.Link == null ? AceLinkKind.InfoHash : e.Link.Kind); }
            });
        }

        [Fact]
        public void M3uHugeInputsAreCapped()
        {
            var sb = new StringBuilder("#EXTM3U\n");
            for (int i = 0; i < 60000; i++) sb.Append("#EXTINF:-1,Canal ").Append(i).Append("\nacestream://").Append(i.ToString("x").PadLeft(40, '0')).Append('\n');
            Assert.Equal(M3uParser.MaxEntries, M3uParser.Parse(sb.ToString()).Count);
            var longLine = "#EXTM3U\n#EXTINF:-1," + new string('x', 100000) + "\nacestream://" + new string('a', 40) + "\n";
            var r = M3uParser.Parse(longLine);
            Assert.True(r.All(e => e.Name.Length <= M3uParser.MaxNameLength));
        }

        [Fact]
        public void MyLinksPasteDecodeOnlyRaisesFormatException()
        {
            var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes("[{\"name\":\"a\",\"url\":\"acestream://" + new string('a', 40) + "\"},{\"type\":\"category\",\"ref\":\"x\"}]"));
            byte[] gz;
            using (var ms = new MemoryStream())
            {
                using (var g = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true)) { var b = Encoding.UTF8.GetBytes("[{\"name\":\"g\"}]"); g.Write(b, 0, b.Length); }
                gz = ms.ToArray();
            }
            var seeds = new[] { plain, "text = \"" + plain + "\"", Convert.ToBase64String(gz) };
            FuzzKit.Run("mylinkspaste", TextInputs(seeds, 13, FuzzKit.Iterations * 2), b => MyLinksPasteClient.Decode(Encoding.UTF8.GetString(b)), typeof(FormatException));
        }

        [Fact]
        public void GzipBombIsRejected()
        {
            byte[] bomb;
            using (var ms = new MemoryStream())
            {
                using (var g = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true))
                {
                    var chunk = Encoding.ASCII.GetBytes(new string(' ', 1 << 20));
                    for (int i = 0; i < 64; i++) g.Write(chunk, 0, chunk.Length);       // 64 MiB of spaces compressed into a few KiB
                }
                bomb = ms.ToArray();
            }
            Assert.True(bomb.Length < 200 * 1024);
            Assert.Throws<FormatException>(() => MyLinksPasteClient.Decode(Convert.ToBase64String(bomb)));
        }

        [Fact]
        public void LinkParsersNeverThrow()
        {
            var seeds = new[]
            {
                "acestream://" + new string('a', 40), "acestream:?content_id=" + new string('b', 40), "acestream:?data=/etc/passwd", "acestream:?data=file:///etc/passwd",
                "acestream:?url=http%3A%2F%2Fexample.org%2Fa.acelive", "magnet:?xt=urn:btih:" + new string('c', 40) + "&tr=udp%3A%2F%2Ft.example%3A80&x.pe=1.2.3.4:5",
                "file://", "http://", "mylinkspaste://abc", new string('d', 40), "acestream:%3Fcontent_id=" + new string('e', 40), "#", "%", "acestream:?%zz=%"
            };
            FuzzKit.Run("links", TextInputs(seeds, 14, FuzzKit.Iterations * 3), b =>
            {
                var s = Encoding.UTF8.GetString(b);
                AceLink l; string err;
                AceLink.TryParse(s, AceLinkKind.ContentId, out l, out err);
                AceLink.TryParse(s, AceLinkKind.InfoHash, out l, out err);
                MagnetLink m; MagnetLink.TryParse(s, out m, out err);
                var e = LinkEntry.FromUrl("n", s, "g");
                Assert.NotEqual(AceLinkKind.TransportFile, e.Link == null ? AceLinkKind.InfoHash : e.Link.Kind);       // a list never points to local files
                string norm; InputClassifier2(s, out norm);
                string host; int port; AceEngine.P2P.Net.TryParseHostPort(s, out host, out port);
            });
        }

        private static void InputClassifier2(string s, out string n) { AceEngine.App.InputClassifier.Classify(s, out n); }

        [Fact]
        public void RangeHeadersNeverThrow()
        {
            var seeds = new[] { "bytes=0-", "bytes=10-20", "bytes=-5", "bytes=5-1", "bytes=0-1,5-9", "bytes=99999999999999999999-", "items=1-2", "bytes=", "bytes=-", "bytes=1-2-3", "bytes=a-b" };
            FuzzKit.Run("range", TextInputs(seeds, 15, FuzzKit.Iterations * 2), b =>
            {
                long f, l;
                var s = Encoding.UTF8.GetString(b);
                RangeHeader.Parse(s, 1000, out f, out l);
                RangeHeader.Parse(s, 0, out f, out l);
            });
        }

        [Fact]
        public void TsServiceXmlResponsesNeverThrow()
        {
            var seeds = new[]
            {
                "<response r=\"5\" sig=\"abc\"><torrent>QUJD</torrent></response>", "QUJDRA==", "<!DOCTYPE x [<!ENTITY a SYSTEM \"file:///etc/passwd\">]><x>&a;</x>",
                "<a><b><c>" + new string('x', 100) + "</c></b></a>", "<" + string.Concat(Enumerable.Repeat("a><a", 3000)) + ">"
            };
            var opt = new ResolverOptions();
            FuzzKit.Run("tsservice", TextInputs(seeds, 16, FuzzKit.Iterations), b =>
            {
                string err;
                TsServiceClient.ParseResponse(b, new string('a', 40), 5, opt, out err);
            });
        }
    }
}
