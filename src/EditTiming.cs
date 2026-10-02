using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    // TEMPORARY DIAGNOSTIC (regression hunt for the slow Editor open).
    //
    // Enabled only by the test-only CLI switches (--editortiming / --mftiming),
    // so a normal run never writes anything. It records two things in
    // edit_timing.txt next to the EXE:
    //   * a phase mark with the milliseconds since process start, so every step
    //     of "MainForm -> Trim/Frame -> Editor -> Open Video" has a measured
    //     wall time;
    //   * a UI-thread responsiveness probe: a background thread posts an
    //     Invoke to the UI thread and measures how long the round trip takes.
    //     A round trip of seconds is exactly the "(Не отвечает)" condition
    //     Windows shows for a window whose thread does not pump.
    internal static class EditTiming
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly object Sync = new object();
        private static string _path;
        private static bool _probeStarted;

        public static bool Enabled;

        public static void Enable(string file)
        {
            _path = file;
            Enabled = true;
        }

        public static long NowMs
        {
            get { return Clock.ElapsedMilliseconds; }
        }

        public static void Mark(string what)
        {
            if (!Enabled) return;
            Write("+" + Clock.ElapsedMilliseconds.ToString() + " ms\t" + what);
        }

        public static void Mark(string what, long ms)
        {
            if (!Enabled) return;
            Write("+" + Clock.ElapsedMilliseconds.ToString() + " ms\t" + what + " (" + ms.ToString() + " ms)");
        }

        public static void Write(string line)
        {
            if (_path == null) return;
            lock (Sync)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
            }
        }

        // Measures how long the UI thread takes to answer. Started from a control
        // that already lives on the UI thread (the editor, once it is shown).
        public static void StartUiProbe(Control control)
        {
            if (!Enabled || control == null) return;
            lock (Sync)
            {
                if (_probeStarted) return;
                _probeStarted = true;
            }
            Thread t = new Thread(new ThreadStart(delegate
            {
                while (true)
                {
                    long round = -1;
                    ManualResetEventSlim ack = new ManualResetEventSlim(false);
                    try
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        control.BeginInvoke((MethodInvoker)delegate { ack.Set(); });
                        if (!ack.Wait(60000)) return;
                        round = sw.ElapsedMilliseconds;
                    }
                    catch { return; }
                    if (round >= 500)
                        Write("+" + Clock.ElapsedMilliseconds.ToString() + " ms\tUI NOT RESPONDING for " + round.ToString() + " ms");
                    Thread.Sleep(100);
                }
            }));
            t.IsBackground = true;
            t.Name = "edit-timing-probe";
            t.Start();
        }
    }
}
