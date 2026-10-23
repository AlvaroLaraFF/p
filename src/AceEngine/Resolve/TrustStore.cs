using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using AceEngine.Transport;
using AceEngine.Util;

namespace AceEngine.Resolve
{
    public enum TrustLevel
    {
        /// <summary>SHA-1 of the file == content_id (does not happen with real content_ids: the id is assigned by a server).</summary>
        Verified,
        /// <summary>The same file was delivered by peers of at least WeakQuorum distinct networks (/24 in IPv4, /48 in IPv6), or it had already been confirmed before.</summary>
        Confirmed,
        /// <summary>A single peer or a single network delivered it: it is accepted (the original does the same) but flagged.</summary>
        Unconfirmed,
        /// <summary>The identity (broadcaster key or infohash) differs from the stored one, but a clear majority of networks confirms it.</summary>
        Changed
    }

    /// <summary>What the peers delivered (InfoHash mode of the MetadataFetcher): the 'info' of a torrent or a complete transport file.</summary>
    public enum MetadataPayload
    {
        Unknown,
        /// <summary>Bytes of the 'info' dictionary (BEP9), verified with SHA-1 against the infohash.</summary>
        Info,
        /// <summary>Complete transport file (Ace live stream, or .torrent), verified against the requested infohash.</summary>
        Transport
    }

    /// <summary>What is known about a descriptor received over P2P (for the quorum and the notice in the UI).</summary>
    public sealed class FetchReport
    {
        /// <summary>What was delivered (filled in in InfoHash mode).</summary>
        public MetadataPayload Payload;
        public int Networks;
        public int Peers;
        public bool ExactHash;
        /// <summary>Another, different file (another SHA-1) was also seen for the same content_id.</summary>
        public bool Conflict;
        public string Checksum;
    }

    public sealed class TrustDecision
    {
        public TrustLevel Level;
        public string Note;
        public bool IsNew;
        /// <summary>Descriptor name different from the list one (filled in elsewhere: only the UI knows the list one).</summary>
        public override string ToString() { return Level + (Note != null ? " (" + Note + ")" : ""); }
    }

    public sealed class TrustEntry
    {
        public string ContentId, Checksum, InfoHash, PubKey, Name;
        public long FirstSeenUnix, LastSeenUnix;
        public int Networks;
        public bool Confirmed;
    }

    /// <summary>
    /// Trust on first use (trust-on-first-use) of the channel descriptors. Since the content_id is not the SHA-1 of the file
    /// (a server assigns it when uploading), there is no cryptographic way to check a descriptor received from a peer: a
    /// malicious peer could deliver one with its own public key and the pieces of the "live stream" signed with it would verify. Defenses:
    ///  - strict structural validation (TransportLoader with Strict),
    ///  - quorum by distinct networks (not by peers: an attacker with many IPs in a /24 counts as one),
    ///  - the checksum, the infohash and the broadcaster key are stored by content_id; if a different identity arrives later, it is only accepted with
    ///    a clear quorum and a notice is given; otherwise it is rejected.
    /// The file is written atomically and bounded (2000 entries, 1 MiB).
    /// </summary>
    public sealed class TrustStore
    {
        public const int MaxEntries = 2000;
        public const int MaxFileBytes = 1024 * 1024;
        private readonly string _path;
        private readonly object _gate = new object();
        private readonly Dictionary<string, TrustEntry> _entries = new Dictionary<string, TrustEntry>(StringComparer.Ordinal);
        private readonly Func<long> _now;

        public TrustStore(string path, Func<long> nowUnix = null)
        {
            _path = path;
            _now = nowUnix ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Load();
        }

        public int Count { get { lock (_gate) return _entries.Count; } }

        public TrustEntry Get(string contentId)
        {
            lock (_gate) { TrustEntry e; return _entries.TryGetValue(contentId, out e) ? Clone(e) : null; }
        }

        private static TrustEntry Clone(TrustEntry e)
        {
            return new TrustEntry { ContentId = e.ContentId, Checksum = e.Checksum, InfoHash = e.InfoHash, PubKey = e.PubKey, Name = e.Name, FirstSeenUnix = e.FirstSeenUnix, LastSeenUnix = e.LastSeenUnix, Networks = e.Networks, Confirmed = e.Confirmed };
        }

        /// <summary>Network of a peer: /24 in IPv4 and /48 in IPv6 (an attacker with many IPs of the same block counts as a single witness).</summary>
        public static string NetworkKey(IPAddress ip)
        {
            if (ip == null) return "?";
            var b = ip.GetAddressBytes();
            if (ip.IsIPv4MappedToIPv6) { ip = ip.MapToIPv4(); b = ip.GetAddressBytes(); }
            if (b.Length == 4) return b[0] + "." + b[1] + "." + b[2] + ".0/24";
            if (b.Length == 16) return Hex.ToHex(b, 0, 6) + "::/48";
            return ip.ToString();
        }

        /// <summary>
        /// Decides whether the descriptor is accepted and with what level of trust. Throws ResolveException if the identity of an already known channel changes without
        /// a clear quorum confirming it. Nothing is stored until it is accepted.
        /// </summary>
        public TrustDecision Evaluate(string contentId, TransportFile tf, byte[] raw, FetchReport report, int quorum, bool enforce)
        {
            string checksum = Hex.ToHex(HashUtil.Sha1(raw));
            string infohash = tf.Live != null ? tf.Live.InfoHashHex : tf.Torrent != null ? tf.Torrent.InfoHashHex : tf.InfoHash != null ? Hex.ToHex(tf.InfoHash) : "";
            string pubkey = tf.Live != null && tf.Live.PubKey != null ? Hex.ToHex(tf.Live.PubKey) : "";
            string name = tf.Name ?? "";
            bool exact = report != null && report.ExactHash || string.Equals(checksum, contentId, StringComparison.OrdinalIgnoreCase);
            int nets = report == null ? 0 : report.Networks;
            var d = new TrustDecision { Level = exact ? TrustLevel.Verified : nets >= quorum ? TrustLevel.Confirmed : TrustLevel.Unconfirmed };
            long now = _now();
            lock (_gate)
            {
                TrustEntry e;
                if (!_entries.TryGetValue(contentId, out e))
                {
                    d.IsNew = true;
                    if (d.Level == TrustLevel.Unconfirmed) d.Note = "Canal no confirmado: lo entrego " + (report != null && report.Peers > 1 ? report.Peers + " pares de una sola red" : "un solo par");
                    Put(new TrustEntry { ContentId = contentId, Checksum = checksum, InfoHash = infohash, PubKey = pubkey, Name = name, FirstSeenUnix = now, LastSeenUnix = now, Networks = nets, Confirmed = d.Level != TrustLevel.Unconfirmed });
                    return d;
                }
                bool sameIdentity = e.PubKey == pubkey && e.InfoHash == infohash;
                if (e.Checksum == checksum)
                {
                    if (e.Confirmed && d.Level == TrustLevel.Unconfirmed) d.Level = TrustLevel.Confirmed;      // several networks had already seen it before
                    d.Note = d.Level == TrustLevel.Unconfirmed ? "Canal no confirmado: solo lo ha entregado un par" : null;
                    e.LastSeenUnix = now; e.Networks = Math.Max(e.Networks, nets); e.Confirmed |= d.Level != TrustLevel.Unconfirmed;
                    SaveLocked();
                    return d;
                }
                if (sameIdentity)
                {
                    // same channel, another file (updated trackers or nodes, other padding): it is accepted and updated
                    AceLog.Info("trust", "content_id " + contentId + ": the descriptor changed bytes but not identity (same key and infohash)");
                    e.Checksum = checksum; e.LastSeenUnix = now; e.Name = name; e.Networks = Math.Max(e.Networks, nets); e.Confirmed |= d.Level != TrustLevel.Unconfirmed;
                    if (d.Level == TrustLevel.Unconfirmed && e.Confirmed) d.Level = TrustLevel.Confirmed;
                    SaveLocked();
                    return d;
                }
                // The identity is another: broadcaster key or infohash different from those seen the first time
                string what = (e.PubKey != pubkey ? "la clave del emisor" : "") + (e.PubKey != pubkey && e.InfoHash != infohash ? " y " : "") + (e.InfoHash != infohash ? "el infohash" : "");
                if (!enforce || d.Level == TrustLevel.Verified || (d.Level == TrustLevel.Confirmed && nets >= Math.Max(2, quorum)))
                {
                    AceLog.Warn("trust", "content_id " + contentId + ": identity changed (" + what + ") with respect to the first use; accepted (" + d.Level + ", " + nets + " networks)");
                    d.Level = TrustLevel.Changed;
                    d.Note = "Atencion: ha cambiado " + what + " de este canal (confirmado por " + nets + " redes)";
                    e.Checksum = checksum; e.InfoHash = infohash; e.PubKey = pubkey; e.Name = name; e.LastSeenUnix = now; e.Networks = nets; e.Confirmed = true;
                    SaveLocked();
                    return d;
                }
                AceLog.Warn("trust", "content_id " + contentId + ": REJECTED, identity changed (" + what + ") and only " + nets + " network(s) confirm it");
                throw new ResolveException("the descriptor of this channel has changed (" + what + ") and several peers do not confirm it: rejected for security");
            }
        }

        private void Put(TrustEntry e)
        {
            _entries[e.ContentId] = e;
            if (_entries.Count > MaxEntries)
            {
                var victim = _entries.Values.OrderBy(x => x.LastSeenUnix).First();
                _entries.Remove(victim.ContentId);
            }
            SaveLocked();
        }

        public void Forget(string contentId) { lock (_gate) { if (_entries.Remove(contentId)) SaveLocked(); } }

        private void Load()
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists || fi.Length > MaxFileBytes) return;
                object v;
                if (!MiniJson.TryParse(File.ReadAllText(_path, Encoding.UTF8), out v)) return;
                var arr = MiniJson.AsArray(v);
                if (arr == null) return;
                foreach (var o in arr)
                {
                    var d = MiniJson.AsObject(o);
                    string id = MiniJson.GetString(d, "id");
                    if (id == null || !Hex.IsHex40(id) || _entries.ContainsKey(id)) continue;
                    _entries[id.ToLowerInvariant()] = new TrustEntry
                    {
                        ContentId = id.ToLowerInvariant(), Checksum = Clip(MiniJson.GetString(d, "sum")), InfoHash = Clip(MiniJson.GetString(d, "ih")), PubKey = Clip(MiniJson.GetString(d, "pk")),
                        Name = Clip(MiniJson.GetString(d, "name")), FirstSeenUnix = MiniJson.GetLong(d, "first") ?? 0, LastSeenUnix = MiniJson.GetLong(d, "last") ?? 0,
                        Networks = (int)Math.Max(0, Math.Min(100, MiniJson.GetLong(d, "nets") ?? 0)), Confirmed = MiniJson.GetBool(d, "ok") ?? false
                    };
                    if (_entries.Count >= MaxEntries) break;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                AceLog.Warn("trust", "could not read " + _path + ": " + ex.Message);
            }
        }

        private static string Clip(string s) { s = s ?? ""; return s.Length <= 2200 ? s : s.Substring(0, 2200); }

        private void SaveLocked()
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                var arr = _entries.Values.Select(e => (object)new Dictionary<string, object>
                {
                    { "id", e.ContentId }, { "sum", e.Checksum }, { "ih", e.InfoHash }, { "pk", e.PubKey }, { "name", e.Name }, { "first", e.FirstSeenUnix }, { "last", e.LastSeenUnix }, { "nets", (long)e.Networks }, { "ok", e.Confirmed }
                }).ToList();
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, MiniJson.Stringify(arr), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                AceLog.Warn("trust", "could not save " + _path + ": " + ex.Message);
            }
        }
    }

    /// <summary>Compares the channel name in the descriptor with the one in the list (the list ones carry quality and marks: "M+ Liga de Campeones 3 720p *").</summary>
    public static class NameCheck
    {
        private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.Ordinal) { "de", "del", "la", "el", "los", "las", "hd", "fhd", "sd", "tv", "the", "and", "http", "https", "www", "com", "org", "net", "top" };

        private static HashSet<string> Tokens(string s)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (s == null) return set;
            var sb = new StringBuilder();
            Action flush = () =>
            {
                if (sb.Length == 0) return;
                string t = sb.ToString().ToLowerInvariant();
                sb.Clear();
                // 720p / 1080p / 1080i -> 720 / 1080
                if (t.Length > 1 && (t[t.Length - 1] == 'p' || t[t.Length - 1] == 'i') && char.IsDigit(t[t.Length - 2])) t = t.Substring(0, t.Length - 1);
                t = RemoveDiacritics(t);
                if (t.Length >= 3 || (t.Length >= 1 && char.IsDigit(t[0]))) { if (!Stop.Contains(t)) set.Add(t); }
            };
            foreach (char c in s) { if (char.IsLetterOrDigit(c)) sb.Append(c); else flush(); }
            flush();
            return set;
        }

        private static string RemoveDiacritics(string s)
        {
            var n = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(n.Length);
            foreach (char c in n) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>true if there is no information to compare or if they share some significant word (letters, not just numbers).</summary>
        public static bool Plausible(string listName, string transportName)
        {
            var a = Tokens(listName);
            var b = Tokens(transportName);
            if (a.Count == 0 || b.Count == 0) return true;
            foreach (var t in a) if (b.Contains(t) && t.Any(char.IsLetter)) return true;
            // only numbers in common (e.g. "3 720"): not enough
            return false;
        }
    }
}
