using System;
using System.Text.RegularExpressions;

namespace YouTubeDownloader
{
    public static class YouTubeUrl
    {
        private static readonly Regex Rx = new Regex(
            @"^(?:https?://)?(?:www\.|m\.|music\.)?(?:youtube\.com/(?:watch\?(?:[^#&]*&)*v=(?<id>[\w-]{11})(?:[&][^ ]*)?|shorts/(?<id>[\w-]{11})(?:[/?][^ ]*)?|live/(?<id>[\w-]{11})(?:[/?][^ ]*)?|embed/(?<id>[\w-]{11})(?:[/?][^ ]*)?|v/(?<id>[\w-]{11})(?:[/?][^ ]*)?)|youtu\.be/(?<id>[\w-]{11})(?:[?/][^ ]*)?)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsValid(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string t = url.Trim();
            foreach (char ch in t)
            {
                if (ch <= ' ' || ch == '"' || ch == '\\') return false;
            }
            return Rx.IsMatch(t);
        }

        public static string ExtractFirst(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] tokens = text.Split(
                new[] { ' ', '\t', '\r', '\n', '"', '\'', '<', '>', '|', ',', ')', '(', ']' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string t in tokens)
            {
                string cand = t.Trim();
                if (IsValid(cand)) return cand;
            }
            return null;
        }
    }
}
