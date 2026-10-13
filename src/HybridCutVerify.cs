using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace YouTubeDownloader
{
    // Acceptance gate для hybrid-результата: БЫСТРАЯ проверка контейнера
    // через ffprobe БЕЗ декодирования (только заголовки). Полный decode
    // готового файла из обычного Save исключён: это был главный тормоз
    // (~десятки секунд на сотни секунд выхода). Frame-exactness границ
    // по-прежнему guard'ится ДО encode: HybridCut.VerifyFrameBudget в TryPlan
    // + точный покадровый select зоны. Здесь — только sanity-структура:
    // файл открывается, video/audio streams на месте, duration совпала,
    // параметры video sane, файл не пустой. Любой провал → fallback,
    // контракт возврата (null = ok, строка = причина) не менялся.
    public static class HybridCutVerify
    {
        public static string CheckOutput(HybridCutRunner.Ctx ctx, string output)
        {
            try
            {
                double expected = 0;
                for (int i = 0; i < ctx.Plan.Regions.Count; i++)
                    expected += ctx.Plan.Regions[i].End - ctx.Plan.Regions[i].Start;
                Meta m;
                string metaError = ProbeMeta(output, out m);
                if (metaError != null) return "verify: " + metaError;
                if (!m.HasVideo || m.Width <= 0 || m.Height <= 0) return "verify: no video stream";
                if (!(m.Fps > 0)) return "verify: unknown fps";
                if (!HybridCut.IsSupportedVideoCodec(m.VideoCodec)) return "verify: bad video codec";
                if (ctx.HasAudio && !m.HasAudio) return "verify: audio missing";
                if (m.Size <= 1024) return "verify: output too small";
                double tol = DurationToleranceSec(ctx.Plan.Regions.Count, ctx.Plan.Fps);
                if (!(m.Duration > 0) || Math.Abs(m.Duration - expected) > tol)
                    return "verify: duration exp=" + expected.ToString("0.000",
                        System.Globalization.CultureInfo.InvariantCulture)
                        + " got=" + m.Duration.ToString("0.000",
                        System.Globalization.CultureInfo.InvariantCulture);
                return null;
            }
            catch (Exception ex) { return "verify exception: " + ex.Message; }
        }

        // Допуск duration в секундах: старый кадровый допуск max(3, regions*2),
        // переведённый в секунды, + epsilon на квантование контейнера, с полом
        // 0.25 с против ложных fallback (ложный fallback = полный encode).
        public static double DurationToleranceSec(int regionCount, double fps)
        {
            if (!(fps > 0)) return 1.0;
            double tol = ((double)Math.Max(3, regionCount * 2)) / fps + 0.05;
            if (tol < 0.25) tol = 0.25;
            return tol;
        }

        private sealed class Meta
        {
            public bool HasVideo;
            public bool HasAudio;
            public string VideoCodec = "";
            public int Width;
            public int Height;
            public double Fps;
            public double Duration;
            public long Size;
        }

        // Быстрый мета-проход БЕЗ декодирования: ffprobe читает только
        // заголовки контейнера (доли секунды). -count_frames здесь запрещён —
        // он декодирует весь файл и возвращает тормоз, который мы убираем.
        private static string ProbeMeta(string path, out Meta meta)
        {
            meta = new Meta();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfprobeExe;
                psi.Arguments = YtDlpRunner.FormatArgs(new string[]
                {
                    "-v", "error",
                    "-show_entries", "format=duration,size",
                    "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate,r_frame_rate",
                    "-of", "flat", path
                });
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
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return "meta timeout"; }
                    if (p.ExitCode != 0) return "meta probe failed: " + se.Trim();
                    ParseMeta(so, meta);
                    return null;
                }
            }
            catch (Exception ex) { return "meta exception: " + ex.Message; }
        }

        private static void ParseMeta(string flat, Meta meta)
        {
            Dictionary<int, string> types = new Dictionary<int, string>();
            Dictionary<int, string> codecs = new Dictionary<int, string>();
            Dictionary<int, int> widths = new Dictionary<int, int>();
            Dictionary<int, int> heights = new Dictionary<int, int>();
            Dictionary<int, string> avgFps = new Dictionary<int, string>();
            Dictionary<int, string> rFps = new Dictionary<int, string>();
            string[] lines = flat.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string val = Unquote(line.Substring(eq + 1).Trim());
                string key = line.Substring(0, eq);
                if (key == "format.duration") { meta.Duration = ParseDouble(val); continue; }
                if (key == "format.size")
                {
                    long sz;
                    if (long.TryParse(val, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out sz)) meta.Size = sz;
                    continue;
                }
                if (!key.StartsWith("streams.stream.", StringComparison.Ordinal)) continue;
                string rest = key.Substring("streams.stream.".Length);
                int dot = rest.IndexOf('.');
                if (dot <= 0) continue;
                int idx;
                if (!int.TryParse(rest.Substring(0, dot),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out idx)) continue;
                string field = rest.Substring(dot + 1);
                if (field == "codec_type") types[idx] = val;
                else if (field == "codec_name") codecs[idx] = val;
                else if (field == "width") widths[idx] = ParseInt(val);
                else if (field == "height") heights[idx] = ParseInt(val);
                else if (field == "avg_frame_rate") avgFps[idx] = val;
                else if (field == "r_frame_rate") rFps[idx] = val;
            }
            List<int> ordered = new List<int>(types.Keys);
            ordered.Sort();
            for (int i = 0; i < ordered.Count; i++)
            {
                int idx = ordered[i];
                string t = types[idx];
                if (string.Equals(t, "video", StringComparison.Ordinal) && !meta.HasVideo)
                {
                    meta.HasVideo = true;
                    string c;
                    if (codecs.TryGetValue(idx, out c)) meta.VideoCodec = c;
                    int w;
                    if (widths.TryGetValue(idx, out w)) meta.Width = w;
                    int h;
                    if (heights.TryGetValue(idx, out h)) meta.Height = h;
                    string f = null;
                    string af;
                    if (avgFps.TryGetValue(idx, out af)) f = af;
                    meta.Fps = HybridCut.ParseFps(f);
                    if (!(meta.Fps > 0))
                    {
                        string rf;
                        if (rFps.TryGetValue(idx, out rf)) meta.Fps = HybridCut.ParseFps(rf);
                    }
                }
                if (string.Equals(t, "audio", StringComparison.Ordinal)) meta.HasAudio = true;
            }
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2);
            return s;
        }

        private static int ParseInt(string s)
        {
            int v;
            if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        private static double ParseDouble(string s)
        {
            double v;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }
    }
}
