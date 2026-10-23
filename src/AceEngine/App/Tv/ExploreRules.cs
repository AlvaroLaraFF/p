using System;
using System.Collections.Generic;

namespace AceEngine.App.Tv
{
    /// <summary>
    /// Classification of channels into the 12 Explore kinds by keywords of the name and of the category. A channel can be
    /// in several kinds. The table is designed for Spanish-language channels (with what appears in the real lists); what fits no kind is still
    /// in the catalog and in the search.
    /// </summary>
    internal static class ExploreRules
    {
        public const int FootballBit = 1 << 0, BasketballBit = 1 << 1, MotorsportBit = 1 << 2, TennisBit = 1 << 3, SportsBit = 1 << 4, NewsBit = 1 << 5,
            CinemaBit = 1 << 6, SeriesBit = 1 << 7, DocumentariesBit = 1 << 8, MusicBit = 1 << 9, KidsBit = 1 << 10, InternationalBit = 1 << 11;

        public static readonly string[] Ids = { "futbol", "baloncesto", "motor", "tenis", "deportes", "noticias", "cine", "series", "documentales", "musica", "infantil", "internacional" };
        public static readonly string[] Names = { "Fútbol", "Baloncesto", "Motor", "Tenis", "Deportes", "Noticias", "Cine", "Series", "Documentales", "Música", "Infantil", "Internacional" };

        // Syntax (over normalized text: lowercase, no accents): "word" = whole word; "word*" = starts with; "*word" = ends with;
        // "*word*" = contains; "two words" = that sequence. An entry with a leading "-" excludes the kind when it matches.
        private static readonly string[][] Keywords =
        {
            /* futbol */ new[] { "futbol*", "football", "soccer", "calcio", "laliga", "la liga", "liga de campeones", "champions", "uefa", "fifa", "europa league", "conference league",
                "bundesliga", "premier league", "serie a", "ligue 1", "copa del rey", "hypermotion", "*rfef", "gol", "real madrid", "barca", "футбол*", "премьер*" },
            /* baloncesto */ new[] { "baloncesto", "basket*", "basquet*", "nba", "wnba", "acb", "euroliga", "euroleague", "liga endesa", "баскетбол*" },
            /* motor */ new[] { "f1", "formula 1", "formula1", "formula e", "motogp", "moto gp", "motor*", "automovilismo", "rally*", "wrc", "nascar", "indycar", "superbike*", "dakar", "motociclismo", "motocross" },
            /* tenis */ new[] { "*tenis*", "*tennis*", "atp", "wta", "roland garros", "wimbledon", "us open", "australian open", "copa davis", "davis cup" },
            /* deportes */ new[] { "deporte*", "deportivo*", "sport*", "esport*", "eurosport*", "*sport", "*sport+", "skysp*", "dazn*", "bein*", "espn*", "eleven", "ziggo", "setanta", "viasat", "gol", "golf",
                "ufc", "wwe", "mma", "boxeo*", "boxing", "fight*", "ciclismo", "cycling", "rugby", "nfl", "nhl", "mlb", "hockey", "cricket", "balonmano", "handball", "voleibol", "natacion", "atletismo",
                "olimpic*", "padel", "hipica", "m 1 global", "спорт*", "матч*", "кхл", "бокс*", "хоккей*", "конный*", "экстрим*", "старт" },
            /* noticias */ new[] { "noticia*", "noticiero*", "news", "*news*", "informativo*", "telediario*", "24 horas", "24h", "cnn", "bloomberg", "al jazeera", "cnbc", "nachrichten", "haber*" },
            /* cine */ new[] { "cine*", "cinema*", "movie*", "film*", "pelicula*", "estreno*", "hollywood", "tcm", "paramount", "cinemax", "sundance", "movistar accion", "dark" },
            /* series */ new[] { "serie*", "-serie a", "originales", "calle 13", "comedy central", "comedia", "axn", "hbo", "warner", "syfy", "amc", "netflix", "dark" },
            /* documentales */ new[] { "documental*", "documentary", "documentaries", "docu*", "discovery", "national geographic", "nat geo", "natgeo", "history", "historia", "odisea", "dmax", "animal planet", "bbc earth" },
            /* musica */ new[] { "musica*", "music*", "mtv", "vh1", "radio*", "concierto*", "los 40", "los40", "40 principales", "kiss tv", "hit tv", "cadena 100", "vevo" },
            /* infantil */ new[] { "infantil*", "kids", "ninos", "junior", "disney*", "nick*", "cartoon*", "boing", "baby tv", "clan", "dreamworks", "pocoyo", "dibujos", "super3", "cbeebies" },
            /* internacional */ new string[0]    // decided by markers, see IsInternational
        };

        private sealed class Compiled { public string[] Include, Exclude; }
        private static readonly Compiled[] Table = Compile();

        private static Compiled[] Compile()
        {
            var res = new Compiled[Keywords.Length];
            for (int k = 0; k < Keywords.Length; k++)
            {
                var inc = new List<string>();
                var exc = new List<string>();
                foreach (var w in Keywords[k])
                {
                    bool neg = w.Length > 0 && w[0] == '-';
                    string p = Pattern(neg ? w.Substring(1) : w);
                    (neg ? exc : inc).Add(p);
                }
                res[k] = new Compiled { Include = inc.ToArray(), Exclude = exc.ToArray() };
            }
            return res;
        }

        /// <summary>"word" -> " word "; "word*" -> " word"; "*word" -> "word "; "*word*" -> "word".</summary>
        private static string Pattern(string w)
        {
            bool starts = w.Length > 0 && w[0] == '*', ends = w.Length > 1 && w[w.Length - 1] == '*';
            string core = w.Trim('*');
            return (starts ? "" : " ") + core + (ends ? "" : " ");
        }

        // Country codes (ISO 3166) that appear in parentheses or brackets in the names of foreign channels: "(DE)", "[UK]". "ES" is not there: it is Spain.
        private static readonly HashSet<string> CountryCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "AD","AR","AT","AU","BE","BG","BR","BY","CA","CH","CL","CN","CO","CZ","DE","DK","EE","EG","FI","FR","GB","GR","HR","HU","IE","IL","IN","IR","IS","IT","JP","KR","LT","LU","LV","MA","MX",
            "NL","NO","NZ","PE","PL","PT","RO","RS","RU","SA","SE","SI","SK","TR","UA","UK","US","UY","VE","ZA"
        };

        // Country codes that also count at the end of the name without parentheses ("Canal+ Sport 3 PL", "FOX DEPORTES AR HD"); those that are words (NO, SE, IN, IS) do not.
        private static readonly HashSet<string> TrailingCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "UK","US","PL","DE","FR","IT","PT","BR","AR","MX","TR","RU","NL","BE","AT","CH"
        };

        /// <summary>
        /// Mask of the Explore kinds the channel belongs to. The name rules; the category only decides when the name fits no kind
        /// ("M+ Vamos 2" says nothing, but hangs under "Movistar Deportes"; on the other hand "DAZN MotoGP" is not cinema just because it hangs under a "Movistar Cine" category).
        /// </summary>
        public static int Classify(Channel c)
        {
            string name = " " + TextNorm.Normalize(c.Name) + " ";
            string full = name + "| " + TextNorm.Normalize(c.CategoryName) + " ";
            int mask = MatchKinds(name);
            if (mask == 0) mask = MatchKinds(full);
            if (IsInternational(c, full)) mask |= InternationalBit;
            return mask;
        }

        private static int MatchKinds(string text)
        {
            int mask = 0;
            for (int k = 0; k < Table.Length; k++)
                if (Matches(Table[k], text)) mask |= 1 << k;
            // "Deportes" (sports) is the catch-all: everything that belongs to a specific discipline is also sport
            if ((mask & (FootballBit | BasketballBit | MotorsportBit | TennisBit)) != 0) mask |= SportsBit;
            return mask;
        }

        private static bool Matches(Compiled t, string text)
        {
            foreach (var p in t.Exclude) if (text.IndexOf(p, StringComparison.Ordinal) >= 0) return false;
            foreach (var p in t.Include) if (text.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// International = clear markers that it is not a national channel: the category or the name say "internacional", the name carries a country other than ES
        /// in parentheses or brackets or at the end ("(DE)", "[UK]", "... PL"), or uses a non-Latin alphabet (Cyrillic, Arabic, Greek...).
        /// </summary>
        private static bool IsInternational(Channel c, string normalizedText)
        {
            if (normalizedText.IndexOf(" internacional", StringComparison.Ordinal) >= 0 || normalizedText.IndexOf(" international", StringComparison.Ordinal) >= 0) return true;
            string name = c.SourceName;
            if (string.IsNullOrEmpty(name)) name = c.Name ?? "";
            name = name.TrimEnd('*', ' ');
            return HasForeignScript(name) || HasCountryCode(name) || HasTrailingCountry(name);
        }

        private static bool HasForeignScript(string s)
        {
            foreach (char ch in s) if (char.IsLetter(ch) && !TextNorm.IsLatinLetter(ch) && ch != 'ª' && ch != 'º') return true;
            return false;
        }

        private static bool HasCountryCode(string s)
        {
            for (int i = 0; i + 3 < s.Length; i++)
            {
                char open = s[i];
                if (open != '(' && open != '[') continue;
                char close = open == '(' ? ')' : ']';
                if (s[i + 3] != close) continue;
                char a = s[i + 1], b = s[i + 2];
                if (a < 'A' || a > 'Z' || b < 'A' || b > 'Z') continue;
                if (CountryCodes.Contains(new string(new[] { a, b }))) return true;
            }
            return false;
        }

        /// <summary>Last word of the name (without quality labels) in uppercase and in the country list.</summary>
        private static bool HasTrailingCountry(string s)
        {
            string[] words = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = words.Length - 1; i >= 1; i--)
            {
                string w = words[i];
                if (QualityRules.Detect(w).Length > 0 || w.Equals("HD", StringComparison.OrdinalIgnoreCase) || w.Equals("SD", StringComparison.OrdinalIgnoreCase)) continue;
                return w.Length == 2 && TrailingCodes.Contains(w);
            }
            return false;
        }
    }
}
