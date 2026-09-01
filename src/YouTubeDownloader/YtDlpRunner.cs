using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeDownloader
{
    public class YtLine
    {
        public string Text;
    }

    public class YtRunResult
    {
        public int ExitCode;
        public string Output;
    }

    public static class OutputParser
    {
        private static readonly Regex RxPercent = new Regex(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
        private static readonly Regex RxSpeed = new Regex(@"at\s+(?<s>[\d.]+\s*[KMGT]?i?B/s|Unknown\s+B/s)", RegexOptions.Compiled);
        private static readonly Regex RxEta = new Regex(@"ETA\s+(?<e>\d+:\d\d)", RegexOptions.Compiled);
        private static readonly Regex RxDest = new Regex(@"\[download\]\s+Destination:\s+(?<f>.+)", RegexOptions.Compiled);
        private static readonly Regex RxExtractDest = new Regex(@"\[ExtractAudio\]\s+Destination:\s+(?<f>.+)", RegexOptions.Compiled);
        private static readonly Regex RxAlready = new Regex(@"has already been downloaded", RegexOptions.Compiled);
        private static readonly Regex RxMerger = new Regex(@"\[Merger\]\s+Merging formats into ""(?<f>.+)""", RegexOptions.Compiled);

        public static double? TryPercent(string line)
        {
            Match m = RxPercent.Match(line);
            if (!m.Success) return null;
            double d;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }

        public static string TrySpeed(string line)
        {
            Match m = RxSpeed.Match(line);
            return m.Success ? m.Groups["s"].Value : null;
        }

        public static string TryEta(string line)
        {
            Match m = RxEta.Match(line);
            return m.Success ? m.Groups["e"].Value : null;
        }

        public static string TryDestination(string line)
        {
            Match m = RxDest.Match(line);
            return m.Success ? m.Groups["f"].Value.Trim() : null;
        }

        public static string TryExtractAudioDestination(string line)
        {
            Match m = RxExtractDest.Match(line);
            return m.Success ? m.Groups["f"].Value.Trim() : null;
        }

        public static string TryMergedFile(string line)
        {
            Match m = RxMerger.Match(line);
            return m.Success ? m.Groups["f"].Value.Trim() : null;
        }

        public static bool IsAlreadyDownloaded(string line)
        {
            return RxAlready.IsMatch(line);
        }

        public static Msg? PhaseOf(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[FixupM")) return Msg.PhaseMerging;
            if (line.StartsWith("[download]")) return Msg.PhaseDownloading;
            if (line.StartsWith("[youtube]") || line.StartsWith("[info]")) return Msg.PhaseFetchingInfo;
            return null;
        }
    }

    public enum DownloadQuality
    {
        BestAvailable,
        P1080,
        P720,
        P480,
        P360,
        AudioOnly
    }

    public static class YtDlpRunner
    {
        public static DownloadQuality ParseQuality(string value)
        {
            if (value == null) return DownloadQuality.BestAvailable;
            string v = value.Trim();
            if (v.Length == 0) return DownloadQuality.BestAvailable;
            if (string.Equals(v, "best", StringComparison.OrdinalIgnoreCase)) return DownloadQuality.BestAvailable;
            if (string.Equals(v, "1080", StringComparison.OrdinalIgnoreCase)) return DownloadQuality.P1080;
            if (string.Equals(v, "720", StringComparison.OrdinalIgnoreCase)) return DownloadQuality.P720;
            if (string.Equals(v, "480", StringComparison.OrdinalIgnoreCase)) return DownloadQuality.P480;
            if (string.Equals(v, "360", StringComparison.OrdinalIgnoreCase)) return DownloadQuality.P360;
            if (string.Equals(v, "audio", StringComparison.OrdinalIgnoreCase)) return DownloadQuality.AudioOnly;
            return DownloadQuality.BestAvailable;
        }

        public static string QualityToSetting(DownloadQuality mode)
        {
            switch (mode)
            {
                case DownloadQuality.P1080: return "1080";
                case DownloadQuality.P720: return "720";
                case DownloadQuality.P480: return "480";
                case DownloadQuality.P360: return "360";
                case DownloadQuality.AudioOnly: return "audio";
                default: return "best";
            }
        }

        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            bool need = arg.Length == 0;
            foreach (char ch in arg)
            {
                if (ch <= ' ' || ch == '"')
                {
                    need = true;
                    break;
                }
            }
            if (!need) return arg;

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char ch in arg)
            {
                if (ch == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (ch == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    if (backslashes > 0)
                    {
                        sb.Append('\\', backslashes);
                        backslashes = 0;
                    }
                    sb.Append(ch);
                }
            }
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string FormatArgs(string[] args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        private static ProcessStartInfo BuildStartInfo(string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = AppPaths.YtExe;
            psi.Arguments = arguments;
            psi.WorkingDirectory = AppPaths.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["PATH"] = AppPaths.BaseDir.TrimEnd('\\') + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
            return psi;
        }

        public static string[] DownloadArgs(string folder, string url, DownloadQuality mode)
        {
            string escaped = folder.Replace("%", "%%");
            if (!escaped.EndsWith("\\")) escaped += "\\";
            string outTemplate = escaped + "%(title)s.%(ext)s";
            List<string> args = new List<string>
            {
                "--newline",
                "--color", "no_color",
                "--encoding", "utf-8",
                "--ignore-config",
                "--no-playlist",
                "--js-runtime", "deno",
                "-f", FormatSpec(mode)
            };
            if (mode == DownloadQuality.AudioOnly)
            {
                args.Add("-x");
                args.Add("--audio-format");
                args.Add("best");
            }
            else
            {
                args.Add("--merge-output-format");
                args.Add("mp4");
            }
            args.Add("--ffmpeg-location");
            args.Add(AppPaths.FfmpegExe);
            args.Add("-o");
            args.Add(outTemplate);
            args.Add(url);
            return args.ToArray();
        }

        public static string FormatSpec(DownloadQuality mode)
        {
            switch (mode)
            {
                case DownloadQuality.AudioOnly: return "bestaudio[ext=m4a]/bestaudio/best";
                case DownloadQuality.P1080: return HeightCappedFormat(1080);
                case DownloadQuality.P720: return HeightCappedFormat(720);
                case DownloadQuality.P480: return HeightCappedFormat(480);
                case DownloadQuality.P360: return HeightCappedFormat(360);
                default: return "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best";
            }
        }

        private static string HeightCappedFormat(int h)
        {
            return "bestvideo[height<=" + h + "][ext=mp4]+bestaudio[ext=m4a]"
                 + "/bestvideo[height<=" + h + "]+bestaudio"
                 + "/best[height<=" + h + "]";
        }

        public static string[] TitleInfoArgs(DownloadQuality mode, string url)
        {
            return new[]
            {
                "--ignore-config", "--encoding", "utf-8", "--color", "no_color",
                "--no-playlist",
                "--print", "title",
                "--print", "size=%(filesize,filesize_approx)s",
                "-f", FormatSpec(mode),
                url
            };
        }

        private static string[] PlainTitleArgs(string url)
        {
            return new[]
            {
                "--ignore-config", "--encoding", "utf-8", "--color", "no_color",
                "--no-playlist", "--print", "title", url
            };
        }

        public static long? ParseSizeValue(string value)
        {
            if (value == null) return null;
            string v = value.Trim();
            if (v.Length == 0) return null;
            if (string.Equals(v, "NA", StringComparison.OrdinalIgnoreCase)) return null;
            long n;
            if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0) return n;
            return null;
        }

        public class TitleSizeInfo
        {
            public string Title;
            public long? SizeBytes;
        }

        public static bool TryGetTitleAndSize(DownloadQuality mode, string url, out TitleSizeInfo info)
        {
            info = null;
            string title;
            long? size;
            bool timedOut;
            if (TryTitleRun(TitleInfoArgs(mode, url), out title, out size, out timedOut))
            {
                info = new TitleSizeInfo { Title = title, SizeBytes = size };
                return true;
            }
            if (mode != DownloadQuality.BestAvailable && !timedOut)
            {
                if (TryTitleRun(PlainTitleArgs(url), out title, out size, out timedOut))
                {
                    info = new TitleSizeInfo { Title = title, SizeBytes = null };
                    return true;
                }
            }
            return false;
        }

        private static bool TryTitleRun(string[] args, out string title, out long? size, out bool timedOut)
        {
            title = null;
            size = null;
            timedOut = false;
            try
            {
                using (Process p = Process.Start(BuildStartInfo(FormatArgs(args))))
                {
                    StringBuilder so = new StringBuilder();
                    PipeDrainCounter drain = new PipeDrainCounter();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) { lock (so) { so.AppendLine(e.Data); } return; }
                        drain.OnData(s, e);
                    };
                    p.ErrorDataReceived += drain.OnData;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(25000))
                    {
                        timedOut = true;
                        try { p.Kill(); }
                        catch { }
                        return false;
                    }
                    drain.WaitDrained(5000);
                    if (p.ExitCode != 0) return false;
                    string output;
                    lock (so) { output = so.ToString(); }
                    foreach (string line in output.Split('\n'))
                    {
                        string s = line.Trim();
                        if (s.Length == 0) continue;
                        if (title == null) { title = s; continue; }
                        if (s.StartsWith("size=", StringComparison.Ordinal) && size == null)
                            size = ParseSizeValue(s.Substring(5));
                    }
                    return !string.IsNullOrEmpty(title);
                }
            }
            catch
            {
                return false;
            }
        }

        internal sealed class PipeDrainCounter
        {
            private int _eofStreams;

            public void OnData(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) Interlocked.Increment(ref _eofStreams);
            }

            public bool Drained()
            {
                return Interlocked.CompareExchange(ref _eofStreams, 0, 0) == 2;
            }

            public bool WaitDrained(int timeoutMs)
            {
                for (int waitedMs = 0; waitedMs < timeoutMs; waitedMs += 50)
                {
                    if (Drained()) return true;
                    Thread.Sleep(50);
                }
                return Drained();
            }
        }

        public static bool TryGetTitle(string url, out string title)
        {
            TitleSizeInfo info;
            bool ok = TryGetTitleAndSize(DownloadQuality.BestAvailable, url, out info);
            title = ok ? info.Title : null;
            return ok;
        }

        public static string[] UpdateArgs()
        {
            return new[] { "-U" };
        }

        public static Task<YtRunResult> RunAsync(string[] args, Action<YtLine> onLine, Action<Process> onStarted)
        {
            Process proc = new Process();
            proc.StartInfo = BuildStartInfo(FormatArgs(args));
            proc.EnableRaisingEvents = true;

            TaskCompletionSource<YtRunResult> tcs = new TaskCompletionSource<YtRunResult>();
            StringBuilder output = new StringBuilder();
            object sync = new object();

            DataReceivedEventHandler outH = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (sync) { output.AppendLine(e.Data); }
                if (onLine != null) onLine(new YtLine { Text = e.Data });
            };
            DataReceivedEventHandler errH = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (sync) { output.AppendLine(e.Data); }
                if (onLine != null) onLine(new YtLine { Text = e.Data });
            };

            proc.OutputDataReceived += outH;
            proc.ErrorDataReceived += errH;
            proc.Exited += delegate(object s, EventArgs e)
            {
                try { proc.WaitForExit(); }
                catch { }
                int code = 0;
                try { code = proc.ExitCode; } catch { }
                string text;
                lock (sync) { text = output.ToString(); }
                tcs.TrySetResult(new YtRunResult { ExitCode = code, Output = text });
            };

            proc.Start();
            if (onStarted != null) onStarted(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            return tcs.Task;
        }

        public static YtRunResult RunSync(string[] args)
        {
            return RunAsync(args, null, null).GetAwaiter().GetResult();
        }

        public class VersionResult
        {
            public int ExitCode;
            public string Version;
        }

        public static Task<VersionResult> GetVersionAsync()
        {
            return Task.Run(new Func<VersionResult>(delegate
            {
                int c;
                string v = GetVersionText(out c);
                return new VersionResult { ExitCode = c, Version = v };
            }));
        }

        public static string GetVersionText(out int exitCode)
        {
            try
            {
                using (Process p = Process.Start(BuildStartInfo("--version")))
                {
                    string o = p.StandardOutput.ReadToEnd().Trim();
                    string e = p.StandardError.ReadToEnd().Trim();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); }
                        catch { }
                        exitCode = -1;
                        return "";
                    }
                    exitCode = p.ExitCode;
                    if (exitCode != 0 && o.Length == 0) o = e;
                    return o;
                }
            }
            catch
            {
                exitCode = -1;
                return "";
            }
        }

        public static void KillTree(Process proc)
        {
            if (proc == null) return;
            try { if (proc.HasExited) return; }
            catch { return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/PID " + proc.Id + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process tk = Process.Start(psi))
                {
                    tk.WaitForExit(5000);
                }
            }
            catch
            {
                try { proc.Kill(); }
                catch { }
            }
        }

        public static void KillTreeNoWait(Process proc)
        {
            if (proc == null) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill", "/PID " + proc.Id + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch
            {
                try { proc.Kill(); }
                catch { }
            }
        }
    }
}
