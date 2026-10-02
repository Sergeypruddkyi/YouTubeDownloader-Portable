using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace YouTubeDownloader
{
    // Measurement harness for the preview scrubbing pipeline.
    //
    // TEST-ONLY: nothing in this file is used by the application at runtime. It
    // exists so the before/after numbers of the scrubbing work can be reproduced
    // on the real user clip instead of being guessed.
    //
    //   --scrubbench <file> baseline   the ORIGINAL EditorForm preview logic:
    //                                  180 ms debounce timer, the `_busy` gate that
    //                                  drops moves, FrameDecoder.StartAt per seek.
    //                                  This is a faithful re-implementation of
    //                                  ArmSeekTimer/OnSeekTimerTick/SeekTo/
    //                                  FinishFrameTask, not an approximation, so the
    //                                  "before" numbers describe the old code.
    //   --scrubbench <file> new        the same scripts driven through PreviewScrubber.
    //
    // Timing rule: a script is over when the pipeline has been idle for
    // SettleIdleMs after the last playhead position, or when nothing at all has
    // happened for SettleCapMs. A fixed "sleep N ms then look" window is NOT used:
    // it silently truncates slow decodes and turns the measurement into a lie.
    //
    // It never writes media to disk: every frame is decoded into memory and the
    // bitmaps are dropped immediately. No temporary files are produced.
    internal static class ScrubBench
    {
        // Idle detector.
        private const int SettlePollMs = 200;
        private const int SettleIdleMs = 1500;
        private const int SettleCapMs = 20000;

        internal sealed class Script
        {
            public string Name;
            public double Start;
            public double Step;
            public int Count;
            public int GapMs;

            public double PositionOf(int i)
            {
                return Start + i * Step;
            }

            public double LastPosition
            {
                get { return PositionOf(Count - 1); }
            }
        }

        internal sealed class Result
        {
            public string Script;
            public string Mode;
            public volatile bool Done;
            public string Error;
            public bool TimedOut;

            public int Requests;
            public int DroppedByBusy;
            public int Decodes;
            public int Published;
            public int ObsoletePublished;
            public int DecodeErrors;
            public int ProcessStarts;

            public double FirstRequestMs = -1;
            public double FirstFrameMs = -1;
            public string FirstFrameKind = "";

            public double FinalRequestMs = -1;
            public double FinalExactMs = -1;
            public double FinalPts = double.NaN;
            public double FinalTarget = double.NaN;

            public int CacheHits;
            public int StepsForward;
            public int ThumbFrames;
            public int CachedFrames;
            public int ExactFrames;
            public int Abandoned;

            public bool FinalOk
            {
                get
                {
                    if (double.IsNaN(FinalPts) || double.IsNaN(FinalTarget)) return false;
                    return Math.Abs(FinalPts - FinalTarget) <= 0.05;
                }
            }
        }

        public static int Run(string file, string mode)
        {
            TryAttachConsole();
            _log.Length = 0;
            string reportPath = Path.Combine(Path.GetTempPath(), "yd_audit", "scrubbench_" + mode + ".txt");
            try { Directory.CreateDirectory(Path.GetDirectoryName(reportPath)); }
            catch { }
            try
            {
                int rc = RunInner(file, mode);
                WriteReport(reportPath);
                return rc;
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                WriteReport(reportPath);
                return 3;
            }
        }

        private static readonly StringBuilder _log = new StringBuilder();

        private static void Log(string s)
        {
            _log.AppendLine(s);
            try { Console.WriteLine(s); }
            catch { }
        }

        private static void WriteReport(string path)
        {
            try { File.WriteAllText(path, _log.ToString(), new UTF8Encoding(false)); }
            catch { }
        }

        private static int RunInner(string file, string mode)
        {
            if (!File.Exists(file))
            {
                Log("scrubbench: file not found: " + file);
                return 2;
            }
            MediaInfo media = MediaProbe.Probe(file);
            if (!media.Ok || media.IsAudioOnly)
            {
                Log("scrubbench: cannot probe video: " + (media.Error ?? "audio only"));
                return 2;
            }

            Log("=== scrub bench: " + mode + " ===");
            Log("file: " + file);
            Log(string.Format(CultureInfo.InvariantCulture,
                "media: {0}x{1} {2} {3:N3}s", media.Width, media.Height, media.VideoCodec ?? "?", media.Duration));
            Log("settle: idle " + SettleIdleMs + " ms, cap " + SettleCapMs + " ms");
            Log("");

            List<Script> scripts = new List<Script>();
            scripts.Add(MakeScript("fast", 100.0, 0.02, 60, 20));
            scripts.Add(MakeScript("slow", 100.0, 0.125, 8, 250));
            scripts.Add(MakeScript("abcd", 100.0, 4.0, 4, 200));
            scripts.Add(MakeScript("revisit", 100.0, 1.0, 5, 150));

            List<Result> results = new List<Result>();
            for (int i = 0; i < scripts.Count; i++)
            {
                Result r = RunOne(file, media, scripts[i], mode);
                results.Add(r);
                Print(r);
            }

            Log("");
            PrintSummary(results);
            return 0;
        }

        private static Script MakeScript(string name, double start, double step, int count, int gapMs)
        {
            Script s = new Script();
            s.Name = name;
            s.Start = start;
            s.Step = step;
            s.Count = count;
            s.GapMs = gapMs;
            return s;
        }

        // Every script runs on its own STA thread with a message pump: the timers,
        // the task continuations and the scrubber deliveries all need one, exactly
        // like the editor does.
        private static Result RunOne(string file, MediaInfo media, Script script, string mode)
        {
            Result r = new Result();
            r.Script = script.Name;
            r.Mode = mode;
            r.FinalTarget = script.LastPosition;

            Thread t = new Thread(delegate()
            {
                try
                {
                    using (Form host = new Form())
                    {
                        IntPtr h = host.Handle;   // installs the WinForms sync context
                        if (string.Equals(mode, "baseline", StringComparison.OrdinalIgnoreCase))
                        {
                            BaselineRun run = new BaselineRun(file, media, script, r, host);
                            run.Start();
                        }
                        else
                        {
                            ScrubberRun run = new ScrubberRun(file, media, script, r, host);
                            run.Start();
                        }
                        Stopwatch guard = Stopwatch.StartNew();
                        while (!r.Done && guard.ElapsedMilliseconds < 90000)
                        {
                            Application.DoEvents();
                            Thread.Sleep(4);
                        }
                        if (!r.Done) r.Error = "harness timeout";
                    }
                }
                catch (Exception ex)
                {
                    r.Error = ex.ToString();
                    r.Done = true;
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(120000))
            {
                r.Error = "thread did not finish";
                r.Done = true;
            }
            return r;
        }

        // ------------------------------------------------------------------
        // baseline: faithful re-implementation of the ORIGINAL editor preview
        // ------------------------------------------------------------------

        private sealed class BaselineRun
        {
            private readonly string _file;
            private readonly MediaInfo _media;
            private readonly Script _script;
            private readonly Result _r;
            private readonly Form _host;

            private readonly WinTimer _seekTimer;     // EditorForm._seekTimer (180 ms)
            private readonly WinTimer _moveTimer;     // feeds the scripted mouse moves
            private readonly WinTimer _poll;          // idle detector

            private FrameDecoder _decoder;
            private volatile bool _busy;
            private double _pending;
            private double _latestRequested;
            private double _lastPts;
            private bool _lastPtsValid;
            private int _index;
            private bool _finalizing;
            private Stopwatch _clock;
            private long _lastActivityMs;

            public BaselineRun(string file, MediaInfo media, Script script, Result r, Form host)
            {
                _file = file;
                _media = media;
                _script = script;
                _r = r;
                _host = host;

                _seekTimer = new WinTimer();
                _seekTimer.Interval = 180;                 // EditorForm: _seekTimer.Interval = 180
                _seekTimer.Tick += OnSeekTick;

                _moveTimer = new WinTimer();
                _moveTimer.Interval = script.GapMs;
                _moveTimer.Tick += OnMove;

                _poll = new WinTimer();
                _poll.Interval = SettlePollMs;
                _poll.Tick += OnPoll;
            }

            public void Start()
            {
                _decoder = new FrameDecoder(_file, _media.Width, _media.Height);
                DecodedFrame f0 = _decoder.StartAt(0);     // the frame the editor shows on open
                if (f0 != null)
                {
                    _lastPts = f0.PtsTime >= 0 ? f0.PtsTime : 0;
                    _lastPtsValid = true;
                    Log("  [baseline] warm-up StartAt(0) -> pts=" + f0.PtsTime.ToString("N3", CultureInfo.InvariantCulture));
                    Dispose(f0);
                }
                _r.ProcessStarts++;
                _clock = Stopwatch.StartNew();
                _lastActivityMs = 0;
                _moveTimer.Start();
                _poll.Start();
            }

            private void OnMove(object sender, EventArgs e)
            {
                if (_index >= _script.Count)
                {
                    _moveTimer.Stop();
                    _r.FinalRequestMs = _clock.ElapsedMilliseconds;
                    return;
                }
                double t = _script.PositionOf(_index);
                _index++;
                _r.Requests++;
                _latestRequested = t;
                if (_r.Requests == 1) _r.FirstRequestMs = _clock.ElapsedMilliseconds;
                ArmSeekTimer(t);
            }

            // EditorForm.ArmSeekTimer - the gate that loses the newest positions.
            private void ArmSeekTimer(double t)
            {
                _lastActivityMs = _clock.ElapsedMilliseconds;
                if (_busy) { _r.DroppedByBusy++; return; }
                _pending = t;
                _seekTimer.Stop();
                _seekTimer.Start();
            }

            // EditorForm.OnSeekTimerTick
            private void OnSeekTick(object sender, EventArgs e)
            {
                _seekTimer.Stop();
                _lastActivityMs = _clock.ElapsedMilliseconds;
                if (_busy || _decoder == null || !_lastPtsValid) return;
                double t = _pending;
                if (Math.Abs(t - _lastPts) < 1e-6) return;
                SeekTo(t);
            }

            // EditorForm.SeekTo
            private void SeekTo(double t)
            {
                _busy = true;
                _r.Decodes++;
                _r.ProcessStarts++;                        // StartAt always spawns ffmpeg
                _lastActivityMs = _clock.ElapsedMilliseconds;
                Log("  [baseline] seek t=" + t.ToString("N3", CultureInfo.InvariantCulture)
                    + " at " + _clock.ElapsedMilliseconds + " ms");
                Task<DecodedFrame> task = Task.Factory.StartNew(new Func<DecodedFrame>(delegate
                {
                    return _decoder.StartAt(t);
                }), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                task.ContinueWith(new Action<Task<DecodedFrame>>(delegate(Task<DecodedFrame> tt)
                {
                    Finish(tt, t);
                }), TaskScheduler.FromCurrentSynchronizationContext());
            }

            // EditorForm.FinishFrameTask - publishes unconditionally, no generation check.
            private void Finish(Task<DecodedFrame> tt, double requested)
            {
                DecodedFrame f = null;
                try { f = tt.Result; }
                catch { }
                _busy = false;
                _lastActivityMs = _clock.ElapsedMilliseconds;
                if (f == null)
                {
                    _r.DecodeErrors++;
                    Log("  [baseline] seek t=" + requested.ToString("N3", CultureInfo.InvariantCulture)
                        + " -> NULL at " + _clock.ElapsedMilliseconds + " ms");
                    return;
                }

                Log("  [baseline] seek t=" + requested.ToString("N3", CultureInfo.InvariantCulture)
                    + " -> frame pts=" + f.PtsTime.ToString("N3", CultureInfo.InvariantCulture)
                    + " at " + _clock.ElapsedMilliseconds + " ms");

                _r.Published++;
                if (Math.Abs(f.PtsTime - _latestRequested) > 1e-6) _r.ObsoletePublished++;

                if (_r.FirstFrameMs < 0)
                {
                    _r.FirstFrameMs = _clock.Elapsed.TotalMilliseconds - _r.FirstRequestMs;
                    _r.FirstFrameKind = "Exact";
                }
                if (_r.FinalExactMs < 0 && _r.FinalRequestMs >= 0
                    && Math.Abs(f.PtsTime - _script.LastPosition) <= 0.05)
                {
                    _r.FinalExactMs = _clock.Elapsed.TotalMilliseconds - _r.FinalRequestMs;
                }

                _lastPts = f.PtsTime >= 0 ? f.PtsTime : _lastPts;
                _lastPtsValid = true;
                _r.FinalPts = _lastPts;
                Dispose(f);
            }

            private void OnPoll(object sender, EventArgs e)
            {
                if (_finalizing) return;
                long now = _clock.ElapsedMilliseconds;
                bool quiet = !_busy && !_seekTimer.Enabled && !_moveTimer.Enabled;
                if (quiet && now - _lastActivityMs >= SettleIdleMs) { Finalize(now, false); return; }
                if (now - _lastActivityMs >= SettleCapMs) { Finalize(now, true); return; }
            }

            private void Finalize(long now, bool timedOut)
            {
                _finalizing = true;
                _r.TimedOut = timedOut;
                Cleanup();
                _r.Done = true;
            }

            private void Cleanup()
            {
                _poll.Stop();
                _seekTimer.Stop();
                _moveTimer.Stop();
                if (_decoder != null) { _decoder.Dispose(); _decoder = null; }
            }

            private static void Dispose(DecodedFrame f)
            {
                try { if (f != null && f.Image != null) f.Image.Dispose(); }
                catch { }
            }
        }

        // ------------------------------------------------------------------
        // new: the same script through PreviewScrubber
        // ------------------------------------------------------------------

        private sealed class ScrubberRun
        {
            private readonly string _file;
            private readonly MediaInfo _media;
            private readonly Script _script;
            private readonly Result _r;
            private readonly Form _host;

            private readonly WinTimer _moveTimer;
            private readonly WinTimer _poll;
            private PreviewScrubber _scrubber;
            private int _index;
            private bool _finalizing;
            private Stopwatch _clock;
            private long _lastActivityMs;

            public ScrubberRun(string file, MediaInfo media, Script script, Result r, Form host)
            {
                _file = file;
                _media = media;
                _script = script;
                _r = r;
                _host = host;

                _moveTimer = new WinTimer();
                _moveTimer.Interval = script.GapMs;
                _moveTimer.Tick += OnMove;

                _poll = new WinTimer();
                _poll.Interval = SettlePollMs;
                _poll.Tick += OnPoll;
            }

            public void Start()
            {
                _scrubber = new PreviewScrubber(_file, _media.Width, _media.Height, _media.Duration, _host);
                _scrubber.FrameReady += OnFrame;
                _scrubber.RequestFinal(0);                  // the frame the editor shows on open
                _clock = Stopwatch.StartNew();
                _lastActivityMs = 0;
                _moveTimer.Start();
                _poll.Start();
            }

            private void OnMove(object sender, EventArgs e)
            {
                if (_index >= _script.Count)
                {
                    _moveTimer.Stop();
                    _r.FinalRequestMs = _clock.ElapsedMilliseconds;   // MouseUp
                    // The forward walk can already have the final position on screen when
                    // the drag ends. That is the best possible outcome, not a missing
                    // measurement, so record it as zero rather than as "n/a".
                    if (_scrubber.HasDisplayed
                        && Math.Abs(_scrubber.DisplayedPts - _script.LastPosition) <= 0.05)
                    {
                        _r.FinalExactMs = 0;
                    }
                    _scrubber.RequestFinal(_script.LastPosition);
                    return;
                }
                double t = _script.PositionOf(_index);
                _index++;
                _r.Requests++;
                if (_r.Requests == 1) _r.FirstRequestMs = _clock.ElapsedMilliseconds;
                _lastActivityMs = _clock.ElapsedMilliseconds;
                _scrubber.Request(t);
            }

            private void OnFrame(Bitmap bmp, double pts, PreviewFrameKind kind)
            {
                _r.Published++;
                _lastActivityMs = _clock.ElapsedMilliseconds;
                if (kind == PreviewFrameKind.Thumbnail) _r.ThumbFrames++;
                else if (kind == PreviewFrameKind.Exact) _r.ExactFrames++;
                else _r.CachedFrames++;

                if (_r.FirstFrameMs < 0)
                {
                    _r.FirstFrameMs = _clock.Elapsed.TotalMilliseconds - _r.FirstRequestMs;
                    _r.FirstFrameKind = kind.ToString();
                }
                if (kind == PreviewFrameKind.Exact)
                {
                    _r.FinalPts = pts;
                    if (_r.FinalExactMs < 0 && _r.FinalRequestMs >= 0
                        && Math.Abs(pts - _script.LastPosition) <= 0.05)
                    {
                        _r.FinalExactMs = _clock.Elapsed.TotalMilliseconds - _r.FinalRequestMs;
                    }
                }
            }

            private void OnPoll(object sender, EventArgs e)
            {
                if (_finalizing) return;
                long now = _clock.ElapsedMilliseconds;
                bool quiet = _scrubber != null && _scrubber.IsIdle && !_moveTimer.Enabled;
                if (quiet && now - _lastActivityMs >= SettleIdleMs) { Finalize(now, false); return; }
                if (now - _lastActivityMs >= SettleCapMs) { Finalize(now, true); return; }
            }

            private void Finalize(long now, bool timedOut)
            {
                _finalizing = true;
                _r.TimedOut = timedOut;

                _r.Decodes = _scrubber.DecodesStarted;
                _r.StepsForward = _scrubber.StepsForward;
                _r.CacheHits = _scrubber.CacheHits;
                _r.Abandoned = _scrubber.DecodesAbandoned;
                _r.ObsoletePublished = _scrubber.StalePublished;
                _r.ProcessStarts = _scrubber.ProcessStarts;
                _r.DecodeErrors = _scrubber.DecodeErrors;

                _scrubber.FrameReady -= OnFrame;
                _scrubber.Dispose();
                _scrubber = null;
                _poll.Stop();
                _moveTimer.Stop();
                _r.Done = true;
            }
        }

        // ------------------------------------------------------------------

        private static void Print(Result r)
        {
            Log("--- " + r.Script + " ---");
            if (r.Error != null) Log("  ERROR: " + r.Error);
            Log("  requests (moves)        : " + r.Requests);
            Log("  dropped by _busy        : " + r.DroppedByBusy);
            Log("  decode attempts         : " + r.Decodes);
            Log("  forward steps           : " + r.StepsForward);
            Log("  ffmpeg process starts   : " + r.ProcessStarts);
            Log("  frames published        : " + r.Published
                + " (exact " + r.ExactFrames + ", cached " + r.CachedFrames + ", thumb " + r.ThumbFrames + ")");
            Log("  cache hits              : " + r.CacheHits);
            Log("  stale results published : " + r.ObsoletePublished);
            Log("  abandoned decodes       : " + r.Abandoned);
            Log("  decode errors           : " + r.DecodeErrors);
            Log(string.Format(CultureInfo.InvariantCulture,
                "  first frame visible     : {0} ms ({1})", Fmt(r.FirstFrameMs), r.FirstFrameKind));
            Log(string.Format(CultureInfo.InvariantCulture,
                "  final exact after drag  : {0} ms", Fmt(r.FinalExactMs)));
            Log(string.Format(CultureInfo.InvariantCulture,
                "  final pts / target      : {0:N3} / {1:N3}  -> {2}",
                r.FinalPts, r.FinalTarget, r.FinalOk ? "OK" : "STALE"));
            Log("  capped by idle cap      : " + (r.TimedOut ? "YES (never settled)" : "no"));
            Log("");
        }

        private static string Fmt(double ms)
        {
            if (ms < 0) return "n/a";
            return ms.ToString("N1", CultureInfo.InvariantCulture);
        }

        private static void PrintSummary(List<Result> results)
        {
            Log("summary (script | req | drop | decodes | steps | procs | pub | stale | first_ms | final_ms | final | cap)");
            for (int i = 0; i < results.Count; i++)
            {
                Result r = results[i];
                Log(string.Format(CultureInfo.InvariantCulture,
                    "{0,-8} | {1,4} | {2,4} | {3,7} | {4,5} | {5,5} | {6,3} | {7,5} | {8,8} | {9,8} | {10} | {11}",
                    r.Script, r.Requests, r.DroppedByBusy, r.Decodes, r.StepsForward, r.ProcessStarts,
                    r.Published, r.ObsoletePublished, Fmt(r.FirstFrameMs), Fmt(r.FinalExactMs),
                    r.FinalOk ? "OK" : "STALE", r.TimedOut ? "CAP" : "-"));
            }
        }

        private static void TryAttachConsole()
        {
            bool attached = AttachConsole(-1);
            if (!attached) AllocConsole();
            try
            {
                IntPtr h = CreateFileW("CONOUT$", 0x40000000 | 0x80000000, 0x1 | 0x2,
                    IntPtr.Zero, 0x3, 0, IntPtr.Zero);
                if (h != IntPtr.Zero && h != (IntPtr)(-1))
                {
                    FileStream fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, true), FileAccess.Write);
                    StreamWriter w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                    Console.SetOut(w);
                }
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    }
}
