using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace YouTubeDownloader
{
    public static class UpdateChecker
    {
        public const string LatestUrl = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

        private static readonly HttpClient Http = CreateClient();
        private static string _lastETag;
        private static string _lastVersion;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            string product = System.Windows.Forms.Application.ProductName ?? "YouTubeDownloader";
            string version = System.Windows.Forms.Application.ProductVersion ?? "1.1";
            client.DefaultRequestHeaders.UserAgent.ParseAdd(product + "/" + version + "-portable");
            return client;
        }

        public static async Task<string> GetLatestVersionAsync()
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
                if (_lastETag != null)
                {
                    req.Headers.TryAddWithoutValidation("If-None-Match", _lastETag);
                }

                using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);

                if (resp.StatusCode == System.Net.HttpStatusCode.NotModified && _lastVersion != null)
                {
                    return _lastVersion;
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
                {
                    throw new Exception("GitHub API rate limit exceeded.");
                }

                resp.EnsureSuccessStatusCode();

                if (resp.Headers.ETag != null)
                {
                    _lastETag = resp.Headers.ETag.ToString();
                }

                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tag_name", out JsonElement tag)
                    && tag.ValueKind == JsonValueKind.String)
                {
                    _lastVersion = tag.GetString();
                    return _lastVersion;
                }

                throw new Exception("Unexpected GitHub API response.");
            }
            catch (HttpRequestException hex)
            {
                throw new Exception("Update check failed: " + hex.Message, hex);
            }
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
