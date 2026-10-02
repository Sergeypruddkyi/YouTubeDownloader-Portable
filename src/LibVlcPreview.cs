using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace YouTubeDownloader
{
    // Embedded LibVLC player for the central preview (TASK 2 / UX contract).
    //
    // This is NOT a standalone window and NOT an independent player: it is the
    // VideoView that occupies the same central area the old PictureBox preview
    // used, and it is driven entirely by the editor's one Timeline playhead.
    //
    // Responsibilities:
    //   * one LibVLC lifecycle: Core.Initialize once, one LibVLC instance, one
    //     MediaPlayer, one VideoView, correct Dispose on close;
    //   * open a file -> LibVLC Media -> assign to MediaPlayer -> play;
    //   * seek to a position (used by the playhead) with "latest wins" semantics:
    //     only the most recent requested time is honoured, older requests are
    //     dropped, no queue of stale positions ever plays back one by one;
    //   * Play / Pause that always operate from the CURRENT playhead position;
    //   * a PositionChanged notification so the editor can move the white
    //     playhead while the video plays.
    //
    // The old FrameDecoder / PreviewScrubber pipeline stays in place as the
    // fallback (frame stepping, Save frame, image preview) and is untouched.
    internal sealed class LibVlcPreview : IDisposable
    {
        private readonly VideoView _videoView;
        private LibVLC _libVlc;
        private MediaPlayer _player;
        private string _currentFile;
        // The probed video codec of _currentFile ("av1", "h264", ...) - the open
        // path pins AV1 media to the dav1d decoder, and the dead-state recovery
        // re-opens the SAME file with the SAME decoder choice.
        private string _currentVideoCodec;
        private bool _initialized;
        private bool _disposed;

        // Latest-wins guard: seek requests arrive from the playhead faster than
        // the player can serve them. Only the newest pending time is applied;
        // intermediate ones are dropped so no queue of stale positions exists.
        private long _pendingSeekMs = -1;
        private bool _seekPending;
        // While the reopened/still-opening media has no live input yet, the seek
        // timer defers instead of letting libvlc drop the Time set silently;
        // the deferral is bounded by this deadline (Environment.TickCount ms).
        private long _seekRetryDeadlineMs;
        // Consecutive dead-player recoveries without a live-input seek in between
        // (bounded so a broken media cannot produce a reopen loop).
        private int _deadSeekRetries;
        private readonly System.Windows.Forms.Timer _seekTimer;
        private readonly System.Windows.Forms.Timer _positionTimer;

        // ---- TEMPORARY DIAGNOSTICS (playhead <-> preview sync loss hunt) ----
        // After an applied seek the ACK window waits for the first player event
        // that proves the seek really landed (Time/PositionChanged). If nothing
        // arrives in time, the NOACK watchdog logs the full player snapshot -
        // that line is the evidence of a dropped seek, with the state it was
        // dropped in. _ackAwait is written by the UI thread and the libvlc
        // event thread, hence volatile.
        private volatile bool _ackAwait;
        private long _ackTargetMs;
        private long _ackAppliedAtMs;
        private float _ackTargetFrac;
        private readonly System.Windows.Forms.Timer _ackTimer;

        // Raised (on the UI thread) while playing to keep the white playhead and
        // the status clock in step with the actual playback position.
        public event Action<double> PlayingPositionChanged;

        // Raised once when playback reaches the end of the media.
        public event Action EndReached;

        public bool IsInitialized
        {
            get { return _initialized; }
        }

        public bool HasMedia
        {
            get { return _player != null && _player.Media != null && !string.IsNullOrEmpty(_currentFile); }
        }

        public bool IsPlaying
        {
            get { return _player != null && _player.IsPlaying; }
        }

        public string CurrentFile
        {
            get { return _currentFile; }
        }

        // Current playback time in milliseconds (read-only diagnostic).
        public long PositionMs
        {
            get { return _player != null ? _player.Time : 0; }
        }

        // Live player state name (read-only diagnostic / acceptance evidence).
        public string StateText
        {
            get
            {
                try { return _player != null ? _player.State.ToString() : "no-player"; }
                catch { return "unknown"; }
            }
        }

        // TEMPORARY DIAGNOSTICS: live video outputs and input seekability, the two
        // facts that decide whether a seek CAN produce a new picture at all.
        public int VoutCount
        {
            get { try { return _player != null ? (int)_player.VoutCount : -1; } catch { return -2; } }
        }

        public int SeekableState
        {
            get { try { return _player != null && _player.IsSeekable ? 1 : 0; } catch { return -1; } }
        }

        // ------------------------------------------------------------------
        // Process-wide engine.
        //
        // Only the ENGINE (libvlc.dll / libvlccore.dll + the plugin-tree walk done
        // by libvlc_new) is shared and long-lived: that is the expensive, disk- and
        // AV-sensitive part, it is not bound to any window, and re-creating it for
        // every editor window used to make each open pay for it again. A
        // MediaPlayer and a VideoView stay per-editor (see Dispose).
        // ------------------------------------------------------------------
        private static readonly object EngineSync = new object();
        private static LibVLC _sharedEngine;
        // 0 = not tried yet, 1 = ready, -1 = failed for good.
        private static int _engineState;

        // Options of the embedded player: display-only (TASK 2.4) and no autoplay
        // surprises. ":start-paused" (per media) delivers the opening pause to the
        // decoder BEFORE the vout exists, so the vout never becomes paused and
        // would drop every paused-seek picture as "too late" (frozen input clock)
        // -> timeline moves, frame does not.
        private static readonly string[] EngineOptions =
        {
            "--no-video-title-show",
            "--no-mouse-events",
            "--no-keyboard-events",
            "--no-drop-late-frames"
        };

        // Prepares the shared engine. Safe to call from any thread, any number of
        // times; blocks only while another caller is bringing it up. Returns false
        // when libvlc is unavailable, and never throws: callers fall back to the
        // FFmpeg preview exactly as they do for a failed Initialize().
        public static bool EnsureEngine()
        {
            lock (EngineSync)
            {
                if (_engineState == 1) return _sharedEngine != null;
                if (_engineState == -1) return false;
                try
                {
                    long t0 = EditTiming.NowMs;
                    Core.Initialize();
                    EditTiming.Mark("LibVlcPreview: Core.Initialize", EditTiming.NowMs - t0);
                    long t1 = EditTiming.NowMs;
                    _sharedEngine = new LibVLC(EngineOptions);
                    EditTiming.Mark("LibVlcPreview: new LibVLC()", EditTiming.NowMs - t1);
                    _engineState = 1;
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("LibVlcPreview engine init failed: " + ex);
                    _sharedEngine = null;
                    _engineState = -1;
                    return false;
                }
            }
        }

        // True once the shared engine is usable (diagnostic / warm-up report).
        public static bool EngineReady
        {
            get { lock (EngineSync) { return _engineState == 1; } }
        }

        public LibVlcPreview(VideoView videoView)
        {
            _videoView = videoView ?? throw new ArgumentNullException(nameof(videoView));

            // The seek timer applies the newest pending seek shortly after the
            // playhead settles. A trailing timer (not a debounce of every move)
            // guarantees the FINAL position always lands, and a short interval
            // keeps intermediate scrubbing responsive without a stale queue.
            _seekTimer = new System.Windows.Forms.Timer();
            _seekTimer.Interval = 16;
            _seekTimer.Tick += OnSeekTimerTick;

            _positionTimer = new System.Windows.Forms.Timer();
            _positionTimer.Interval = 40;
            _positionTimer.Tick += OnPositionTimerTick;

            _ackTimer = new System.Windows.Forms.Timer();
            _ackTimer.Interval = 250;
            _ackTimer.Tick += OnAckTimerTick;
        }

        // One-time LibVLC + MediaPlayer + VideoView wiring. Safe to call more
        // than once (no-op after the first successful init).
        public void Initialize()
        {
            if (_initialized || _disposed) return;
            try
            {
                // The engine (Core.Initialize + libvlc_new = the plugin-tree walk)
                // is process-wide and prepared once: by the start-up warm-up, or
                // right here when this is the first editor of the process.
                if (!EnsureEngine()) return;
                _libVlc = _sharedEngine;
                // VideoView must be display-only (TASK 2.4): no native libvlc
                // controls (Play / Full / Ratio / fullscreen context menu), no
                // VLC transport or seek bar, no keyboard shortcuts. The editor's
                // own Timeline playhead and bottom Play button are the ONLY
                // controls, so libvlc's built-in mouse/keyboard interaction and
                // its right-click context menu are switched off.
                _player = new MediaPlayer(_libVlc);
                _videoView.MediaPlayer = _player;
                EditTiming.Mark("LibVlcPreview: MediaPlayer + VideoView wired");

                // While playing, the player is the source of the actual current
                // time, so the editor can keep the white playhead in step.
                _player.PositionChanged += OnPlayerPositionChanged;
                _player.EndReached += OnPlayerEndReached;
                _player.EncounteredError += OnPlayerError;

                // TEMPORARY DIAGNOSTICS: transport-state + seekability trail. These
                // handlers run on a libvlc thread and only log their own arguments -
                // they never call back into the player from inside an event.
                _player.Playing += delegate { VlcDiag.Write(VlcDiag.NextSeq() + " EV Playing"); };
                _player.Paused += delegate { VlcDiag.Write(VlcDiag.NextSeq() + " EV Paused"); };
                _player.Stopped += delegate { VlcDiag.Write(VlcDiag.NextSeq() + " EV Stopped"); };
                _player.EndReached += delegate { VlcDiag.Write(VlcDiag.NextSeq() + " EV EndReached"); };
                _player.EncounteredError += delegate { VlcDiag.Write(VlcDiag.NextSeq() + " EV Error"); };
                _player.SeekableChanged += delegate (object s, MediaPlayerSeekableChangedEventArgs e)
                {
                    VlcDiag.Write(VlcDiag.NextSeq() + " EV Seekable=" + e.Seekable);
                };
                _player.Buffering += delegate (object s, MediaPlayerBufferingEventArgs e)
                {
                    VlcDiag.Write(VlcDiag.NextSeq() + " EV Buffering " + e.Cache.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
                };
                _player.TimeChanged += delegate (object s, MediaPlayerTimeChangedEventArgs e)
                {
                    if (_ackAwait && Math.Abs(e.Time - _ackTargetMs) < 700)
                    {
                        VlcDiag.Write(VlcDiag.NextSeq() + " ACK-Time " + e.Time + " target=" + _ackTargetMs);
                        DisarmAckWindow();
                    }
                };
                EditTiming.Mark("LibVlcPreview: MediaPlayer + VideoView wired");

                _initialized = true;
                EditTiming.Mark("LibVlcPreview: initialized");
            }
            catch (Exception ex)
            {
                // Leave _initialized false so the caller can fall back to the
                // existing FFmpeg preview instead of crashing the editor.
                Debug.WriteLine("LibVlcPreview init failed: " + ex);
                _initialized = false;
            }
        }

        // Opens a video file and shows its FIRST FRAME while staying PAUSED
        // (no autoplay, TASK 2.4). The user chooses when to press Play.
        // Replaces any previous media in the same MediaPlayer (reopen-safe).
        public bool OpenFile(string path, double startPositionSec, string videoCodec = null)
        {
            if (!_initialized || _disposed) return false;
            VlcDiag.Write(VlcDiag.NextSeq() + " OPEN " + Path.GetFileName(path) + " start=" + startPositionSec.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                + " codec=" + (videoCodec ?? "?"));
            try
            {
                long tStop = EditTiming.NowMs;
                Stop();
                EditTiming.Mark("LibVlcPreview.OpenFile: Stop", EditTiming.NowMs - tStop);

                long tMedia = EditTiming.NowMs;
                var media = new Media(_libVlc, path, FromType.FromPath);
                // Start the media ALREADY PAUSED: libvlc decodes and shows the first
                // frame but does not advance playback. This is the reliable way to
                // present the opening frame with no autoplay (a Play()/Pause() pair
                // can race and leave the media running).
                media.AddOption(":start-paused");
                // libvlc's avcodec AV1 path cannot serve more than ONE paused seek:
                // the first one displays, after that the input clock refuses the
                // flushed pictures ("Timestamp conversion failed ... Could not
                // convert timestamp ... for FFmpeg" in the vlc log) and every
                // following paused seek moves Time without ever showing a frame -
                // only Play() re-anchors the clock. The dedicated dav1d decoder has
                // no such defect (proven by the LibVlcSpike.SeekStorm probe), so
                // AV1 media is pinned to it. The probe report and the acceptance
                // runs are the evidence; H.264 keeps its stock path untouched.
                if (string.Equals(videoCodec, "av1", StringComparison.OrdinalIgnoreCase))
                    media.AddOption(":codec=dav1d");
                EditTiming.Mark("LibVlcPreview.OpenFile: new Media", EditTiming.NowMs - tMedia);

                long tSet = EditTiming.NowMs;
                _player.Media = media;
                EditTiming.Mark("LibVlcPreview.OpenFile: MediaPlayer.Media = media", EditTiming.NowMs - tSet);
                _currentFile = path;
                _currentVideoCodec = videoCodec;

                long tPlay = EditTiming.NowMs;
                _player.Play();
                EditTiming.Mark("LibVlcPreview.OpenFile: Play()", EditTiming.NowMs - tPlay);

                // ":start-paused" guarantees no autoplay, but it also makes libvlc
                // decode NOTHING on its own: the vout never gets a picture, so the
                // VideoView stays black instead of showing the opening frame. One
                // seek to the start position forces the decoder to produce and
                // display that first frame while the player stays PAUSED right
                // there. It is applied immediately (the input thread exists as soon
                // as Play() returns) and again through the latest-wins timer, so a
                // playhead move made right after opening still wins over it.
                long startMs = startPositionSec <= 0 ? 0 : (long)(startPositionSec * 1000.0);
                _player.Time = startMs;
                Seek(startPositionSec);
                _positionTimer.Stop();
                VlcDiag.Write(VlcDiag.NextSeq() + " OPEN done startMs=" + startMs + " st=" + _player.State + " vout=" + _player.VoutCount);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("LibVlcPreview open failed: " + ex);
                _currentFile = null;
                return false;
            }
        }

        // Plays from the CURRENT position (never restarts from the beginning).
        // If a playhead position is supplied it is seeked to first, but only when
        // the player is not already there: a Time set immediately followed by
        // Play() can race, so the redundant seek of an already-seeked player (the
        // normal case, because the playhead already drove the player) is skipped.
        public void Play(double? positionSec = null)
        {
            if (!_initialized || _disposed || _player == null) return;
            if (positionSec.HasValue)
            {
                long target = (long)(positionSec.Value * 1000.0);
                if (target < 0) target = 0;
                long current = _player.Time;
                if (Math.Abs(current - target) > 250) _player.Time = target;
            }
            _player.Play();
            _positionTimer.Start();
        }

        // Pauses at the current position; the playhead stays where it is.
        public void Pause()
        {
            if (!_initialized || _disposed || _player == null) return;
            _player.Pause();
        }

        // Toggle: returns true if now playing, false if paused/stopped.
        public bool TogglePlayPause(double? positionSec = null)
        {
            if (_player == null || !_initialized || _disposed) return false;
            if (_player.IsPlaying)
            {
                _player.Pause();
                return false;
            }
            Play(positionSec);
            return true;
        }

        // Latest-wins seek: records the newest requested time, drops any earlier
        // pending one, and lets the timer apply only the final value.
        public void Seek(double positionSec)
        {
            if (!_initialized || _disposed || _player == null) return;
            long newPending = (long)(positionSec * 1000.0);
            if (newPending < 0) newPending = 0;
            // TEMPORARY DIAGNOSTICS: every request with a player snapshot taken
            // BEFORE the pending value is replaced (drop= shows which request
            // latest-wins is discarding).
            try
            {
                VlcDiag.Write(VlcDiag.NextSeq() + " REQ t=" + newPending + " drop=" + (_seekPending ? _pendingSeekMs.ToString() : "none")
                    + " st=" + _player.State + " time=" + _player.Time + " vout=" + _player.VoutCount
                    + " skbl=" + (_player.IsSeekable ? 1 : 0));
            }
            catch { }
            _pendingSeekMs = newPending;
            _seekPending = true;
            _seekRetryDeadlineMs = Environment.TickCount + 4000;
            _seekTimer.Stop();
            _seekTimer.Start();
        }

        private void OnSeekTimerTick(object sender, EventArgs e)
        {
            _seekTimer.Stop();
            if (!_seekPending || _player == null) return;
            try
            {
                // After the media ran to its end (EndReached -> Ended) or after an
                // explicit Stop() (Stopped) the input thread and the vout are GONE:
                // a Time set is then either dropped entirely (Stopped) or only
                // updates the reported value while no new frame is ever decoded
                // (Ended), so the picture stops following the playhead for good.
                // Bring the same file back through the normal open path, which
                // restarts the player PAUSED at the requested frame (:start-paused,
                // no autoplay) and re-arms the pending seek so it lands on the live
                // input created by that open.
                if (PlayerIsDead() && !string.IsNullOrEmpty(_currentFile))
                {
                    if (_deadSeekRetries < 3)
                    {
                        _deadSeekRetries++;
                        VlcDiag.Write(VlcDiag.NextSeq() + " DEAD-RECOVERY #" + _deadSeekRetries + " target=" + _pendingSeekMs + " st=" + _player.State);
                        long deadTarget = _pendingSeekMs;
                        _seekPending = false;
                        _pendingSeekMs = -1;
                        OpenFile(_currentFile, deadTarget / 1000.0, _currentVideoCodec);
                        return;
                    }
                    // Bounded: after three failed recoveries stop re-opening and
                    // fall through, so a broken media cannot turn into a 16 ms
                    // reopen loop. The counter resets on the first live-input seek.
                    VlcDiag.Write(VlcDiag.NextSeq() + " DEAD-RECOVERY EXHAUSTED target=" + _pendingSeekMs + " st=" + _player.State);
                }
                else
                {
                    _deadSeekRetries = 0;
                }
                // libvlc creates the input thread ASYNCHRONOUSLY after Play(): while
                // the state is still NothingSpecial/Opening/Buffering a Time set is
                // dropped SILENTLY, and the pending seek would be lost forever - the
                // playhead would show the target while the player stays where it
                // was. So the pending seek is NOT consumed while the input is not
                // live yet: the timer re-arms and retries until the input accepts
                // it or the deadline passes (bounded, so a media that never opens
                // cannot spin the timer forever).
                VLCState st = _player.State;
                if (st != VLCState.Playing && st != VLCState.Paused
                    && Environment.TickCount < _seekRetryDeadlineMs)
                {
                    _seekTimer.Start();
                    return;
                }
                long target = _pendingSeekMs;
                _seekPending = false;
                _pendingSeekMs = -1;
                long before = _player.Time;
                _player.Time = target;
                long after = _player.Time;
                VlcDiag.Write(VlcDiag.NextSeq() + " APPLY t=" + target + " timeBefore=" + before + " timeAfter=" + after
                    + " st=" + _player.State + " vout=" + _player.VoutCount + " skbl=" + (_player.IsSeekable ? 1 : 0));
                ArmAckWindow(target);
            }
            catch (Exception ex)
            {
                _seekPending = false;
                _pendingSeekMs = -1;
                VlcDiag.Write(VlcDiag.NextSeq() + " APPLY-EXCEPTION t=" + _pendingSeekMs + " " + ex.Message);
                Debug.WriteLine("LibVlcPreview seek failed: " + ex);
            }
        }

        // TEMPORARY DIAGNOSTICS: after an applied seek, wait for the first player
        // event that proves the seek landed (TimeChanged / PositionChanged). The
        // watchdog fires when NO such event arrived in time - the fingerprint of
        // the playhead <-> preview sync loss.
        private void ArmAckWindow(long targetMs)
        {
            _ackTargetMs = targetMs;
            try
            {
                long len = _player.Length;
                _ackTargetFrac = len > 0 ? (float)((double)targetMs / len) : 0f;
            }
            catch { _ackTargetFrac = 0f; }
            _ackAppliedAtMs = Environment.TickCount;
            _ackAwait = true;
            _ackTimer.Start();
        }

        // Only flips the flag: it is also called from the libvlc event thread,
        // where touching the WinForms timer is not allowed. The timer tick stops
        // itself on the first tick after the flag went down.
        private void DisarmAckWindow()
        {
            _ackAwait = false;
        }

        private void OnAckTimerTick(object sender, EventArgs e)
        {
            if (!_ackAwait) { _ackTimer.Stop(); return; }
            if (Environment.TickCount - _ackAppliedAtMs < 2500) return;
            _ackAwait = false;
            _ackTimer.Stop();
            try
            {
                VlcDiag.Write(VlcDiag.NextSeq() + " NOACK t=" + _ackTargetMs + " waitedMs=" + (Environment.TickCount - _ackAppliedAtMs)
                    + " st=" + _player.State + " time=" + _player.Time + " vout=" + _player.VoutCount
                    + " skbl=" + (_player.IsSeekable ? 1 : 0));
            }
            catch { }
        }

        // True while the player holds no live input (media ended or was stopped):
        // seeking in these states can move the reported Time but can never
        // produce a new picture.
        private bool PlayerIsDead()
        {
            try
            {
                VLCState s = _player.State;
                return s == VLCState.Stopped || s == VLCState.Ended;
            }
            catch { return false; }
        }

        // Stops playback and releases the current Media. The player and LibVLC
        // instances survive so reopening does not rebuild them.
        public void Stop()
        {
            if (_player == null) return;
            try { VlcDiag.Write(VlcDiag.NextSeq() + " STOP st=" + _player.State); } catch { }
            try { _player.Stop(); }
            catch { }
            _seekTimer.Stop();
            _seekPending = false;
            _ackTimer.Stop();
            DisarmAckWindow();
        }

        private void OnPlayerPositionChanged(object sender, MediaPlayerPositionChangedEventArgs e)
        {
            // Called on a libvlc thread; forward to the UI thread. While the ACK
            // window is open this event is the proof the applied seek really
            // landed: log it with its fraction so the decode position can be
            // compared with the requested one without touching the player from
            // inside the libvlc event.
            if (_ackAwait)
            {
                VlcDiag.Write(VlcDiag.NextSeq() + " ACK-Pos pos=" + e.Position.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
                    + " targetFrac=" + _ackTargetFrac.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
                DisarmAckWindow();
            }
            // Called on a libvlc thread; forward to the UI thread.
            if (PlayingPositionChanged == null || _disposed) return;
            var h = _videoView.IsHandleCreated ? _videoView : null;
            try
            {
                if (h != null && h.IsHandleCreated && !h.IsDisposed)
                {
                    h.BeginInvoke((MethodInvoker)delegate
                    {
                        if (PlayingPositionChanged != null && _player != null && _player.Length > 0)
                            PlayingPositionChanged(e.Position * _player.Length / 1000.0);
                    });
                }
            }
            catch { }
        }

        private void OnPlayerEndReached(object sender, EventArgs e)
        {
            if (EndReached == null || _disposed) return;
            try
            {
                if (_videoView.IsHandleCreated && !_videoView.IsDisposed)
                {
                    _videoView.BeginInvoke((MethodInvoker)delegate
                    {
                        if (EndReached != null) EndReached();
                    });
                }
            }
            catch { }
        }

        private void OnPlayerError(object sender, EventArgs e)
        {
            // Report through the same channel; the editor decides what to show.
            try
            {
                if (_videoView.IsHandleCreated && !_videoView.IsDisposed)
                {
                    _videoView.BeginInvoke((MethodInvoker)delegate
                    {
                        if (EndReached != null) EndReached();
                    });
                }
            }
            catch { }
        }

        private void OnPositionTimerTick(object sender, EventArgs e)
        {
            // Fallback source of the current time while playing, for editors that
            // did not subscribe to PositionChanged or when the position event is
            // not reliable.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _seekTimer.Stop();
            _seekTimer.Dispose();
            _positionTimer.Stop();
            _positionTimer.Dispose();
            _ackTimer.Stop();
            DisarmAckWindow();
            try { _ackTimer.Dispose(); } catch { }
            try { _videoView.MediaPlayer = null; } catch { }
            try { _player?.Stop(); } catch { }
            try { _player?.Dispose(); } catch { }
            // The LibVLC engine is IMPORTED from the process-wide instance
            // (EnsureEngine) and outlives this editor, so it is deliberately NOT
            // disposed here: reopening the editor must not pay for another
            // libvlc_new / plugin-tree walk.
            _player = null;
            _libVlc = null;
        }
    }
}
