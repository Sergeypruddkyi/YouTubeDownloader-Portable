using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace YouTubeDownloader
{
    public class YtLine
    {
        public string Text;
        public bool IsError;
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

        public static string TryMergedFile(string line)
        {
            Match m = RxMerger.Match(line);
            return m.Success ? m.Groups["f"].Value.Trim() : null;
        }

        public static bool IsAlreadyDownloaded(string line)
        {
            return RxAlready.IsMatch(line);
        }

        public static string PhaseOf(string line)
        {
            if (string.IsNullOrEmpty(line)) return null;
            if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.StartsWith("[FixupM")) return "Склейка/обработка (FFmpeg)…";
            if (line.StartsWith("[download]")) return "Скачивание…";
            if (line.StartsWith("[youtube]") || line.StartsWith("[info]")) return "Получение данных о видео…";
            return null;
        }
    }

    public static class YtDlpRunner
    {
        public static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            bool need = arg.Contains(" ") || arg.Contains("\"");
            return need ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
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

        public static string[] DownloadArgs(string folder, string url)
        {
            string escaped = folder.Replace("%", "%%");
            if (!escaped.EndsWith("\\")) escaped += "\\";
            string outTemplate = escaped + "%(title)s.%(ext)s";
            return new[]
            {
                "--newline",
                "--color", "no_color",
                "--encoding", "utf-8",
                "--ignore-config",
                "--js-runtime", "deno",
                "-f", "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
                "--merge-output-format", "mp4",
                "--ffmpeg-location", AppPaths.FfmpegExe,
                "-o", outTemplate,
                url
            };
        }

        public static string[] UpdateArgs()
        {
            return new[] { "-U" };
        }

        public static Task<YtRunResult> RunAsync(string[] args, Action<YtLine> onLine, Action<Process> onStarted)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = AppPaths.YtExe;
            psi.Arguments = FormatArgs(args);
            psi.WorkingDirectory = AppPaths.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["PATH"] = AppPaths.BaseDir.TrimEnd('\\') + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");

            Process proc = new Process();
            proc.StartInfo = psi;
            proc.EnableRaisingEvents = true;

            TaskCompletionSource<YtRunResult> tcs = new TaskCompletionSource<YtRunResult>();
            StringBuilder output = new StringBuilder();
            object sync = new object();

            DataReceivedEventHandler outH = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (sync) { output.AppendLine(e.Data); }
                if (onLine != null) onLine(new YtLine { Text = e.Data, IsError = false });
            };
            DataReceivedEventHandler errH = delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (sync) { output.AppendLine(e.Data); }
                if (onLine != null) onLine(new YtLine { Text = e.Data, IsError = true });
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

        public static string GetVersionText(out int exitCode)
        {
            ProcessStartInfo psi = new ProcessStartInfo(AppPaths.YtExe, "--version");
            psi.WorkingDirectory = AppPaths.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd().Trim();
                string e = p.StandardError.ReadToEnd().Trim();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { }
                    exitCode = -1;
                    return "";
                }
                exitCode = p.ExitCode;
                if (exitCode != 0 && o.Length == 0) o = e;
                return o;
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
    }
}
