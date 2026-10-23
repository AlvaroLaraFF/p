using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AceEngine.Util;

namespace AceEngine.Sources
{
    /// <summary>
    /// Import of M3U / M3U8 lists (second source of links, besides mylinkspaste): #EXTINF lines with attributes (group-title, tvg-*)
    /// and title, followed by an acestream://, magnet: or http(s) URL. Tolerates BOM, CRLF, #EXTGRP, #EXTVLCOPT, URLs without #EXTINF and attributes with commas.
    /// </summary>
    public static class M3uParser
    {
        private static readonly Regex Attr = new Regex("([A-Za-z0-9_\\-]+)=\"([^\"]*)\"", RegexOptions.Compiled);

        /// <summary>Cap on entries and on line/name length (a hostile list cannot fill the memory or the screen).</summary>
        public const int MaxEntries = 20000;
        public const int MaxLineLength = 4096;
        public const int MaxNameLength = 200;

        public static List<LinkEntry> Parse(string text, string baseUrl = null)
        {
            var res = new List<LinkEntry>();
            if (string.IsNullOrEmpty(text)) return res;
            if (text[0] == '﻿') text = text.Substring(1);
            string title = null, group = null, extGroup = null;
            bool hls = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.Length > MaxLineLength) { title = null; group = null; continue; }
                if (res.Count >= MaxEntries) break;
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#EXT-X-TARGETDURATION", StringComparison.OrdinalIgnoreCase)) hls = true;
                if (line[0] == '#')
                {
                    if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseExtInf(line.Substring(8), out title, out group);
                    }
                    else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase)) extGroup = line.Substring(8).Trim();
                    continue;
                }
                string url = line;
                if (baseUrl != null && url.IndexOf("://", StringComparison.Ordinal) < 0 && !url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("acestream:", StringComparison.OrdinalIgnoreCase))
                {
                    Uri b, abs;
                    if (Uri.TryCreate(baseUrl, UriKind.Absolute, out b) && Uri.TryCreate(b, url, out abs)) url = abs.ToString();
                }
                var e = LinkEntry.FromUrl(Clip(title ?? DefaultName(url), MaxNameLength), url, Clip(group ?? extGroup, MaxNameLength));
                res.Add(e);
                title = null; group = null;
            }
            // An HLS playlist (master or media) is not a channel list: its lines are not interpreted as links
            if (hls && baseUrl != null) return new List<LinkEntry> { LinkEntry.FromUrl(baseUrl.Substring(baseUrl.LastIndexOf('/') + 1), baseUrl) };
            return res;
        }

        private static string Clip(string s, int n) { return s == null || s.Length <= n ? s : s.Substring(0, n); }

        private static void ParseExtInf(string s, out string title, out string group)
        {
            title = null; group = null;
            // "<duration> attributes...,<title>": the comma that separates the title is the first one outside quotes (the title may contain commas)
            int comma = -1;
            bool inQ = false;
            for (int i = 0; i < s.Length && comma < 0; i++)
            {
                if (s[i] == '"') inQ = !inQ;
                else if (s[i] == ',' && !inQ) comma = i;
            }
            string head = comma >= 0 ? s.Substring(0, comma) : s;
            string t = comma >= 0 ? s.Substring(comma + 1).Trim() : null;
            foreach (Match m in Attr.Matches(head))
            {
                string k = m.Groups[1].Value.ToLowerInvariant();
                if (k == "group-title") group = m.Groups[2].Value;
                else if (k == "tvg-name" && string.IsNullOrEmpty(t)) t = m.Groups[2].Value;
            }
            title = string.IsNullOrEmpty(t) ? null : t;
        }

        private static string DefaultName(string url)
        {
            int q = url.IndexOfAny(new[] { '?', '#' });
            string p = q >= 0 ? url.Substring(0, q) : url;
            int slash = p.LastIndexOf('/');
            string n = slash >= 0 && slash < p.Length - 1 ? p.Substring(slash + 1) : p;
            return n.Length > 60 ? n.Substring(0, 60) : n;
        }

        /// <summary>Reads a list from a local path or an http(s) URL.</summary>
        public static async Task<List<LinkEntry>> LoadAsync(string pathOrUrl, HttpClient http, CancellationToken ct)
        {
            if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                bool own = http == null;
                if (own) http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                try
                {
                    using (var resp = await http.GetAsync(pathOrUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        var bytes = await HttpLimits.ReadBodyAsync(resp.Content, HttpLimits.M3uBody, ct).ConfigureAwait(false);
                        return Parse(DecodeText(bytes), pathOrUrl);
                    }
                }
                finally { if (own) http.Dispose(); }
            }
            string path = pathOrUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(pathOrUrl).LocalPath : pathOrUrl;
            return Parse(DecodeText(File.ReadAllBytes(path)), null);
        }

        /// <summary>UTF-8 (with or without BOM); if it is not valid UTF-8, Latin-1.</summary>
        public static string DecodeText(byte[] bytes)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (ArgumentException) { return Encoding.GetEncoding("iso-8859-1").GetString(bytes); }
        }
    }
}
