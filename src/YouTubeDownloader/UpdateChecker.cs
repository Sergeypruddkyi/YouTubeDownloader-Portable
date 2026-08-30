using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace YouTubeDownloader
{
    public static class UpdateChecker
    {
        public const string LatestUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

        public static Task<string> GetLatestVersionAsync()
        {
            return Task.Run(new Func<string>(delegate
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)12288;
                }
                catch { }
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(LatestUrl);
                req.UserAgent = "YouTubeDownloader-GUI/1.0 (portable)";
                req.Method = "GET";
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                {
                    string json = sr.ReadToEnd();
                    Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (!m.Success) throw new Exception("Unexpected GitHub API response.");
                    return m.Groups[1].Value;
                }
            }));
        }

        public static int CompareVersions(string a, string b)
        {
            long[] sa = Parse(a);
            long[] sb = Parse(b);
            int n = Math.Max(sa.Length, sb.Length);
            for (int i = 0; i < n; i++)
            {
                long x = i < sa.Length ? sa[i] : 0;
                long y = i < sb.Length ? sb[i] : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        private static long[] Parse(string v)
        {
            List<long> list = new List<long>();
            if (!string.IsNullOrEmpty(v))
            {
                v = v.TrimStart('v');
                foreach (string p in v.Split('.'))
                {
                    string digits = Regex.Match(p, "\\d+").Value;
                    long val;
                    if (digits.Length > 0 && long.TryParse(digits, out val)) list.Add(val);
                }
            }
            return list.ToArray();
        }
    }
}
