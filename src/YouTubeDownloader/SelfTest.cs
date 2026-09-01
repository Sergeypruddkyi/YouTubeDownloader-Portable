using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

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
            TestQualitySettings();
            TestUrlValidator();
            TestQuoting();
            TestDownloadArgs();
            TestParseQuality();
            TestTitleInfoArgs();
            TestSizeParsing();
            TestFormatSize();
            TestOutputParser();
            TestClassifier();
            TestInvalidUrlRun();
            TestPipeDrainLifecycle();
            TestApplyAutoUrl();

            if (includeNet) TestGitHub(ver);

            Line("");
            Line("Итог: PASS=" + _pass + " FAIL=" + _fail);
            WriteResultFile(Buf.ToString());
            return _fail == 0 ? 0 : 1;
        }

        public static int RunClipboard(string expected)
        {
            TryAttachConsole();
            string url = null;
            string err = null;
            Thread t = new Thread(new ThreadStart(delegate
            {
                try
                {
                    string text = Clipboard.GetText();
                    url = YouTubeUrl.ExtractFirst(text);
                }
                catch (Exception ex) { err = ex.Message; }
            }));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(5000);
            if (err != null)
            {
                Line("[FAIL] Clipboard: " + err);
                WriteResultFile(Buf.ToString());
                return 1;
            }
            Line("CLIPBOARD_URL=" + (url ?? "<null>"));
            bool ok = url != null && (expected == null || string.Equals(url, expected, StringComparison.OrdinalIgnoreCase));
            Check("Clipboard → URL", ok, null);
            WriteResultFile(Buf.ToString());
            return ok ? 0 : 1;
        }

        public static int RunTitle(string url)
        {
            TryAttachConsole();
            string title;
            bool ok = YtDlpRunner.TryGetTitle(url, out title);
            Line("TITLE=" + (title ?? "<null>"));
            Check("Получение названия видео", ok, url);
            WriteResultFile(Buf.ToString());
            return ok ? 0 : 1;
        }

        private static void TestSettings()
        {
            try
            {
                Settings st = new Settings(AppPaths.SettingsPath);
                st.LastFolder = "C:\\__selftest__";
                st.HasChosenFolder = true;
                st.Save();
                Settings st2 = new Settings(AppPaths.SettingsPath);
                st2.Load();
                bool ok = string.Equals(st2.LastFolder, "C:\\__selftest__", StringComparison.Ordinal) && st2.HasChosenFolder;

                Settings legacy = new Settings(AppPaths.SettingsPath);
                legacy.LastFolder = "E:\\YouTubeDownloader\\Видео";
                legacy.Save();
                Settings legacy2 = new Settings(AppPaths.SettingsPath);
                legacy2.Load();
                bool legacyIgnored = !legacy2.HasChosenFolder;

                Check("settings.ini запись/чтение (LastFolder + FolderChosen)", ok && legacyIgnored, AppPaths.SettingsPath);
                try { File.Delete(AppPaths.SettingsPath); }
                catch { }
            }
            catch (Exception ex)
            {
                Check("settings.ini запись/чтение", false, ex.Message);
            }
        }

        private static void TestQualitySettings()
        {
            try
            {
                Settings st = new Settings(AppPaths.SettingsPath);
                st.Quality = DownloadQuality.P720;
                st.Save();
                Settings st2 = new Settings(AppPaths.SettingsPath);
                st2.Load();
                bool ok720 = st2.Quality == DownloadQuality.P720 && string.Equals(st2.Get("Quality"), "720", StringComparison.Ordinal);

                st.Quality = DownloadQuality.AudioOnly;
                st.Save();
                Settings st3 = new Settings(AppPaths.SettingsPath);
                st3.Load();
                bool okAudio = st3.Quality == DownloadQuality.AudioOnly;

                File.WriteAllText(AppPaths.SettingsPath, "[General]\r\nQuality=some-garbage\r\n", new UTF8Encoding(false));
                Settings st4 = new Settings(AppPaths.SettingsPath);
                st4.Load();
                bool okFallback = st4.Quality == DownloadQuality.BestAvailable;

                Settings st5 = new Settings(AppPaths.SettingsPath);
                st5.Load();
                bool okMissing = st5.Quality == DownloadQuality.BestAvailable;

                Check("settings.ini Quality (round-trip + fallback)", ok720 && okAudio && okFallback && okMissing, AppPaths.SettingsPath);
                try { File.Delete(AppPaths.SettingsPath); }
                catch { }
            }
            catch (Exception ex)
            {
                Check("settings.ini Quality (round-trip + fallback)", false, ex.Message);
            }
        }

        private static void TestDownloadArgs()
        {
            string folder = "C:\\__selftest__";
            string url = "https://youtu.be/dQw4w9WgXcQ";
            string template = "C:\\__selftest__\\%(title)s.%(ext)s";
            try
            {
                string[] best = YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.BestAvailable);
                string[] expectedBest = new[]
                {
                    "--newline", "--color", "no_color", "--encoding", "utf-8", "--ignore-config", "--no-playlist", "--js-runtime", "deno",
                    "-f", "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
                    "--merge-output-format", "mp4",
                    "--ffmpeg-location", AppPaths.FfmpegExe,
                    "-o", template,
                    url
                };
                Check("DownloadArgs BestAvailable == v1.0.1 + --no-playlist", SeqEqual(best, expectedBest), null);

                bool capsOk =
                    FormatIs(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P1080), "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=1080]+bestaudio/best[height<=1080]") &&
                    FormatIs(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P720), "bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=720]+bestaudio/best[height<=720]") &&
                    FormatIs(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P480), "bestvideo[height<=480][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=480]+bestaudio/best[height<=480]") &&
                    FormatIs(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P360), "bestvideo[height<=360][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=360]+bestaudio/best[height<=360]");
                Check("DownloadArgs height caps 1080/720/480/360", capsOk, null);

                string[] a1080 = YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P1080);
                bool capFlags = Contains(a1080, "--merge-output-format") && NextIs(a1080, "--merge-output-format", "mp4")
                    && Contains(a1080, "--ffmpeg-location") && !Contains(a1080, "-x");
                Check("DownloadArgs cap flags (merge mp4, ffmpeg, без -x)", capFlags, null);

                string[] audio = YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.AudioOnly);
                bool audioOk = NextIs(audio, "-f", "bestaudio[ext=m4a]/bestaudio/best")
                    && Contains(audio, "-x")
                    && NextIs(audio, "--audio-format", "best")
                    && !Contains(audio, "--merge-output-format")
                    && Contains(audio, "--ffmpeg-location");
                Check("DownloadArgs AudioOnly (bestaudio, -x, без merge в mp4)", audioOk, null);

                bool noPlaylistAll =
                    Contains(best, "--no-playlist") &&
                    Contains(a1080, "--no-playlist") &&
                    Contains(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P720), "--no-playlist") &&
                    Contains(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P480), "--no-playlist") &&
                    Contains(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P360), "--no-playlist") &&
                    Contains(audio, "--no-playlist");
                Check("DownloadArgs --no-playlist во всех 6 режимах", noPlaylistAll, null);

                string playlistUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLrAXtmErZgOeiKm4sgNOknGvNjby9efdf";
                string[] plArgs = YtDlpRunner.DownloadArgs(folder, playlistUrl, DownloadQuality.P720);
                bool playlistRegression =
                    Contains(plArgs, "--no-playlist") &&
                    string.Equals(plArgs[plArgs.Length - 1], playlistUrl, StringComparison.Ordinal) &&
                    NextIs(plArgs, "-f", "bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=720]+bestaudio/best[height<=720]");
                Check("DownloadArgs URL с list= → только видео (--no-playlist, mode сохранён)", playlistRegression, null);
            }
            catch (Exception ex)
            {
                Check("DownloadArgs", false, ex.Message);
            }
        }

        private static void TestParseQuality()
        {
            bool fallbackOk =
                YtDlpRunner.ParseQuality(null) == DownloadQuality.BestAvailable &&
                YtDlpRunner.ParseQuality("") == DownloadQuality.BestAvailable &&
                YtDlpRunner.ParseQuality("   ") == DownloadQuality.BestAvailable &&
                YtDlpRunner.ParseQuality("garbage") == DownloadQuality.BestAvailable &&
                YtDlpRunner.ParseQuality("BEST") == DownloadQuality.BestAvailable &&
                YtDlpRunner.ParseQuality("bеst") == DownloadQuality.BestAvailable;

            bool valuesOk =
                YtDlpRunner.ParseQuality("1080") == DownloadQuality.P1080 &&
                YtDlpRunner.ParseQuality(" 720 ") == DownloadQuality.P720 &&
                YtDlpRunner.ParseQuality("480") == DownloadQuality.P480 &&
                YtDlpRunner.ParseQuality("360") == DownloadQuality.P360 &&
                YtDlpRunner.ParseQuality("AUDIO") == DownloadQuality.AudioOnly;

            bool roundTrip =
                YtDlpRunner.ParseQuality(YtDlpRunner.QualityToSetting(DownloadQuality.BestAvailable)) == DownloadQuality.BestAvailable &&
                YtDlpRunner.ParseQuality(YtDlpRunner.QualityToSetting(DownloadQuality.P1080)) == DownloadQuality.P1080 &&
                YtDlpRunner.ParseQuality(YtDlpRunner.QualityToSetting(DownloadQuality.P720)) == DownloadQuality.P720 &&
                YtDlpRunner.ParseQuality(YtDlpRunner.QualityToSetting(DownloadQuality.P480)) == DownloadQuality.P480 &&
                YtDlpRunner.ParseQuality(YtDlpRunner.QualityToSetting(DownloadQuality.P360)) == DownloadQuality.P360 &&
                YtDlpRunner.ParseQuality(YtDlpRunner.QualityToSetting(DownloadQuality.AudioOnly)) == DownloadQuality.AudioOnly;

            Check("ParseQuality unknown/missing → BestAvailable", fallbackOk, null);
            Check("ParseQuality значения режимов + round-trip", valuesOk && roundTrip, null);
        }

        private static void TestTitleInfoArgs()
        {
            string url = "https://youtu.be/dQw4w9WgXcQ";
            try
            {
                string[] args = YtDlpRunner.TitleInfoArgs(DownloadQuality.BestAvailable, url);
                bool baseOk = Contains(args, "--no-playlist") && Contains(args, "--ignore-config")
                    && AnyPair(args, "--print", "title")
                    && AnyPair(args, "--print", "size=%(filesize,filesize_approx)s")
                    && string.Equals(args[args.Length - 1], url, StringComparison.Ordinal);
                Check("TitleInfoArgs base (title + size print + --no-playlist)", baseOk, null);

                bool modesOk = true;
                foreach (DownloadQuality m in new[] { DownloadQuality.BestAvailable, DownloadQuality.P1080, DownloadQuality.P720, DownloadQuality.P480, DownloadQuality.P360, DownloadQuality.AudioOnly })
                {
                    string[] a = YtDlpRunner.TitleInfoArgs(m, url);
                    if (!NextIs(a, "-f", YtDlpRunner.FormatSpec(m))) { modesOk = false; break; }
                    if (!Contains(a, "size=%(filesize,filesize_approx)s")) { modesOk = false; break; }
                }
                Check("TitleInfoArgs -f == FormatSpec для всех 6 режимов", modesOk, null);

                string[] dlBest = YtDlpRunner.DownloadArgs("C:\\x", url, DownloadQuality.BestAvailable);
                string[] dlAudio = YtDlpRunner.DownloadArgs("C:\\x", url, DownloadQuality.AudioOnly);
                bool sameSpec =
                    NextIs(dlBest, "-f", YtDlpRunner.FormatSpec(DownloadQuality.BestAvailable)) &&
                    NextIs(dlAudio, "-f", YtDlpRunner.FormatSpec(DownloadQuality.AudioOnly)) &&
                    FormatIs(YtDlpRunner.DownloadArgs("C:\\x", url, DownloadQuality.P1080), YtDlpRunner.FormatSpec(DownloadQuality.P1080));
                Check("DownloadArgs и TitleInfoArgs используют единый FormatSpec", sameSpec, null);
            }
            catch (Exception ex)
            {
                Check("TitleInfoArgs", false, ex.Message);
            }
        }

        private static void TestSizeParsing()
        {
            bool okValid = YtDlpRunner.ParseSizeValue("21042523") == 21042523L
                && YtDlpRunner.ParseSizeValue(" 533067 ") == 533067L
                && YtDlpRunner.ParseSizeValue("0") == 0L;

            bool okNa = YtDlpRunner.ParseSizeValue("NA") == null
                && YtDlpRunner.ParseSizeValue("na") == null;

            bool okGarbage = YtDlpRunner.ParseSizeValue(null) == null
                && YtDlpRunner.ParseSizeValue("") == null
                && YtDlpRunner.ParseSizeValue("   ") == null
                && YtDlpRunner.ParseSizeValue("12.5MB") == null
                && YtDlpRunner.ParseSizeValue("-5") == null
                && YtDlpRunner.ParseSizeValue("99999999999999999999") == null;

            Check("ParseSizeValue валидные числа", okValid, null);
            Check("ParseSizeValue NA/пусто/мусор → null", okNa && okGarbage, null);
        }

        private static void TestFormatSize()
        {
            L10n.Set(Lang.En);
            bool en = string.Equals(L10n.FormatSize(533067), "521 KB", StringComparison.Ordinal)
                && string.Equals(L10n.FormatSize(21042523), "20.1 MB", StringComparison.Ordinal)
                && string.Equals(L10n.FormatSize(1610612736), "1.5 GB", StringComparison.Ordinal)
                && string.Equals(L10n.FormatSize(512), "512 B", StringComparison.Ordinal);
            L10n.Set(Lang.Ru);
            bool ru = string.Equals(L10n.FormatSize(21042523), "20,1 МБ", StringComparison.Ordinal)
                && string.Equals(L10n.FormatSize(1610612736), "1,5 ГБ", StringComparison.Ordinal);
            L10n.Set(Lang.En);
            Check("FormatSize B/KB/MB/GB EN+RU", en && ru, null);
        }

        private static void TestOutputParser()
        {
            string ex = OutputParser.TryExtractAudioDestination("[ExtractAudio] Destination: C:\\Музыка\\Song.m4a");
            bool extractOk = string.Equals(ex, "C:\\Музыка\\Song.m4a", StringComparison.Ordinal);

            bool phaseOk = OutputParser.PhaseOf("[ExtractAudio] Destination: C:\\x\\y.m4a") == Msg.PhaseMerging;

            bool notConfused =
                OutputParser.TryExtractAudioDestination("[download] Destination: C:\\x\\y.mp4") == null &&
                OutputParser.TryDestination("[ExtractAudio] Destination: C:\\x\\y.m4a") == null;

            string dl = OutputParser.TryDestination("[download] Destination: C:\\Видео\\clip.f137.mp4");
            bool downloadOk = string.Equals(dl, "C:\\Видео\\clip.f137.mp4", StringComparison.Ordinal)
                && OutputParser.PhaseOf("[download] Destination: C:\\x\\y.mp4") == Msg.PhaseDownloading;

            Check("OutputParser ExtractAudio Destination", extractOk && phaseOk && notConfused, null);
            Check("OutputParser download Destination (регрессия)", downloadOk, null);
        }

        private static bool SeqEqual(string[] a, string[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool Contains(string[] args, string value)
        {
            foreach (string s in args)
                if (string.Equals(s, value, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool NextIs(string[] args, string key, string value)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.Ordinal))
                    return string.Equals(args[i + 1], value, StringComparison.Ordinal);
            return false;
        }

        private static bool AnyPair(string[] args, string key, string value)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.Ordinal) && string.Equals(args[i + 1], value, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool FormatIs(string[] args, string expected)
        {
            return NextIs(args, "-f", expected);
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
                !YouTubeUrl.IsValid("https://youtu.be/dQw4w9WgXcQ\ta=b") &&
                !YouTubeUrl.IsValid("https://youtu.be/dQw4w9WgXcQ\"x") &&
                !YouTubeUrl.IsValid("https://youtu.be\\dQw4w9WgXcQ") &&
                string.Equals(YouTubeUrl.ExtractFirst("Смотри: https://youtu.be/dQw4w9WgXcQ?si=x (классика)"), "https://youtu.be/dQw4w9WgXcQ?si=x", StringComparison.Ordinal);
            Check("Валидатор YouTube-ссылок", ok, null);
        }

        private static void TestQuoting()
        {
            bool ok =
                string.Equals(YtDlpRunner.Quote("abc"), "abc", StringComparison.Ordinal) &&
                string.Equals(YtDlpRunner.Quote("a b"), "\"a b\"", StringComparison.Ordinal) &&
                string.Equals(YtDlpRunner.Quote("a\"b"), "\"a\\\"b\"", StringComparison.Ordinal) &&
                string.Equals(YtDlpRunner.Quote("a\\"), "a\\", StringComparison.Ordinal) &&
                string.Equals(YtDlpRunner.Quote("a\tb"), "\"a\tb\"", StringComparison.Ordinal) &&
                string.Equals(YtDlpRunner.Quote(""), "\"\"", StringComparison.Ordinal);
            Check("Кавычкирование аргументов (Windows argv)", ok, null);
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

        private static void TestPipeDrainLifecycle()
        {
            try
            {
                // Reproduces the diagnosed hang shape: the direct child (cmd) exits
                // immediately, while a grandchild (ping, spawned by start /b with
                // inherited std handles) keeps the redirected stdout/stderr pipes
                // open for ~15s. A parameterless WaitForExit() would block until the
                // grandchild dies; the bounded drain wait must return earlier and
                // keep output buffered before EOF.
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = "/c start \"yd_drain\" /b ping -n 15 127.0.0.1 & echo PIPE_DRAIN_OK & exit /b 0";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                Stopwatch sw = Stopwatch.StartNew();
                Process p = Process.Start(psi);
                StringBuilder so = new StringBuilder();
                YtDlpRunner.PipeDrainCounter drain = new YtDlpRunner.PipeDrainCounter();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { lock (so) { so.AppendLine(e.Data); } return; }
                    drain.OnData(s, e);
                };
                p.ErrorDataReceived += drain.OnData;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                bool exited = p.WaitForExit(25000);
                drain.WaitDrained(5000);
                double seconds = sw.Elapsed.TotalSeconds;
                string text;
                lock (so) { text = so.ToString(); }
                bool pipeEof = drain.Drained();
                bool markerKept = text.IndexOf("PIPE_DRAIN_OK", StringComparison.Ordinal) >= 0;
                try { p.Dispose(); } catch { }
                bool ok = exited && markerKept && seconds < 12.0;
                Check("Pipe lifecycle: parent exit + живой child не блокирует", ok, string.Format("exit={0}, {1:F1}s, pipeEOF={2}, строка сохранена={3}", exited, seconds, pipeEof, markerKept));
            }
            catch (Exception ex)
            {
                Check("Pipe lifecycle: parent exit + живой child не блокирует", false, ex.Message);
            }
        }

        private static void TestApplyAutoUrl()
        {
            // Regression for the clipboard re-trigger loop: PollClipboard ->
            // ApplyAutoUrl -> UpdateUrlStatus re-armed _titleTimer on every tick
            // even when the field already held the same URL, so a fresh title
            // fetch was spawned roughly every 2s and every result was discarded
            // as stale (_titleSeq) — the UI stayed on "Fetching title…" forever.
            // Detection: in the steady state after a tick the timer is stopped;
            // the fixed same-URL path must keep it stopped, while a changed URL
            // must re-arm it (fetch scheduled).
            try
            {
                MainForm form = new MainForm();
                try
                {
                    System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    TextBox txt = (TextBox)typeof(MainForm).GetField("txtUrl", F).GetValue(form);
                    System.Windows.Forms.Timer timer = (System.Windows.Forms.Timer)typeof(MainForm).GetField("_titleTimer", F).GetValue(form);
                    var miApply = typeof(MainForm).GetMethod("ApplyAutoUrl", F);

                    const string urlA = "https://youtu.be/dQw4w9WgXcQ";
                    const string urlB = "https://youtu.be/BaW_jenozKc";

                    // New URL: applied to the field.
                    miApply.Invoke(form, new object[] { urlA });
                    bool newUrlApplied = string.Equals(txt.Text, urlA, StringComparison.Ordinal);

                    // Steady state after a tick: timer stopped. Same URL → no-op.
                    timer.Stop();
                    miApply.Invoke(form, new object[] { urlA });
                    bool sameUrlNoRefetch = !timer.Enabled && string.Equals(txt.Text, urlA, StringComparison.Ordinal);

                    // Changed URL: applied and fetch re-armed (timer started).
                    miApply.Invoke(form, new object[] { urlB });
                    bool changedUrlRefetch = timer.Enabled && string.Equals(txt.Text, urlB, StringComparison.Ordinal);

                    Check("ApplyAutoUrl: тот же URL → no re-fetch, новый → fetch", newUrlApplied && sameUrlNoRefetch && changedUrlRefetch,
                        "new=" + newUrlApplied + " sameNoRefetch=" + sameUrlNoRefetch + " changedRefetch=" + changedUrlRefetch);
                }
                finally
                {
                    form.Dispose();
                }
            }
            catch (Exception ex)
            {
                Check("ApplyAutoUrl: тот же URL → no re-fetch, новый → fetch", false, ex.Message);
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
