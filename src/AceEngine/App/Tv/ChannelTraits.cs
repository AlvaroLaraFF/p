using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using AceEngine.Util;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// Resolution (picture height) of a channel, for the "minimum resolution" filter in Settings. It is obtained, in this order, from the quality label of the name
    /// ("1080p", "FHD" = 1080p, "720p", "HD" = 720p, "4K"/"UHD"/"2160p" = 2160p, "SD" = 480p), from the Ace search metadata (bitrate) and from the bitrate
    /// of the channel descriptor learned when checking it (<see cref="ChannelHints"/>). 0 = unknown.
    /// </summary>
    public static class ChannelResolution
    {
        /// <summary>Values of the minimum resolution in Settings: Cualquiera (any), 720p, 1080p, 4K.</summary>
        public static readonly int[] Steps = { 0, 720, 1080, 2160 };

        /// <summary>Height from a QualityRules quality label ("1080p", "FHD", "720p", "HD", "4K", "SD"); 0 if it is none of them.</summary>
        public static int FromLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return 0;
            switch (label.ToLowerInvariant())
            {
                case "4k": case "uhd": case "2160p": case "2160i": return 2160;
                case "1080p": case "1080i": case "fhd": return 1080;
                case "720p": case "720i": case "hd": return 720;
                case "sd": case "576p": case "576i": case "480p": case "480i": case "360p": return 480;
                default: return 0;
            }
        }

        /// <summary>
        /// Approximate height from the bitrate (kbit/s) of a live stream: from 10 Mbit/s up 4K, from 4 Mbit/s 1080p, from 2 Mbit/s 720p and below that SD.
        /// It is an estimate (it depends on the codec); it is only used when the name does not say the quality.
        /// </summary>
        public static int FromBitrateKbps(long kbps)
        {
            if (kbps <= 0) return 0;
            if (kbps >= 10000) return 2160;
            if (kbps >= 4000) return 1080;
            if (kbps >= 2000) return 720;
            return 480;
        }

        /// <summary>Text of a Steps value: "Cualquiera", "720p", "1080p", "4K".</summary>
        public static string StepName(int minHeight)
        {
            switch (minHeight) { case 720: return "720p"; case 1080: return "1080p"; case 2160: return "4K"; default: return "Cualquiera"; }
        }

        /// <summary>The nearest Steps value below or equal (anything else falls on Cualquiera).</summary>
        public static int NormalizeStep(int v)
        {
            return v >= 2160 ? 2160 : v >= 1080 ? 1080 : v >= 720 ? 720 : 0;
        }

        /// <summary>true if the channel reaches the minimum resolution; with min == 0 (Cualquiera) all pass, also those that do not say their quality.</summary>
        public static bool Passes(Channel c, int minHeight)
        {
            return minHeight <= 0 || (c != null && c.Resolution >= minHeight);
        }
    }

    /// <summary>
    /// Language of a channel. Rules, in this order (the first one that recognizes something wins):
    ///  1) Ace search metadata (languages, countries) when present;
    ///  2) mark in parentheses, brackets or braces in the original name: "(DE)", "[UK]", "(PL) HD", "[ES/EN]" (country or language code);
    ///  3) loose uppercase mark at the end of the name, after the quality: "Canal+ Sport 3 PL", "FOX DEPORTES AR HD";
    ///  4) script of the name (Cyrillic = Russian, Greek, Arabic, Hebrew, Korean, Japanese, Chinese) and letters specific to a language (ñ, ã, õ, ł, ą, ğ, ß...);
    ///  5) channel or network known by its name ("Sport TV", "Eleven", "Sky Sports", "Magenta Sport", "Polsat", "Movistar", "Liga de Campeones"...);
    ///  6) the origin category (mark or word of a country or language: "Portugal", "Premier League"...).
    /// With no signal the language is "" (unknown): the UI treats it as Spanish (the lists are mostly Spanish), so it comes out with "Todos" and
    /// with "Español" and with no other language. A code is the two-letter ISO 639-1 in lowercase.
    /// The "SPORT TV" category of the real lists does NOT count as Portuguese: it is the name of a list (it contains DAZN, M+ LaLiga, Liga de Campeones...); what counts
    /// is the channel called "Sport TV".
    /// </summary>
    public static class ChannelLanguages
    {
        /// <summary>The four languages always offered in Settings (after Todos); the others only if they appear in the data.</summary>
        public static readonly string[] Primary = { "es", "en", "pt", "pl" };

        // Order in which the other languages are offered when they appear in the data (those not here, at the end by count)
        private static readonly string[] OtherOrder = { "de", "fr", "it", "nl", "ru", "tr", "el", "ro", "hu", "cs", "sk", "sr", "hr", "sv", "no", "da", "fi", "he", "ar", "zh", "ja", "ko" };

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "es", "Español" }, { "en", "Inglés" }, { "pt", "Portugués" }, { "pl", "Polaco" }, { "de", "Alemán" }, { "fr", "Francés" }, { "it", "Italiano" },
            { "nl", "Neerlandés" }, { "ru", "Ruso" }, { "tr", "Turco" }, { "el", "Griego" }, { "ro", "Rumano" }, { "hu", "Húngaro" }, { "cs", "Checo" }, { "sk", "Eslovaco" },
            { "sr", "Serbio" }, { "hr", "Croata" }, { "sv", "Sueco" }, { "no", "Noruego" }, { "da", "Danés" }, { "fi", "Finés" }, { "he", "Hebreo" }, { "ar", "Árabe" },
            { "zh", "Chino" }, { "ja", "Japonés" }, { "ko", "Coreano" }
        };

        /// <summary>Display name: "Español"... ("Todos" for ""); a code without a name is shown in uppercase.</summary>
        public static string DisplayName(string code)
        {
            if (string.IsNullOrEmpty(code)) return "Todos";
            string n;
            return Names.TryGetValue(code, out n) ? n : code.ToUpperInvariant();
        }

        // Country or language marks (two or three letters) -> language. In a two-letter mark the country rules: "AR" is Argentina, "UK" is United Kingdom.
        private static readonly Dictionary<string, string> Marks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "es", "es" }, { "spa", "es" }, { "esp", "es" }, { "mx", "es" }, { "ar", "es" }, { "co", "es" }, { "cl", "es" }, { "pe", "es" }, { "ve", "es" }, { "uy", "es" },
            { "pt", "pt" }, { "por", "pt" }, { "br", "pt" },
            { "pl", "pl" }, { "pol", "pl" },
            { "en", "en" }, { "eng", "en" }, { "uk", "en" }, { "gb", "en" }, { "us", "en" }, { "usa", "en" }, { "ie", "en" }, { "au", "en" }, { "nz", "en" }, { "za", "en" },
            { "de", "de" }, { "deu", "de" }, { "ger", "de" }, { "at", "de" },
            { "fr", "fr" }, { "fra", "fr" }, { "fre", "fr" },
            { "it", "it" }, { "ita", "it" },
            { "nl", "nl" }, { "nld", "nl" }, { "dut", "nl" }, { "be", "nl" },
            { "ru", "ru" }, { "rus", "ru" },
            { "tr", "tr" }, { "tur", "tr" },
            { "gr", "el" }, { "el", "el" }, { "ell", "el" }, { "gre", "el" },
            { "ro", "ro" }, { "ron", "ro" }, { "rum", "ro" },
            { "hu", "hu" }, { "hun", "hu" },
            { "cz", "cs" }, { "cs", "cs" }, { "ces", "cs" }, { "cze", "cs" },
            { "sk", "sk" }, { "slk", "sk" }, { "slo", "sk" },
            { "rs", "sr" }, { "srp", "sr" }, { "hr", "hr" }, { "hrv", "hr" },
            { "se", "sv" }, { "sv", "sv" }, { "swe", "sv" },
            { "no", "no" }, { "nor", "no" }, { "dk", "da" }, { "da", "da" }, { "dan", "da" }, { "fi", "fi" }, { "fin", "fi" },
            { "il", "he" }, { "he", "he" }, { "heb", "he" },
            { "sa", "ar" }, { "ae", "ar" }, { "eg", "ar" }, { "qa", "ar" }, { "ara", "ar" },
            { "cn", "zh" }, { "zh", "zh" }, { "zho", "zh" }, { "chi", "zh" },
            { "jp", "ja" }, { "ja", "ja" }, { "jpn", "ja" },
            { "kr", "ko" }, { "ko", "ko" }, { "kor", "ko" }
        };

        // Loose marks accepted at the end of an uppercase name (stricter than the parenthesis ones: "DE", "IT", "NO", "EN" are common words)
        private static readonly HashSet<string> BareMarks = new HashSet<string>(StringComparer.Ordinal) { "PL", "PT", "ES", "UK", "US", "BR", "RU", "TR", "FR", "NL", "MX", "AR" };

        // Language names in metadata or in text ("spanish", "español"...)
        private static readonly Dictionary<string, string> Words = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "spanish", "es" }, { "espanol", "es" }, { "castellano", "es" }, { "english", "en" }, { "ingles", "en" }, { "portuguese", "pt" }, { "portugues", "pt" },
            { "polish", "pl" }, { "polaco", "pl" }, { "polski", "pl" }, { "german", "de" }, { "deutsch", "de" }, { "aleman", "de" }, { "french", "fr" }, { "frances", "fr" },
            { "italian", "it" }, { "italiano", "it" }, { "dutch", "nl" }, { "russian", "ru" }, { "ruso", "ru" }, { "turkish", "tr" }, { "turco", "tr" },
            { "greek", "el" }, { "griego", "el" }, { "arabic", "ar" }, { "arabe", "ar" }
        };

        /// <summary>Two-letter code of a language from its code (es, spa, eng...) or its name (spanish, español...); "" if not recognized.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string f = TextNorm.Normalize(s);
            string d;
            if (Words.TryGetValue(f, out d)) return d;
            int dash = f.IndexOf(' ');
            if (dash > 0) f = f.Substring(0, dash);                       // "en-GB" -> "en"
            if (f.Length == 2 && Names.ContainsKey(f)) return f;
            if (f.Length == 3 && Marks.TryGetValue(f, out d) && d.Length == 2 && Names.ContainsKey(d)) return d;
            return Names.ContainsKey(f) ? f : "";
        }

        /// <summary>Language of a channel. name = clean name, source = name as it came, group = origin category; languages/countries = Ace search metadata.</summary>
        public static string Detect(string name, string source, string group, IEnumerable<string> languages = null, IEnumerable<string> countries = null)
        {
            string r;
            // 1) Ace search metadata
            if (languages != null) foreach (var l in languages) { r = Normalize(l); if (r.Length > 0) return r; }
            if (countries != null) foreach (var c in countries) { string k = TextNorm.Normalize(c); if (k.Length >= 2 && k.Length <= 3 && Marks.TryGetValue(k, out r)) return r; }

            string raw = !string.IsNullOrEmpty(source) ? source : name ?? "";
            // 2) mark in parentheses
            r = BracketMark(raw);
            if (r != null) return r;
            // 3) loose mark at the end
            r = TrailingMark(raw);
            if (r != null) return r;
            // 4) script and own letters
            r = FromScript(raw.Length > 0 ? raw : name);
            if (r != null) return r;
            // 5) known network or channel
            string norm = " " + TextNorm.Normalize(string.IsNullOrEmpty(name) ? raw : name) + " ";
            r = FromKnownNames(norm);
            if (r != null) return r;
            // 6) the origin category
            if (!string.IsNullOrEmpty(group))
            {
                r = BracketMark(group);
                if (r != null) return r;
                r = FromKnownGroup(" " + TextNorm.Normalize(group) + " ");
                if (r != null) return r;
            }
            return "";
        }

        /// <summary>true if the channel passes the language filter. wanted == "" (Todos) lets everything through; "es" also lets through those of unknown language.</summary>
        public static bool Passes(Channel c, string wanted)
        {
            if (string.IsNullOrEmpty(wanted)) return true;
            if (c == null) return false;
            string l = c.Language ?? "";
            if (l.Length == 0) return wanted == "es";
            return l == wanted;
        }

        /// <summary>
        /// Languages offered in Settings: Todos (""), Español, Inglés, Portugués, Polaco and the others present in the channels (by count), and the current one even if it no longer
        /// appears in the data.
        /// </summary>
        public static List<string> Options(IEnumerable<Channel> channels, string current)
        {
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            if (channels != null)
                foreach (var c in channels)
                {
                    string l = c.Language;
                    if (string.IsNullOrEmpty(l)) continue;
                    int n; count.TryGetValue(l, out n); count[l] = n + 1;
                }
            var res = new List<string> { "" };
            res.AddRange(Primary);
            var others = count.Keys.Where(k => !res.Contains(k)).ToList();
            others.Sort((a, b) =>
            {
                int ia = Array.IndexOf(OtherOrder, a), ib = Array.IndexOf(OtherOrder, b);
                if (ia < 0) ia = OtherOrder.Length; if (ib < 0) ib = OtherOrder.Length;
                int r = ia.CompareTo(ib);
                if (r != 0) return r;
                r = count[b].CompareTo(count[a]);
                return r != 0 ? r : string.CompareOrdinal(a, b);
            });
            res.AddRange(others);
            if (!string.IsNullOrEmpty(current) && !res.Contains(current)) res.Add(current);
            return res;
        }

        // ---------------------------------------------------------------- marks

        private static string BracketMark(string s)
        {
            string found = null;
            for (int i = 0; i < s.Length; i++)
            {
                char open = s[i], close = open == '(' ? ')' : open == '[' ? ']' : open == '{' ? '}' : '\0';
                if (close == '\0') continue;
                int end = s.IndexOf(close, i + 1);
                if (end < 0) continue;
                string inner = s.Substring(i + 1, end - i - 1);
                string m = MarkOf(inner);
                if (m != null) found = m;                                    // the last one wins ("SkySP PL (UK)": UK)
                i = end;
            }
            return found;
        }

        /// <summary>The content of a parenthesis is a mark if it is only two- or three-letter codes (separated by "/", "," "-" or space) and the first one is recognized.</summary>
        private static string MarkOf(string inner)
        {
            var toks = inner.Split(new[] { '/', ',', '-', '|', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length == 0 || toks.Length > 3) return null;
            foreach (var t in toks)
            {
                if (t.Length < 2 || t.Length > 3) return null;
                foreach (char ch in t) if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))) return null;
            }
            string r;
            return Marks.TryGetValue(toks[0].ToLowerInvariant(), out r) ? r : null;
        }

        private static string TrailingMark(string s)
        {
            var toks = s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            while (toks.Count > 0 && IsQualityOrStars(toks[toks.Count - 1])) toks.RemoveAt(toks.Count - 1);
            if (toks.Count < 2) return null;
            string last = toks[toks.Count - 1];
            string r;
            return BareMarks.Contains(last) && Marks.TryGetValue(last.ToLowerInvariant(), out r) ? r : null;
        }

        private static bool IsQualityOrStars(string t)
        {
            string label;
            if (QualityRules.TryCanonical(t, out label)) return true;
            foreach (char c in t) if (c != '*' && c != '★' && c != '☆') return false;
            return true;
        }

        // ---------------------------------------------------------------- script

        private static string FromScript(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int cyr = 0, gre = 0, ara = 0, heb = 0, kor = 0, jpn = 0, han = 0;
            foreach (char c in s)
            {
                if (c >= 0x0400 && c <= 0x04FF) cyr++;
                else if (c >= 0x0370 && c <= 0x03FF) gre++;
                else if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F)) ara++;
                else if (c >= 0x0590 && c <= 0x05FF) heb++;
                else if ((c >= 0xAC00 && c <= 0xD7AF) || (c >= 0x1100 && c <= 0x11FF)) kor++;
                else if (c >= 0x3040 && c <= 0x30FF) jpn++;
                else if (c >= 0x4E00 && c <= 0x9FFF) han++;
            }
            if (cyr >= 2) return "ru";
            if (gre >= 2) return "el";
            if (ara >= 2) return "ar";
            if (heb >= 2) return "he";
            if (kor >= 2) return "ko";
            if (jpn >= 2) return "ja";
            if (han >= 2) return "zh";
            foreach (char c in s)
            {
                switch (c)
                {
                    case 'ñ': case 'Ñ': case '¿': case '¡': return "es";
                    case 'ã': case 'õ': case 'Ã': case 'Õ': return "pt";
                    case 'ł': case 'Ł': case 'ą': case 'ę': case 'ś': case 'ć': case 'ń': case 'ż': case 'ź': return "pl";
                    case 'ğ': case 'ı': case 'ş': case 'Ğ': case 'İ': case 'Ş': return "tr";
                    case 'ß': return "de";
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- known names

        // Phrases (already normalized, whole words) of channels and networks with a language of their own. The most specific first.
        private static readonly string[][] KnownNames =
        {
            new[] { "es", "movistar", "m+", "laliga", "la liga", "liga de campeones", "teledeporte", "tdp", "gol play", "gol tv", "hypermotion", "la sexta", "antena 3", "telecinco",
                          "cuatro", "onetoro", "caza y pesca", "calle 13", "canal cocina", "esport3", "esport 3", "tv3", "canal sur", "tve", "la 1", "la 2", "deportes", "deporte", "futbol",
                          "baloncesto", "canal de tenis", "espn deportes", "fox deportes", "dazn la liga", "vamos", "supertennis" },
            new[] { "pt", "sport tv", "sporttv", "sport tv+", "eleven", "benfica", "sporting tv", "porto canal", "rtp", "tvi", "sic", "abola", "btv" },
            new[] { "pl", "polsat", "tvp", "nsport", "sport klub", "tvn", "canal+ sport pl" },
            new[] { "de", "magenta sport", "eurosport360", "eurosport 360", "auto motor und sport", "rtl+", "orf", "servus tv", "sat 1", "zdf", "ard", "sky sport news" },
            new[] { "nl", "ziggo", "ziggo sport" },
            new[] { "fr", "rmc sport", "l equipe", "sfr sport", "bein sports max" },
            new[] { "it", "rai", "mediaset", "eurosport italia" },
            new[] { "tr", "trt", "s sport", "bein sports haber" },
            new[] { "en", "sky sports", "bt sport", "tnt sports", "premier sports", "espn", "fox sports", "nbc sports", "cbs sports", "tennis channel", "nba league pass", "nba tv", "golf channel", "willow" }
        };

        private static string FromKnownNames(string norm)
        {
            foreach (var group in KnownNames)
                for (int i = 1; i < group.Length; i++)
                    if (norm.IndexOf(" " + group[i] + " ", StringComparison.Ordinal) >= 0) return group[0];
            return null;
        }

        private static readonly string[][] KnownGroups =
        {
            new[] { "es", "espana", "spain", "castellano", "movistar", "deportes", "liga de campeones", "baloncesto", "futbol" },
            new[] { "pt", "portugal", "portugues", "primeira liga", "liga portugal" },
            new[] { "pl", "polonia", "polska", "polaco", "poland", "ekstraklasa" },
            new[] { "en", "ingles", "english", "inglaterra", "england", "uk", "united kingdom", "premier league" },
            new[] { "de", "alemania", "deutschland", "german", "aleman", "bundesliga" },
            new[] { "fr", "francia", "france", "ligue 1" },
            new[] { "it", "italia", "italy", "serie a" },
            new[] { "ru", "rusia", "russia" },
            new[] { "tr", "turquia", "turkey", "super lig" },
            new[] { "nl", "holanda", "paises bajos", "netherlands", "eredivisie" }
        };

        private static string FromKnownGroup(string norm)
        {
            foreach (var group in KnownGroups)
                for (int i = 1; i < group.Length; i++)
                    if (norm.IndexOf(" " + group[i] + " ", StringComparison.Ordinal) >= 0) return group[0];
            return null;
        }
    }

    /// <summary>
    /// Bitrate of the live streams learned when checking them (the channel descriptor carries it): used to know the quality of channels whose name does not say it.
    /// It is stored in data/bitrates.json (bounded) so that it survives a restart. Shared state of the whole app: Attach turns it on at startup.
    /// </summary>
    public static class ChannelHints
    {
        public const int MaxEntries = 6000;
        public const int MaxFileBytes = 512 * 1024;
        public const string FileName = "bitrates.json";
        private static readonly ConcurrentDictionary<string, int> Map = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private static string _path;
        private static Timer _saveTimer;
        private static int _dirty;

        /// <summary>Known kbit/s of the channel, or 0.</summary>
        public static int BitrateKbps(string id)
        {
            int v;
            return id != null && Map.TryGetValue(id, out v) ? v : 0;
        }

        public static void Learn(string id, int kbps)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 2100 || kbps <= 0 || kbps > 125000) return;
            int old;
            if (Map.TryGetValue(id, out old) && old == kbps) return;
            if (Map.Count >= MaxEntries && !Map.ContainsKey(id))
            {
                foreach (var k in Map.Keys.Take(MaxEntries / 4)) { int dummy; Map.TryRemove(k, out dummy); }
            }
            Map[id] = kbps;
            ScheduleSave();
        }

        /// <summary>Saves what is pending and stops saving (tests and shutdown).</summary>
        public static void Detach()
        {
            Save();
            lock (typeof(ChannelHints)) { _path = null; if (_saveTimer != null) { _saveTimer.Dispose(); _saveTimer = null; } }
        }

        /// <summary>Forgets everything (tests).</summary>
        public static void Reset()
        {
            Map.Clear();
            lock (typeof(ChannelHints)) { _path = null; if (_saveTimer != null) { _saveTimer.Dispose(); _saveTimer = null; } }
        }

        /// <summary>Loads data/bitrates.json and saves future changes there.</summary>
        public static void Attach(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir)) return;
            lock (typeof(ChannelHints))
            {
                _path = System.IO.Path.Combine(dataDir, FileName);
                string text = TvFiles.ReadBounded(_path, MaxFileBytes);
                if (text == null) return;
                try
                {
                    object v;
                    if (!MiniJson.TryParse(text, out v)) return;
                    var o = MiniJson.AsObject(v);
                    if (o == null) return;
                    foreach (var kv in o)
                    {
                        if (Map.Count >= MaxEntries) break;
                        long n = 0;
                        if (kv.Value is long) n = (long)kv.Value; else if (kv.Value is double) n = (long)(double)kv.Value;
                        if (n > 0 && n <= 125000 && kv.Key.Length <= 2100) Map[kv.Key] = (int)n;
                    }
                }
                catch (Exception ex) { AceLog.Debug("hints", "bitrates.json: " + ex.Message); }
            }
        }

        private static void ScheduleSave()
        {
            lock (typeof(ChannelHints))
            {
                if (_path == null) return;
                Interlocked.Exchange(ref _dirty, 1);
                if (_saveTimer == null) _saveTimer = new Timer(_ => Save(), null, 20000, Timeout.Infinite);
                else _saveTimer.Change(20000, Timeout.Infinite);
            }
        }

        /// <summary>Saves now (when closing the app or by the timer).</summary>
        public static void Save()
        {
            string path;
            lock (typeof(ChannelHints)) path = _path;
            if (path == null || Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try
            {
                var o = new Dictionary<string, object>();
                foreach (var kv in Map) o[kv.Key] = (long)kv.Value;
                TvFiles.WriteAtomic(path, MiniJson.Stringify(o));
            }
            catch (Exception ex) { AceLog.Debug("hints", "could not save bitrates.json: " + ex.Message); }
        }
    }

    /// <summary>
    /// Settings filters over channel lists. The minimum resolution applies to all the lists (recents, categories, Explore and results); the language only to
    /// the categories section and the search (Explore and results), not to the recents.
    /// </summary>
    public static class ChannelFilter
    {
        /// <summary>true if the channel is shown with the current settings. withLanguage: also apply the language (false for the recents).</summary>
        public static bool Passes(Channel c, UserSettings s, bool withLanguage)
        {
            if (c == null) return false;
            if (s == null) return true;
            if (!ChannelResolution.Passes(c, s.MinResolution)) return false;
            return !withLanguage || ChannelLanguages.Passes(c, s.Language);
        }

        public static bool Active(UserSettings s, bool withLanguage)
        {
            return s != null && (s.MinResolution > 0 || (withLanguage && !string.IsNullOrEmpty(s.Language)));
        }

        public static List<Channel> Apply(IEnumerable<Channel> channels, UserSettings s, bool withLanguage)
        {
            var res = new List<Channel>();
            if (channels == null) return res;
            bool all = !Active(s, withLanguage);
            foreach (var c in channels) if (c != null && (all || Passes(c, s, withLanguage))) res.Add(c);
            return res;
        }

        /// <summary>The categories with at least one visible channel; those left with none disappear. With no active filters, the list as it is.</summary>
        public static List<Category> VisibleCategories(IEnumerable<Category> categories, UserSettings s)
        {
            var res = new List<Category>();
            if (categories == null) return res;
            bool all = !Active(s, true);
            foreach (var cat in categories)
                if (all || cat.Channels.Any(c => Passes(c, s, true))) res.Add(cat);
            return res;
        }
    }
}
