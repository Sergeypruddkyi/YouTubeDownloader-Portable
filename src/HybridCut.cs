using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace YouTubeDownloader
{
    public static class HybridCut
    {
        public sealed class Plan
        {
            public bool Eligible;
            public string Reason = "";
            public List<TrimRegion> Regions = new List<TrimRegion>();
            public List<double> HeadEnds = new List<double>();
            public List<double> Keyframes = new List<double>();
            public string VideoCodec = "";
            public double Fps;
            public int AudioRate;
        }

        public const double MinCopyTailSeconds = 0.25;
        public const double MinKeepMultipleOfMaxGop = 2.0;

        public static bool IsSupportedVideoCodec(string codec)
        {
            if (string.IsNullOrEmpty(codec)) return false;
            string c = codec.ToLowerInvariant();
            return c == "av1" || c == "h264" || c == "vp9" || c == "hevc" || c == "h265";
        }

        public static string[] ZoneVideoArgs(string codec)
        {
            if (string.IsNullOrEmpty(codec)) return null;
            string c = codec.ToLowerInvariant();
            if (c == "av1") return new string[] { "-c:v", "libaom-av1", "-cpu-used", "8", "-crf", "30", "-b:v", "0", "-pix_fmt", "yuv420p" };
            if (c == "h264") return new string[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p" };
            if (c == "hevc" || c == "h265") return new string[] { "-c:v", "libx265", "-preset", "veryfast", "-crf", "24", "-pix_fmt", "yuv420p" };
            if (c == "vp9") return new string[] { "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "30", "-pix_fmt", "yuv420p" };
            return null;
        }

        public static Plan TryPlan(string input, List<TrimRegion> regions, string videoCodec, double fps, double duration, bool hasAudio = false, int audioRate = 0)
        {
            Plan plan = new Plan();
            plan.Regions = regions ?? new List<TrimRegion>();
            plan.VideoCodec = (videoCodec ?? "").ToLowerInvariant();
            plan.Fps = fps;
            if (plan.Regions.Count == 0) { plan.Reason = "no regions"; return plan; }
            if (!IsSupportedVideoCodec(plan.VideoCodec)) { plan.Reason = "unsupported video codec: " + videoCodec; return plan; }
            if (!(fps > 0) || fps > 240) { plan.Reason = "unknown fps"; return plan; }
            if (hasAudio)
            {
                int rate = audioRate > 0 ? audioRate : ProbeAudioRate(input);
                if (rate <= 0) { plan.Reason = "unknown audio rate"; return plan; }
                plan.AudioRate = rate;
            }
            List<double> keys;
            try { keys = ProbeKeyframes(input); }
            catch (Exception ex) { plan.Reason = "keyframe probe failed: " + ex.Message; return plan; }
            if (keys == null || keys.Count == 0) { plan.Reason = "no keyframes found"; return plan; }
            plan.Keyframes = keys;
            double maxGop = EstimateMaxGop(keys, duration);
            double keepSum = 0;
            for (int i = 0; i < plan.Regions.Count; i++) keepSum += plan.Regions[i].End - plan.Regions[i].Start;
            if (maxGop > 0 && keepSum < maxGop * MinKeepMultipleOfMaxGop)
            {
                plan.Reason = "keep too short vs GOP (full encode cheaper)";
                return plan;
            }
            plan.HeadEnds = PlanHeadEnds(keys, plan.Regions, fps);
            // Verify: каждый zone-сегмент обязан давать ровно ожидаемое число кадров.
            // Если keyframe-карта или fps врут (напр. VFR-дрейф), границы поплывут —
            // тогда hybrid запрещён и Save пойдёт через проверенный полный encode.
            if (!VerifyFrameBudget(keys, plan.Regions, plan.HeadEnds, fps))
            {
                plan.Reason = "frame budget mismatch (fps/keyframes unreliable)";
                return plan;
            }
            plan.Eligible = true;
            return plan;
        }

        // Ожидаемое число кадров каждого куска: round((end-start)*fps).
        // Зона [rs,he) обязана содержать целое число кадров с допуском ±1
        // (rounding входной границы), иначе select вырежет не то.
        public static bool VerifyFrameBudget(List<double> keys, List<TrimRegion> regions,
            List<double> heads, double fps)
        {
            if (keys == null || regions == null || heads == null || heads.Count != regions.Count) return false;
            if (!(fps > 0)) return false;
            for (int i = 0; i < regions.Count; i++)
            {
                double rs = regions[i].Start;
                double he = heads[i];
                if (he <= rs + 0.0005) continue; // чистый copy, зоны нет
                int ki = PrevKeyIndex(keys, rs);
                double k0 = keys[ki];
                long n0 = (long)Math.Round((rs - k0) * fps);
                long n1 = (long)Math.Round((he - k0) * fps);
                if (n1 <= n0) return false;
                // Длина зоны обязана совпадать с целым числом кадров ±полкадра.
                double zoneLen = he - rs;
                double frac = zoneLen * fps - Math.Round(zoneLen * fps);
                if (Math.Abs(frac) > 0.5 + 1e-6) return false;
                if (n0 < 0 || n1 < 0) return false;
            }
            return true;
        }

        public static List<double> PlanHeadEnds(List<double> keys, List<TrimRegion> regions, double fps)
        {
            List<double> heads = new List<double>();
            if (keys == null || regions == null || !(fps > 0)) return heads;
            for (int i = 0; i < regions.Count; i++)
            {
                double s = regions[i].Start;
                double e = regions[i].End;
                if (s <= 0.001) { heads.Add(s); continue; }
                int ki = PrevKeyIndex(keys, s);
                if (s - keys[ki] <= 0.5 / fps) { heads.Add(s); continue; }
                int ni = NextKeyIndex(keys, s);
                double headEnd = ni < keys.Count ? keys[ni] : e;
                if (headEnd > e) headEnd = e;
                if (e - headEnd < MinCopyTailSeconds) headEnd = e;
                heads.Add(headEnd);
            }
            return heads;
        }

        public static int PrevKeyIndex(List<double> keys, double t)
        {
            int lo = 0;
            int hi = keys.Count - 1;
            int ans = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (keys[mid] <= t + 1e-9) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        public static int NextKeyIndex(List<double> keys, double t)
        {
            int lo = 0;
            int hi = keys.Count - 1;
            int ans = keys.Count;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (keys[mid] > t + 1e-9) { ans = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            return ans;
        }

        public static List<double> ProbeKeyframes(string input)
        {
            List<double> keys = new List<double>();
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = AppPaths.FfprobeExe;
            psi.Arguments = YtDlpRunner.FormatArgs(new string[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", input });
            psi.WorkingDirectory = AppPaths.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            using (Process p = Process.Start(psi))
            {
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } throw new IOException("ffprobe keyframe probe timed out"); }
                if (p.ExitCode != 0) throw new IOException("ffprobe keyframe probe failed: " + se.Trim());
                string[] lines = so.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0) continue;
                    int comma = line.IndexOf(',');
                    if (comma <= 0) continue;
                    string ts = line.Substring(0, comma).Trim();
                    string fl = line.Substring(comma + 1).Trim();
                    if (fl.IndexOf('K') < 0) continue;
                    double t;
                    if (!double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out t)) continue;
                    if (t < 0) continue;
                    if (keys.Count == 0 || t - keys[keys.Count - 1] > 1e-6) keys.Add(t);
                }
            }
            return keys;
        }

        public static double ProbeFps(string input)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfprobeExe;
                psi.Arguments = YtDlpRunner.FormatArgs(new string[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=r_frame_rate,avg_frame_rate", "-of", "flat", input });
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return 0; }
                    if (p.ExitCode != 0) return 0;
                    double best = 0;
                    string[] lines = so.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        double f = ParseFps(line.Substring(eq + 1).Trim().Trim('"'));
                        if (f > best) best = f;
                    }
                    return best;
                }
            }
            catch { return 0; }
        }

        public static int ProbeAudioRate(string input)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfprobeExe;
                psi.Arguments = YtDlpRunner.FormatArgs(new string[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate", "-of", "csv=p=0", input });
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return 0; }
                    if (p.ExitCode != 0) return 0;
                    string[] lines = so.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        int v;
                        if (int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0)
                            return v;
                    }
                    return 0;
                }
            }
            catch { return 0; }
        }

        public static long ToSample(double seconds, int rate)
        {
            if (!(rate > 0)) rate = 44100;
            return (long)Math.Round(seconds * (double)rate);
        }

        public static double CopyDuration(double from, double to)
        {
            double d = to - from;
            return d > 0 ? d : 0;
        }

        public static double ParseFps(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim().Trim('"');
            int slash = s.IndexOf('/');
            if (slash > 0)
            {
                double n, d;
                if (double.TryParse(s.Substring(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out n)
                    && double.TryParse(s.Substring(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d != 0)
                {
                    double f = n / d;
                    if (f > 0 && f <= 240) return f;
                    return 0;
                }
                return 0;
            }
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 && v <= 240) return v;
            return 0;
        }

        public static string Fmt(double v)
        {
            return v.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        public static double EstimateMaxGop(List<double> keys, double duration)
        {
            if (keys == null || keys.Count < 2) return 0;
            double mx = 0;
            for (int i = 1; i < keys.Count; i++)
            {
                double g = keys[i] - keys[i - 1];
                if (g > mx) mx = g;
            }
            if (duration > 0 && keys.Count > 0)
            {
                double tail = duration - keys[keys.Count - 1];
                if (tail > mx) mx = tail;
            }
            return mx;
        }
    }
}
