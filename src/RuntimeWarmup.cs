using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace YouTubeDownloader
{
    // Start-up warm-up: pays the one-time cost of the native runtime BEFORE the
    // user asks for it.
    //
    // Why this exists: the editor window used to be created only after its
    // constructor had finished, and that constructor did two things that resolve
    // from disk and are therefore slow the FIRST time in a session:
    //
    //   * it brought up the LibVLC engine - libvlc.dll / libvlccore.dll plus a
    //     walk of the plugin tree (hundreds of DLLs, ~100 MB) - see
    //     LibVlcPreview.EnsureEngine();
    //   * it spawned ffprobe.exe (~100 MB) for the media probe.
    //
    // Both are pure one-time costs with no per-file dependency, so they are paid
    // here, on a background thread, right after the main window is shown - while
    // the user is still typing a URL or picking a file. Pressing Trim / Frame
    // then finds the engine and the sidecar binaries already resident, so the
    // editor opens immediately even on the very first click (TASK: fast first
    // editor open).
    internal static class RuntimeWarmup
    {
        private static int _started;
        private static int _done;

        // True once the whole warm-up pass has finished (diagnostic only).
        public static bool Done
        {
            get { return Volatile.Read(ref _done) == 1; }
        }

        // Idempotent and safe to call from any thread; never throws.
        public static void Start()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
            Thread t = new Thread(new ThreadStart(Run));
            t.IsBackground = true;
            t.Name = "runtime-warmup";
            t.Start();
        }

        private static void Run()
        {
            EditTiming.Mark("RuntimeWarmup: start");
            try
            {
                long t0 = EditTiming.NowMs;
                LibVlcPreview.EnsureEngine();
                EditTiming.Mark("RuntimeWarmup: LibVLC engine ready=" + LibVlcPreview.EngineReady,
                    EditTiming.NowMs - t0);
            }
            catch { }

            // The big sidecar binaries are loaded here once so the first real use
            // (ffprobe for the media probe, ffmpeg for the opening frame) does not
            // pay for faulting them in from disk.
            long t1 = EditTiming.NowMs;
            WarmProcess(AppPaths.FfprobeExe, "-version");
            WarmProcess(AppPaths.FfmpegExe, "-version");
            EditTiming.Mark("RuntimeWarmup: sidecars warm", EditTiming.NowMs - t1);

            Volatile.Write(ref _done, 1);
            EditTiming.Mark("RuntimeWarmup: done");
        }

        // Runs one sidecar binary with a harmless switch and drains its output, so
        // the image, its dependencies and every plugin it opens land in the file
        // cache. Output is discarded; a failure is irrelevant (the real call sites
        // report their own errors).
        private static void WarmProcess(string exe, string args)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = args;
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                }
            }
            catch { }
        }
    }
}
