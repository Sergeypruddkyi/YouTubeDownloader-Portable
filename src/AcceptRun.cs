using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    // END-TO-END acceptance run for the Cutter's embedded LibVLC preview.
    //
    // It walks the REAL user route, in one continuous session per file:
    //     MainForm -> Trim / Frame (the real button handler) -> Editor -> Open Video
    // and drives the editor like a user does: real mouse clicks on the Play button
    // (mouse_event at its screen position), playhead moves through the filmstrip's
    // own mouse messages, and screen captures of the video area as evidence that a
    // frame is really displayed.
    //
    // The step machine runs on the UI thread inside a WinForms timer, so the
    // largest gap between two ticks is exactly the largest time the UI thread did
    // not pump - the condition Windows reports as "(Не отвечает)".
    internal static class AcceptRun
    {
        private const int TickMs = 100;
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private static readonly StringBuilder Report = new StringBuilder();
        private static int _failures;

        private static void Line(string s)
        {
            Report.AppendLine(s);
            try { Console.WriteLine(s); }
            catch { }
        }

        private static void Check(string name, bool ok, string details)
        {
            Line((ok ? "[PASS] " : "[FAIL] ") + name + (string.IsNullOrEmpty(details) ? "" : " — " + details));
            if (!ok) _failures++;
        }

        private static string Fmt(double sec)
        {
            if (double.IsNaN(sec)) return "n/a";
            return sec.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Rect(Rectangle r)
        {
            return "(" + r.Left + "," + r.Top + ")-(" + r.Right + "," + r.Bottom + ")";
        }

        public static int Run(string file, int staySeconds)
        {
            if (staySeconds <= 0) staySeconds = 150;
            _failures = 0;
            Line("=== ACCEPTANCE: MainForm -> Trim/Frame -> Editor -> Open Video ===");
            Line("file: " + file);
            Exception loopError = null;
            Thread t = new Thread(new ThreadStart(delegate
            {
                try
                {
                    MainForm mf = new MainForm();
                    mf.Shown += delegate
                    {
                        System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic
                            | System.Reflection.BindingFlags.Instance;
                        typeof(MainForm).GetField("_currentFile", F).SetValue(mf, file);
                        Button b = (Button)typeof(MainForm).GetField("btnTrimFrame", F).GetValue(mf);
                        Line("[INFO] clicking the real Trim / Frame button");
                        Driver driver = new Driver(file, mf);
                        driver.Start();
                        b.PerformClick();       // real handler; returns when the editor is closed
                        driver.AfterEditorClosed();
                        driver.Finish();
                        mf.Close();
                    };
                    ArmWatchdogClose(mf, staySeconds);
                    Application.Run(mf);
                }
                catch (Exception ex) { loopError = ex; }
            }));
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            if (!t.Join((staySeconds + 240) * 1000))
            {
                Line("[FAIL] acceptance: окно не закрылось за " + (staySeconds + 240) + " c");
                _failures++;
            }
            if (loopError != null)
            {
                Line("[FAIL] acceptance: " + loopError.Message);
                _failures++;
            }
            Line(_failures == 0 ? "=== ACCEPTANCE: PASS ===" : "=== ACCEPTANCE: FAIL (" + _failures + ") ===");
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.BaseDir, "accept_result.txt"), Report.ToString(),
                    new UTF8Encoding(false));
            }
            catch { }
            return _failures;
        }

        // Closes the editor after staySeconds so the run needs no human.
        private static void ArmWatchdogClose(Form owner, int seconds)
        {
            System.Threading.Timer timer = null;
            timer = new System.Threading.Timer(new TimerCallback(delegate
            {
                try
                {
                    owner.BeginInvoke((MethodInvoker)delegate
                    {
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

        // The step machine: every step runs on the UI thread (a timer tick IS the UI
        // thread), so a blocked UI thread stops the ticks and shows up as a gap.
        private sealed class Driver
        {
            private readonly string _file;
            private readonly Form _main;
            private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
            private EditorForm _ed;
            private int _step;
            private long _nextStepAt;
            private long _lastTickAt;
            private long _clickAt;
            private long _maxStallMs;
            private long _openMs = -1;
            private long _posBeforePauseMs = -1;
            private long _resumeFromMs = -1;
            private long _pauseSig;
            private long _endSig;
            private long _lastTrimClickAt;
            private long _lastProbeAt;
            private double _duration;
            private long _shotSig;
            private bool _inTick;
            private bool _lastTickRanStep;

            // ---- fast-scrub (sync-loss hunt) phase state ----
            private Thread _hand;
            private volatile bool _handDone;
            private long _preDragSig;
            private double _preDragPlayerMs;
            private double _preDragPlayhead;
            private double _reopenAtMs = -1;

            public Driver(string file, Form main)
            {
                _file = file;
                _main = main;
            }

            public void Start()
            {
                _duration = MediaProbe.Probe(_file).Duration;
                _timer.Interval = TickMs;
                _timer.Tick += delegate { Tick(); };
                _lastTickAt = Environment.TickCount;
                _clickAt = _lastTickAt;
                _timer.Start();
            }

            public void Finish()
            {
                _timer.Stop();
                Line("[INFO] editor usable " + _openMs + " ms after the Trim/Frame click");
                Line("[INFO] biggest UI-thread stall during the whole run: " + _maxStallMs + " ms");
                Check("UI thread отзывчив всю сессию (нет блока > 3000 мс; порог \"(Не отвечает)\" — 5000 мс)",
                    _maxStallMs < 3000, "max stall " + _maxStallMs + " ms");
            }

            private void Tick()
            {
                long now = Environment.TickCount;
                long gap = now - _lastTickAt;
                _lastTickAt = now;
                // Only the idle gaps count: the tick that runs a step spends time in
                // the step itself (screen capture, click delays), which is not a UI
                // stall. A real stall shows up on the NEXT idle tick.
                if (!_lastTickRanStep && gap > _maxStallMs) _maxStallMs = gap;
                _lastTickRanStep = false;
                if (now < _nextStepAt) return;
                // Reentrancy guard: the steps use test hooks that pump messages
                // (DoEvents), and a timer tick queued during that pump must not run
                // the same step a second time.
                if (_inTick) return;
                _inTick = true;
                try { _lastTickRanStep = true; RunStep(); }
                catch (Exception ex)
                {
                    Line("[FAIL] accept step " + _step + ": " + ex.Message);
                    _failures++;
                    _step = 99;
                }
                finally { _inTick = false; }
            }

            private void Wait(int ms) { _nextStepAt = Environment.TickCount + ms; }

            private EditorForm Editor
            {
                get
                {
                    if (_ed != null && !_ed.IsDisposed) return _ed;
                    foreach (Form f in Application.OpenForms)
                    {
                        EditorForm e = f as EditorForm;
                        if (e != null) { _ed = e; break; }
                    }
                    return _ed;
                }
            }

            private void RunStep()
            {
                // Claim this step for the whole run: anything that pumps messages
                // inside it (DoEvents in the test hooks) may re-enter Tick, and that
                // tick must not repeat this step.
                _nextStepAt = Environment.TickCount + 500;
                EditorForm ed = Editor;
                switch (_step)
                {
                    case 0:
                        if (ed == null || !ed.TestReady || !ed.TestLibVlcActive)
                        {
                            // A real user whose click did nothing clicks again. The
                            // single programmatic click in Shown can land inside the
                            // main window's own busy window (startup update check),
                            // and OnTrimFrame silently returns - so from here the
                            // driver repeats a REAL mouse click while no editor
                            // exists. Never PerformClick from a tick: the click
                            // handler blocks in ShowDialog and the tick must not
                            // block with it.
                            if (ed == null && Environment.TickCount - _lastTrimClickAt >= 3000
                                && Environment.TickCount - _clickAt <= 60000)
                            {
                                _lastTrimClickAt = Environment.TickCount;
                                RealClickTrimFrame();
                            }
                            // Periodic evidence of WHY the editor has not appeared
                            // (or is not ready): the main window's busy flags tell
                            // whether a guard swallowed the click.
                            if (Environment.TickCount - _lastProbeAt >= 3000)
                            {
                                _lastProbeAt = Environment.TickCount;
                                System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic
                                    | System.Reflection.BindingFlags.Instance;
                                bool downloading = (bool)(typeof(MainForm).GetField("_downloading", F)
                                    ?.GetValue(_main) ?? false);
                                bool updateRunning = (bool)(typeof(MainForm).GetField("_updateRunning", F)
                                    ?.GetValue(_main) ?? false);
                                Line("[INFO] waiting: editor=" + (ed != null)
                                    + " ready=" + (ed != null && ed.TestReady)
                                    + " libvlc=" + (ed != null && ed.TestLibVlcActive)
                                    + " mainDownloading=" + downloading
                                    + " mainUpdateRunning=" + updateRunning);
                            }
                            if (Environment.TickCount - _clickAt > 60000)
                            {
                                Check("Open Video: Editor становится готов за 60 c", false,
                                    "ready=" + (ed != null && ed.TestReady)
                                    + " libvlc=" + (ed != null && ed.TestLibVlcActive));
                                _step = 99;
                            }
                            Wait(80);
                            return;
                        }
                        _openMs = Environment.TickCount - _clickAt;
                        Line("[INFO] editor ready, duration=" + Fmt(ed.TestDuration) + " c");
                        Wait(1400);         // let the player paint its first frame
                        _step = 1;
                        return;

                    case 1:
                        CheckOpen(ed);
                        _step = 10;
                        return;

                    case 10:
                        // Poll the video area until a frame is really painted, so the
                        // report contains the measured time-to-first-frame instead of
                        // a single possibly-too-early snapshot.
                        {
                            double nonBlack;
                            int distinct;
                            long sig = ShotSignature(ed.TestVideoViewScreenRect, out nonBlack, out distinct);
                            long sinceReady = Environment.TickCount - _openMs - _clickAt;
                            if (nonBlack > 0.02 && distinct > 8)
                            {
                                _shotSig = sig;
                                Line("[INFO] first frame painted " + sinceReady + " ms after the Trim/Frame click"
                                    + " (nonBlack=" + nonBlack.ToString("0.00") + " distinct=" + distinct + ")");
                                Check("Open -> первый кадр появляется в VideoView", sinceReady < 5000,
                                    "time to first frame " + sinceReady + " ms");
                                ed.TestSeekTo(10.0);
                                Line("[INFO] moved the playhead to 10 s");
                                Wait(1500);
                                _step = 2;
                                return;
                            }
                            if (sinceReady > 6000)
                            {
                                Check("Open -> первый кадр появляется в VideoView", false,
                                    "через 6000 мс экран остался чёрным (nonBlack=" + nonBlack.ToString("0.00") + ")");
                                _step = 99;
                                return;
                            }
                            Wait(300);
                            return;
                        }

                    case 2:
                        CheckSeekWhilePaused(ed);
                        ClickPlay("Play", true);
                        Wait(1500);
                        _step = 3;
                        return;

                    case 3:
                        Check("Play: воспроизведение началось", ed.TestLibVlcPlaying,
                            "playing=" + ed.TestLibVlcPlaying);
                        Wait(1500);
                        _step = 4;
                        return;

                    case 4:
                        CheckPlayheadFollows(ed);
                        _posBeforePauseMs = (long)(ed.TestPosition * 1000);
                        ClickPlay("Play (pause)", false);
                        Wait(900);
                        _step = 5;
                        return;

                    case 5:
                        CheckPause(ed);
                        _resumeFromMs = (long)(ed.TestPosition * 1000);
                        ClickPlay("Play (resume)", true);
                        Wait(1600);
                        _step = 6;
                        return;

                    case 6:
                        CheckResume(ed);
                        ed.TestSeekTo(Math.Min(_duration * 0.4, 40.0));
                        Line("[INFO] moved the playhead during playback");
                        Wait(1600);
                        _step = 7;
                        return;

                    case 7:
                        CheckSeekWhilePlaying(ed);
                        ClickPlay("Play (pause)", false);
                        Wait(900);
                        _step = 8;
                        return;

                    case 8:
                        Check("Pause после seek: воспроизведение остановлено", !ed.TestLibVlcPlaying,
                            "playing=" + ed.TestLibVlcPlaying + " pos=" + Fmt(ed.TestPosition));
                        _pauseSig = ShotSignature(ed.TestVideoViewScreenRect, out _, out _);
                        Wait(600);
                        _step = 20;
                        return;

                    // ---- (I) the user's Play -> Stop(pause) recipe, part 1:
                    // after a Play/Pause cycle the playhead must STILL drive the
                    // preview (this is the state the play/pause toggle leaves).
                    case 20:
                        Line("[INFO] post-pause: state=" + ed.TestLibVlcState
                            + " playerMs=" + Fmt(ed.TestLibVlcPositionMs)
                            + " playhead=" + Fmt(ed.TestPosition));
                        ed.TestSeekTo(12.0);
                        Wait(1800);
                        _step = 21;
                        return;

                    case 21:
                        {
                            double nonBlack;
                            int distinct;
                            long sig = ShotSignature(ed.TestVideoViewScreenRect, out nonBlack, out distinct);
                            double ms = ed.TestLibVlcPositionMs;
                            Line("[INFO] seek after pause: state=" + ed.TestLibVlcState
                                + " playerMs=" + Fmt(ms) + " playhead=" + Fmt(ed.TestPosition)
                                + " nonBlack=" + nonBlack.ToString("0.00"));
                            Check("Seek при паузе после Play/Pause: кадр изменился на 12 c", sig != _pauseSig,
                                "sigChanged=" + (sig != _pauseSig) + " nonBlack=" + nonBlack.ToString("0.00"));
                            Check("Seek при паузе после Play/Pause: позиция применена",
                                ed.TestLibVlcState == "Paused" && Math.Abs(ms - 12000) < 1500,
                                "state=" + ed.TestLibVlcState + " playerMs=" + Fmt(ms));
                            // ---- (II) the dead-state repro: play from near the end so
                            // the media reaches its end and the player auto-stops
                            // (EndReached -> no live input). Then the user's broken step:
                            // move the playhead and expect the preview to follow.
                            ed.TestSeekTo(Math.Max(0, _duration - 1.5));
                            Wait(900);
                            _step = 22;
                            return;
                        }

                    case 22:
                        ClickPlay("Play (near end)", true);
                        Wait(3500);
                        _step = 23;
                        return;

                    case 23:
                        {
                            double nonBlack;
                            int distinct;
                            _endSig = ShotSignature(ed.TestVideoViewScreenRect, out nonBlack, out distinct);
                            Line("[INFO] after end-of-media: state=" + ed.TestLibVlcState
                                + " playerMs=" + Fmt(ed.TestLibVlcPositionMs)
                                + " playhead=" + Fmt(ed.TestPosition)
                                + " playing=" + ed.TestLibVlcPlaying);
                            Check("Медиа доиграло до конца: плеер остановлен (нет живого input)",
                                ed.TestLibVlcState == "Stopped" || ed.TestLibVlcState == "Ended",
                                "state=" + ed.TestLibVlcState);
                            ed.TestSeekTo(15.0);
                            Wait(2000);
                            _step = 24;
                            return;
                        }

                    case 24:
                        {
                            double nonBlack;
                            int distinct;
                            long sig = ShotSignature(ed.TestVideoViewScreenRect, out nonBlack, out distinct);
                            double ms = ed.TestLibVlcPositionMs;
                            Line("[INFO] seek after stop: state=" + ed.TestLibVlcState
                                + " playerMs=" + Fmt(ms) + " playhead=" + Fmt(ed.TestPosition)
                                + " nonBlack=" + nonBlack.ToString("0.00") + " distinct=" + distinct);
                            Check("Timeline seek после Play->Stop: кадр изменился на 15 c", sig != _endSig,
                                "sigChanged=" + (sig != _endSig) + " nonBlack=" + nonBlack.ToString("0.00"));
                            Check("Timeline seek после Play->Stop: позиция применена, кадр соответствует playhead",
                                Math.Abs(ed.TestPosition - 15.0) < 1.2 && Math.Abs(ms - 15000) < 1500
                                    && ed.TestLibVlcState == "Paused",
                                "playhead=" + Fmt(ed.TestPosition) + " playerMs=" + Fmt(ms)
                                    + " state=" + ed.TestLibVlcState);
                            // ---- (III) the fast-scrub sync-loss hunt: real-hand
                            // drags, a full-speed burst, play/pause + drag, and a
                            // fresh open followed IMMEDIATELY by a fast drag.
                            Wait(600);
                            _step = 30;
                            return;
                        }

                    // ---- (III) fast-scrub sync-loss hunt ----------------------
                    // A real hand: the cursor is moved by mouse_event at hand speed
                    // while the UI thread stays free - moves land in the message
                    // queue exactly like a user's drag, including arriving while
                    // libvlc is still processing the previous seek.

                    case 30:
                        // Paused, fast back-and-forth drag between two strip points.
                        _preDragSig = ShotSignature(ed.TestVideoViewScreenRect, out _, out _);
                        _preDragPlayerMs = ed.TestLibVlcPositionMs;
                        _preDragPlayhead = ed.TestPosition;
                        StartHandDrag(ed, 0.25, 0.65, 30, 28);
                        Line("[INFO] hand drag #1 (paused): preDragPlayerMs=" + Fmt(_preDragPlayerMs / 1000.0)
                            + " preDragPlayhead=" + Fmt(_preDragPlayhead));
                        Wait(1200);
                        _step = 31;
                        return;

                    case 31:
                        if (!_handDone) { Wait(300); return; }
                        Wait(2500);     // let the final AV1/H264 paused decode finish
                        _step = 32;
                        return;

                    case 32:
                        {
                            long sig = ShotSignature(ed.TestVideoViewScreenRect, out double nonBlack, out _);
                            double playhead = ed.TestPosition;
                            double playerMs = ed.TestLibVlcPositionMs;
                            Line("[INFO] after hand drag #1: playhead=" + Fmt(playhead) + " playerMs=" + Fmt(playerMs / 1000.0)
                                + " state=" + ed.TestLibVlcState + " vout=" + ed.TestLibVlcVoutCount
                                + " skbl=" + ed.TestLibVlcSeekable + " nonBlack=" + nonBlack.ToString("0.00"));
                            Check("Быстрый drag (paused): playhead и player сошлись",
                                Math.Abs(playhead - playerMs / 1000.0) < 1.5,
                                "playhead=" + Fmt(playhead) + " player=" + Fmt(playerMs / 1000.0));
                            Check("Быстрый drag (paused): preview живой (кадр изменился)",
                                sig != _preDragSig && nonBlack > 0.02,
                                "sigChanged=" + (sig != _preDragSig) + " nonBlack=" + nonBlack.ToString("0.00"));
                            Wait(400);
                            _step = 33;
                            return;
                        }

                    case 33:
                        // Full-speed burst: every move back to back through the
                        // strip's own message path; latest-wins must land the FINAL
                        // position and the preview must show it.
                        _preDragSig = ShotSignature(ed.TestVideoViewScreenRect, out _, out _);
                        _preDragPlayerMs = ed.TestLibVlcPositionMs;
                        {
                            double d = _duration;
                            var times = new System.Collections.Generic.List<double>();
                            for (int i = 0; i < 41; i++)
                                times.Add((i % 2 == 0) ? d * 0.30 : d * 0.55);
                            times.Add(d * 0.40);
                            ed.TestScrubBurst(times.ToArray());
                        }
                        Wait(3000);
                        _step = 34;
                        return;

                    case 34:
                        {
                            double playhead = ed.TestPosition;
                            double playerMs = ed.TestLibVlcPositionMs;
                            long sig = ShotSignature(ed.TestVideoViewScreenRect, out double nonBlack, out _);
                            Line("[INFO] after full-speed burst: playhead=" + Fmt(playhead) + " playerMs=" + Fmt(playerMs / 1000.0)
                                + " state=" + ed.TestLibVlcState + " nonBlack=" + nonBlack.ToString("0.00"));
                            Check("Полный-speed burst (paused): playhead и player сошлись",
                                Math.Abs(playhead - playerMs / 1000.0) < 1.5,
                                "playhead=" + Fmt(playhead) + " player=" + Fmt(playerMs / 1000.0));
                            Check("Полный-speed burst (paused): preview живой",
                                sig != _preDragSig && nonBlack > 0.02,
                                "sigChanged=" + (sig != _preDragSig) + " nonBlack=" + nonBlack.ToString("0.00"));
                            Wait(400);
                            _step = 35;
                            return;
                        }

                    case 35:
                        // Play -> Pause -> fast drag: the state the user leaves after
                        // a transport round-trip.
                        ClickPlay("Play (before drag #2)", true);
                        Wait(1500);
                        _step = 36;
                        return;

                    case 36:
                        ClickPlay("Pause (before drag #2)", false);
                        Wait(900);
                        _step = 37;
                        return;

                    case 37:
                        _preDragSig = ShotSignature(ed.TestVideoViewScreenRect, out _, out _);
                        _preDragPlayerMs = ed.TestLibVlcPositionMs;
                        StartHandDrag(ed, 0.20, 0.50, 30, 28);
                        Line("[INFO] hand drag #2 (after Play->Pause): preDragPlayerMs=" + Fmt(_preDragPlayerMs / 1000.0));
                        Wait(1200);
                        _step = 38;
                        return;

                    case 38:
                        if (!_handDone) { Wait(300); return; }
                        Wait(2500);
                        _step = 39;
                        return;

                    case 39:
                        {
                            double playhead = ed.TestPosition;
                            double playerMs = ed.TestLibVlcPositionMs;
                            long sig = ShotSignature(ed.TestVideoViewScreenRect, out double nonBlack, out _);
                            Line("[INFO] after hand drag #2: playhead=" + Fmt(playhead) + " playerMs=" + Fmt(playerMs / 1000.0)
                                + " state=" + ed.TestLibVlcState + " vout=" + ed.TestLibVlcVoutCount + " nonBlack=" + nonBlack.ToString("0.00"));
                            Check("Drag после Play->Pause: playhead и player сошлись",
                                Math.Abs(playhead - playerMs / 1000.0) < 1.5,
                                "playhead=" + Fmt(playhead) + " player=" + Fmt(playerMs / 1000.0));
                            Check("Drag после Play->Pause: preview живой",
                                sig != _preDragSig && nonBlack > 0.02,
                                "sigChanged=" + (sig != _preDragSig) + " nonBlack=" + nonBlack.ToString("0.00"));
                            Wait(400);
                            _step = 40;
                            return;
                        }

                    case 40:
                        // Fresh open, then the very FIRST user action is a fast
                        // drag - the "Open -> PAUSED -> быстро двигаем playhead"
                        // repro window.
                        _reopenAtMs = Environment.TickCount;
                        Line("[INFO] reopening the media (fresh open, first action = fast drag)");
                        ed.TestReopen();
                        Wait(500);
                        _step = 41;
                        return;

                    case 41:
                        if (ed.TestBusy || !ed.TestLibVlcActive)
                        {
                            if (Environment.TickCount - _reopenAtMs > 30000)
                            {
                                Check("Reopen: редактор снова готов за 30 c", false, "busy=" + ed.TestBusy);
                                _step = 43;
                                return;
                            }
                            Wait(500);
                            return;
                        }
                        // Reopen done: drag IMMEDIATELY, no settle delay.
                        _preDragSig = ShotSignature(ed.TestVideoViewScreenRect, out _, out _);
                        _preDragPlayerMs = ed.TestLibVlcPositionMs;
                        StartHandDrag(ed, 0.30, 0.60, 25, 30);
                        Line("[INFO] hand drag #3 (first action after fresh open)");
                        Wait(1200);
                        _step = 42;
                        return;

                    case 42:
                        if (!_handDone) { Wait(300); return; }
                        Wait(3000);
                        _step = 43;
                        return;

                    case 43:
                        {
                            double playhead = ed.TestPosition;
                            double playerMs = ed.TestLibVlcPositionMs;
                            long sig = ShotSignature(ed.TestVideoViewScreenRect, out double nonBlack, out _);
                            Line("[INFO] after hand drag #3: playhead=" + Fmt(playhead) + " playerMs=" + Fmt(playerMs / 1000.0)
                                + " state=" + ed.TestLibVlcState + " vout=" + ed.TestLibVlcVoutCount + " nonBlack=" + nonBlack.ToString("0.00"));
                            Check("Первый drag сразу после Open: playhead и player сошлись",
                                Math.Abs(playhead - playerMs / 1000.0) < 1.5,
                                "playhead=" + Fmt(playhead) + " player=" + Fmt(playerMs / 1000.0));
                            Check("Первый drag сразу после Open: preview живой",
                                sig != _preDragSig && nonBlack > 0.02,
                                "sigChanged=" + (sig != _preDragSig) + " nonBlack=" + nonBlack.ToString("0.00"));
                            try { ed.Close(); } catch { }
                            _timer.Stop();
                            _step = 100;
                            return;
                        }

                    default:
                        _timer.Stop();
                        return;
                }
            }

            // Runs right after the editor really closed (the modal loop returned).
            // The post-close checks cannot live in a timer step: closing the editor
            // returns control to Run() immediately, the timer never gets there.
            public void AfterEditorClosed()
            {
                bool gone = true;
                foreach (Form f in Application.OpenForms)
                {
                    if (f is EditorForm) { gone = false; break; }
                }
                Check("Editor закрылся, MainForm остаётся живым", gone && !_main.IsDisposed,
                    "editorGone=" + gone + " mainAlive=" + !_main.IsDisposed);

                long maxRound = 0;
                System.Threading.ManualResetEventSlim done = new System.Threading.ManualResetEventSlim(false);
                Thread probe = new Thread(new ThreadStart(delegate
                {
                    for (int i = 0; i < 6; i++)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        try { _main.Invoke((MethodInvoker)delegate { }); }
                        catch { break; }
                        long r = sw.ElapsedMilliseconds;
                        if (r > maxRound) maxRound = r;
                    }
                    done.Set();
                }));
                probe.IsBackground = true;
                probe.Start();
                // Pump here: the probe measures how fast the closed-editor state of
                // the main window answers, which is what the user would notice.
                Stopwatch pump = Stopwatch.StartNew();
                while (!done.IsSet && pump.ElapsedMilliseconds < 5000)
                {
                    Application.DoEvents();
                    Thread.Sleep(15);
                }
                Check("MainForm отвечает после закрытия Editor", done.IsSet && maxRound < 1000,
                    "max invoke round trip " + maxRound + " ms");
            }

            // ---- the individual checks ------------------------------------------
            private void CheckOpen(EditorForm ed)
            {
                Line("[INFO] Open: paused=" + (!ed.TestLibVlcPlaying) + " playerMs=" + Fmt(ed.TestLibVlcPositionMs)
                    + " playhead=" + Fmt(ed.TestPosition) + " videoVisible=" + ed.TestVideoViewVisible
                    + " vlcChildren=" + ed.TestVideoViewChildControlCount + " playEnabled=" + ed.TestPlayButtonEnabled);
                Check("Open -> first frame, no autoplay (PAUSED)", !ed.TestLibVlcPlaying,
                    "player playing=" + ed.TestLibVlcPlaying);
                Check("Open -> position 0", ed.TestLibVlcPositionMs <= 1500 && ed.TestPosition <= 0.001,
                    "playerMs=" + Fmt(ed.TestLibVlcPositionMs) + " playhead=" + Fmt(ed.TestPosition));
                Check("Open -> VideoView виден и без встроенных VLC controls",
                    ed.TestVideoViewVisible && ed.TestVideoViewChildControlCount == 0,
                    "visible=" + ed.TestVideoViewVisible + " children=" + ed.TestVideoViewChildControlCount);

                Rectangle video = ed.TestVideoViewScreenRect;
                Rectangle play = ed.TestPlayButtonScreenRect;
                Rectangle strip = ed.TestFilmstripScreenRect;
                Line("[INFO] video=" + Rect(video) + "  play=" + Rect(play) + "  filmstrip=" + Rect(strip));
                Check("Play находится ПОД VideoView", play.Top >= video.Bottom - 2,
                    "play.Top=" + play.Top + " video.Bottom=" + video.Bottom);
                Check("Play находится ДО FilmstripTimeline", play.Bottom <= strip.Top + 2,
                    "play.Bottom=" + play.Bottom + " filmstrip.Top=" + strip.Top);
                Check("Play не внутри VideoView", !video.Contains(play),
                    "play=" + Rect(play) + " video=" + Rect(video));
                // The first frame itself is measured by the polling step (case 10),
                // so the reported time is the real time-to-first-frame.
            }

            private void CheckSeekWhilePaused(EditorForm ed)
            {
                double pos = ed.TestPosition;
                Line("[INFO] seek while paused: playhead=" + Fmt(pos) + " playerMs=" + Fmt(ed.TestLibVlcPositionMs)
                    + " playing=" + ed.TestLibVlcPlaying);
                Check("Timeline seek при paused: playhead ~10 c", Math.Abs(pos - 10.0) < 1.2, "playhead=" + Fmt(pos));
                Check("Timeline seek не запускает playback", !ed.TestLibVlcPlaying,
                    "playing=" + ed.TestLibVlcPlaying);
                double nonBlack;
                int distinct;
                long sig = ShotSignature(ed.TestVideoViewScreenRect, out nonBlack, out distinct);
                Check("Кадр изменился после seek (показана новая позиция)", sig != _shotSig,
                    "sigBefore=" + _shotSig + " sigAfter=" + sig + " nonBlack=" + nonBlack.ToString("0.00"));
            }

            private void CheckPlayheadFollows(EditorForm ed)
            {
                double playhead = ed.TestPosition;
                double player = ed.TestLibVlcPositionMs / 1000.0;
                Line("[INFO] playback: playhead=" + Fmt(playhead) + " player=" + Fmt(player) + " c");
                Check("Playhead движется вместе с видео", playhead > 10.5, "playhead=" + Fmt(playhead));
                Check("Playhead синхронизирован с MediaPlayer.Time", Math.Abs(playhead - player) < 1.5,
                    "playhead=" + Fmt(playhead) + " player=" + Fmt(player));
            }

            private void CheckPause(EditorForm ed)
            {
                long posMs = (long)(ed.TestPosition * 1000);
                Line("[INFO] pause: playhead=" + Fmt(ed.TestPosition) + " was " + _posBeforePauseMs + " ms");
                Check("Pause: воспроизведение остановлено, позиция сохранена",
                    !ed.TestLibVlcPlaying && Math.Abs(posMs - _posBeforePauseMs) < 1500,
                    "playing=" + ed.TestLibVlcPlaying + " pos=" + posMs + " before=" + _posBeforePauseMs);
            }

            private void CheckResume(EditorForm ed)
            {
                long posMs = (long)(ed.TestPosition * 1000);
                Line("[INFO] resume: playhead=" + Fmt(ed.TestPosition) + " resumed at " + _resumeFromMs + " ms");
                Check("Resume: продолжение с текущей позиции (не с начала)",
                    ed.TestLibVlcPlaying && posMs >= _resumeFromMs - 500 && posMs > 2000,
                    "playing=" + ed.TestLibVlcPlaying + " posMs=" + posMs + " resumeFrom=" + _resumeFromMs);
            }

            private void CheckSeekWhilePlaying(EditorForm ed)
            {
                double pos = ed.TestPosition;
                Line("[INFO] seek while playing: playhead=" + Fmt(pos) + " playing=" + ed.TestLibVlcPlaying);
                Check("Seek во время playback: позиция перешла на новую", pos > 20.0, "playhead=" + Fmt(pos));
                Check("Seek во время playback: воспроизведение продолжается", ed.TestLibVlcPlaying,
                    "playing=" + ed.TestLibVlcPlaying);
            }

            // A coarse fingerprint of what is really on screen inside the video
            // rectangle plus the share of non-black pixels: evidence that a frame is
            // displayed (and that it changed after a seek) instead of a black area.
            private static long ShotSignature(Rectangle r, out double nonBlack, out int distinct)
            {
                nonBlack = 0;
                distinct = 0;
                if (r.Width < 16 || r.Height < 16) return 0;
                try
                {
                    using (Bitmap bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(r.Width, r.Height));
                        int total = 0;
                        int lit = 0;
                        long sig = 17;
                        HashSet<int> colors = new HashSet<int>();
                        for (int y = 0; y < bmp.Height; y += 6)
                        {
                            for (int x = 0; x < bmp.Width; x += 6)
                            {
                                Color c = bmp.GetPixel(x, y);
                                total++;
                                if (c.R > 24 || c.G > 24 || c.B > 24) lit++;
                                sig = sig * 31 + c.ToArgb();
                                if (colors.Count < 2000) colors.Add(c.ToArgb());
                            }
                        }
                        nonBlack = total > 0 ? (double)lit / total : 0;
                        distinct = colors.Count;
                        return sig;
                    }
                }
                catch { return 0; }
            }

            // A real mouse click on the MainForm Trim/Frame button: the same input a
        // user produces when the first click did nothing. Fire-and-forget: it only
        // posts input, it never blocks the tick in the editor's ShowDialog.
        private void RealClickTrimFrame()
        {
            try
            {
                System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance;
                Button b = (Button)typeof(MainForm).GetField("btnTrimFrame", F).GetValue(_main);
                if (b == null || !b.IsHandleCreated) return;
                Rectangle rect = b.RectangleToScreen(b.ClientRectangle);
                if (rect.Width <= 0 || rect.Height <= 0) return;
                SetForegroundWindow(_main.Handle);
                int x = rect.Left + rect.Width / 2;
                int y = rect.Top + rect.Height / 2;
                SetCursorPos(x, y);
                Thread.Sleep(50);
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(40);
                mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                Line("[INFO] real mouse click on Trim / Frame at " + x + "," + y);
            }
            catch (Exception ex)
            {
                Line("[INFO] Trim/Frame re-click failed: " + ex.Message);
            }
        }

        // A real mouse click in the centre of the Play button, with the editor
            // brought to the foreground first: the same input a user produces, which
            // also proves the button really is where the layout says it is. The click
            // is followed by a pump until the player reaches the expected state; if
            // the window activation ate the very first click (exactly what can
            // happen to a user), the window is activated again and clicked once more.
            private void ClickPlay(string what, bool expectPlaying)
            {
                if (ClickPlayOnce(what, expectPlaying)) return;
                Line("[INFO] the click did not switch the player (" + what + "), re-activating and clicking again");
                ClickPlayOnce(what, expectPlaying);
            }

            private bool ClickPlayOnce(string what, bool expectPlaying)
            {
                EditorForm ed = Editor;
                if (ed == null) return false;
                Rectangle rect = ed.TestPlayButtonScreenRect;
                if (rect.Width <= 0 || rect.Height <= 0) return false;
                try
                {
                    SetForegroundWindow(ed.Handle);
                    ed.Activate();
                    for (int i = 0; i < 5 && GetForegroundWindow() != ed.Handle; i++)
                    {
                        SetForegroundWindow(ed.Handle);
                        Thread.Sleep(40);
                    }
                    int x = rect.Left + rect.Width / 2;
                    int y = rect.Top + rect.Height / 2;
                    SetCursorPos(x, y);
                    Thread.Sleep(60);
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(50);
                    mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                    Line("[INFO] real mouse click on the Play button (" + what + ") at " + x + "," + y);
                }
                catch (Exception ex)
                {
                    Line("[INFO] real click failed: " + ex.Message);
                    return false;
                }
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1800)
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                    EditorForm e = Editor;
                    if (e != null && !e.IsDisposed && e.TestLibVlcPlaying == expectPlaying)
                    {
                        Line("[INFO] player state reached after " + sw.ElapsedMilliseconds + " ms (expected playing="
                            + expectPlaying + ")");
                        return true;
                    }
                    if (sw.ElapsedMilliseconds > 900) break;
                }
                EditorForm cur = Editor;
                if (cur != null && !cur.IsDisposed)
                {
                    Line("[INFO] state at the end of the first attempt: playing=" + cur.TestLibVlcPlaying
                        + " playhead=" + Fmt(cur.TestPosition) + " playerMs=" + Fmt(cur.TestLibVlcPositionMs));
                }
                return false;
            }

            // A REAL drag on the filmstrip: a background thread moves the actual
            // cursor over the strip with mouse_event at hand speed (paceMs apart),
            // pressing at fractionFrom and releasing at fractionTo of the strip
            // width, ping-ponging between the two points `moves` times. The UI
            // thread stays free the whole time, so the WM_MOUSEMOVE stream lands
            // in the message queue exactly like a user's fast drag - including
            // arriving while libvlc is still processing the previous seek.
            private void StartHandDrag(EditorForm ed, double fractionFrom, double fractionTo, int moves, int paceMs)
            {
                Rectangle strip = ed.TestFilmstripScreenRect;
                if (strip.Width < 40 || strip.Height < 8)
                {
                    Line("[FAIL] hand drag: filmstrip rect too small " + Rect(strip));
                    _failures++;
                    _handDone = true;
                    return;
                }
                int y = strip.Top + strip.Height / 2;
                int xa = strip.Left + (int)(strip.Width * fractionFrom);
                int xb = strip.Left + (int)(strip.Width * fractionTo);
                _handDone = false;
                _hand = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        SetForegroundWindow(ed.Handle);
                        Thread.Sleep(120);
                        SetCursorPos(xa, y);
                        Thread.Sleep(60);
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(40);
                        for (int i = 1; i <= moves; i++)
                        {
                            double f = (i % 2 == 0) ? fractionTo : fractionFrom;
                            int x = strip.Left + (int)(strip.Width * f);
                            SetCursorPos(x, y);
                            Thread.Sleep(paceMs);
                        }
                        SetCursorPos(xb, y);
                        Thread.Sleep(40);
                        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                    }
                    catch (Exception ex)
                    {
                        Line("[INFO] hand drag failed: " + ex.Message);
                    }
                    finally
                    {
                        try { mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero); } catch { }
                        _handDone = true;
                    }
                }));
                _hand.IsBackground = true;
                _hand.Start();
            }
        }





    }
}
