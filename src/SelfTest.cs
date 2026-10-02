using System;
using System.Collections.Generic;
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
            TestRunAsyncDrain();
            TestApplyAutoUrl();
            TestCutModel();
            TestThumbCacheDeliver();
            TestFilmstripPrepPlan();
            TestTrimJobArgs();
            TestTrimRegionMath();
            TestTrimNaming();
            TestMediaProbe();
            TestFrameDecoder();
            TestPngExtract();
            TestTrimJobRun();
            TestEditorTrimRuntime();
            TestEditorTrimNothingLeft();
            TestEditorFrameCrop();

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
            string dir = MakeTempDir();
            try
            {
                string path = Path.Combine(dir, "settings.ini");
                Settings st = new Settings(path);
                st.LastFolder = "C:\\__selftest__";
                st.HasChosenFolder = true;
                st.Save();
                Settings st2 = new Settings(path);
                st2.Load();
                bool ok = string.Equals(st2.LastFolder, "C:\\__selftest__", StringComparison.Ordinal) && st2.HasChosenFolder;

                bool atomicOk = !File.Exists(path + ".tmp") && !File.Exists(path + ".bak");

                // The Cutter keeps its two folder memories in the same file as the
                // download folder, and must not disturb any of them.
                Settings folders = new Settings(path);
                folders.LastFolder = "C:\\__download__";
                folders.OpenFolder = "C:\\__open__";
                folders.FrameFolder = "C:\\__frame__";
                folders.ImageFolder = "C:\\__image__";
                folders.Save();
                Settings folders2 = new Settings(path);
                folders2.Load();
                bool foldersOk = string.Equals(folders2.LastFolder, "C:\\__download__", StringComparison.Ordinal)
                    && string.Equals(folders2.OpenFolder, "C:\\__open__", StringComparison.Ordinal)
                    && string.Equals(folders2.FrameFolder, "C:\\__frame__", StringComparison.Ordinal)
                    && string.Equals(folders2.ImageFolder, "C:\\__image__", StringComparison.Ordinal);
                Check("settings.ini независимые папки (download / Open / Image / Save frame)", foldersOk, path);

                Settings legacy = new Settings(path);
                legacy.LastFolder = "E:\\YouTubeDownloader\\Видео";
                legacy.Save();
                Settings legacy2 = new Settings(path);
                legacy2.Load();
                bool legacyIgnored = !legacy2.HasChosenFolder;

                Check("settings.ini запись/чтение (LastFolder + FolderChosen, temp dir)", ok && legacyIgnored, path);
                Check("settings.ini atomic save (нет .tmp/.bak остатков)", atomicOk, null);
            }
            catch (Exception ex)
            {
                Check("settings.ini запись/чтение", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static void TestQualitySettings()
        {
            string dir = MakeTempDir();
            try
            {
                string path = Path.Combine(dir, "settings.ini");
                Settings st = new Settings(path);
                st.Quality = DownloadQuality.P720;
                st.Save();
                Settings st2 = new Settings(path);
                st2.Load();
                bool ok720 = st2.Quality == DownloadQuality.P720 && string.Equals(st2.Get("Quality"), "720", StringComparison.Ordinal);

                st.Quality = DownloadQuality.AudioOnly;
                st.Save();
                Settings st3 = new Settings(path);
                st3.Load();
                bool okAudio = st3.Quality == DownloadQuality.AudioOnly;

                File.WriteAllText(path, "[General]\r\nQuality=some-garbage\r\n", new UTF8Encoding(false));
                Settings st4 = new Settings(path);
                st4.Load();
                bool okFallback = st4.Quality == DownloadQuality.BestAvailable;

                Settings st5 = new Settings(path);
                st5.Load();
                bool okMissing = st5.Quality == DownloadQuality.BestAvailable;

                Check("settings.ini Quality (round-trip + fallback)", ok720 && okAudio && okFallback && okMissing, path);
            }
            catch (Exception ex)
            {
                Check("settings.ini Quality (round-trip + fallback)", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
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
                    "--newline", "--color", "no_color", "--encoding", "utf-8", "--ignore-config", "--no-playlist", "--windows-filenames", "--js-runtime", "deno",
                    "-f", "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best",
                    "--merge-output-format", "mp4",
                    "--ffmpeg-location", AppPaths.FfmpegExe,
                    "-o", template,
                    url
                };
                Check("DownloadArgs BestAvailable (полный набор + --no-playlist + --windows-filenames)", SeqEqual(best, expectedBest), null);

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

                bool winFilenamesAll =
                    Contains(best, "--windows-filenames") &&
                    Contains(a1080, "--windows-filenames") &&
                    Contains(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P720), "--windows-filenames") &&
                    Contains(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P480), "--windows-filenames") &&
                    Contains(YtDlpRunner.DownloadArgs(folder, url, DownloadQuality.P360), "--windows-filenames") &&
                    Contains(audio, "--windows-filenames");
                Check("DownloadArgs --windows-filenames во всех 6 режимах", winFilenamesAll, null);

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

            bool nocookie =
                YouTubeUrl.IsValid("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ") &&
                YouTubeUrl.IsValid("https://youtube-nocookie.com/v/dQw4w9WgXcQ") &&
                YouTubeUrl.IsValid("https://www.youtube-nocookie.com/shorts/dQw4w9WgXcQ") &&
                !YouTubeUrl.IsValid("https://youtube-nocookie.com/unknown/dQw4w9WgXcQ");
            Check("Валидатор: youtube-nocookie.com (embed/v/shorts, whitelist)", nocookie, null);

            bool watchPath =
                YouTubeUrl.IsValid("https://www.youtube.com/watch/dQw4w9WgXcQ") &&
                YouTubeUrl.IsValid("https://youtube.com/watch/dQw4w9WgXcQ?t=42s") &&
                !YouTubeUrl.IsValid("https://youtube.com/watch/dQw4w9WgX") &&
                !YouTubeUrl.IsValid("https://www.youtube.com/watch?v=");
            Check("Валидатор: формат watch/VIDEO_ID", watchPath, null);

            bool extract =
                string.Equals(YouTubeUrl.ExtractFirst("https://youtu.be/dQw4w9WgXcQ;https://youtu.be/BaW_jenozKc"), "https://youtu.be/dQw4w9WgXcQ", StringComparison.Ordinal) &&
                string.Equals(YouTubeUrl.ExtractFirst("[https://youtu.be/dQw4w9WgXcQ]"), "https://youtu.be/dQw4w9WgXcQ", StringComparison.Ordinal) &&
                string.Equals(YouTubeUrl.ExtractFirst("https://www.youtube.com/watch?v=dQw4w9WgXcQ,и ещё текст"), "https://www.youtube.com/watch?v=dQw4w9WgXcQ", StringComparison.Ordinal);
            Check("ExtractFirst: разделение по ';', '[', ','", extract, null);
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
            bad += Expect("write open denied", "ERROR: unable to open for writing: [Errno 13] Permission denied", ErrorCategory.PathError) ? 0 : 1;
            bad += Expect("winerror rename", "ERROR: unable to rename video file: [WinError 5] Access is denied: 'a.part' -> 'a.mp4'", ErrorCategory.PathError) ? 0 : 1;
            bad += Expect("content 403 not path", "ERROR: [youtube] xyz: HTTP Error 403: Forbidden", ErrorCategory.UpdateSuspect) ? 0 : 1;
            bad += Expect("generic access text unknown", "ERROR: The uploader has disabled access. Access is denied by policy.", ErrorCategory.Unknown) ? 0 : 1;
            bad += Expect("unknown", "ERROR: something completely else 42", ErrorCategory.Unknown) ? 0 : 1;
            Check("Классификатор ошибок (16 кейсов)", bad == 0, "ошибок: " + bad);
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

        private static void TestRunAsyncDrain()
        {
            if (!File.Exists(AppPaths.YtExe))
            {
                Check("RunAsync: полный drain stdout/stderr (--version)", false, "yt.exe не найден");
                return;
            }
            try
            {
                List<string> lines = new List<string>();
                Process startedProc = null;
                YtRunResult r = YtDlpRunner.RunAsync(
                    new[] { "--ignore-config", "--version" },
                    delegate(YtLine line) { lock (lines) { lines.Add(line.Text); } },
                    delegate(Process p) { startedProc = p; }).GetAwaiter().GetResult();
                bool started = startedProc != null;
                string outp = r.Output != null ? r.Output.Trim() : "";
                string lastLine = "";
                lock (lines)
                {
                    if (lines.Count > 0) lastLine = lines[lines.Count - 1].Trim();
                }
                bool drained = r.ExitCode == 0 && outp.Length > 0
                    && char.IsDigit(outp[0])
                    && outp == lastLine;
                Check("RunAsync: полный drain stdout/stderr (--version)", drained && started,
                    "exit=" + r.ExitCode + " out=<" + outp + "> lines=" + (lines != null ? lines.Count.ToString() : "?") + " onStarted=" + started);
            }
            catch (Exception ex)
            {
                Check("RunAsync: полный drain stdout/stderr (--version)", false, ex.Message);
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

        private static string MakeTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "YouTubeDownloader", "selftest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string Ffmpeg(params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = AppPaths.FfmpegExe;
            psi.Arguments = YtDlpRunner.FormatArgs(args);
            psi.WorkingDirectory = AppPaths.BaseDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                StringBuilder errBuf = new StringBuilder();
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null) lock (errBuf) { if (errBuf.Length < 4000) errBuf.AppendLine(e.Data); }
                };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit(120000);
                if (p.ExitCode != 0) throw new IOException("ffmpeg failed: " + errBuf.ToString());
            }
            return null;
        }

        private static string MakeTestVideo(string path, double seconds, bool withAudio)
        {
            List<string> args = new List<string>();
            args.Add("-hide_banner");
            args.Add("-loglevel");
            args.Add("error");
            args.Add("-y");
            args.Add("-f");
            args.Add("lavfi");
            args.Add("-i");
            args.Add("testsrc2=duration=" + seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ":size=320x240:rate=15");
            if (withAudio)
            {
                args.Add("-f");
                args.Add("lavfi");
                args.Add("-i");
                args.Add("sine=frequency=440:duration=" + seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            }
            args.Add("-c:v");
            args.Add("libx264");
            args.Add("-preset");
            args.Add("ultrafast");
            args.Add("-pix_fmt");
            args.Add("yuv420p");
            if (withAudio)
            {
                args.Add("-c:a");
                args.Add("aac");
                args.Add("-b:a");
                args.Add("64k");
                args.Add("-shortest");
            }
            args.Add(path);
            Ffmpeg(args.ToArray());
            return path;
        }

        private static double ProbeDuration(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfprobeExe;
                psi.Arguments = YtDlpRunner.FormatArgs(new[] { "-v", "error", "-show_entries", "stream=codec_type,duration", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1", path });
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                    p.BeginErrorReadLine();
                    string so = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(30000);
                    if (p.ExitCode != 0) return -1;
                    double best = -1;
                    string[] lines = so.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (!line.StartsWith("duration=", StringComparison.Ordinal)) continue;
                        double d;
                        if (double.TryParse(line.Substring(9), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) && d > best)
                            best = d;
                    }
                    return best;
                }
            }
            catch
            {
                return -1;
            }
        }

        private static bool RunTrim(string input, string output, List<TrimRegion> regions, bool hasAudio)
        {
            try
            {
                string[] args = TrimJob.BuildTrimArgs(input, output, regions, hasAudio);
                Ffmpeg(args);
                return File.Exists(output);
            }
            catch
            {
                return false;
            }
        }

        private static void TestCutModel()
        {
            try
            {
                CutModel m = new CutModel(100);
                bool mergeD2 = m.AddCut(10, 20) && m.AddCut(15, 30) && m.CutCount == 1
                    && Math.Abs(m.Cuts[0].Start - 10) < 1e-9 && Math.Abs(m.Cuts[0].End - 30) < 1e-9;

                bool touchMerge = m.AddCut(30, 40) && m.CutCount == 1;
                bool minLen = !m.AddCut(50, 50.01);

                bool edges = true;
                CutModel m2 = new CutModel(10);
                List<TrimRegion> empty = m2.KeepRegions();
                edges = edges && empty.Count == 1 && Math.Abs(empty[0].Start) < 1e-9 && Math.Abs(empty[0].End - 10) < 1e-9;

                CutModel m3 = new CutModel(10);
                m3.AddCut(0, 3);
                m3.AddCut(8, 10);
                List<TrimRegion> mid = m3.KeepRegions();
                edges = edges && mid.Count == 1 && Math.Abs(mid[0].Start - 3) < 1e-9 && Math.Abs(mid[0].End - 8) < 1e-9;

                CutModel m4 = new CutModel(10);
                m4.AddCut(2, 4);
                m4.AddCut(6, 8);
                List<TrimRegion> keeps = m4.KeepRegions();
                edges = edges && keeps.Count == 3
                    && Math.Abs(keeps[0].Start) < 1e-9 && Math.Abs(keeps[0].End - 2) < 1e-9
                    && Math.Abs(keeps[1].Start - 4) < 1e-9 && Math.Abs(keeps[1].End - 6) < 1e-9
                    && Math.Abs(keeps[2].Start - 8) < 1e-9 && Math.Abs(keeps[2].End - 10) < 1e-9;

                bool undoRedo = true;
                CutModel m5 = new CutModel(10);
                m5.AddCut(1, 2);
                m5.AddCut(4, 5);
                m5.Undo();
                undoRedo = undoRedo && m5.CutCount == 1 && m5.CanRedo;
                m5.Redo();
                undoRedo = undoRedo && m5.CutCount == 2 && !m5.CanRedo;
                m5.Undo();
                m5.Undo();
                undoRedo = undoRedo && m5.CutCount == 0 && !m5.CanUndo;

                bool tx = true;
                CutModel m6 = new CutModel(10);
                m6.AddCut(1, 2);
                m6.BeginTransaction();
                m6.AddCut(3, 4);
                m6.Rollback();
                tx = tx && m6.CutCount == 1;
                m6.BeginTransaction();
                m6.AddCut(3, 4);
                m6.Commit();
                tx = tx && m6.CutCount == 2 && m6.CanUndo;

                Check("CutModel: слияние пересечений (D2) + min-длина", mergeD2 && touchMerge && minLen, null);
                Check("CutModel: комплемент (края/середина/пусто)", edges, "regions=" + keeps.Count);
                Check("CutModel: undo/redo (D5)", undoRedo && tx, null);
            }
            catch (Exception ex)
            {
                Check("CutModel", false, ex.Message);
            }
        }

        private class MockInvoker : System.ComponentModel.ISynchronizeInvoke
        {
            public bool InvokeRequired { get { return true; } }
            public IAsyncResult BeginInvoke(Delegate method, object[] args)
            {
                method.DynamicInvoke(args);
                return null;
            }
            public object EndInvoke(IAsyncResult result) { return null; }
            public object Invoke(Delegate method, object[] args)
            {
                return method.DynamicInvoke(args);
            }
        }

        private static void TestThumbCacheDeliver()
        {
            try
            {
                MockInvoker invoker = new MockInvoker();
                using (ThumbCache tc = new ThumbCache("test.mp4", invoker))
                {
                    ThumbRun run = new ThumbRun(1, 0, 10, 1, 40);
                    System.Reflection.MethodInfo mi = typeof(ThumbCache).GetMethod("Deliver",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    List<System.Drawing.Bitmap> batch = new List<System.Drawing.Bitmap>();
                    List<int> idx = new List<int>();
                    mi.Invoke(tc, new object[] { run, batch, idx, false });
                }
                Check("ThumbCache: BeginInvoke без TargetParameterCountException", true, null);
            }
            catch (Exception ex)
            {
                Check("ThumbCache: BeginInvoke без TargetParameterCountException", false, ex.InnerException != null ? ex.InnerException.Message : ex.Message);
            }
        }

        private static void TestFilmstripPrepPlan()
        {
            try
            {
                int n = FilmstripPrep.PlanFrames(1704.0, 636);
                int expected = (636 + FilmstripPrep.PixelsPerFrame - 1) / FilmstripPrep.PixelsPerFrame;
                int w = FilmstripPrep.SourceThumbWidth(1920, 1080);
                int fb = FilmstripPrep.SourceThumbWidth(0, 0);
                Check("FilmstripPrep: кадры от ширины strip, не от секунд",
                    n == expected && n >= 32 && n <= 512, "n=" + n + " expected=" + expected);
                Check("FilmstripPrep: proxy width кратна 4 (BGR24 stride)",
                    (w & 3) == 0 && w >= 8 && (fb & 3) == 0, "w=" + w + " fb=" + fb);
            }
            catch (Exception ex)
            {
                Check("FilmstripPrep", false, ex.Message);
            }
        }

        private static void TestTrimJobArgs()
        {
            try
            {
                List<TrimRegion> regions = new List<TrimRegion>();
                regions.Add(new TrimRegion(0, 2));
                regions.Add(new TrimRegion(4, 6));
                string fc = TrimJob.BuildFilterComplex(regions, true);
                bool withAudio = fc.Contains("trim=start=0.000000:end=2.000000")
                    && fc.Contains("atrim=start=4.000000:end=6.000000")
                    && fc.Contains("setpts=PTS-STARTPTS") && fc.Contains("asetpts=PTS-STARTPTS")
                    && fc.Contains("concat=n=2:v=1:a=1[v][a]")
                    && !fc.Contains("aselect");

                string fcNoAudio = TrimJob.BuildFilterComplex(regions, false);
                bool noAudio = fcNoAudio.Contains("concat=n=2:v=1:a=0[v]") && !fcNoAudio.Contains("atrim");

                string[] args = TrimJob.BuildTrimArgs("in.mp4", "out.mp4", regions, true);
                bool trimArgs = IndexOf(args, "-filter_complex") >= 0 && IndexOf(args, "-c:v") >= 0
                    && IndexOf(args, "libx264") >= 0 && IndexOf(args, "yuv420p") >= 0
                    && IndexOf(args, "-progress") >= 0 && NextIs(args, "-progress", "pipe:1")
                    && IndexOf(args, "-c:a") >= 0;
                string[] argsNoAudio = TrimJob.BuildTrimArgs("in.mp4", "out.mp4", regions, false);
                bool noAudioArgs = IndexOf(argsNoAudio, "-c:a") < 0 && IndexOf(argsNoAudio, "-map") >= 0;

                bool progress = TrimJob.TryProgressOutTimeUs("out_time_us=1234567").HasValue
                    && Math.Abs(TrimJob.TryProgressOutTimeUs("out_time_us=1234567").Value - 1.234567) < 1e-9
                    && !TrimJob.TryProgressOutTimeUs("out_time=1.2").HasValue
                    && TrimJob.IsProgressEnd("progress=end") && !TrimJob.IsProgressEnd("progress=continue");

                Check("TrimJob: filter_complex trim/atrim+concat (aselect заменён)", withAudio && noAudio, null);
                Check("TrimJob: аргументы trim (кодеки/progress/ветка без аудио)", trimArgs && noAudioArgs && progress, null);
                TestHybridPlanMath();
            }
            catch (Exception ex)
            {
                Check("TrimJob", false, ex.Message);
            }
        }

        private static void TestHybridPlanMath()
        {
            try
            {
                List<double> keys = new List<double>();
                keys.Add(0.0);
                keys.Add(2.0);
                keys.Add(4.0);
                keys.Add(6.0);
                double fps = 60.0;
                List<TrimRegion> regions = new List<TrimRegion>();
                regions.Add(new TrimRegion(0.0, 1.0));
                regions.Add(new TrimRegion(3.0, 5.0));
                List<double> heads = HybridCut.PlanHeadEnds(keys, regions, fps);
                bool ok = heads.Count == 2
                    && Math.Abs(heads[0] - 0.0) < 1e-9
                    && Math.Abs(heads[1] - 4.0) < 1e-9;
                List<TrimRegion> onKey = new List<TrimRegion>();
                onKey.Add(new TrimRegion(2.0, 5.0));
                List<double> headsKey = HybridCut.PlanHeadEnds(keys, onKey, fps);
                ok = ok && headsKey.Count == 1 && Math.Abs(headsKey[0] - 2.0) < 1e-9;
                ok = ok && HybridCut.IsSupportedVideoCodec("av1") && HybridCut.IsSupportedVideoCodec("h264");
                ok = ok && !HybridCut.IsSupportedVideoCodec("mpeg4") && !HybridCut.IsSupportedVideoCodec(null);
                ok = ok && HybridCut.ZoneVideoArgs("av1") != null && HybridCut.ZoneVideoArgs("mpeg4") == null;
                ok = ok && Math.Abs(HybridCut.ParseFps("60/1") - 60.0) < 1e-9;
                ok = ok && HybridCut.EstimateMaxGop(keys, 8.0) >= 2.0 - 1e-9;
                ok = ok && HybridCut.PrevKeyIndex(keys, 3.0) == 1 && HybridCut.NextKeyIndex(keys, 3.0) == 2;
                ok = ok && HybridCut.ToSample(1.0, 44100) == 44100 && HybridCut.ToSample(1.0, 48000) == 48000
                    && HybridCut.ToSample(0.5, 48000) == 24000;
                ok = ok && Math.Abs(HybridCut.CopyDuration(100.0, 110.0) - 10.0) < 1e-9
                    && HybridCut.CopyDuration(5.0, 5.0) == 0;
                ok = ok && HybridCut.VerifyFrameBudget(keys, regions, heads, fps);
                Check("HybridCut: plan head/copy split (pure math)", ok,
                    "heads=" + heads[0].ToString("0.0") + "," + heads[1].ToString("0.0"));
            }
            catch (Exception ex)
            {
                Check("HybridCut: plan head/copy split (pure math)", false, ex.Message);
            }
        }

        private static string MakeTestAudio(string path, double seconds)
        {
            Ffmpeg(new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=" + seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                "-c:a", "aac", "-b:a", "64k", path
            });
            return path;
        }

        private static void TestMediaProbe()
        {
            if (!AppPaths.FfprobePresent())
            {
                Check("MediaProbe (offline)", false, "ffprobe.exe не найден");
                return;
            }
            string dir = MakeTempDir();
            try
            {
                string video = MakeTestVideo(Path.Combine(dir, "v.mp4"), 2, true);
                MediaInfo vi = MediaProbe.Probe(video);
                bool vOk = vi.Ok && vi.HasVideo && vi.HasAudio && vi.Width == 320 && vi.Height == 240
                    && Math.Abs(vi.Duration - 2.0) < 0.1
                    && (string.Equals(vi.VideoCodec, "h264", StringComparison.OrdinalIgnoreCase));

                string audio = MakeTestAudio(Path.Combine(dir, "a.m4a"), 2);
                MediaInfo ai = MediaProbe.Probe(audio);
                bool aOk = ai.Ok && ai.IsAudioOnly && Math.Abs(ai.Duration - 2.0) < 0.1;

                MediaInfo bad = MediaProbe.Probe(Path.Combine(dir, "missing.mp4"));
                bool badOk = !bad.Ok && bad.Error != null;

                Check("MediaProbe: видео/аудио-only/ошибка (D14)", vOk && aOk && badOk,
                    "v=" + vi.Ok + "/" + vi.Duration.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " a:ok=" + ai.Ok + " video=" + ai.HasVideo + " audio=" + ai.HasAudio
                    + " dur=" + ai.Duration.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " err=" + (ai.Error ?? "none")
                    + " bad=" + badOk);
            }
            catch (Exception ex)
            {
                Check("MediaProbe", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static void TestFrameDecoder()
        {
            if (!AppPaths.FfmpegPresentForTest())
            {
                Check("FrameDecoder (offline)", false, "ffmpeg.exe не найден");
                return;
            }
            string dir = MakeTempDir();
            try
            {
                string video = MakeTestVideo(Path.Combine(dir, "v.mp4"), 4, true);
                using (FrameDecoder dec = new FrameDecoder(video, 320, 240))
                {
                    DecodedFrame f1 = dec.StartAt(1.0);
                    bool first = f1 != null && f1.Image != null && Math.Abs(f1.PtsTime - 1.0) < 0.1;
                    DecodedFrame f2 = dec.StepForward();
                    bool second = f2 != null && f2.PtsTime > f1.PtsTime + 0.03;
                    DecodedFrame f3 = dec.StepBackward();
                    bool back = f3 != null && Math.Abs(f3.PtsTime - f1.PtsTime) < 0.1;
                    if (f1 != null && f1.Image != null) f1.Image.Dispose();
                    if (f2 != null && f2.Image != null) f2.Image.Dispose();
                    if (f3 != null && f3.Image != null) f3.Image.Dispose();
                    Check("FrameDecoder: seek+pts_time (D9), шаг вперёд/назад (D5)", first && second && back,
                        "pts1=" + (f1 != null ? f1.PtsTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "null")
                        + " pts2=" + (f2 != null ? f2.PtsTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "null")
                        + " pts3=" + (f3 != null ? f3.PtsTime.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "null"));
                }
            }
            catch (Exception ex)
            {
                Check("FrameDecoder", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static void TestPngExtract()
        {
            if (!AppPaths.FfmpegPresentForTest())
            {
                Check("PNG identity (offline)", false, "ffmpeg.exe не найден");
                return;
            }
            string dir = MakeTempDir();
            try
            {
                string video = MakeTestVideo(Path.Combine(dir, "v.mp4"), 4, false);
                string png1 = Path.Combine(dir, "f1.png");
                string png2 = Path.Combine(dir, "f2.png");
                Ffmpeg(TrimJob.BuildPngArgs(video, png1, 2.0));
                Ffmpeg(TrimJob.BuildPngArgs(video, png2, 2.0));
                string h1 = Sha256(png1);
                string h2 = Sha256(png2);
                bool identical = h1 != null && string.Equals(h1, h2, StringComparison.OrdinalIgnoreCase);
                Check("PNG identity: SHA-256 двух извлечений одного pts_time (§6)", identical, h1 != null ? h1.Substring(0, 16) : null);
            }
            catch (Exception ex)
            {
                Check("PNG identity", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static void TestTrimJobRun()
        {
            if (!AppPaths.FfmpegPresentForTest())
            {
                Check("Trim (multi-cut)", false, "ffmpeg.exe не найден");
                return;
            }
            string dir = MakeTempDir();
            try
            {
                string input = MakeTestVideo(Path.Combine(dir, "in.mp4"), 10, true);
                List<TrimRegion> regions = new List<TrimRegion>();
                regions.Add(new TrimRegion(0, 2));
                regions.Add(new TrimRegion(4, 6));
                regions.Add(new TrimRegion(8, 10));
                string output = Path.Combine(dir, "out.mp4");
                bool ran = RunTrim(input, output, regions, true);
                double dur = ran ? ProbeDuration(output) : -1;
                bool ok = ran && dur > 0 && Math.Abs(dur - 6.0) <= 0.05;
                string hashBefore = Sha256(input);
                double durAgain = ok ? ProbeDuration(input) : -1;
                string hashAfter = Sha256(input);
                bool originalUntouched = string.Equals(hashBefore, hashAfter, StringComparison.OrdinalIgnoreCase) && Math.Abs(durAgain - 10.0) < 0.1;
                Check("Trim: 3 выреза на testsrc, выход 6.00±0.05 (§6)", ok, "dur=" + dur.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                Check("Trim: оригинал не изменён (§6)", originalUntouched, null);
            }
            catch (Exception ex)
            {
                Check("Trim (multi-cut)", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static bool Near(double a, double b)
        {
            return Math.Abs(a - b) < 1e-6;
        }

        private static string RegionsDiag(List<TrimRegion> regions)
        {
            if (regions == null) return "(null)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < regions.Count; i++)
            {
                if (sb.Length > 0) sb.Append(" + ");
                sb.Append(regions[i].Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('-');
                sb.Append(regions[i].End.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.Length == 0 ? "(пусто)" : sb.ToString();
        }

        // The arithmetic the trim rests on, without touching ffmpeg: applied ranges in,
        // surviving segments out. Covers the boundaries the task calls out explicitly.
        private static void TestTrimRegionMath()
        {
            try
            {
                // Один вырезанный диапазон 20-30 из 100 с → 0-20 и 30-100.
                List<TrimRegion> keep = TrimJob.ComputeKeepRegions(
                    new List<TrimRegion> { new TrimRegion(20, 30) }, 100);
                bool single = keep.Count == 2
                    && Near(keep[0].Start, 0) && Near(keep[0].End, 20)
                    && Near(keep[1].Start, 30) && Near(keep[1].End, 100);

                // Несколько диапазонов → сегменты в правильном порядке по времени.
                keep = TrimJob.ComputeKeepRegions(
                    new List<TrimRegion> { new TrimRegion(20, 30), new TrimRegion(50, 60) }, 100);
                bool multi = keep.Count == 3
                    && Near(keep[0].Start, 0) && Near(keep[0].End, 20)
                    && Near(keep[1].Start, 30) && Near(keep[1].End, 50)
                    && Near(keep[2].Start, 60) && Near(keep[2].End, 100);

                // Граница 0 и граница Duration: сегмент с краю просто не появляется.
                List<TrimRegion> atStartList = TrimJob.ComputeKeepRegions(
                    new List<TrimRegion> { new TrimRegion(0, 10) }, 100);
                bool atStart = atStartList.Count == 1
                    && Near(atStartList[0].Start, 10) && Near(atStartList[0].End, 100);
                List<TrimRegion> atEndList = TrimJob.ComputeKeepRegions(
                    new List<TrimRegion> { new TrimRegion(90, 100) }, 100);
                bool atEnd = atEndList.Count == 1
                    && Near(atEndList[0].Start, 0) && Near(atEndList[0].End, 90);

                // Несортированные, перекрывающиеся и соседние диапазоны → один общий вырез.
                // 20-30 ∪ 25-35 = 20-35, а 50-60 и 60-70 склеиваются в 50-70,
                // значит остаются 0-20, 35-50 и 70-100.
                keep = TrimJob.ComputeKeepRegions(new List<TrimRegion>
                {
                    new TrimRegion(50, 60), new TrimRegion(20, 30),
                    new TrimRegion(25, 35), new TrimRegion(60, 70)
                }, 100);
                bool merged = keep.Count == 3
                    && Near(keep[0].Start, 0) && Near(keep[0].End, 20)
                    && Near(keep[1].Start, 35) && Near(keep[1].End, 50)
                    && Near(keep[2].Start, 70) && Near(keep[2].End, 100);

                // Полностью удаляемый ролик: остатка нет (не пустой, а именно нулевой список).
                bool nothing = TrimJob.ComputeKeepRegions(
                    new List<TrimRegion> { new TrimRegion(0, 100) }, 100).Count == 0;
                bool nothingSplit = TrimJob.ComputeKeepRegions(new List<TrimRegion>
                {
                    new TrimRegion(0, 40), new TrimRegion(40, 100)
                }, 100).Count == 0;

                // Крошечный вырез отбрасывается; выход за границы обрезается.
                List<TrimRegion> tiny = TrimJob.ComputeKeepRegions(
                    new List<TrimRegion> { new TrimRegion(10, 10.01) }, 100);
                bool tinyDropped = tiny.Count == 1 && Near(tiny[0].Start, 0) && Near(tiny[0].End, 100);
                List<TrimRegion> clampedLo = TrimJob.ComputeKeepRegions(
                    new List<TrimRegion> { new TrimRegion(-5, 10) }, 100);
                bool clampedStart = clampedLo.Count == 1
                    && Near(clampedLo[0].Start, 10) && Near(clampedLo[0].End, 100);
                List<TrimRegion> clampedHi = TrimJob.ComputeKeepRegions(
                    new List<TrimRegion> { new TrimRegion(95, 120) }, 100);
                bool clampedEnd = clampedHi.Count == 1
                    && Near(clampedHi[0].Start, 0) && Near(clampedHi[0].End, 95);

                // Ни один сегмент не имеет нулевой или отрицательной длины.
                bool allPositive = true;
                List<TrimRegion> all = TrimJob.ComputeKeepRegions(new List<TrimRegion>
                {
                    new TrimRegion(0, 5), new TrimRegion(10, 15), new TrimRegion(20, 100)
                }, 100);
                for (int i = 0; i < all.Count; i++) if (all[i].End - all[i].Start <= 0) allPositive = false;

                Check("TrimMath: один диапазон → два оставшихся сегмента", single, RegionsDiag(
                    TrimJob.ComputeKeepRegions(new List<TrimRegion> { new TrimRegion(20, 30) }, 100)));
                Check("TrimMath: несколько диапазонов, порядок сегментов", multi, RegionsDiag(
                    TrimJob.ComputeKeepRegions(new List<TrimRegion> { new TrimRegion(20, 30), new TrimRegion(50, 60) }, 100)));
                Check("TrimMath: граница 0 и граница Duration", atStart && atEnd,
                    RegionsDiag(atStartList) + " | " + RegionsDiag(atEndList));
                Check("TrimMath: перекрытия/соседние/несортированные → один вырез", merged, RegionsDiag(keep));
                Check("TrimMath: весь ролик помечен → остатка нет", nothing && nothingSplit, null);
                Check("TrimMath: крошечный диапазон и выход за границы", tinyDropped && clampedStart && clampedEnd,
                    RegionsDiag(tiny) + " | " + RegionsDiag(clampedLo) + " | " + RegionsDiag(clampedHi));
                Check("TrimMath: нет нулевых/отрицательных сегментов", allPositive, RegionsDiag(all));
                Check("TrimMath: сумма оставшегося = длительность минус вырезы",
                    Near(TrimJob.ExpectedOutputDuration(all), 100 - 5 - 5 - 80), null);
            }
            catch (Exception ex)
            {
                Check("TrimMath", false, ex.Message);
            }
        }

        // Result naming: never the source, never an existing result.
        private static void TestTrimNaming()
        {
            string dir = MakeTempDir();
            try
            {
                string src = Path.Combine(dir, "video.mp4");
                File.WriteAllText(src, "src");

                string p1 = TrimJob.SuggestTrimmedPath(src);
                bool first = string.Equals(Path.GetFileName(p1), "video_trimmed.mp4", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetDirectoryName(p1), dir, StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(p1, "1");

                string p2 = TrimJob.SuggestTrimmedPath(src);
                bool second = string.Equals(Path.GetFileName(p2), "video_trimmed (2).mp4", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(p2, "2");

                string p3 = TrimJob.SuggestTrimmedPath(src);
                bool third = string.Equals(Path.GetFileName(p3), "video_trimmed (3).mp4", StringComparison.OrdinalIgnoreCase);

                bool neverSource = !string.Equals(p1, src, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p2, src, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(p3, src, StringComparison.OrdinalIgnoreCase);
                bool sourceKept = File.Exists(src) && File.ReadAllText(src) == "src";

                // The result always follows the SOURCE folder - it is never placed in a
                // temp directory of its own. A nested source path is the case that would
                // expose any hardcoded output folder.
                string nestedDir = Path.Combine(dir, "my videos", "season 1");
                Directory.CreateDirectory(nestedDir);
                string nestedSrc = Path.Combine(nestedDir, "clip.mp4");
                File.WriteAllText(nestedSrc, "src");
                string nestedOut = TrimJob.SuggestTrimmedPath(nestedSrc);
                bool nested = string.Equals(Path.GetDirectoryName(nestedOut), nestedDir, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetFileName(nestedOut), "clip_trimmed.mp4", StringComparison.OrdinalIgnoreCase);

                Check("TrimNaming: video.mp4 → video_trimmed.mp4 → (2) → (3)", first && second && third,
                    Path.GetFileName(p1) + ", " + Path.GetFileName(p2) + ", " + Path.GetFileName(p3));
                Check("TrimNaming: исходник не перезаписывается", neverSource && sourceKept, null);
                Check("TrimNaming: результат всегда рядом с исходником (не в Temp)", nested,
                    nestedOut.Substring(dir.Length).TrimStart('\\'));
            }
            catch (Exception ex)
            {
                Check("TrimNaming", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        // The real runtime check of the whole path: a real EditorForm is opened on a
        // generated clip, a range is dragged and applied through the real controls, the
        // real "Save video (trim)" button is clicked, and the file that lands on disk is
        // what gets verified - not the internals.
        private static void TestEditorTrimRuntime()
        {
            if (!AppPaths.FfmpegPresentForTest())
            {
                Check("Trim через UI (runtime)", false, "ffmpeg.exe не найден");
                return;
            }
            string dir = MakeTempDir();
            try
            {
                string input = MakeTestVideo(Path.Combine(dir, "clip.mp4"), 12, true);
                string hashBefore = Sha256(input);
                double durBefore = ProbeDuration(input);

                string saved = null;
                string status = null;
                int progressPeak = -1;
                int progressEnd = -1;
                bool progressVisible = false;
                bool enabledAtOpen = true;
                bool enabledAfterApply = false;
                string err = null;

                Thread t = new Thread(new ThreadStart(delegate
                {
                    EditorForm form = null;
                    try
                    {
                        form = new EditorForm(input);
                        form.TestSuppressDialogs = true;
                        form.Shown += delegate
                        {
                            try
                            {
                                System.Diagnostics.Stopwatch ready = System.Diagnostics.Stopwatch.StartNew();
                                while (!form.TestReady && ready.ElapsedMilliseconds < 60000)
                                {
                                    Application.DoEvents();
                                    Thread.Sleep(10);
                                }
                                if (!form.TestReady) { err = "медиа не открылось за 60 c"; return; }
                                Application.DoEvents();

                                // Ничего не помечено: обрезка должна быть недоступна.
                                enabledAtOpen = form.TestSaveEnabled;

                                // Помечаем 2-4 c playhead-механикой: playhead на 2 c →
                                // Cut (Start), playhead на 4 c → Cut (End).
                                form.TestSeekTo(2.0);
                                form.TestCut();
                                form.TestSeekTo(4.0);
                                form.TestCut();
                                enabledAfterApply = form.TestSaveEnabled;

                                // Реальная кнопка → реальный ffmpeg → реальный файл.
                                saved = form.TestClickSaveVideo(120000);
                                status = form.TestStatusText;
                                progressPeak = form.TestProgressPeak;
                                progressEnd = form.TestProgressValue;
                                progressVisible = form.TestProgressVisible;
                            }
                            catch (Exception ex) { err = ex.Message; }
                            finally
                            {
                                try { form.Close(); }
                                catch { }
                            }
                        };
                        form.ShowDialog();
                    }
                    catch (Exception ex) { if (err == null) err = ex.Message; }
                }));
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                bool joined = t.Join(180000);

                if (!joined)
                {
                    Check("Trim через UI (runtime)", false, "EditorForm не закрылся за 180 c");
                    return;
                }
                if (err != null)
                {
                    Check("Trim через UI (runtime)", false, err);
                    return;
                }

                bool exists = saved != null && File.Exists(saved);
                double dur = exists ? ProbeDuration(saved) : -1;
                bool nameOk = exists && string.Equals(Path.GetFileName(saved), "clip_trimmed.mp4", StringComparison.OrdinalIgnoreCase);
                bool durOk = exists && Math.Abs(dur - 10.0) <= 0.35;   // 12 c минус вырезанные 2-4 c
                string hashAfter = Sha256(input);
                double durAfter = ProbeDuration(input);
                bool originalOk = string.Equals(hashBefore, hashAfter, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(durAfter - durBefore) < 0.1;

                Check("Trim UI: кнопка включается только после Cut",
                    !enabledAtOpen && enabledAfterApply,
                    "до Cut=" + enabledAtOpen + ", после Cut=" + enabledAfterApply);
                Check("Trim UI: результат создан как clip_trimmed.mp4", nameOk,
                    saved == null ? "файл не создан" : Path.GetFileName(saved));
                Check("Trim UI: длительность 10.00±0.35 c (12 c − вырез 2..4 c)", durOk,
                    "dur=" + dur.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                Check("Trim UI: оригинал не изменён (SHA-256 и длительность)", originalOk,
                    "dur=" + durAfter.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));

                // The success notice lives in the editor's status line, carries the file
                // NAME only (no folder, no drive), and no system dialog is involved.
                bool noticeOk = status != null
                    && status.Contains("clip_trimmed.mp4")
                    && status.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0
                    && status.IndexOf("\\", StringComparison.Ordinal) < 0
                    && status.IndexOf("/", StringComparison.Ordinal) < 0;
                Check("Trim UI: уведомление в статус-строке, только имя файла", noticeOk, "status=" + (status ?? "null"));
                // And the result really sits next to the source, not in a temp folder.
                bool beside = exists && string.Equals(Path.GetDirectoryName(saved), dir, StringComparison.OrdinalIgnoreCase);
                Check("Trim UI: результат сохранён рядом с исходником", beside,
                    exists ? Path.GetDirectoryName(saved) : "нет файла");
                // The progress line of the save: it runs during the job and stays at 100 %.
                Check("Trim UI: прогресс 0% → заполнение → 100% после сохранения",
                    progressPeak > 0 && progressEnd == 100 && progressVisible,
                    "peak=" + progressPeak + "%, конец=" + progressEnd + "%, виден=" + progressVisible);
            }
            catch (Exception ex)
            {
                Check("Trim через UI (runtime)", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        // The guard for "every part of the video is marked for removal": the click must
        // produce no file at all instead of a broken zero-length result. A clip shorter
        // than the default pending range makes the very first selection cover all of it.
        private static void TestEditorTrimNothingLeft()
        {
            string dir = MakeTempDir();
            try
            {
                string input = MakeTestVideo(Path.Combine(dir, "tiny.mp4"), 4, true);
                string hashBefore = Sha256(input);

                string saved = null;
                bool enabledAfterApply = false;
                string err = null;

                Thread t = new Thread(new ThreadStart(delegate
                {
                    EditorForm form = null;
                    try
                    {
                        form = new EditorForm(input);
                        form.TestSuppressDialogs = true;
                        form.Shown += delegate
                        {
                            try
                            {
                                System.Diagnostics.Stopwatch ready = System.Diagnostics.Stopwatch.StartNew();
                                while (!form.TestReady && ready.ElapsedMilliseconds < 60000)
                                {
                                    Application.DoEvents();
                                    Thread.Sleep(10);
                                }
                                if (!form.TestReady) { err = "медиа не открылось за 60 c"; return; }
                                Application.DoEvents();

                                // Mark the whole 4 s clip for removal with the playhead:
                                // Start at 0, End past the clip end (clamped to duration).
                                form.TestSeekTo(0.0);
                                form.TestCut();
                                form.TestSeekTo(4.0);
                                form.TestCut();
                                enabledAfterApply = form.TestSaveEnabled;
                                saved = form.TestClickSaveVideo(30000);
                            }
                            catch (Exception ex) { err = ex.Message; }
                            finally
                            {
                                try { form.Close(); }
                                catch { }
                            }
                        };
                        form.ShowDialog();
                    }
                    catch (Exception ex) { if (err == null) err = ex.Message; }
                }));
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                bool joined = t.Join(120000);

                if (!joined || err != null)
                {
                    Check("Trim UI: весь ролик помечен → файл не создаётся", false,
                        !joined ? "EditorForm не закрылся за 120 c" : err);
                    return;
                }

                string[] leftovers = Directory.GetFiles(dir, "*_trimmed*");
                bool noFile = saved == null && leftovers.Length == 0;
                bool originalOk = string.Equals(hashBefore, Sha256(input), StringComparison.OrdinalIgnoreCase);

                Check("Trim UI: весь ролик помечен → файл не создаётся",
                    noFile && enabledAfterApply,
                    "кнопка после Cut=" + enabledAfterApply + ", создано файлов=" + leftovers.Length);
                Check("Trim UI: при отказе оригинал не изменён", originalOk, null);
            }
            catch (Exception ex)
            {
                Check("Trim UI: весь ролик помечен → файл не создаётся", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        // The Save-frame crop flow end to end, without a dialog: the REAL frame
        // of a generated clip is decoded into the temp PNG, ffmpeg cuts the
        // rectangle out of that same file, the result lands in the folder the
        // button would use, and the temp PNG is gone afterwards. Two rectangles:
        // an offset one (proves the coordinates are SOURCE pixels) and the whole
        // frame (proves the frame really is the source resolution 320x240 and
        // not something the preview scaled down).
        private static void TestEditorFrameCrop()
        {
            if (!AppPaths.FfmpegPresentForTest())
            {
                Check("Crop frame (runtime)", false, "ffmpeg.exe не найден");
                return;
            }
            string dir = MakeTempDir();
            try
            {
                string input = MakeTestVideo(Path.Combine(dir, "crop.mp4"), 4, false);
                System.Drawing.Rectangle sel = new System.Drawing.Rectangle(40, 30, 120, 90);
                System.Drawing.Rectangle whole = new System.Drawing.Rectangle(0, 0, 320, 240);
                string saved = null, temp = null, err = null;
                string savedFull = null, tempFull = null, errFull = null;
                string openErr = null;
                bool cropOk = false, fullOk = false;

                Thread t = new Thread(new ThreadStart(delegate
                {
                    EditorForm form = null;
                    try
                    {
                        form = new EditorForm(input);
                        form.TestSuppressDialogs = true;
                        form.Shown += delegate
                        {
                            try
                            {
                                System.Diagnostics.Stopwatch ready = System.Diagnostics.Stopwatch.StartNew();
                                while (!form.TestReady && ready.ElapsedMilliseconds < 60000)
                                {
                                    Application.DoEvents();
                                    Thread.Sleep(10);
                                }
                                if (!form.TestReady) { openErr = "не открылся за 60 с"; return; }
                                Application.DoEvents();
                                cropOk = form.TestCropFrame(dir, sel, out saved, out temp, out err);
                                if (cropOk)
                                    fullOk = form.TestCropFrame(dir, whole, out savedFull, out tempFull, out errFull);
                            }
                            catch (Exception ex) { openErr = ex.Message; }
                            finally
                            {
                                try { form.Close(); }
                                catch { }
                            }
                        };
                        form.ShowDialog();
                    }
                    catch (Exception ex) { if (openErr == null) openErr = ex.Message; }
                }));
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                bool joined = t.Join(180000);

                if (!joined || openErr != null)
                {
                    Check("Crop frame (runtime)", false,
                        !joined ? "EditorForm не закрылся за 180 с" : openErr);
                    return;
                }

                System.Drawing.Size got = System.Drawing.Size.Empty;
                if (cropOk && saved != null && File.Exists(saved))
                {
                    using (System.Drawing.Bitmap b = new System.Drawing.Bitmap(saved)) got = b.Size;
                }
                Check("Crop frame: итог = вырезанная область 120x90 в координатах исходника",
                    cropOk && got.Width == sel.Width && got.Height == sel.Height,
                    cropOk ? (got.Width + "x" + got.Height) : ("err=" + (err ?? "нет файла")));

                System.Drawing.Size gotFull = System.Drawing.Size.Empty;
                if (fullOk && savedFull != null && File.Exists(savedFull))
                {
                    using (System.Drawing.Bitmap b = new System.Drawing.Bitmap(savedFull)) gotFull = b.Size;
                }
                Check("Crop frame: полный кадр = исходное разрешение 320x240",
                    fullOk && gotFull.Width == 320 && gotFull.Height == 240,
                    fullOk ? (gotFull.Width + "x" + gotFull.Height) : ("err=" + (errFull ?? "нет файла")));

                bool tempGone = temp != null && !File.Exists(temp)
                    && tempFull != null && !File.Exists(tempFull);
                Check("Crop frame: временные PNG после сохранения удалены", tempGone,
                    "temp=" + (temp == null ? "(null)" : File.Exists(temp).ToString())
                    + ", tempFull=" + (tempFull == null ? "(null)" : File.Exists(tempFull).ToString()));
            }
            catch (Exception ex)
            {
                Check("Crop frame (runtime)", false, ex.Message);
            }
            finally
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static string Sha256(string path)
        {
            try
            {
                using (System.IO.FileStream fs = File.OpenRead(path))
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(fs);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static int IndexOf(string[] args, string value)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], value, StringComparison.Ordinal)) return i;
            return -1;
        }

        // Waits until the preview has caught up with the playhead. A scrub always ends
        // with an exact request for the final position, so this works at ANY zoom -
        // unlike a fixed target time, which a coarse scale cannot land on exactly.
        private static bool PumpPreviewToPlayhead(EditorForm form, int timeoutMs)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Application.DoEvents();
                double p = form.TestShownPts;
                if (form.TestScrubberIdle && !double.IsNaN(p) && Math.Abs(p - form.TestPosition) <= 0.1) return true;
                Thread.Sleep(10);
            }
            return false;
        }

        // Builds one red range the way the user does: move the white playhead, press
        // Cut for the Start, move it again, press Cut for the End.
        private static void MakeRange(EditorForm form, double start, double end)
        {
            form.TestSeekTo(start);
            PumpPreviewToPlayhead(form, 12000);
            form.TestCut();
            form.TestSeekTo(end);
            PumpPreviewToPlayhead(form, 12000);
            form.TestCut();
        }

        // Moves the playhead to a time and reports where it actually landed. Zoomed out,
        // the strip can only land on its own pixel grid, so callers compare against
        // SeekTolerance instead of the requested value.
        private static double SeekAndReport(EditorForm form, double t)
        {
            form.TestSeekTo(t);
            PumpPreviewToPlayhead(form, 12000);
            return form.TestPosition;
        }

        // One screen pixel expressed in seconds at the current zoom: the largest error
        // a click on the strip can produce.
        private static double SeekTolerance(EditorForm form)
        {
            double px = form.TestPixelsPerSecond;
            return px > 0 ? 0.5 / px + 0.05 : 1.0;
        }

        private static string Fmt(double v)
        {
            return v.ToString("N3", System.Globalization.CultureInfo.InvariantCulture);
        }

        // Pumps the UI until the whole filmstrip is prepared and shown at once.
        // Partial coverage is not a valid ready state: either the strip is
        // complete, or nothing of it is on screen yet.
        private static bool PumpUntilStripReady(EditorForm form, int timeoutMs)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Application.DoEvents();
                if (form.TestOverviewDone && !form.TestOverviewRunning
                    && form.TestVisibleCells > 0 && form.TestVisibleCellsMissing == 0)
                    return true;
                Thread.Sleep(25);
            }
            return form.TestOverviewDone && form.TestVisibleCells > 0 && form.TestVisibleCellsMissing == 0;
        }

        // ------------------------------------------------------- overview checks
        //
        // Opening a file must prepare the ENTIRE clip as one filmstrip and only
        // then show it. Progressive holes are forbidden. Zoom stretches that
        // already-prepared strip and must not start a second generation pass.
        // Returns the number of failed checks.
        private static int RunOverviewScenarios(EditorForm form)
        {
            int failures = 0;

            bool coveredAtOpen = PumpUntilStripReady(form, 90000);
            bool done = form.TestOverviewDone;
            int visible = form.TestVisibleCells;
            int missing = form.TestVisibleCellsMissing;
            bool fits = form.TestWholeClipVisible;
            double dur = form.TestDuration;
            double px = form.TestPixelsPerSecond;
            int storeAfterOpen = form.TestStoreCount;
            int planned = form.TestDenseFrameCount;

            if (!done || !fits || !coveredAtOpen || visible <= 0 || missing != 0 || storeAfterOpen <= 0)
            {
                Line("[FAIL] Overview: done=" + done + ", весь ролик виден=" + fits
                    + ", клеток " + visible + ", без своего кадра " + missing
                    + ", план=" + planned + " [" + form.TestOverviewDiag + "]");
                failures++;
            }
            else
            {
                Line("[PASS] Overview: весь ролик " + L10n.FormatDurationShort(dur)
                    + " виден целиком (" + Fmt(px) + " px/s), все " + visible
                    + " клеток имеют свой кадр, подготовлено " + storeAfterOpen
                    + " / план " + planned + ", prep=" + form.TestStripPrepMode
                    + " " + form.TestStripPrepMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "ms — прогрессивных дыр нет");
            }

            // Zoom In: the prepared strip is already complete, so zoom must not
            // rebuild it and coverage must stay whole.
            form.TestZoomIn();
            bool coveredZoom = PumpUntilStripReady(form, 5000);
            int visibleZoom = form.TestVisibleCells;
            int missingZoom = form.TestVisibleCellsMissing;
            int storeAfterZoom = form.TestStoreCount;
            bool sameStrip = storeAfterZoom == storeAfterOpen && storeAfterZoom > 0;
            if (!coveredZoom || !sameStrip || missingZoom != 0)
            {
                Line("[FAIL] Zoom In: покрытие=" + coveredZoom + ", без своего кадра " + missingZoom
                    + " из " + visibleZoom + ", кадров " + storeAfterZoom
                    + " (было " + storeAfterOpen + ", план " + planned + ")");
                failures++;
            }
            else
            {
                Line("[PASS] Zoom In: покрытие целое (" + visibleZoom
                    + " клеток), strip не пересобирался (" + storeAfterZoom + " кадров)");
            }

            form.TestZoomFit();
            bool coveredBack = PumpUntilStripReady(form, 5000);
            if (!coveredBack)
            {
                Line("[FAIL] Fit после Zoom In: " + form.TestOverviewDiag);
                failures++;
            }
            else Line("[PASS] Fit после Zoom In: обзор на месте [" + form.TestOverviewDiag + "]");

            return failures;
        }

        // ------------------------------------------------- timeline zoom / undo checks
        //
        // Two editor-state features that must never touch the media:
        //   * zoom changes ONLY the view scale - the real times of every red range, the
        //     playhead time and the frame on screen survive it untouched;
        //   * Cancel is the undo of the LAST marking action, one item per press: the
        //     pending Start first, then the newest red range, never everything at once.
        // Returns the number of failed checks.
        private static int RunTimelineZoomUndoScenarios(EditorForm form)
        {
            int failures = 0;
            // The view is at Fit here (1 px = Width/Duration seconds), so a click can only
            // land on the nearest pixel: the range times are compared with the tolerance
            // of that scale instead of a fixed sub-pixel 0.05 s.
            double tol0 = SeekTolerance(form);
            form.TestResetRanges();

            // ---------------------------------------------------------- undo: red only
            MakeRange(form, 2.0, 4.0);
            MakeRange(form, 8.0, 10.0);
            if (form.TestMarkedCount != 2)
            {
                Line("[FAIL] Undo: ожидались RED1 2.000-4.000 и RED2 8.000-10.000, получено ["
                    + form.TestMarksDiag + "]");
                failures++;
            }
            else
            {
                Line("[PASS] Undo: созданы RED1 2.000-4.000 и RED2 8.000-10.000 [" + form.TestMarksDiag + "]");

                form.TestCancelRange();
                bool only1 = form.TestMarkedCount == 1
                    && Math.Abs(form.TestMarkedStart(0) - 2.0) <= tol0
                    && Math.Abs(form.TestMarkedEnd(0) - 4.0) <= tol0;
                if (!only1)
                {
                    Line("[FAIL] Undo #1: ожидался только RED1 2.000-4.000, получено [" + form.TestMarksDiag + "]");
                    failures++;
                }
                else Line("[PASS] Undo #1: Cancel удалил только RED2, RED1 не тронут [" + form.TestMarksDiag + "]");

                form.TestCancelRange();
                if (form.TestMarkedCount != 0)
                {
                    Line("[FAIL] Undo #2: ожидалось 0 диапазонов, получено [" + form.TestMarksDiag + "]");
                    failures++;
                }
                else Line("[PASS] Undo #2: Cancel удалил RED1, диапазонов не осталось");

                form.TestCancelRange();
                if (form.TestMarkedCount != 0)
                {
                    Line("[FAIL] Undo #3: Cancel без диапазонов изменил состояние [" + form.TestMarksDiag + "]");
                    failures++;
                }
                else Line("[PASS] Undo #3: отменять нечего — состояние не изменилось");
            }

            // ------------------------------------------------- undo: pending goes first
            form.TestResetRanges();
            MakeRange(form, 2.0, 4.0);
            MakeRange(form, 8.0, 10.0);
            form.TestSeekTo(15.0);
            PumpPreviewToPlayhead(form, 12000);
            form.TestCut();
            if (!form.TestRangeBuilding || form.TestMarkedCount != 2)
            {
                Line("[FAIL] Undo+pending: pending=" + form.TestRangeBuilding + ", помечено "
                    + form.TestMarkedCount + " [" + form.TestMarksDiag + "], ожидался pending Start и 2 диапазона");
                failures++;
            }
            else
            {
                Line("[PASS] Undo+pending: pending Start на " + Fmt(form.TestRangeStart)
                    + " с при RED1/RED2 [" + form.TestMarksDiag + "]");

                form.TestCancelRange();
                if (form.TestRangeBuilding || form.TestMarkedCount != 2)
                {
                    Line("[FAIL] Undo+pending: после Cancel pending=" + form.TestRangeBuilding
                        + ", помечено " + form.TestMarkedCount + " [" + form.TestMarksDiag + "]");
                    failures++;
                }
                else Line("[PASS] Undo+pending: Cancel снял только pending Start, RED1/RED2 на месте ["
                    + form.TestMarksDiag + "]");

                form.TestCancelRange();
                bool red2Gone = form.TestMarkedCount == 1 && Math.Abs(form.TestMarkedStart(0) - 2.0) <= tol0;
                if (!red2Gone)
                {
                    Line("[FAIL] Undo+pending: следующий Cancel должен удалить RED2, получено ["
                        + form.TestMarksDiag + "]");
                    failures++;
                }
                else Line("[PASS] Undo+pending: следующий Cancel удалил RED2 [" + form.TestMarksDiag + "]");
            }

            // ----------------------------------------------------- no-cut zone survives
            form.TestResetRanges();
            MakeRange(form, 2.0, 4.0);
            MakeRange(form, 8.0, 10.0);
            form.TestSeekTo(3.0);
            PumpPreviewToPlayhead(form, 12000);
            form.TestCut();
            if (form.TestRangeBuilding || form.TestMarkedCount != 2
                || !string.Equals(form.TestStatusText, L10n.T(Msg.EdCutBlockedInMark), StringComparison.Ordinal))
            {
                Line("[FAIL] Cut внутри RED: pending=" + form.TestRangeBuilding + ", помечено "
                    + form.TestMarkedCount + " [" + form.TestMarksDiag + "], статус=\""
                    + form.TestStatusText + "\"");
                failures++;
            }
            else Line("[PASS] Cut внутри RED: запрещён, RED1/RED2 не тронуты [" + form.TestMarksDiag + "]");

            form.TestSeekTo(6.0);
            PumpPreviewToPlayhead(form, 12000);
            form.TestCut();                                  // Start armed at 6 s (free gap 4-8)
            bool armedAt6 = form.TestRangeBuilding;
            form.TestSeekTo(9.0);
            PumpPreviewToPlayhead(form, 12000);
            form.TestCut();                                  // End at 9 s would cross RED2 8-10
            if (!armedAt6 || form.TestMarkedCount != 2
                || !string.Equals(form.TestStatusText, L10n.T(Msg.EdCutBlockedOverlap), StringComparison.Ordinal))
            {
                Line("[FAIL] Cut через RED: armed6=" + armedAt6 + ", помечено " + form.TestMarkedCount
                    + " [" + form.TestMarksDiag + "], статус=\"" + form.TestStatusText + "\"");
                failures++;
            }
            else Line("[PASS] Cut через RED: запрещён, помечено по-прежнему [" + form.TestMarksDiag + "]");
            form.TestCancelRange();                          // drop the armed Start again

            // ------------------------------------------------------------------- zoom
            form.TestResetRanges();
            MakeRange(form, 2.0, 4.0);
            MakeRange(form, 8.0, 10.0);
            PumpPreviewToPlayhead(form, 12000);

            double dur = form.TestDuration;
            int width = form.TestTimelineWidth;
            // Opening / ZoomToFit scale: the whole clip is visible as a compact
            // strip (~55% of the track), not stretched to the viewport width.
            double expectedOpen = width > 0 && dur > 0 ? width * 0.55 / dur : 0;

            bool opensWhole = form.TestWholeClipVisible;
            bool compactOpen = expectedOpen > 0
                && Math.Abs(form.TestPixelsPerSecond - expectedOpen) <= 0.05 * expectedOpen;
            bool floorAtOpen = !form.TestCanZoomOut;
            if (!opensWhole || !compactOpen || !floorAtOpen)
            {
                Line("[FAIL] Стартовый масштаб: весь ролик виден=" + form.TestWholeClipVisible
                    + ", px/s=" + Fmt(form.TestPixelsPerSecond) + " (ожидалось "
                    + Fmt(expectedOpen) + "), canZoomOut=" + form.TestCanZoomOut);
                failures++;
            }
            else Line("[PASS] Стартовый масштаб: весь ролик виден компактно ("
                + Fmt(form.TestPixelsPerSecond) + " px/s), zoom-out ниже initial запрещён");

            form.TestZoomIn();
            double px0 = form.TestPixelsPerSecond;
            double pos0 = form.TestPosition;
            double shown0 = form.TestShownPts;
            double d0s = form.TestMarkedStart(0), d0e = form.TestMarkedEnd(0);
            double d1s = form.TestMarkedStart(1), d1e = form.TestMarkedEnd(1);

            form.TestZoomFit();
            double pxFit = form.TestPixelsPerSecond;
            bool wholeClip = form.TestWholeClipVisible;
            bool rangesSame = Math.Abs(form.TestMarkedStart(0) - d0s) < 1e-9
                && Math.Abs(form.TestMarkedEnd(0) - d0e) < 1e-9
                && Math.Abs(form.TestMarkedStart(1) - d1s) < 1e-9
                && Math.Abs(form.TestMarkedEnd(1) - d1e) < 1e-9
                && form.TestMarkedCount == 2;
            bool posSame = Math.Abs(form.TestPosition - pos0) < 1e-9;
            bool fitScale = expectedOpen > 0 && Math.Abs(pxFit - expectedOpen) <= 0.05 * expectedOpen;

            if (!wholeClip || !(pxFit < px0) || !rangesSame || !posSame || !fitScale)
            {
                Line("[FAIL] Zoom Out: wholeClip=" + wholeClip + ", px/s=" + Fmt(px0) + " -> " + Fmt(pxFit)
                    + " (ожидалось " + Fmt(expectedOpen) + "), rangesSame=" + rangesSame
                    + ", playheadSame=" + posSame
                    + " [" + form.TestMarksDiag + "]");
                failures++;
            }
            else
            {
                Line("[PASS] Zoom Out: весь ролик " + L10n.FormatDurationShort(dur)
                    + " снова компактен (" + Fmt(pxFit) + " px/s), был масштаб "
                    + Fmt(px0) + " px/s");
            }

            // Extra ZoomOut at the opening scale is a no-op: the initial view
            // is the floor, the clip must not shrink further.
            form.TestZoomOut();
            form.TestZoomOut();
            bool stayedAtFloor = !form.TestCanZoomOut
                && expectedOpen > 0
                && Math.Abs(form.TestPixelsPerSecond - expectedOpen) <= 0.05 * expectedOpen;
            if (!stayedAtFloor)
            {
                Line("[FAIL] Zoom floor: после ZoomOut на initial px/s="
                    + Fmt(form.TestPixelsPerSecond) + " (ожидалось " + Fmt(expectedOpen)
                    + "), canZoomOut=" + form.TestCanZoomOut);
                failures++;
            }
            else Line("[PASS] Zoom floor: ZoomOut на initial scale не уходит ниже "
                + Fmt(form.TestPixelsPerSecond) + " px/s");

            // The whole point of the coarse scale: the last second of the clip is inside
            // the visible window, and a click at the far right really maps to the end.
            double endT = SeekAndReport(form, dur);
            bool endReached = Math.Abs(endT - dur) <= SeekTolerance(form) + 0.2;
            if (!wholeClip || !endReached)
            {
                Line("[FAIL] Zoom Out: клик по правому краю не попадает в конец ролика: "
                    + Fmt(endT) + " вместо " + Fmt(dur));
                failures++;
            }
            else Line("[PASS] Zoom Out: клик по правому краю даёт конец ролика " + Fmt(endT));

            // Check 11/12 again with the playhead moved by the click above: the zoom
            // itself may not have moved any real time.
            double posAfterClick = form.TestPosition;
            form.TestZoomIn();
            double pxIn = form.TestPixelsPerSecond;
            bool zoomedIn = pxIn > pxFit + 1e-9 && form.TestCanZoomOut;
            bool stableIn = Math.Abs(form.TestPosition - posAfterClick) < 1e-9
                && Math.Abs(form.TestMarkedStart(0) - d0s) < 1e-9
                && Math.Abs(form.TestMarkedEnd(1) - d1e) < 1e-9
                && form.TestMarkedCount == 2;
            if (!zoomedIn || !stableIn)
            {
                Line("[FAIL] Zoom In: px/s=" + Fmt(pxIn) + ", canZoomOut=" + form.TestCanZoomOut
                    + ", времена не изменились=" + stableIn + " [" + form.TestMarksDiag + "]");
                failures++;
            }
            else Line("[PASS] Zoom In: масштаб " + Fmt(pxFit) + " -> " + Fmt(pxIn)
                + " px/s, времена диапазонов и playhead не изменились");

            form.TestZoomOut();
            bool backToFit = Math.Abs(form.TestPixelsPerSecond - pxFit) <= 0.02 * pxFit;
            if (!backToFit)
            {
                Line("[FAIL] Zoom In/Out: возврат к общему обзору дал " + Fmt(form.TestPixelsPerSecond)
                    + " px/s вместо " + Fmt(pxFit));
                failures++;
            }
            else Line("[PASS] Zoom In/Out: возврат к масштабу «весь ролик» " + Fmt(form.TestPixelsPerSecond) + " px/s");

            // ------------------------------------------------- preview survives the zoom
            form.TestZoomFit();
            PumpPreviewToPlayhead(form, 12000);
            double posZ = form.TestPosition;
            double shownZ = form.TestShownPts;
            form.TestZoomIn();
            PumpPreviewToPlayhead(form, 12000);
            bool previewKept = Math.Abs(form.TestPosition - posZ) < 1e-9
                && Math.Abs(form.TestShownPts - shownZ) <= 0.1;
            if (!previewKept)
            {
                Line("[FAIL] Zoom: preview/playhead поехали: playhead " + Fmt(posZ) + " -> "
                    + Fmt(form.TestPosition) + ", кадр " + Fmt(shownZ) + " -> " + Fmt(form.TestShownPts));
                failures++;
            }
            else Line("[PASS] Zoom: preview остаётся на том же моменте " + Fmt(shownZ)
                + " с, playhead " + Fmt(form.TestPosition) + " с");
            form.TestZoomFit();

            // -------------------------------------------- a normal Cut after the zoom
            double p1 = SeekAndReport(form, 20.0);
            form.TestCut();
            bool armedAfterZoom = form.TestRangeBuilding;
            double p2 = SeekAndReport(form, 30.0);
            form.TestCut();
            double tol = SeekTolerance(form);
            bool thirdOk = armedAfterZoom && form.TestMarkedCount == 3
                && Math.Abs(form.TestMarkedStart(2) - p1) <= tol
                && Math.Abs(form.TestMarkedEnd(2) - p2) <= tol
                && Math.Abs(form.TestMarkedStart(0) - d0s) < 1e-9
                && Math.Abs(form.TestMarkedEnd(1) - d1e) < 1e-9;
            if (!thirdOk)
            {
                Line("[FAIL] Cut после Zoom: armed=" + armedAfterZoom + ", помечено " + form.TestMarkedCount
                    + " [" + form.TestMarksDiag + "], ожидался 3-й диапазон " + Fmt(p1) + "-" + Fmt(p2));
                failures++;
            }
            else Line("[PASS] Cut после Zoom: 3-й диапазон " + Fmt(form.TestMarkedStart(2)) + "-"
                + Fmt(form.TestMarkedEnd(2)) + " при масштабе «весь ролик», первые два не тронуты");

            // Leave the editor in the state a freshly opened file starts in.
            form.TestResetRanges();
            form.TestZoomFit();
            return failures;
        }

        // TASK 2.4 verification of the embedded LibVLC preview UX contract on the
        // real editor:
        //   * Open Video  -> first frame shown, playback PAUSED, playhead at 0;
        //   * Timeline seek while paused -> shows the frame, stays paused (no autoplay);
        //   * Play -> starts from the current playhead position;
        //   * Pause -> stops, keeps the position;
        //   * Resume -> continues from that position.
        public static int RunLibVlcPlayback(string file)
        {
            TryAttachConsole();
            Line("=== LibVLC playback UX test ===");
            int failures = 0;
            EditorForm form = null;
            Exception loopError = null;
            Thread t = new Thread(new ThreadStart(delegate
            {
                try
                {
                    form = new EditorForm(file);
                    form.Shown += delegate
                    {
                        try
                        {
                            // Wait until the embedded LibVLC player holds the media.
                            System.Diagnostics.Stopwatch ready = System.Diagnostics.Stopwatch.StartNew();
                            while ((!form.TestLibVlcActive) && ready.ElapsedMilliseconds < 60000)
                            {
                                Application.DoEvents();
                                Thread.Sleep(20);
                            }
                            if (!form.TestLibVlcActive)
                            {
                                Line("[FAIL] LibVLC player не активирован за 60 c (fallback FFmpeg preview)");
                                failures++;
                                CloseEditor(form);
                                return;
                            }

                            // A user waits for the file to load before touching the
                            // playhead. The timeline is only usable once the editor is
                            // ready (media probed, real Duration set, opening frame
                            // shown); before that the track still reports its 1 s
                            // placeholder and a drag to 10 s would be clamped to it.
                            System.Diagnostics.Stopwatch timelineReady = System.Diagnostics.Stopwatch.StartNew();
                            while (!form.TestReady && timelineReady.ElapsedMilliseconds < 60000)
                            {
                                Application.DoEvents();
                                Thread.Sleep(20);
                            }
                            Line("[INFO] editor ready=" + form.TestReady + " duration=" + Fmt(form.TestDuration) + " c");

                            // (B) Open -> first frame, PAUSED, position ~0.
                            bool openPaused = !form.TestLibVlcPlaying;
                            double openPos = form.TestLibVlcPositionMs;
                            double openTimelinePos = form.TestPosition;
                            if (!openPaused || double.IsNaN(openPos) || openPos > 1500)
                            {
                                Line("[FAIL] Open: paused=" + openPaused
                                    + " posMs=" + Fmt(openPos) + " ожидался первый кадр и PAUSED");
                                failures++;
                            }
                            else
                            {
                                Line("[PASS] Open: первый кадр, playback PAUSED, posMs=" + Fmt(openPos)
                                    + " timeline=" + Fmt(openTimelinePos));
                            }

                            // (C) Drag playhead to ~10s -> frame changes, still PAUSED.
                            form.TestSeekTo(10.0);
                            PumpLibVlc(form, 8000);
                            double seekPos = form.TestLibVlcPositionMs;
                            bool seekPaused = !form.TestLibVlcPlaying;
                            Line("[INFO] seek: timeline=" + Fmt(form.TestPosition)
                                + " posMs=" + Fmt(seekPos) + " paused=" + seekPaused);
                            if (!seekPaused || double.IsNaN(seekPos) || Math.Abs(seekPos - 10000) > 2500)
                            {
                                Line("[FAIL] Seek(10s): paused=" + seekPaused + " posMs=" + Fmt(seekPos)
                                    + " ожидался кадр ~10 с и PAUSED");
                                failures++;
                            }
                            else
                            {
                                Line("[PASS] Seek(10s): кадр ~10 с, playback PAUSED (нет autoplay) posMs=" + Fmt(seekPos));
                            }

                            // (D) Play -> starts from the playhead (~10s), position advances,
                            // and the white playhead follows the actual playback time.
                            double playheadBefore = form.TestPosition;
                            form.TestPlayPause();
                            PumpLibVlc(form, 3000);
                            bool playingAfter = form.TestLibVlcPlaying;
                            double playPos = form.TestLibVlcPositionMs;
                            double playheadAfter = form.TestPosition;
                            bool playheadSynced = playheadAfter > playheadBefore + 1.0
                                && Math.Abs(playheadAfter * 1000.0 - playPos) < 1500;
                            if (!playingAfter || double.IsNaN(playPos) || playPos < 9000)
                            {
                                Line("[FAIL] Play: playing=" + playingAfter + " posMs=" + Fmt(playPos)
                                    + " ожидалось воспроизведение от ~10 с");
                                failures++;
                            }
                            else if (!playheadSynced)
                            {
                                Line("[FAIL] Playhead: timeline " + Fmt(playheadBefore) + " -> " + Fmt(playheadAfter)
                                    + " c при posMs=" + Fmt(playPos)
                                    + " ожидалось движение playhead вместе с видео");
                                failures++;
                            }
                            else
                            {
                                Line("[PASS] Play: началось с текущей позиции posMs=" + Fmt(playPos)
                                    + ", playhead " + Fmt(playheadBefore) + " -> " + Fmt(playheadAfter) + " c");
                            }

                            // (E) Pause -> stops, keeps position.
                            form.TestPlayPause();
                            PumpLibVlc(form, 1500);
                            bool pausedAfter = !form.TestLibVlcPlaying;
                            double pausedPos = form.TestLibVlcPositionMs;
                            if (!pausedAfter || double.IsNaN(pausedPos))
                            {
                                Line("[FAIL] Pause: paused=" + pausedAfter);
                                failures++;
                            }
                            else
                            {
                                Line("[PASS] Pause: остановлено на posMs=" + Fmt(pausedPos));
                            }

                            // (F) Resume -> continues from the paused position.
                            double beforeResume = form.TestLibVlcPositionMs;
                            form.TestPlayPause();
                            PumpLibVlc(form, 1500);
                            bool resumed = form.TestLibVlcPlaying;
                            double resumedPos = form.TestLibVlcPositionMs;
                            if (!resumed || double.IsNaN(resumedPos) || resumedPos < beforeResume)
                            {
                                Line("[FAIL] Resume: playing=" + resumed + " posMs=" + Fmt(resumedPos)
                                    + " до=" + Fmt(beforeResume));
                                failures++;
                            }
                            else
                            {
                                Line("[PASS] Resume: продолжено с posMs=" + Fmt(resumedPos));
                            }

                            // (G) Seek while playing -> jumps and keeps playing.
                            // The player keeps running during the settle window, so the
                            // expected position is the target plus the elapsed playback
                            // time, never exactly the target.
                            form.TestSeekTo(20.0);
                            PumpLibVlc(form, 3000);
                            bool stillPlaying = form.TestLibVlcPlaying;
                            double afterPlaySeek = form.TestLibVlcPositionMs;
                            if (!stillPlaying || double.IsNaN(afterPlaySeek)
                                || afterPlaySeek < 18000 || afterPlaySeek > 20000 + 3000 + 2000)
                            {
                                Line("[FAIL] Seek во время Play: playing=" + stillPlaying + " posMs=" + Fmt(afterPlaySeek));
                                failures++;
                            }
                            else
                            {
                                Line("[PASS] Seek во время Play: переход на ~20 с, playback продолжается posMs=" + Fmt(afterPlaySeek));
                            }

                            // (H) VideoView is display-only: it must host no child
                            // controls at all (no VLC transport / seek bar / Full /
                            // Ratio / fullscreen, no second timeline).
                            int childControls = form.TestVideoViewChildControlCount;
                            if (childControls != 0)
                            {
                                Line("[FAIL] VideoView: childControlCount=" + childControls
                                    + " ожидалось 0 (только область отображения)");
                                failures++;
                            }
                            else
                            {
                                Line("[PASS] VideoView: без встроенных VLC controls (childControlCount=0)");
                            }

                            // (J) Close / reopen: the same editor reopens the file through
                            // the exact Open Video... path and the embedded player comes back
                            // in the first-frame PAUSED state at 0 (no leftover decoder, no
                            // playing client).
                            form.TestReopen(file);
                            System.Diagnostics.Stopwatch reopenReady = System.Diagnostics.Stopwatch.StartNew();
                            while ((!form.TestReady || !form.TestLibVlcActive)
                                && reopenReady.ElapsedMilliseconds < 60000)
                            {
                                Application.DoEvents();
                                Thread.Sleep(20);
                            }
                            PumpLibVlc(form, 1500);
                            bool reopenActive = form.TestLibVlcActive;
                            bool reopenPaused = !form.TestLibVlcPlaying;
                            double reopenPos = form.TestLibVlcPositionMs;
                            if (!reopenActive || !reopenPaused || double.IsNaN(reopenPos) || reopenPos > 1500)
                            {
                                Line("[FAIL] Reopen: active=" + reopenActive + " paused=" + reopenPaused
                                    + " posMs=" + Fmt(reopenPos) + " ожидался первый кадр и PAUSED");
                                failures++;
                            }
                            else
                            {
                                Line("[PASS] Reopen: новый Media открыт, первый кадр, PAUSED, posMs=" + Fmt(reopenPos));
                            }
                        }
                        catch (Exception ex)
                        {
                            Line("[FAIL] LibVLC playback: " + ex.Message);
                            failures++;
                        }
                        CloseEditor(form);
                    };
                    form.ShowDialog();
                }
                catch (Exception ex) { loopError = ex; }
            }));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            if (!t.Join(120000))
            {
                Line("[FAIL] LibVLC playback: окно не закрылось за 120 c");
                failures++;
            }
            if (loopError != null)
            {
                Line("[FAIL] LibVLC playback: " + loopError.Message);
                failures++;
            }
            if (failures == 0) Line("[PASS] LibVLC playback UX соответствует контракту");
            try { File.WriteAllText(Path.Combine(AppPaths.BaseDir, "playtest_result.txt"), Buf.ToString(), new UTF8Encoding(false)); } catch { }
            return failures;
        }

        // Reports the moment the editor is actually usable (media probed, first
        // frame shown, timeline live) AND how long the UI thread takes to answer
        // an Invoke, which is the same thing Windows turns into "(Не отвечает)".
        private static void StartOpenProbe(Form owner, long clickAtMs, int staySeconds)
        {
            Thread t = new Thread(new ThreadStart(delegate
            {
                EditorForm ed = null;
                Stopwatch life = Stopwatch.StartNew();
                while (life.ElapsedMilliseconds < (staySeconds + 120) * 1000)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    try
                    {
                        owner.Invoke((MethodInvoker)delegate
                        {
                            if (ed == null || ed.IsDisposed)
                            {
                                ed = null;
                                foreach (Form f in Application.OpenForms)
                                {
                                    EditorForm e2 = f as EditorForm;
                                    if (e2 != null) ed = e2;
                                }
                            }
                        });
                    }
                    catch { return; }
                    long round = sw.ElapsedMilliseconds;
                    if (clickAtMs > 0 && round >= 300)
                        EditTiming.Write("+" + EditTiming.NowMs + " ms\tUI thread busy for " + round
                            + " ms while opening (this is the \"(Не отвечает)\" window)");
                    if (ed != null && !ed.IsDisposed)
                    {
                        bool ready = false;
                        try { ed.Invoke((MethodInvoker)delegate { ready = ed.TestReady; }); }
                        catch { return; }
                        if (ready)
                        {
                            long usableAt = EditTiming.NowMs;
                            EditTiming.Write("+" + usableAt + " ms\tEDITOR USABLE: " + (usableAt - clickAtMs)
                                + " ms after the Trim/Frame click");
                            Line("editor usable after " + (usableAt - clickAtMs) + " ms");
                            return;
                        }
                    }
                    Thread.Sleep(60);
                }
            }));
            t.IsBackground = true;
            t.Name = "open-probe";
            t.Start();
        }

        // Closes the editor (and the owner) after staySeconds, so an unattended
        // run does not need a human to press Close.
        private static void ArmWatchdogClose(Form owner, int seconds)
        {
            System.Threading.Timer timer = null;
            timer = new System.Threading.Timer(new TimerCallback(delegate
            {
                try
                {
                    owner.BeginInvoke((MethodInvoker)delegate
                    {
                        EditTiming.Mark("Watchdog: closing editor");
                        foreach (Form f in Application.OpenForms)
                        {
                            EditorForm ed = f as EditorForm;
                            if (ed != null) { try { ed.Close(); } catch { } }
                        }
                    });
                }
                catch { }
                try { if (timer != null) timer.Dispose(); } catch { }
            }), null, seconds * 1000, Timeout.Infinite);
        }

        private static void PumpLibVlc(EditorForm form, int ms)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
        }

        private static void CloseEditor(EditorForm form)
        {
            try { form.Close(); }
            catch { }
        }

        // ----- TEMPORARY diagnostic harness (regression: slow Editor open) -----
        // Runs the REAL user route and measures it:
        //   viaMainForm = true : MainForm -> Trim/Frame (real button) -> Editor
        //   viaMainForm = false: the same EditorForm, opened directly
        // Phase marks go to edit_timing.txt; the window closes itself after
        // staySeconds so the run is unattended.
        public static int RunEditorOpenTiming(string file, int staySeconds, bool viaMainForm)
        {
            TryAttachConsole();
            if (staySeconds <= 0) staySeconds = 25;
            EditTiming.Enable(Path.Combine(AppPaths.BaseDir, "edit_timing.txt"));
            EditTiming.Mark("=== OPEN TIMING start; file=" + file + "; route="
                + (viaMainForm ? "MainForm->Trim/Frame" : "EditorForm direct") + " ===");
            Line("=== Editor open timing (" + (viaMainForm ? "MainForm route" : "direct") + ") ===");
            Line("file: " + file + "   stay: " + staySeconds + " s");

            long clickAtMs = -1;
            Exception loopError = null;
            Thread t = new Thread(new ThreadStart(delegate
            {
                try
                {
                    if (viaMainForm)
                    {
                        MainForm mf = new MainForm();
                        mf.Shown += delegate
                        {
                            EditTiming.Mark("MainForm shown");
                            System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Instance;
                            typeof(MainForm).GetField("_currentFile", F).SetValue(mf, file);
                            Button b = (Button)typeof(MainForm).GetField("btnTrimFrame", F).GetValue(mf);
                            clickAtMs = EditTiming.NowMs;
                            EditTiming.Mark("Trim/Frame clicked (real button handler)");
                            StartOpenProbe(mf, clickAtMs, staySeconds);
                            b.PerformClick();       // runs until the editor is closed
                            EditTiming.Mark("Trim/Frame handler returned (editor closed)");
                            mf.Close();
                        };
                        ArmWatchdogClose(mf, staySeconds);
                        Application.Run(mf);
                    }
                    else
                    {
                        EditorForm form = new EditorForm(file);
                        clickAtMs = EditTiming.NowMs;
                        StartOpenProbe(form, clickAtMs, staySeconds);
                        ArmWatchdogClose(form, staySeconds);
                        form.ShowDialog();
                    }
                }
                catch (Exception ex) { loopError = ex; }
            }));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            if (!t.Join((staySeconds + 240) * 1000))
            {
                Line("[FAIL] окно не закрылось за " + (staySeconds + 240) + " c");
                EditTiming.Mark("FAIL: window did not close");
            }
            if (loopError != null)
            {
                Line("[FAIL] " + loopError.Message);
                EditTiming.Mark("FAIL: " + loopError.Message);
            }
            EditTiming.Mark("=== OPEN TIMING end ===");
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.BaseDir, "open_timing_result.txt"),
                    Buf.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return loopError != null ? 1 : 0;
        }

        public static int RunEditorSmoke(string file, int staySeconds)
        {
            if (staySeconds <= 0) staySeconds = 20;
            TryAttachConsole();
            Line("=== EditorForm smoke test ===");
            int failures = 0;
            try
            {
                EditorForm form = null;
                Exception loopError = null;
                long checkMs = 0;
                Thread t = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        form = new EditorForm(file);
                        form.Shown += delegate
                        {
                            System.Diagnostics.Stopwatch checks = System.Diagnostics.Stopwatch.StartNew();
                            // Drive a real scrub through the editor before closing: this is
                            // the same path a mouse drag takes (position change -> throttled
                            // request -> MouseUp final request), so it proves the preview
                            // wiring works, not just that the window opens.
                            try
                            {
                                // The editor decodes its opening frame in the background, so
                                // wait until the media is actually open before scrubbing.
                                System.Diagnostics.Stopwatch ready = System.Diagnostics.Stopwatch.StartNew();
                                while (!form.TestReady && ready.ElapsedMilliseconds < 60000)
                                {
                                    Application.DoEvents();
                                    Thread.Sleep(10);
                                }
                                if (!form.TestReady)
                                {
                                    Line("[FAIL] EditorForm scrub: медиа не открылось за 60 c");
                                    failures++;
                                }
                                else
                                {
                                    double[] pos = new double[] { 20.0, 20.5, 21.0, 40.0, 40.25, 60.0 };
                                    form.TestDriveScrub(pos);
                                    // The scrubber hands frames over through BeginInvoke, so
                                    // keep pumping until the UI thread has applied the last
                                    // one, not merely until the worker went idle.
                                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                                    while (sw.ElapsedMilliseconds < 12000)
                                    {
                                        Application.DoEvents();
                                        double p = form.TestShownPts;
                                        if (form.TestScrubberIdle && !double.IsNaN(p) && Math.Abs(p - 60.0) <= 0.1) break;
                                        Thread.Sleep(10);
                                    }
                                    Application.DoEvents();
                                    double shown = form.TestShownPts;
                                    if (!form.TestHasPreviewFrame)
                                    {
                                        Line("[FAIL] EditorForm scrub: preview остался пустым [" + form.TestScrubDiag + "]");
                                        failures++;
                                    }
                                    else if (double.IsNaN(shown) || Math.Abs(shown - 60.0) > 0.1)
                                    {
                                        Line("[FAIL] EditorForm scrub: preview pts=" + shown.ToString("N3", System.Globalization.CultureInfo.InvariantCulture)
                                            + ", ожидалось 60.000 [" + form.TestScrubDiag + "]");
                                        failures++;
                                    }
                                    else
                                    {
                                        Line("[PASS] EditorForm scrub: preview pts=" + shown.ToString("N3", System.Globalization.CultureInfo.InvariantCulture)
                                            + " [" + form.TestScrubDiag + "]");

                                        // New playhead-driven range flow. There are no
                                        // handles: move the white playhead, press Cut to
                                        // set Start, move the playhead, press Cut for End.
                                        // Scenario A: playhead -> 2 s, set Start there.
                                        form.TestSeekTo(2.0);
                                        PumpPreviewToPlayhead(form, 12000);
                                        form.TestCut();
                                        double startAfterA = form.TestRangeStart;
                                        if (!form.TestRangeBuilding || Math.Abs(startAfterA - 2.0) > SeekTolerance(form))
                                        {
                                            Line("[FAIL] EditorForm Start: building=" + form.TestRangeBuilding
                                                + ", start=" + startAfterA.ToString("N3", System.Globalization.CultureInfo.InvariantCulture)
                                                + ", ожидалось 2.000 и building=True");
                                            failures++;
                                        }
                                        else
                                        {
                                            Line("[PASS] EditorForm Start: зафиксирован в позиции playhead 2.000");

                                            // Scenario B: move the playhead to 12 s and set End.
                                            form.TestSeekTo(12.0);
                                            PumpPreviewToPlayhead(form, 12000);
                                            form.TestCut();
                                            if (form.TestMarkedCount != 1
                                                || Math.Abs(form.TestMarkedStart(0) - 2.0) > SeekTolerance(form)
                                                || Math.Abs(form.TestMarkedEnd(0) - 12.0) > SeekTolerance(form)
                                                || form.TestRangeBuilding)
                                            {
                                                Line("[FAIL] EditorForm End: помечено " + form.TestMarkedCount
                                                    + " [" + form.TestMarksDiag + "], building=" + form.TestRangeBuilding
                                                    + ", ожидался 1 диапазон 2.000-12.000");
                                                failures++;
                                            }
                                            else
                                            {
                                                Line("[PASS] EditorForm End: диапазон создан между Start и playhead " + form.TestMarksDiag);

                                                // Scenario C: a second range built the same way,
                                                // in another place: Start at 14 s, End at 19 s.
                                                form.TestSeekTo(14.0);
                                                PumpPreviewToPlayhead(form, 12000);
                                                form.TestCut();
                                                form.TestSeekTo(19.0);
                                                PumpPreviewToPlayhead(form, 12000);
                                                form.TestCut();
                                                bool two = form.TestMarkedCount == 2
                                                    && Math.Abs(form.TestMarkedStart(0) - 2.0) <= SeekTolerance(form)
                                                    && Math.Abs(form.TestMarkedEnd(0) - 12.0) <= SeekTolerance(form)
                                                    && Math.Abs(form.TestMarkedStart(1) - 14.0) <= SeekTolerance(form)
                                                    && Math.Abs(form.TestMarkedEnd(1) - 19.0) <= SeekTolerance(form);
                                                if (!two)
                                                {
                                                    Line("[FAIL] EditorForm 2-й диапазон: [" + form.TestMarksDiag
                                                        + "], ожидалось 2.000-12.000 и 14.000-19.000");
                                                    failures++;
                                                }
                                                else
                                                {
                                                    Line("[PASS] EditorForm 2-й диапазон: два диапазона " + form.TestMarksDiag);

                                                    // Scenario D: a red range is a no-cut zone, so a
                                                    // Start may not be placed on it. The playhead goes
                                                    // inside the first red range (2-12); Cut must not
                                                    // arm a Start, must leave both red ranges alone and
                                                    // must say so on the status line.
                                                    form.TestSeekTo(5.0);
                                                    PumpPreviewToPlayhead(form, 12000);
                                                    form.TestCut();
                                                    bool blockedStart = !form.TestRangeBuilding
                                                        && form.TestMarkedCount == 2
                                                        && string.Equals(form.TestStatusText,
                                                            L10n.T(Msg.EdCutBlockedInMark), StringComparison.Ordinal);
                                                    if (!blockedStart)
                                                    {
                                                        Line("[FAIL] EditorForm Cut в RED: building=" + form.TestRangeBuilding
                                                            + ", помечено " + form.TestMarkedCount
                                                            + " [" + form.TestMarksDiag + "], статус=\""
                                                            + form.TestStatusText + "\"");
                                                        failures++;
                                                    }
                                                    else
                                                    {
                                                        Line("[PASS] EditorForm Cut в RED: Start не создан, "
                                                            + "строящегося диапазона нет, красные " + form.TestMarksDiag + " не тронуты");
                                                    }

                                                    // Scenario E: a span that would cross a red range is
                                                    // refused at the End press. Start at 13 s is in the free
                                                    // gap (12-14); End at 18 s would cross the second red range
                                                    // (14-19), so no range may be created. The armed Start is
                                                    // kept, so the user can move the playhead and try again.
                                                    form.TestSeekTo(13.0);
                                                    PumpPreviewToPlayhead(form, 12000);
                                                    form.TestCut();                 // Start armed at 13 s (free)
                                                    bool armedAt13 = form.TestRangeBuilding;
                                                    form.TestSeekTo(18.0);
                                                    PumpPreviewToPlayhead(form, 12000);
                                                    form.TestCut();                 // End crosses 14-19: refused
                                                    bool blockedCross = form.TestMarkedCount == 2
                                                        && string.Equals(form.TestStatusText,
                                                            L10n.T(Msg.EdCutBlockedOverlap), StringComparison.Ordinal);
                                                    if (!armedAt13 || !blockedCross)
                                                    {
                                                        Line("[FAIL] EditorForm Cut через RED: armed13=" + armedAt13
                                                            + ", помечено " + form.TestMarkedCount
                                                            + " [" + form.TestMarksDiag + "], статус=\""
                                                            + form.TestStatusText + "\"");
                                                        failures++;
                                                    }
                                                    else
                                                    {
                                                        Line("[PASS] EditorForm Cut через RED: диапазон не создан, "
                                                            + "помечено по-прежнему " + form.TestMarksDiag);
                                                    }

                                                    // Scenario F: Cancel drops the range being built only.
                                                    // The two committed red ranges must survive and the
                                                    // armed Start must be gone.
                                                    form.TestCancelRange();
                                                    bool canceled = form.TestMarkedCount == 2 && !form.TestRangeBuilding;
                                                    if (!canceled)
                                                    {
                                                        Line("[FAIL] EditorForm cancel: помечено " + form.TestMarkedCount
                                                            + " [" + form.TestMarksDiag + "], building=" + form.TestRangeBuilding);
                                                        failures++;
                                                    }
                                                    else
                                                    {
                                                        Line("[PASS] EditorForm cancel: красные диапазоны на месте "
                                                            + form.TestMarksDiag + ", строящийся диапазон снят");
                                                    }
                                                }
                                            }
                                            // Leave the editor as a freshly opened file looks, so a
                                            // following visual run starts from a known picture.
                                            form.TestResetRanges();
                                        }
                                    }

                                    // The whole-clip overview: the editor must be ready
                                    // with the complete map of the clip on one screen,
                                    // and a zoom must load only the region it exposes.
                                    failures += RunOverviewScenarios(form);

                                    // Zoom of the timeline and Cancel-as-undo: pure editor
                                    // state, checked independently of the scenarios above so
                                    // a failure there cannot hide these results.
                                    failures += RunTimelineZoomUndoScenarios(form);
                                }
                            }
                            catch (Exception ex)
                            {
                                Line("[FAIL] EditorForm scrub: " + ex.Message);
                                failures++;
                            }
                            checks.Stop();
                            checkMs = checks.ElapsedMilliseconds;
                            Line("EditorForm checks: " + (checkMs / 1000.0).ToString("0.0",
                                System.Globalization.CultureInfo.InvariantCulture)
                                + " c (лимит ожидания закрытия — " + (checkMs / 1000 + staySeconds + 180) + " c)");

                            System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer();
                            // Long enough for the visual run: after the checks the window
                            // has to stay open for a real-mouse pass (marks, Cut, Cancel,
                            // and - with a longer stay - a real trim).
                            closeTimer.Interval = staySeconds * 1000;
                            closeTimer.Tick += delegate
                            {
                                closeTimer.Stop();
                                try { form.Close(); }
                                catch { }
                            };
                            closeTimer.Start();
                        };
                        form.ShowDialog();
                    }
                    catch (Exception ex) { loopError = ex; }
                }));
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                // The close timer only starts after the whole check suite, and every
                // zoom / undo check waits for a decoded preview frame, so the margin has
                // to cover the check phase (tens of seconds) on top of the stay. A much
                // tighter margin would report a deadlock for a perfectly healthy run.
                if (!t.Join(staySeconds * 1000 + 180000))
                {
                    Line("[FAIL] EditorForm: окно не закрылось за " + (staySeconds + 180) + " c (deadlock?)");
                    failures++;
                }
                if (loopError != null)
                {
                    Line("[FAIL] EditorForm: исключение — " + loopError.Message);
                    failures++;
                    WriteResultFile(Buf.ToString());
                    return failures;
                }
                if (failures == 0) Line("[PASS] EditorForm: открыт, прорисован и закрыт без deadlock");
                WriteResultFile(Buf.ToString());
                return failures;
            }
            catch (Exception ex)
            {
                Line("[FAIL] EditorForm smoke: " + ex.Message);
                WriteResultFile(Buf.ToString());
                return 1;
            }
        }

        private static void TestGitHub(string localVersion)        {
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
