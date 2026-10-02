using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace YouTubeDownloader
{
    public static class HybridCutSteps
    {
        public static HybridCutRunner.StepResult RunZone(HybridCutRunner.Ctx ctx, int regionIdx,
            double zoneStart, double zoneEnd, string[] zoneArgs)
        {
            HybridCutRunner.StepResult r = new HybridCutRunner.StepResult();
            HybridCut.Plan plan = ctx.Plan;
            int ki = HybridCut.PrevKeyIndex(plan.Keyframes, zoneStart);
            double k0 = plan.Keyframes[ki];
            double fps = plan.Fps;
            long n0 = (long)Math.Round((zoneStart - k0) * fps);
            long n1 = (long)Math.Round((zoneEnd - k0) * fps);
            if (n1 <= n0) { r.Error = "empty zone"; return r; }
            string zp = Path.Combine(ctx.WorkDir, "z" + regionIdx + ".mp4");
            List<string> args = new List<string>();
            args.Add("-hide_banner");
            args.Add("-ss");
            args.Add(HybridCut.Fmt(k0));
            args.Add("-i");
            args.Add(ctx.Input);
            args.Add("-vf");
            args.Add("select='gte(n," + n0 + ")*lt(n," + n1 + ")',setpts=PTS-STARTPTS");
            // TASK-009 recipe: зона — CFR с явным -r исходного fps, иначе concat
            // зоны (re-encode) с copy-хвостами дрейфует по стыкам.
            args.Add("-fps_mode");
            args.Add("cfr");
            args.Add("-r");
            args.Add(fps.ToString("0.######", CultureInfo.InvariantCulture));
            args.Add("-g");
            args.Add(Math.Max(1, (int)Math.Round(fps)).ToString(CultureInfo.InvariantCulture));
            args.Add("-video_track_timescale");
            args.Add("15360");
            args.Add("-an");
            for (int k = 0; k < zoneArgs.Length; k++) args.Add(zoneArgs[k]);
            // Стоп-ограничитель зоны: без него FFmpeg после seek декодирует
            // всё до конца файла (~328 с / ~19.7k кадров ради ~1.6 с зоны),
            // отбрасывая лишнее в select. -frames:v останавливает чтение
            // входа сразу после нужного числа кадров зоны (n1-n0).
            args.Add("-frames:v");
            args.Add((n1 - n0).ToString(CultureInfo.InvariantCulture));
            args.Add("-y");
            args.Add(zp);
            r = RunFfmpeg(ctx, args.ToArray());
            if (!r.Ok) return r;
            if (!File.Exists(zp)) { r.Ok = false; r.Error = "zone output missing"; return r; }
            return r;
        }

        public static HybridCutRunner.StepResult RunCopy(HybridCutRunner.Ctx ctx,
            double from, double to, string outPath)
        {
            List<string> args = new List<string>();
            args.Add("-hide_banner");
            // TASK-009 recipe: входной -ss (до -i) + copy + абсолютный -to.
            // Выходной -ss (после -i) с copy даёт дрейф frame count
            // (лишние/потерянные кадры на стыках, проверено на Test.mp4).
            args.Add("-ss");
            args.Add(HybridCut.Fmt(from));
            args.Add("-i");
            args.Add(ctx.Input);
            // -to после входного -ss — это метка ВЫХОДНОГО времени (от 0 после
            // make_zero), поэтому здесь нужен именно duration через -t,
            // иначе copy-хвост получится длиной `to`, а не `to-from`.
            if (to > from + 0.0005) { args.Add("-t"); args.Add(HybridCut.Fmt(HybridCut.CopyDuration(from, to))); }
            args.Add("-c:v");
            args.Add("copy");
            args.Add("-an");
            args.Add("-video_track_timescale");
            args.Add("15360");
            args.Add("-avoid_negative_ts");
            args.Add("make_zero");
            args.Add("-y");
            args.Add(outPath);
            HybridCutRunner.StepResult rc = RunFfmpeg(ctx, args.ToArray());
            if (!rc.Ok) return rc;
            if (!File.Exists(outPath)) { rc.Ok = false; rc.Error = "copy output missing"; return rc; }
            return rc;
        }

        public static HybridCutRunner.StepResult RunConcatCopy(HybridCutRunner.Ctx ctx, string vcat)
        {
            HybridCutRunner.StepResult r = new HybridCutRunner.StepResult();
            if (ctx.Parts.Count == 0) { r.Error = "no parts to concat"; return r; }
            if (ctx.Parts.Count == 1)
            {
                try { File.Copy(ctx.Parts[0], vcat, true); r.Ok = true; return r; }
                catch (Exception ex) { r.Error = "single part copy: " + ex.Message; return r; }
            }
            string list = Path.Combine(ctx.WorkDir, "vlist.txt");
            try
            {
                using (StreamWriter w = new StreamWriter(list, false, new UTF8Encoding(false)))
                {
                    for (int i = 0; i < ctx.Parts.Count; i++)
                        w.WriteLine("file '" + ctx.Parts[i].Replace("/", "//").Replace("'", "'\\''") + "'");
                }
            }
            catch (Exception ex) { r.Error = "concat list: " + ex.Message; return r; }
            List<string> args = new List<string>();
            args.Add("-hide_banner");
            args.Add("-f");
            args.Add("concat");
            args.Add("-safe");
            args.Add("0");
            args.Add("-i");
            args.Add(list);
            args.Add("-c");
            args.Add("copy");
            args.Add("-video_track_timescale");
            args.Add("15360");
            args.Add("-y");
            args.Add(vcat);
            r = RunFfmpeg(ctx, args.ToArray());
            if (!r.Ok) return r;
            if (!File.Exists(vcat)) { r.Ok = false; r.Error = "vcat missing"; return r; }
            return r;
        }

        public static HybridCutRunner.StepResult RunAudio(HybridCutRunner.Ctx ctx, string afile)
        {
            HybridCutRunner.StepResult r = new HybridCutRunner.StepResult();
            // TASK-009 recipe: sample-accurate atrim + concat через -filter_complex, AAC 128k.
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            List<string> labels = new List<string>();
            int rate = ctx.Plan.AudioRate > 0 ? ctx.Plan.AudioRate : 44100;
            for (int i = 0; i < ctx.Plan.Regions.Count; i++)
            {
                double rs = ctx.Plan.Regions[i].Start;
                double re = ctx.Plan.Regions[i].End;
                sb.Append("[0:a]atrim=start_sample=" + HybridCut.ToSample(rs, rate) + ":end_sample=" + HybridCut.ToSample(re, rate));
                sb.Append(",asetpts=PTS-STARTPTS[a" + i + "];");
                labels.Add("[a" + i + "]");
            }
            sb.Append(string.Join("", labels.ToArray()));
            sb.Append("concat=n=" + ctx.Plan.Regions.Count + ":v=0:a=1[a]");
            List<string> args = new List<string>();
            args.Add("-hide_banner");
            args.Add("-i");
            args.Add(ctx.Input);
            args.Add("-filter_complex");
            args.Add(sb.ToString());
            args.Add("-map");
            args.Add("[a]");
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("128k");
            args.Add("-y");
            args.Add(afile);
            r = RunFfmpeg(ctx, args.ToArray());
            if (!r.Ok) return r;
            if (!File.Exists(afile)) { r.Ok = false; r.Error = "audio output missing"; return r; }
            return r;
        }

        // Быстрый ADTS-based Hybrid Audio Cut (первый этап: один cut, два keep
        // региона, AAC LC 44100 stereo). Проверенный рецепт: prefix/suffix —
        // packet-exact copy в ADTS (границы по сетке 1024 сэмплов, отбор через
        // -frames:a, -ss усечённый вниз), локальный bridge PCM→AAC→ADTS с
        // удалением priming-первого и flush-последнего кадров, сборка через
        // concat-последовательность ADTS frames + один финальный MP4 mux copy.
        // Любая неприменимость или провал проверки → !Ok: caller запускает
        // существующий полный RunAudio (глобальный fallback выше по стеку).
        public static HybridCutRunner.StepResult RunAudioHybridFast(HybridCutRunner.Ctx ctx, string afile)
        {
            HybridCutRunner.StepResult r = new HybridCutRunner.StepResult();
            {
                if (ctx.Plan.Regions.Count != 2) { r.Error = "fast audio: need exactly 2 regions"; return r; }
                double rs0 = ctx.Plan.Regions[0].Start;
                double cutStart = ctx.Plan.Regions[0].End;
                double cutEnd = ctx.Plan.Regions[1].Start;
                double re1 = ctx.Plan.Regions[1].End;
                if (!(cutEnd > cutStart) || !(rs0 >= 0) || !(re1 > cutEnd)) { r.Error = "fast audio: bad regions"; return r; }

                                int rate, channels;
                string audioErr = ProbeAudioStream(ctx.Input, ctx.Plan.AudioRate, out rate, out channels);
                if (audioErr != null) { r.Error = audioErr; return r; }
                if (rate != 44100 || channels != 2) { r.Error = "fast audio: need AAC LC 44100 stereo"; return r; }

                const double contextSec = 0.5;
                double reqPrefixSeam = cutStart - contextSec;
                double reqSuffixSeam = cutEnd + contextSec;
                if (!(reqPrefixSeam >= rs0 + 0.1) || !(reqSuffixSeam <= re1 - 0.1))
                { r.Error = "fast audio: context does not fit"; return r; }
                // Сетки пакетов: prefix — floor (только целые пакеты до шва),
                // suffix — ceil (начиная с пакета шва). Всё в целых сэмплах.
                long pSeam = (long)Math.Floor(reqPrefixSeam * rate / 1024.0);
                long sSeam = (long)Math.Ceiling(reqSuffixSeam * rate / 1024.0);
                long p1 = pSeam * 1024L;
                long p2 = HybridCut.ToSample(cutStart, rate);
                long s1 = HybridCut.ToSample(cutEnd, rate);
                long s2 = sSeam * 1024L;
                long bridgeSamples = (p2 - p1) + (s2 - s1);
                if (bridgeSamples <= 4096 || bridgeSamples % 1024L == 0)
                { r.Error = "fast audio: bad bridge size"; return r; }
                long prefixPackets = pSeam - HybridCut.ToSample(rs0, rate) / 1024L;
                // Prefix обязан начинаться с 0 (первый этап: keep от начала).
                // Иначе packet-оффсеты prefix надо сдвигать — не делаем, fallback.
                if (rs0 > 0.0005 || prefixPackets <= 0) { r.Error = "fast audio: prefix must start at 0"; return r; }
                long endSample = HybridCut.ToSample(re1, rate);
                long totalPackets = (long)Math.Ceiling(endSample / 1024.0);
                long suffixPackets = totalPackets - sSeam;
                if (suffixPackets <= 0) { r.Error = "fast audio: empty suffix"; return r; }

                // Prefix: первые P пакетов, точный счёт через -frames:a.
                                string prefixAac = Path.Combine(ctx.WorkDir, "fa_prefix.aac");
                HybridCutRunner.StepResult cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", ctx.Input, "-map", "0:a:0",
                    "-c:a", "copy", "-frames:a", prefixPackets.ToString(CultureInfo.InvariantCulture),
                    "-y", prefixAac
                });
                if (!cr.Ok) { r.Canceled = cr.Canceled; r.Error = "fast audio prefix: " + cr.Error; return r; }

                // Bridge PCM sample-exact, затем AAC encode (единственный re-encode).
                                string bridgeWav = Path.Combine(ctx.WorkDir, "fa_bridge.wav");
                cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", ctx.Input, "-filter_complex",
                    "[0:a]atrim=start_sample=" + p1.ToString(CultureInfo.InvariantCulture)
                        + ":end_sample=" + p2.ToString(CultureInfo.InvariantCulture)
                        + ",asetpts=PTS-STARTPTS[p];"
                    + "[0:a]atrim=start_sample=" + s1.ToString(CultureInfo.InvariantCulture)
                        + ":end_sample=" + s2.ToString(CultureInfo.InvariantCulture)
                        + ",asetpts=PTS-STARTPTS[s];"
                    + "[p][s]concat=n=2:v=0:a=1[b]",
                    "-map", "[b]", "-ar", rate.ToString(CultureInfo.InvariantCulture),
                    "-c:a", "pcm_s16le", "-y", bridgeWav
                });
                if (!cr.Ok) { r.Canceled = cr.Canceled; r.Error = "fast audio bridge pcm: " + cr.Error; return r; }
                                string bridgeM4a = Path.Combine(ctx.WorkDir, "fa_bridge.m4a");
                cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", bridgeWav, "-ar", rate.ToString(CultureInfo.InvariantCulture),
                    "-c:a", "aac", "-b:a", "128k", "-y", bridgeM4a
                });
                if (!cr.Ok) { r.Canceled = cr.Canceled; r.Error = "fast audio bridge enc: " + cr.Error; return r; }

                // Bridge в ADTS, отбросить priming-первый и flush-последний кадры.
                string bridgeAac = Path.Combine(ctx.WorkDir, "fa_bridge_all.aac");
                cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", bridgeM4a, "-c:a", "copy", "-y", bridgeAac
                });
                if (!cr.Ok) { r.Canceled = cr.Canceled; r.Error = "fast audio bridge adts: " + cr.Error; return r; }
                long bridgeFrames = CountPackets(bridgeAac);
                if (bridgeFrames < 3) { r.Error = "fast audio: bridge frames unreadable"; return r; }
                long bridgeKeep = bridgeFrames - 2;
                string bridgeNp = Path.Combine(ctx.WorkDir, "fa_bridge_np.aac");
                cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", bridgeAac, "-ss", FmtTrunc(512.0 / rate),
                    "-c:a", "copy", "-frames:a", bridgeKeep.ToString(CultureInfo.InvariantCulture),
                    "-y", bridgeNp
                });
                if (!cr.Ok) { r.Canceled = cr.Canceled; r.Error = "fast audio bridge trim: " + cr.Error; return r; }

                // Suffix: пакеты от sSeam, -ss усечённый вниз (пакет шва kept),
                // точный счёт через -frames:a.
                                string suffixAac = Path.Combine(ctx.WorkDir, "fa_suffix.aac");
                cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", ctx.Input, "-ss", FmtTrunc(s2 / (double)rate),
                    "-map", "0:a:0", "-c:a", "copy",
                    "-frames:a", suffixPackets.ToString(CultureInfo.InvariantCulture),
                    "-y", suffixAac
                });
                if (!cr.Ok) { r.Canceled = cr.Canceled; r.Error = "fast audio suffix: " + cr.Error; return r; }

                // Сборка последовательности ADTS frames + один финальный MP4 mux.
                                string list = Path.Combine(ctx.WorkDir, "fa_list.txt");
                using (StreamWriter w = new StreamWriter(list, false, new UTF8Encoding(false)))
                {
                    w.WriteLine("file '" + prefixAac.Replace("'", "'\\''") + "'");
                    w.WriteLine("file '" + bridgeNp.Replace("'", "'\\''") + "'");
                    w.WriteLine("file '" + suffixAac.Replace("'", "'\\''") + "'");
                }
                string joint = Path.Combine(ctx.WorkDir, "fa_joint.aac");
                cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-f", "concat", "-safe", "0", "-i", list,
                    "-c:a", "copy", "-y", joint
                });
                if (!cr.Ok) { r.Canceled = cr.Canceled; r.Error = "fast audio concat: " + cr.Error; return r; }
                                cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", joint, "-c:a", "copy", "-y", afile
                });
                if (!cr.Ok) { r.Canceled = cr.Canceled; r.Error = "fast audio mux: " + cr.Error; return r; }
                if (!File.Exists(afile)) { r.Error = "fast audio output missing"; return r; }

                                double expectedAudio = (cutStart - rs0) + (re1 - cutEnd);
                string verr = VerifyFastAudio(ctx, afile, expectedAudio,
                    prefixPackets, bridgeKeep, suffixPackets, rate, cutStart, bridgeWav, s2);
                if (verr != null) { r.Error = verr; return r; }
                r.Ok = true;
                return r;
            }
        }

        // Fmt усечённый вниз до микросекунд: для packet-boundary -ss округление
        // ВВЕРХ роняет граничный пакет (проверено: -ss 180.511928 вместо
        // 180.511927 потерял первый пакет suffix). Усечение гарантирует kept.
        private static string FmtTrunc(double v)
        {
            double t = Math.Floor(v * 1000000.0 + 0.0001) / 1000000.0;
            if (t < 0) t = 0;
            return t.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        private static string ProbeAudioStream(string input, int planRate, out int rate, out int channels)
        {
            rate = 0;
            channels = 0;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfprobeExe;
                psi.Arguments = YtDlpRunner.FormatArgs(new string[]
                {
                    "-v", "error", "-select_streams", "a:0",
                    "-show_entries", "stream=codec_name,profile,sample_rate,channels",
                    "-of", "flat", input
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
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return "fast audio: probe timeout"; }
                    if (p.ExitCode != 0) return "fast audio: probe failed";
                    string codec = "", profile = "";
                    string[] lines = so.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].TrimEnd('\r');
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string val = line.Substring(eq + 1).Trim().Trim('"');
                        string key = line.Substring(0, eq);
                        if (key.EndsWith(".codec_name", StringComparison.Ordinal)) codec = val;
                        else if (key.EndsWith(".profile", StringComparison.Ordinal)) profile = val;
                        else if (key.EndsWith(".sample_rate", StringComparison.Ordinal))
                        {
                            int sv;
                            if (int.TryParse(val, System.Globalization.NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out sv)) rate = sv;
                        }
                        else if (key.EndsWith(".channels", StringComparison.Ordinal))
                        {
                            int ch;
                            if (int.TryParse(val, System.Globalization.NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out ch)) channels = ch;
                        }
                    }
                    if (!string.Equals(codec, "aac", StringComparison.OrdinalIgnoreCase))
                        return "fast audio: not AAC";
                    if (profile.Length > 0 && profile.IndexOf("LC", StringComparison.OrdinalIgnoreCase) < 0)
                        return "fast audio: not LC";
                    if (!(rate > 0) || rate != planRate) return "fast audio: bad rate";
                    return null;
                }
            }
            catch (Exception ex) { return "fast audio probe: " + ex.Message; }
        }

        private static long CountPackets(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfprobeExe;
                psi.Arguments = YtDlpRunner.FormatArgs(new string[]
                {
                    "-v", "error", "-select_streams", "a:0",
                    "-show_entries", "packet=pts_time", "-of", "csv=p=0", path
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
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return -1; }
                    if (p.ExitCode != 0) return -1;
                    long n = 0;
                    string[] lines = so.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                        if (lines[i].Trim().Length > 0) n++;
                    return n;
                }
            }
            catch { return -1; }
        }

        private sealed class PcmStat
        {
            public long Total;
            public double Peak;
            public double Rms;
            public long MaxZeroRun;
        }

        private static bool DecodeWindow(HybridCutRunner.Ctx ctx, string input, double ss, double dur, string outWav)
        {
            try
            {
                HybridCutRunner.StepResult cr = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-ss", HybridCut.Fmt(ss), "-i", input,
                    "-t", HybridCut.Fmt(dur), "-map", "0:a:0",
                    "-c:a", "pcm_s16le", "-y", outWav
                });
                return cr.Ok && File.Exists(outWav);
            }
            catch { return false; }
        }

        private static bool ReadPcmStats(string path, out PcmStat st)
        {
            st = new PcmStat();
            try
            {
                byte[] buf = File.ReadAllBytes(path);
                // WAV с заголовком: s16le samples начинаются с data-chunk.
                // Ищем "data" маркер, иначе считаем весь файл сырым (не должно случиться).
                int off = 0;
                for (int i = 0; i + 8 <= buf.Length && i < 256; i++)
                {
                    if (buf[i] == 'd' && buf[i + 1] == 'a' && buf[i + 2] == 't' && buf[i + 3] == 'a')
                    { off = i + 8; break; }
                }
                long n = 0;
                double sumSq = 0;
                double peak = 0;
                long run = 0;
                long maxRun = 0;
                for (int i = off; i + 1 < buf.Length; i += 2)
                {
                    short v = (short)(buf[i] | (buf[i + 1] << 8));
                    double a = Math.Abs((double)v) / 32768.0;
                    if (a > peak) peak = a;
                    sumSq += a * a;
                    n++;
                    if (Math.Abs(v) < 32) { run++; if (run > maxRun) maxRun = run; }
                    else run = 0;
                }
                if (n == 0) return false;
                st.Total = n;
                st.Peak = peak;
                st.Rms = Math.Sqrt(sumSq / n);
                st.MaxZeroRun = maxRun;
                return true;
            }
            catch { return false; }
        }

        // Быстрая проверка сборки БЕЗ полного decode: мета + один packet-скан
        // (total/monotonic/min-dur) + короткие boundary-окна (energy/тишина).
        private static string VerifyFastAudio(HybridCutRunner.Ctx ctx, string afile, double expected,
            long prefixPackets, long bridgeKeep, long suffixPackets, int rate, double splice, string bridgeWav,
            long suffixSrcSample)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfprobeExe;
                psi.Arguments = YtDlpRunner.FormatArgs(new string[]
                {
                    "-v", "error",
                    "-show_entries", "format=duration,size",
                    "-show_entries", "stream=codec_name,profile,sample_rate,channels",
                    "-show_entries", "packet=dts_time,duration_time",
                    "-of", "flat", afile
                });
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                double duration = 0;
                long size = 0;
                string codec = "", profile = "";
                int srate = 0, ch = 0;
                long pktTotal = 0;
                double prevDts = -1e18;
                double minDur = 1e18;
                bool monotonic = true;
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return "fast audio verify timeout"; }
                    if (p.ExitCode != 0) return "fast audio verify probe failed: " + se.Trim();
                    string[] lines = so.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].TrimEnd('\r');
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string val = line.Substring(eq + 1).Trim().Trim('"');
                        string key = line.Substring(0, eq);
                        if (key == "format.duration") duration = ParseDbl(val);
                        else if (key == "format.size")
                        {
                            long sz;
                            if (long.TryParse(val, System.Globalization.NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out sz)) size = sz;
                        }
                        else if (key.EndsWith(".codec_name", StringComparison.Ordinal)) codec = val;
                        else if (key.EndsWith(".profile", StringComparison.Ordinal)) profile = val;
                        else if (key.EndsWith(".sample_rate", StringComparison.Ordinal)) srate = ParseInt(val);
                        else if (key.EndsWith(".channels", StringComparison.Ordinal)) ch = ParseInt(val);
                        else if (key.EndsWith(".dts_time", StringComparison.Ordinal))
                        {
                            // Flat-формат не даёт отдельной строки ".packet":
                            // один dts_time == один пакет, здесь же и считаем.
                            pktTotal++;
                            double d = ParseDbl(val);
                            if (d < prevDts) monotonic = false;
                            prevDts = d;
                        }
                        else if (key.EndsWith(".duration_time", StringComparison.Ordinal))
                        {
                            double d = ParseDbl(val);
                            if (d < minDur) minDur = d;
                        }
                    }
                }
                if (!string.Equals(codec, "aac", StringComparison.OrdinalIgnoreCase)) return "fast audio verify: codec";
                if (profile.Length > 0 && profile.IndexOf("LC", StringComparison.OrdinalIgnoreCase) < 0)
                    return "fast audio verify: profile";
                if (srate != rate || ch != 2) return "fast audio verify: stream params";
                if (size <= 1024) return "fast audio verify: too small";
                double tol = HybridCutVerify.DurationToleranceSec(ctx.Plan.Regions.Count, ctx.Plan.Fps);
                if (!(duration > 0) || Math.Abs(duration - expected) > tol)
                    return "fast audio verify: duration";
                if (pktTotal != prefixPackets + bridgeKeep + suffixPackets)
                    return "fast audio verify: packet count";
                if (!monotonic) return "fast audio verify: dts";
                if (!(minDur > 0.01)) return "fast audio verify: packet duration";
                // Boundary: окно splice ±0.3 с — energy/тишина против эталона.
                // Эталон: source-хвост + bridge.wav-голова одним ffmpeg-concat.
                string finWin = Path.Combine(ctx.WorkDir, "fa_chk_fin.wav");
                if (!DecodeWindow(ctx, afile, splice - 0.3, 0.6, finWin)) return "fast audio verify: fin window";
                string expHead = Path.Combine(ctx.WorkDir, "fa_chk_src.wav");
                if (!DecodeWindow(ctx, ctx.Input, splice - 0.3, 0.3, expHead)) return "fast audio verify: exp head";
                string expBHead = Path.Combine(ctx.WorkDir, "fa_chk_b.wav");
                HybridCutRunner.StepResult br = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", bridgeWav, "-ss", HybridCut.Fmt(0.0),
                    "-t", HybridCut.Fmt(0.3), "-map", "0:a",
                    "-c:a", "pcm_s16le", "-y", expBHead
                });
                if (!br.Ok || !File.Exists(expBHead)) return "fast audio verify: exp bridge";
                string expFull = Path.Combine(ctx.WorkDir, "fa_chk_exp.wav");
                br = RunFfmpeg(ctx, new string[]
                {
                    "-hide_banner", "-i", expHead, "-i", expBHead,
                    "-filter_complex", "[0:a][1:a]concat=n=2:v=0:a=1[x]",
                    "-map", "[x]", "-c:a", "pcm_s16le", "-y", expFull
                });
                if (!br.Ok || !File.Exists(expFull)) return "fast audio verify: exp concat";
                PcmStat fs, es;
                if (!ReadPcmStats(finWin, out fs) || !ReadPcmStats(expFull, out es))
                    return "fast audio verify: pcm read";
                if (fs.Total != es.Total) return "fast audio verify: window length";
                if (!EnergyClose(fs, es)) return "fast audio verify: splice energy";
                if (fs.MaxZeroRun > 441 && es.MaxZeroRun <= 441) return "fast audio verify: splice silence";
                // Suffix energy: окна привязаны к НАЧАЛУ suffix с обеих сторон
                // (output: (P+B) пакетов; source: suffixSrcSample), иначе
                // структурное сравнение ловит 12-мс сдвиг вместо дефектов.
                // Только energy (±6 дБ, alignment-insensitive); silence-правило
                // здесь давало ложные fallback, дыры ловятся counts/duration.
                double suffixOutStart = (prefixPackets + bridgeKeep) * 1024.0 / rate;
                double suffixSrcStart = suffixSrcSample / (double)rate;
                string finSuf = Path.Combine(ctx.WorkDir, "fa_chk_fs.wav");
                string expSuf = Path.Combine(ctx.WorkDir, "fa_chk_es.wav");
                if (!DecodeWindow(ctx, afile, suffixOutStart + 0.5, 2.0, finSuf)) return "fast audio verify: suf window";
                if (!DecodeWindow(ctx, ctx.Input, suffixSrcStart + 0.5, 2.0, expSuf)) return "fast audio verify: exp suf";
                PcmStat f2, e2;
                if (!ReadPcmStats(finSuf, out f2) || !ReadPcmStats(expSuf, out e2))
                    return "fast audio verify: suf pcm read";
                if (!EnergyClose(f2, e2)) return "fast audio verify: suffix energy";
                return null;
            }
            catch (Exception ex) { return "fast audio verify: " + ex.Message; }
        }

        private static bool EnergyClose(PcmStat a, PcmStat b)
        {
            // ±6 дБ по RMS и пику: ловит неверный контент/громкие дыры,
            // пропускает priming-оттенки и кодерный шум (измерено ≪1 дБ).
            double rmsR = (a.Rms + 1e-9) / (b.Rms + 1e-9);
            double peakR = (a.Peak + 1e-9) / (b.Peak + 1e-9);
            if (rmsR < 0.5 || rmsR > 2.0) return false;
            if (peakR < 0.5 || peakR > 2.0) return false;
            return true;
        }

        private static int ParseInt(string s)
        {
            int v;
            if (int.TryParse(s, System.Globalization.NumberStyles.Integer,
                CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        private static double ParseDbl(string s)
        {
            double v;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        public static HybridCutRunner.StepResult RunMux(HybridCutRunner.Ctx ctx, string vcat, string afile, string output)
        {
            List<string> args = new List<string>();
            args.Add("-hide_banner");
            args.Add("-i");
            args.Add(vcat);
            if (afile != null) { args.Add("-i"); args.Add(afile); }
            args.Add("-c");
            args.Add("copy");
            args.Add("-movflags");
            args.Add("+faststart");
            args.Add("-y");
            args.Add(output);
            HybridCutRunner.StepResult r = RunFfmpeg(ctx, args.ToArray());
            if (!r.Ok) return r;
            if (!File.Exists(output)) { r.Ok = false; r.Error = "mux output missing"; return r; }
            return r;
        }

        public static HybridCutRunner.StepResult RunFfmpeg(HybridCutRunner.Ctx ctx, string[] args)
        {
            HybridCutRunner.StepResult r = new HybridCutRunner.StepResult();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                psi.Arguments = YtDlpRunner.FormatArgs(args);
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = false;
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) HybridCutRunner.NoteErr(ctx, e.Data);
                    };
                    p.Start();
                    p.BeginErrorReadLine();
                    while (!p.WaitForExit(200))
                    {
                        if (HybridCutRunner.IsCanceled(ctx))
                        {
                            try { YtDlpRunner.KillTree(p); } catch { }
                            try { p.WaitForExit(5000); } catch { }
                            r.Canceled = true;
                            r.Error = "canceled";
                            return r;
                        }
                    }
                    if (p.ExitCode != 0) { r.Error = "ffmpeg exit code " + p.ExitCode; return r; }
                    r.Ok = true;
                    return r;
                }
            }
            catch (Exception ex) { r.Error = ex.Message; return r; }
        }
    }
}
