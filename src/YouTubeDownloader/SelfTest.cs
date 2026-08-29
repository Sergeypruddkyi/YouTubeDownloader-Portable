using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace YouTubeDownloader
{
    internal static class SelfTest
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        private const int ATTACH_PARENT_PROCESS = -1;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 0x3;

        private static readonly StringBuilder Buf = new StringBuilder();
        private static int _pass;
        private static int _fail;

        private static void Line(string s)
        {
            Buf.AppendLine(s);
            try { Console.WriteLine(s); }
            catch { }
        }

        private static void Check(string name, bool ok, string details)
        {
            if (ok) { _pass++; Line("[PASS] " + name + (string.IsNullOrEmpty(details) ? "" : " — " + details)); }
            else { _fail++; Line("[FAIL] " + name + (string.IsNullOrEmpty(details) ? "" : " — " + details)); }
        }

        public static int Run(bool includeNet)
        {
            TryAttachConsole();
            Line("=== YouTubeDownloader selftest ===");
            Line("Каталог приложения: " + AppPaths.BaseDir);
            Line("");

            Check("yt.exe найден", File.Exists(AppPaths.YtExe), AppPaths.YtExe);
            Check("deno.exe найден", File.Exists(AppPaths.DenoExe), AppPaths.DenoExe);
            Check("ffmpeg.exe найден", File.Exists(AppPaths.FfmpegExe), AppPaths.FfmpegExe);
            Check("ffprobe.exe найден", File.Exists(AppPaths.FfprobeExe), AppPaths.FfprobeExe);

            int code = 0;
            string ver = File.Exists(AppPaths.YtExe) ? YtDlpRunner.GetVersionText(out code) : null;
            Check("yt.exe --version", code == 0 && !string.IsNullOrEmpty(ver), ver);

            TestSettings();
            TestUrlValidator();
            TestClassifier();
            TestInvalidUrlRun();

            if (includeNet) TestGitHub(ver);

            Line("");
            Line("Итог: PASS=" + _pass + " FAIL=" + _fail);
            WriteResultFile(Buf.ToString());
            return _fail == 0 ? 0 : 1;
        }

        private static void TestSettings()
        {
            try
            {
                string testPath = Path.Combine(AppPaths.BaseDir, "settings.ini");
                Settings st = new Settings(testPath);
                st.LastFolder = "C:\\__selftest__";
                st.Save();
                Settings st2 = new Settings(testPath);
                st2.Load();
                bool ok = string.Equals(st2.LastFolder, "C:\\__selftest__", StringComparison.Ordinal);
                Check("settings.ini запись/чтение", ok, testPath);
                try { File.Delete(testPath); }
                catch { }
            }
            catch (Exception ex)
            {
                Check("settings.ini запись/чтение", false, ex.Message);
            }
        }

        private static void TestUrlValidator()
        {
            bool ok =
                YouTubeUrl.IsValid("https://www.youtube.com/watch?v=dQw4w9WgXcQ") &&
                YouTubeUrl.IsValid("https://youtu.be/dQw4w9WgXcQ?si=abc") &&
                YouTubeUrl.IsValid("https://www.youtube.com/shorts/abcdefghi90?feature=share") &&
                YouTubeUrl.IsValid("https://m.youtube.com/watch?app=desktop&v=abcdefghijk") &&
                YouTubeUrl.IsValid("youtube.com/watch?v=dQw4w9WgXcQ") &&
                !YouTubeUrl.IsValid("https://www.google.com/search?q=youtube") &&
                !YouTubeUrl.IsValid("") &&
                string.Equals(YouTubeUrl.ExtractFirst("Смотри: https://youtu.be/dQw4w9WgXcQ?si=x (классика)"), "https://youtu.be/dQw4w9WgXcQ?si=x", StringComparison.Ordinal);
            Check("Валидатор YouTube-ссылок", ok, null);
        }

        private static void TestClassifier()
        {
            int bad = 0;
            bad += Expect("Unable to extract", "ERROR: [youtube] dQw4w9WgXcQ: Unable to extract uploader id", ErrorCategory.UpdateSuspect) ? 0 : 1;
            bad += Expect("nsig failed", "WARNING: nsig extraction failed: some formats may be missing", ErrorCategory.UpdateSuspect) ? 0 : 1;
            bad += Expect("bot check", "ERROR: Sign in to confirm you're not a bot.", ErrorCategory.UpdateSuspect) ? 0 : 1;
            bad += Expect("format", "ERROR: Requested format is not available", ErrorCategory.UpdateSuspect) ? 0 : 1;
            bad += Expect("403", "ERROR: HTTP Error 403: Forbidden", ErrorCategory.UpdateSuspect) ? 0 : 1;
            bad += Expect("invalid url", "ERROR: 'xyz' is not a valid URL.", ErrorCategory.InvalidUrl) ? 0 : 1;
            bad += Expect("network 503", "ERROR: unable to download video data: HTTP Error 503", ErrorCategory.Network) ? 0 : 1;
            bad += Expect("dns", "ERROR: <urlopen error [Errno 11001] getaddrinfo failed>", ErrorCategory.Network) ? 0 : 1;
            bad += Expect("private", "ERROR: [youtube] xyz: Private video. Sign in if you've been granted access", ErrorCategory.ContentUnavailable) ? 0 : 1;
            bad += Expect("age", "ERROR: Sign in to confirm your age", ErrorCategory.ContentUnavailable) ? 0 : 1;
            bad += Expect("disk full", "ERROR: [Errno 28] No space left on device", ErrorCategory.PathError) ? 0 : 1;
            bad += Expect("access denied", "ERROR: [Errno 13] Permission denied: 'D:\\x\\y.mp4.part'", ErrorCategory.PathError) ? 0 : 1;
            bad += Expect("unknown", "ERROR: something completely else 42", ErrorCategory.Unknown) ? 0 : 1;
            Check("Классификатор ошибок (13 кейсов)", bad == 0, "ошибок: " + bad);
        }

        private static bool Expect(string name, string output, ErrorCategory expected)
        {
            ErrorClassifier.Result r = ErrorClassifier.Classify(output, 1);
            bool ok = r != null && r.Category == expected;
            if (!ok) Line("    case '" + name + "': ожидалось " + expected + ", получено " + (r != null ? r.Category.ToString() : "null"));
            return ok;
        }

        private static void TestInvalidUrlRun()
        {
            if (!File.Exists(AppPaths.YtExe))
            {
                Check("Обработка ошибки yt-dlp (invalid URL, offline)", false, "yt.exe не найден");
                return;
            }
            try
            {
                YtRunResult r = YtDlpRunner.RunSync(new[] { "--ignore-config", "--newline", "not-a-valid-url" });
                ErrorClassifier.Result cls = ErrorClassifier.Classify(r.Output, r.ExitCode);
                bool ok = r.ExitCode != 0 && cls != null && cls.Category == ErrorCategory.InvalidUrl;
                string details = "exit=" + r.ExitCode + ", категория=" + (cls != null ? cls.Category.ToString() : "null");
                if (cls != null && cls.MatchedLine != null) details += " | " + cls.MatchedLine;
                Check("Обработка ошибки yt-dlp (invalid URL, offline)", ok, details);
            }
            catch (Exception ex)
            {
                Check("Обработка ошибки yt-dlp (invalid URL, offline)", false, ex.Message);
            }
        }

        private static void TestGitHub(string localVersion)
        {
            try
            {
                string latest = UpdateChecker.GetLatestVersionAsync().GetAwaiter().GetResult();
                int cmp = UpdateChecker.CompareVersions(latest, localVersion);
                string state = cmp > 0 ? "доступно обновление" : (cmp == 0 ? "актуальна" : "latest старше локальной");
                Check("GitHub: latest yt-dlp", true, "latest=" + latest + ", локальная=" + localVersion + " (" + state + ")");
            }
            catch (Exception ex)
            {
                Check("GitHub: latest yt-dlp", false, "сеть недоступна: " + ex.Message);
            }
        }

        private static void TryAttachConsole()
        {
            bool attached = AttachConsole(ATTACH_PARENT_PROCESS);
            if (!attached) AllocConsole();
            try
            {
                IntPtr h = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h != IntPtr.Zero && h != (IntPtr)(-1))
                {
                    FileStream fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, true), FileAccess.Write);
                    StreamWriter w = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                    Console.SetOut(w);
                }
            }
            catch { }
        }

        private static void WriteResultFile(string text)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.BaseDir, "selftest_result.txt"), text, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
