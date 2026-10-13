using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace YouTubeDownloader
{
    // TEMPORARY DIAGNOSTIC (regression hunt: playhead <-> preview sync loss).
    // Enabled only by the test-only CLI switches (--vlcdiag / --accept), so a
    // normal run never writes anything. Appends one line per event to
    // vlc_diag.txt next to the EXE: seek requests, applied seeks, player
    // state events, seek acknowledgements (and their absence), reopen recovery.
    internal static class VlcDiag
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly object Sync = new object();
        private static string _path;
        private static long _seq;

        public static bool Enabled;

        public static void Enable(string tag)
        {
            _path = Path.Combine(AppPaths.BaseDir, "vlc_diag.txt");
            Enabled = true;
            Write("=== run " + tag + " ===");
        }

        public static long NextSeq()
        {
            return System.Threading.Interlocked.Increment(ref _seq);
        }

        public static void Write(string line)
        {
            if (_path == null) return;
            lock (Sync)
            {
                try { File.AppendAllText(_path, "+" + Clock.ElapsedMilliseconds.ToString() + " ms\t" + line + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
            }
        }
    }
}
