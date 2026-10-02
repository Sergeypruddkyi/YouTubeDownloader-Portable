using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    // Time-continuous filmstrip timeline:
    // X = (t - viewStart) * pxPerSec, T = viewStart + x / pxPerSec.
    // No cells, no slots, no fixed thumb width as a time step.
    internal sealed class TimelineClip
    {
        public double Start;             // seconds on the timeline
        public double Duration;          // seconds
        public string Caption = "";

        public double End
        {
            get { return Start + Duration; }
        }
    }

    internal sealed class FilmstripView
    {
        public double ViewStart;         // time at viewport x = 0
        public double PxPerSec = 1;
        public int ViewportWidth;

        public double ViewSpan
        {
            get { return PxPerSec > 0 ? (double)ViewportWidth / PxPerSec : 0; }
        }

        public double ViewEnd
        {
            get { return ViewStart + ViewSpan; }
        }

        public int XAt(double t)
        {
            return (int)Math.Round((t - ViewStart) * PxPerSec);
        }

        public double TimeAt(int x)
        {
            if (PxPerSec <= 0) return ViewStart;
            return ViewStart + (double)x / PxPerSec;
        }
    }
    // The ONE filmstrip timeline of the editor: a horizontal viewport over a clip.
    //
    // Geometry is time-continuous:
    //
    //     x = (time - viewStart) * pxPerSec          // clip, frames, playhead
    //
    // Painting never waits for anything: it only reads ready Bitmaps from the
    // store (memory). All decoding lives in ThumbService, off the UI thread.
    public sealed class FilmstripTimeline : Control
    {
        // Height of the filmstrip content on the track.
        public const int ThumbHeight = 44;

        private const int PlayheadWidth = 2;
        private const int RulerH = 26;
        private const int HeaderH = 20;
        private const double MaxPxPerSec = 200.0;
        // Experimental zoom ceiling (test limit): the ruler must not go finer
        // than ~30 s major labels. DrawRuler picks the first step with
        // step*pxPerSec >= 110, so 15 s labels appear from 110/15 = 7.33 px/s
        // upward - staying under that keeps 30 s as the finest major step.
        private const double MaxZoomPxPerSec = 7.0;
        // The opening view shows the WHOLE clip 00:00 .. end at once, drawn as
        // one compact clip on the left that takes a fixed share of the track;
        // the rest of the track to the right of the clip's end stays empty.
        // The scale therefore follows the duration: PxPerSec = Width * share /
        // Duration - chosen ONCE at open, not a mode and not "fit".
        private const double InitialFillFraction = 0.55;
        // Absolute floor only while duration/width are still unknown. Once the
        // clip is open the real minimum is the opening scale (see
        // MinPixelsPerSecond): Ctrl+wheel must not go coarser than that.
        private const double MinPxPerSec = 0.01;
        // One notch of Ctrl+wheel. A fixed RATIO (not a fixed number of
        // seconds) keeps the gesture identical at every scale.
        private const double ZoomStep = 1.6;
        private const int ViewportDebounceMs = 60;
        private const int RepaintCoalesceMs = 40;
        // Start and End of a range are placed at the white playhead, never dragged, so
        // there are no handles to grab. The only rule left is that a committed range
        // may not collapse to zero: its two ends must be at least two pixels apart.
        private const double MinRangeGapPx = 2.0;

        private static readonly Color ClipTeal = Color.FromArgb(42, 193, 193);
        private static readonly Color PlayheadColor = Color.FromArgb(240, 240, 240);
        private static readonly Color SurfaceMaterial = Color.FromArgb(46, 48, 52);
        private static readonly Color HeaderText = Color.FromArgb(15, 30, 32);
        // The selected range: translucent, so the frames under it stay readable.
        private static readonly Color RangeFill = Color.FromArgb(70, Theme.Orange);
        private static readonly Color RangeEdge = Theme.Orange;
        // An applied range (marked for deletion): red and translucent, so the frames
        // under the overlay stay readable and the mark cannot be mistaken for a handle.
        private static readonly Color MarkFill = Color.FromArgb(85, 226, 66, 60);
        private static readonly Color MarkEdge = Color.FromArgb(226, 66, 60);

        private readonly TimelineClip _clip = new TimelineClip();
        private readonly FilmstripView _view = new FilmstripView();
        private double _position;
        private bool _viewInitialized;
        // True while the view still sits at the scale the editor opened with.
        private bool _atInitial;

        // The one prepared filmstrip of the clip (see FilmstripPrep): null until
        // the whole strip is ready - the UI never shows a half-built strip.
        private FilmstripPrep.Prepared _strip;

        private bool _scrubbing;
        private bool _panning;
        private int _panLastX;

        // Last non-zero direction of travel; drives which side gets prefetched.
        private int _direction;
        private double _lastViewStart;

        // Playhead-driven range building. The user never drags a handle: the white
        // playhead is the only pointer. Pressing "start" arms a pending range whose
        // start is the current playhead; the playhead then moves freely, and pressing
        // "end" commits [pendingStart .. playhead] as a red mark. Until a start is
        // armed there is no yellow range on the strip at all.
        //
        // _pendingActive == true means a start has been placed and the range is still
        // being built; _pendingStart holds that start time. The live end always
        // follows the current playhead, so nothing has to be dragged.
        private double _pendingStart;
        private bool _pendingActive;

        // Ranges already confirmed as "to be removed", in clip time. This iteration
        // keeps them in memory only: nothing is encoded, written or cut.
        private readonly List<MarkRange> _marks = new List<MarkRange>();

        private sealed class MarkRange
        {
            public readonly double Start;
            public readonly double End;

            public MarkRange(double start, double end)
            {
                Start = start;
                End = end;
            }
        }

        private bool _dirty;
        private readonly Timer _repaintTimer;
        private readonly Timer _viewportTimer;

        // What a Cut press actually did, so the editor can report it exactly instead
        // of guessing from a bool. A red range is a no-cut zone: the playhead has to be
        // in free space to start a Cut, and the finished span must not share any length
        // with an already committed red range.
        public enum MarkResult
        {
            StartPlaced,        // the playhead was free: a Start was armed there
            Committed,          // the span was completed and became a new red range
            RejectedInMark,     // the playhead sits on a red range: no Start was placed
            RejectedOverlap,    // the span crosses a red range: no range was added
            RejectedTooShort    // the span is shorter than the minimum: no range was added
        }

        // What a Cancel press actually did. Cancel is the undo of the last marking
        // action, taken in the order the marks were made:
        //   * while a range is being built, it drops that pending range;
        //   * otherwise it removes the LAST committed red range;
        //   * one press removes at most one thing - it never clears everything.
        public enum CancelResult
        {
            Nothing,        // nothing was pending and no red range existed
            PendingDropped, // the range being built was dropped
            RangeRemoved    // the newest committed red range was removed
        }

        public event Action PositionChanged;

        // Raised whenever the VIEW scale or the visible window changes (zoom, scroll,
        // resize). Purely a view notification: the clip, the ranges and the playhead
        // are not affected by it.
        public event Action ViewChanged;

        public FilmstripTimeline()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Input;
            Cursor = Cursors.Hand;

            _clip.Start = 0;
            _clip.Duration = 1;

            _repaintTimer = new Timer();
            _repaintTimer.Interval = RepaintCoalesceMs;
            _repaintTimer.Tick += OnRepaintTick;

            _viewportTimer = new Timer();
            _viewportTimer.Interval = ViewportDebounceMs;
            _viewportTimer.Tick += OnViewportTick;
        }

        // Clip header text ("file name + duration"), drawn like a clip block label.
        public string Caption
        {
            get { return _clip.Caption; }
            set
            {
                string v = value ?? "";
                if (v == _clip.Caption) return;
                _clip.Caption = v;
                Invalidate();
            }
        }

        public double Duration
        {
            get { return _clip.Duration; }
            set
            {
                double v = value;
                if (v <= 0) v = 1;
                _clip.Duration = v;
                if (_position > v) _position = v;
                // Applied red ranges belong to the media that was open, so opening
                // another file starts unmarked and with no range being built.
                _marks.Clear();
                _pendingActive = false;
                ResetView();
                Invalidate();
            }
        }

        public double Position
        {
            get { return _position; }
            set
            {
                double t = ClampTime(value);
                if (Math.Abs(t - _position) < 1e-7) return;
                _position = t;
                Invalidate();
                if (PositionChanged != null) PositionChanged();
            }
        }

        public bool IsScrubbing
        {
            get { return _scrubbing; }
        }

        // ------------------------------------------------------------- range
        // A range is built from the white playhead, not from draggable handles.
        // "HasRange" now means "a start is placed and the range is still being built".
        // RangeStart is that placed start; RangeEnd is the live end, which is simply
        // wherever the playhead is right now. These properties keep their names so the
        // editor and its tests read the same state, only its meaning is playhead-driven.

        public bool HasRange { get { return _pendingActive; } }
        public double RangeStart { get { return _pendingActive ? _pendingStart : _position; } }
        public double RangeEnd { get { return _position; } }

        // Places the start of a new range at the given time (the caller passes the
        // playhead). From here the playhead moves freely and the range end follows it
        // until CommitMarkAt fixes the end.
        public void BeginMarkAt(double start)
        {
            _pendingStart = ClampTime(start);
            _pendingActive = true;
            Invalidate();
        }

        // True when the given time lies on an already committed red range. The red
        // range counts with its edges, so a start exactly on a boundary is refused as
        // well: the strip never guesses whether the user meant "just before" or "just
        // after", it just keeps the no-cut zone intact.
        public bool IsInMark(double t)
        {
            double tt = ClampTime(t);
            for (int i = 0; i < _marks.Count; i++)
            {
                if (tt >= _marks[i].Start - TimeEps && tt <= _marks[i].End + TimeEps) return true;
            }
            return false;
        }

        // True when [start .. end] would share any real length with a committed red
        // range. Ranges that merely touch at an endpoint do not count: they have no
        // common part, so they never overlap on the strip.
        public bool WouldOverlapMarks(double start, double end)
        {
            double a = ClampTime(start);
            double b = ClampTime(end);
            if (b < a) { double swap = a; a = b; b = swap; }
            for (int i = 0; i < _marks.Count; i++)
            {
                double s = _marks[i].Start;
                double e = _marks[i].End;
                if (a < e - TimeEps && s < b - TimeEps) return true;
            }
            return false;
        }

        // Fixes the end of the range being built at the given time (the playhead) and
        // turns [start .. end] into a red mark. The red-range guards live here as well
        // as in ApplyCut, so the commit stage can never create an overlapping range,
        // no matter which entry point the caller uses.
        public MarkResult CommitMarkAt(double end)
        {
            if (!_pendingActive) return MarkResult.RejectedInMark;
            double a = _pendingStart;
            double b = ClampTime(end);
            if (b < a) { double swap = a; a = b; b = swap; }
            // Too short to be a real cut: keep the armed Start, add nothing.
            if (b - a < RangeGapSeconds()) return MarkResult.RejectedTooShort;
            // The finished span would eat into a red range: add nothing, change no
            // existing range and do not silently trim the span to fit.
            if (WouldOverlapMarks(a, b)) return MarkResult.RejectedOverlap;
            _marks.Add(new MarkRange(a, b));
            _pendingActive = false;
            Invalidate();
            return MarkResult.Committed;
        }

        // Drops the range currently being built. Red marks already committed stay.
        public void CancelMark()
        {
            if (!_pendingActive) return;
            _pendingActive = false;
            Invalidate();
        }

        // True while there is anything Cancel can undo: a range being built, or at
        // least one committed red range. This is what the Cancel button's enabled
        // state follows, so a button that cannot do anything is visibly off.
        public bool HasUndoable
        {
            get { return _pendingActive || _marks.Count > 0; }
        }

        // Cancel / Undo of the last marking action. Exactly one thing is removed per
        // press, in the order the user created them:
        //   * a range being built (the pending Start) goes first, and the committed
        //     red ranges are not touched at all;
        //   * with nothing pending, the NEWEST red range is removed - one press per
        //     range, so the earlier ones survive and can be undone one by one;
        //   * with nothing left the call is a no-op and reports Nothing.
        // Nothing here touches the media, the times of the remaining ranges or the
        // playhead; the strip only drops a range from its in-memory list.
        public CancelResult CancelOrUndo()
        {
            if (_pendingActive)
            {
                _pendingActive = false;
                Invalidate();
                return CancelResult.PendingDropped;
            }
            if (_marks.Count > 0)
            {
                _marks.RemoveAt(_marks.Count - 1);
                Invalidate();
                return CancelResult.RangeRemoved;
            }
            return CancelResult.Nothing;
        }

        // ------------------------------------------------- marked for deletion
        // Cut confirms the range being built, Cancel drops it. Both only move state in
        // memory - nothing is encoded, written or cut - and neither moves the playhead.

        public int MarkedCount
        {
            get { return _marks.Count; }
        }

        // Total length of what is currently marked for deletion.
        public double MarkedSeconds
        {
            get
            {
                double total = 0;
                for (int i = 0; i < _marks.Count; i++) total += _marks[i].End - _marks[i].Start;
                return total;
            }
        }

        public double MarkedStart(int index)
        {
            return index >= 0 && index < _marks.Count ? _marks[index].Start : double.NaN;
        }

        public double MarkedEnd(int index)
        {
            return index >= 0 && index < _marks.Count ? _marks[index].End : double.NaN;
        }

        // The single "mark point" action, driven entirely by the white playhead:
        //   * first press  -> place the range start at the playhead (arm the range);
        //   * second press -> fix the range end at the playhead and commit the red mark.
        // A committed red range is a no-cut zone, so a Cut is only possible in the free
        // parts of the strip:
        //   * a Start is refused while the playhead sits on a red range;
        //   * a span that would cross or touch a red range is refused at the End press.
        // In both cases nothing is created and no existing red range is changed: the
        // user is told, and can move the playhead and press again.
        public MarkResult ApplyCut()
        {
            if (!_pendingActive)
            {
                // Start press: the playhead must be in free space. On a red range the
                // Start is not placed and no pending Cut is started.
                if (IsInMark(_position)) return MarkResult.RejectedInMark;
                BeginMarkAt(_position);
                return MarkResult.StartPlaced;
            }
            return CommitMarkAt(_position);
        }

        // Cancel drops the range currently being built. Committed red ranges stay.
        // Superseded by CancelOrUndo, which keeps this behaviour for the pending case
        // and adds the "remove the newest red range" step on top of it.
        public void CancelRange()
        {
            CancelOrUndo();
        }

        // Test hook: the state a freshly opened media starts in - no committed marks
        // and no range being built (only the white playhead is present).
        internal void TestResetRanges()
        {
            _marks.Clear();
            _pendingActive = false;
            Invalidate();
        }

        // Diagnostics (used by verification probes / reports).
        public double ViewStart
        {
            get { return _view.ViewStart; }
        }

        public double ViewEnd
        {
            get { return _view.ViewEnd; }
        }

        public double PixelsPerSecond
        {
            get { return _view.PxPerSec; }
        }

        // Frames of the prepared strip; 0 while nothing is prepared yet.
        public int StoreCount
        {
            get { return _strip != null ? _strip.Count : 0; }
        }

        // The store must be attached AFTER Duration, so the view is already sized.
        public void AttachService(ThumbService service)
        {
            // The old per-cell service is gone; the timeline now shows exactly
            // the one prepared strip handed to it via SetPreparedStrip.
        }

        // Hands the timeline its finished filmstrip. Passing null returns the
        // track to the neutral "preparing" state; passing a strip makes the
        // whole clip appear at once - the only two states the user ever sees.
        internal void SetPreparedStrip(FilmstripPrep.Prepared strip)
        {
            _strip = strip;
            Invalidate();
        }

        // ---------------------------------------------------------------- view

        private void SyncSurface()
        {
            _view.ViewportWidth = Width;
        }

        private void ResetView()
        {
            if (Width <= 0)
            {
                _viewInitialized = false;
                return;
            }
            // The opening view: the WHOLE clip is visible at once, from 00:00 on
            // the left to the end of the media, drawn as a compact clip that
            // takes InitialFillFraction of the track width; the track continues
            // empty to the right of the clip.
            _view.ViewportWidth = Width;
            _view.PxPerSec = InitialPixelsPerSecond();
            ClampZoom();
            _view.ViewStart = 0;
            _lastViewStart = 0;
            _direction = 0;
            _viewInitialized = true;
            _atInitial = true;
            if (ViewChanged != null) ViewChanged();
        }

        // The scale the editor opens with: the whole clip visible at once, the
        // clip itself taking InitialFillFraction of the track. Falls back to the
        // plain minimum while there is no duration or no width yet.
        private double InitialPixelsPerSecond()
        {
            if (_clip.Duration <= 0 || Width <= 0) return MinPxPerSec;
            return Width * InitialFillFraction / _clip.Duration;
        }

        // Zoom-out floor = the scale the editor opened with. Wheel / ZoomOut
        // must not shrink the clip past that compact whole-clip view.
        private double MinPixelsPerSecond()
        {
            double initial = InitialPixelsPerSecond();
            if (initial > MinPxPerSec) return initial;
            return MinPxPerSec;
        }

        // Effective zoom ceiling for the clip that is open. Long clips open
        // coarser than the ceiling and get it; a clip that already opens finer
        // than it (short clips) keeps the previous absolute ceiling, so its
        // opening view and its zoom range are untouched.
        private double MaxPixelsPerSecond()
        {
            if (MinPixelsPerSecond() > MaxZoomPxPerSec) return MaxPxPerSec;
            return MaxZoomPxPerSec;
        }

        private void ClampZoom()
        {
            double min = MinPixelsPerSecond();
            double max = MaxPixelsPerSecond();
            if (_view.PxPerSec < min) _view.PxPerSec = min;
            if (_view.PxPerSec > max) _view.PxPerSec = max;
        }

        private void ClampView()
        {
            if (_view.PxPerSec <= 0) return;
            double span = Width / _view.PxPerSec;
            double maxStart = _clip.Duration - span;
            if (maxStart < 0) maxStart = 0;
            if (_view.ViewStart < 0) _view.ViewStart = 0;
            if (_view.ViewStart > maxStart) _view.ViewStart = maxStart;
        }

        // Single funnel for every viewport change: clamp, remember the direction
        // of travel, repaint. There is nothing to request any more: the strip is
        // either fully prepared or not shown at all, and zoom/scroll only move
        // the viewport over the already-prepared pictures.
        private void OnViewportChanged()
        {
            if (!_viewInitialized) return;
            ClampZoom();
            if (Math.Abs(_view.PxPerSec - InitialPixelsPerSecond()) < 1e-9)
                _atInitial = true;
            ClampView();
            SyncSurface();
            double delta = _view.ViewStart - _lastViewStart;
            if (Math.Abs(delta) > 1e-6)
            {
                _direction = delta > 0 ? 1 : -1;
                _lastViewStart = _view.ViewStart;
            }
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        // Zoom = change of the horizontal time scale. The time under the mouse
        // anchor stays under the mouse: the strip stretches, the visible time
        // range shrinks, thumbnails grow wider. Never touches Position/ranges.
        public void Zoom(double factor, int anchorX)
        {
            if (!_viewInitialized) return;
            if (factor <= 0) return;
            double anchorT = _view.TimeAt(anchorX);
            double px = _view.PxPerSec * factor;
            double min = MinPixelsPerSecond();
            if (px < min) px = min;
            if (px > MaxPixelsPerSecond()) px = MaxPixelsPerSecond();
            if (Math.Abs(px - _view.PxPerSec) < 1e-9) return;
            _atInitial = Math.Abs(px - min) < 1e-9;
            double frac = Width > 0 ? (double)anchorX / Width : 0.5;
            _view.PxPerSec = px;
            double span = Width / _view.PxPerSec;
            _view.ViewStart = anchorT - frac * span;
            OnViewportChanged();
        }

        public void ScrollBy(double seconds)
        {
            if (!_viewInitialized) return;
            if (Math.Abs(seconds) < 1e-9) return;
            _view.ViewStart += seconds;
            OnViewportChanged();
        }

        // ------------------------------------------------------------------- zoom
        // Zoom is a property of the VIEW only. It changes how many seconds one screen
        // pixel covers; it never changes the clip, the real Start/End of any red range,
        // the playhead time or what the preview is showing. Everything the user marked
        // and every moment they seeked to is stored in clip time, so after any zoom
        // change the same range is still at the same time and the playhead still points
        // at the same frame - only the scale they are drawn at is different.

        public bool CanZoomOut
        {
            get
            {
                if (!_viewInitialized) return false;
                return _view.PxPerSec > MinPixelsPerSecond() * (1.0 + 1e-9);
            }
        }

        public bool CanZoomIn
        {
            get
            {
                if (!_viewInitialized) return false;
                return _view.PxPerSec < MaxPixelsPerSecond() * (1.0 - 1e-9);
            }
        }

        // How much finer the view is than the scale the editor opened with:
        // 1.0 means the opening scale, larger means fewer seconds per screen.
        public double ZoomLevel
        {
            get
            {
                double fit = MinPixelsPerSecond();
                if (fit <= 0 || _view.PxPerSec <= 0) return 1.0;
                return _view.PxPerSec / fit;
            }
        }

        public void ZoomIn()
        {
            ZoomBy(ZoomStep);
        }

        public void ZoomOut()
        {
            ZoomBy(1.0 / ZoomStep);
        }

        // Back to the scale the editor opened with: the whole clip visible at
        // once, from 00:00 on the left. Plain scale reset, not a mode.
        public void ZoomToFit()
        {
            if (!_viewInitialized || Width <= 0) return;
            _view.PxPerSec = InitialPixelsPerSecond();
            ClampZoom();
            _view.ViewStart = 0;
            _atInitial = true;
            OnViewportChanged();
        }

        private void ZoomBy(double factor)
        {
            if (!_viewInitialized) return;
            if (factor <= 0) return;
            int anchor = _view.XAt(_position);
            if (anchor < 0 || anchor > Width) anchor = Width / 2;
            Zoom(factor, anchor);
        }

        // Keeps the playhead on screen when it is driven from outside (frame
        // stepping, seeking). A no-op while it is already visible.
        public void EnsureVisible(double t)
        {
            if (!_viewInitialized || _view.PxPerSec <= 0) return;
            double span = Width / _view.PxPerSec;
            if (t < _view.ViewStart) ScrollBy(t - _view.ViewStart - span * 0.1);
            else if (t > _view.ViewStart + span) ScrollBy(t - (_view.ViewStart + span) + span * 0.1);
        }

        // ------------------------------------------------------------ requests

        private void RequestThumbsSoon()
        {
            _viewportTimer.Stop();
            _viewportTimer.Start();
        }

        private void OnViewportTick(object sender, EventArgs e)
        {
            _viewportTimer.Stop();
        }

        // The plan of the whole-clip strip, derived from the PHYSICAL WIDTH of
        // the clip at the current scale - one proxy frame per PixelsPerFrame
        // pixels - never from the number of seconds of media.
        internal int PlanStripFrames()
        {
            if (_clip.Duration <= 0) return 0;
            int width = Width > 0 ? Width : _view.ViewportWidth;
            if (width <= 0) return 0;
            if (!_viewInitialized) ResetView();
            if (!_viewInitialized)
            {
                // Control not laid out yet: still plan from the known width so
                // preparation can start instead of returning 0 and never showing.
                int fallback = (int)Math.Round(width * InitialFillFraction);
                if (fallback < 1) fallback = 1;
                return FilmstripPrep.PlanFrames(_clip.Duration, fallback);
            }
            SyncSurface();
            int w = (int)Math.Round(_clip.Duration * _view.PxPerSec);
            if (w < 1) w = 1;
            return FilmstripPrep.PlanFrames(_clip.Duration, w);
        }

        // Diagnostics: every sampled time either has its own prepared frame or
        // the strip is not there yet (0/0). There is no "ready vs missing" any
        // more - the strip only ever appears whole.
        internal void VisibleCoverage(out int visible, out int ready)
        {
            visible = 0;
            ready = 0;
            if (_strip == null || Width <= 0 || _clip.Duration <= 0) return;
            SyncSurface();
            double span = _view.ViewSpan;
            if (span <= 0) return;
            double dt = span / 120.0;
            if (dt <= 0) dt = 1.0;
            for (double t = _view.ViewStart; t <= _view.ViewEnd + 1e-9; t += dt) visible++;
            ready = visible;
        }

        private void OnRepaintTick(object sender, EventArgs e)
        {
            _repaintTimer.Stop();
            if (!_dirty) return;
            _dirty = false;
            Invalidate();
        }

        // ------------------------------------------------------------ geometry

        private double ClampTime(double t)
        {
            if (t < _clip.Start) return _clip.Start;
            if (t > _clip.End) return _clip.End;
            return t;
        }

        private double RangeGapSeconds()
        {
            double px = _view.PxPerSec;
            return px > 0 ? MinRangeGapPx / px : 0.01;
        }

        // Time tolerance (1 ms) for the red-range guards. It is far below anything the
        // playhead can express in practice, so "on a red range" and "shares real length
        // with a red range" mean what the user sees, not a floating-point accident.
        private const double TimeEps = 1e-3;

        private const int ClipTopGap = 12;
        private const int ClipBottomGap = 12;

        public const int TotalTimelineHeight = RulerH + ClipTopGap + HeaderH + ThumbHeight + ClipBottomGap;

        // Screen rect of the clip: header band + content. The clip is placed by
        // time; its width follows the zoom, and it is always at least as wide as
        // the viewport, so the timeline never shows empty space around the clip.
        private Rectangle ClipRect(Rectangle r)
        {
            int x = _view.XAt(_clip.Start);
            long w = (long)Math.Round(_clip.Duration * _view.PxPerSec);
            if (w < 1) w = 1;
            if (w > 4000000L) w = 4000000L;
            int top = RulerH + ClipTopGap;
            int h = HeaderH + ThumbHeight;
            return new Rectangle(x, top, (int)w, h);
        }

        private Rectangle ContentRect(Rectangle r)
        {
            Rectangle c = ClipRect(r);
            return new Rectangle(c.Left, c.Top + HeaderH, c.Width, ThumbHeight);
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Input);
            Rectangle r = ClientRectangle;
            if (r.Width < 2 || r.Height < 2) return;
            if (!_viewInitialized || _view.PxPerSec <= 0)
            {
                ResetView();
                if (!_viewInitialized) return;
            }
            SyncSurface();
            DrawSurface(g, r);
            DrawClip(g, r);
            DrawRuler(g, r);
            DrawMarks(g, r);          // committed: red ranges already marked for deletion
            DrawPendingRange(g, r);   // the range being built between its start and the playhead
            DrawPlayhead(g, r);
        }

        // The continuous filmstrip: the visible time window is painted as a
        // sequence of frame pictures placed by TIME, not by cell index.
        // Each picture covers [sampleT .. nextSampleT) and is stretched by the
        // current pxPerSec, so zoom physically stretches the strip.
        // Until the whole strip is prepared nothing is drawn but the neutral
        // surface material - the user never sees a half-built strip.
        private void DrawSurface(Graphics g, Rectangle r)
        {
            Rectangle content = ContentRect(r);
            Rectangle vis = Rectangle.Intersect(content, r);
            if (vis.Width <= 0 || vis.Height <= 0) return;

            Region old = g.Clip;
            g.SetClip(vis);

            using (Brush b = new SolidBrush(SurfaceMaterial))
                g.FillRectangle(b, vis);

            FilmstripPrep.Prepared strip = _strip;
            if (strip == null || strip.Count <= 0)
            {
                // Neutral hold: nothing of the strip is shown until the whole
                // result exists. Partial / progressive filling is forbidden.
                DrawPreparing(g, vis);
            }
            else if (strip.Count > 0 && _clip.Duration > 0)
            {
                double dt = _clip.Duration / strip.Count;
                double t0 = Math.Max(_clip.Start, _view.ViewStart - dt);
                double t1 = Math.Min(_clip.End, _view.ViewEnd + dt);
                int first = (int)Math.Floor((t0 - _clip.Start) / dt);
                if (first < 0) first = 0;
                if (first > strip.Count - 1) first = strip.Count - 1;
                int last = (int)Math.Floor((t1 - _clip.Start) / dt);
                if (last > strip.Count - 1) last = strip.Count - 1;
                for (int i = first; i <= last; i++)
                {
                    double ts = _clip.Start + i * dt;
                    double tn = ts + dt;
                    if (tn > _clip.End) tn = _clip.End;
                    int x0 = _view.XAt(ts);
                    int x1 = _view.XAt(tn);
                    if (x1 <= x0) x1 = x0 + 1;
                    if (x1 < vis.Left || x0 > vis.Right) continue;
                    using (Bitmap bmp = FilmstripPrep.FrameView(strip, i))
                    {
                        if (bmp != null)
                            DrawFrame(g, bmp, x0, content.Top, x1 - x0, content.Height);
                    }
                }
            }

            g.Clip = old;
        }

        private static void DrawPreparing(Graphics g, Rectangle vis)
        {
            if (vis.Width < 8 || vis.Height < 8) return;
            const string text = "Preparing timeline...";
            using (Font f = new Font("Segoe UI", 9f))
            using (Brush b = new SolidBrush(Color.FromArgb(180, Theme.Light)))
            {
                SizeF ts = g.MeasureString(text, f);
                float x = vis.Left + (vis.Width - ts.Width) / 2f;
                float y = vis.Top + (vis.Height - ts.Height) / 2f;
                g.DrawString(text, f, b, x, y);
            }
        }

        // One time-span picture: cropped to fill its time rect edge to edge, so
        // the strip is continuous and stretches with pxPerSec like CapCut.
        private static void DrawFrame(Graphics g, Bitmap bmp, int x, int top, int w, int h)
        {
            int sw = bmp.Width;
            int sh = bmp.Height;
            if (sw <= 0 || sh <= 0 || w <= 0 || h <= 0) return;
            // Cover: fill the whole time rect, crop the overflow.
            double scale = Math.Max((double)w / sw, (double)h / sh);
            int dw = Math.Max(1, (int)Math.Round(sw * scale));
            int dh = Math.Max(1, (int)Math.Round(sh * scale));
            // Source crop centered, destination is the exact time rect.
            int sx = (sw - (int)Math.Round(w / scale)) / 2;
            int sy = (sh - (int)Math.Round(h / scale)) / 2;
            if (sx < 0) sx = 0;
            if (sy < 0) sy = 0;
            int cw = (int)Math.Round(w / scale);
            int ch = (int)Math.Round(h / scale);
            if (sx + cw > sw) cw = sw - sx;
            if (sy + ch > sh) ch = sh - sy;
            if (cw <= 0 || ch <= 0) return;
            Rectangle dst = new Rectangle(x, top, w, h);
            lock (bmp)
            {
                g.DrawImage(bmp, dst, sx, sy, cw, ch, GraphicsUnit.Pixel);
            }
        }

        // The clip as an object: its header band and its border. Everything is
        // painted inside the clip rect, so the clip never leaks outside itself.
        private void DrawClip(Graphics g, Rectangle r)
        {
            Rectangle c = ClipRect(r);
            Region old = g.Clip;
            g.SetClip(Rectangle.Intersect(c, Rectangle.Inflate(r, 4, 4)));

            if (_clip.Caption.Length > 0)
            {
                using (Brush b = new SolidBrush(ClipTeal))
                    g.FillRectangle(b, c.Left, c.Top, c.Width, HeaderH);
                TextRenderer.DrawText(g, _clip.Caption, HeaderFont,
                    new Rectangle(c.Left + 6, c.Top, c.Width - 12, HeaderH),
                    HeaderText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            using (Pen p = new Pen(ClipTeal, 1f))
                g.DrawRectangle(p, c.Left, c.Top, c.Width - 1, c.Height - 1);

            g.Clip = old;
        }

        private static string FormatRulerTime(double t)
        {
            int total = (int)Math.Round(t);
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            if (h > 0)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, s);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}", m, s);
        }

        // Top ruler: major ticks with labels plus minor ticks, the step chosen so
        // labels keep a readable spacing at the current zoom.
        private void DrawRuler(Graphics g, Rectangle r)
        {
            using (Brush b = new SolidBrush(Theme.Back))
                g.FillRectangle(b, 0, 0, r.Width, RulerH);
            double pxPerSec = _view.PxPerSec;
            double[] steps = new double[] { 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 60.0, 120.0, 300.0, 600.0, 900.0, 1800.0, 3600.0 };
            double step = steps[steps.Length - 1];
            for (int i = 0; i < steps.Length; i++)
                if (steps[i] * pxPerSec >= 110.0) { step = steps[i]; break; }
            double minor = step / 5.0;
            double viewEnd = _view.ViewEnd;
            using (Pen major = new Pen(Color.FromArgb(200, Theme.Dim)))
            using (Pen minorPen = new Pen(Color.FromArgb(110, Theme.Dim)))
            using (Brush text = new SolidBrush(Theme.Light))
            using (Font f = new Font("Consolas", 12f))
            {
                double t = Math.Floor(_view.ViewStart / minor) * minor;
                if (t < 0) t = 0;
                for (; t <= viewEnd + 1e-9; t += minor)
                {
                    int x = _view.XAt(t);
                    if (x < -60) continue;
                    if (x > r.Width + 60) break;
                    if (Math.Abs(t / step - Math.Round(t / step)) < 1e-6)
                    {
                        g.DrawLine(major, x, RulerH - 9, x, RulerH - 1);
                        string label = FormatRulerTime(t);
                        SizeF ts = g.MeasureString(label, f);
                        int lx = x + 3;
                        if (lx + ts.Width > r.Width - 2) lx = Math.Max(1, r.Width - 2 - (int)ts.Width);
                        g.DrawString(label, f, text, lx, 2);
                    }
                    else
                    {
                        g.DrawLine(minorPen, x, RulerH - 4, x, RulerH - 1);
                    }
                }
            }
            // The ruler is a band of its own above the frames; the separator is what
            // keeps it from reading as part of the clip block.
            using (Pen edge = new Pen(Color.FromArgb(90, 0, 0, 0), 1f))
                g.DrawLine(edge, 0, RulerH - 1, r.Width, RulerH - 1);
        }

        private static readonly Font HeaderFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);

        // The range being built: a translucent orange fill from the placed start to
        // the current playhead (which is the live end), plus a single marker line at
        // the fixed start. There are no draggable grips - the playhead is the only
        // pointer, so the strip never shows a handle the user would have to grab. When
        // no start is armed this draws nothing and only the white playhead is visible.
        private void DrawPendingRange(Graphics g, Rectangle r)
        {
            if (!_pendingActive) return;
            Rectangle content = ContentRect(r);
            Rectangle clip = ClipRect(r);
            double a = Math.Min(_pendingStart, _position);
            double b = Math.Max(_pendingStart, _position);
            int left = _view.XAt(a);
            int right = _view.XAt(b);
            if (right < 0 || left > r.Width) return;

            Rectangle fill = Rectangle.Intersect(
                new Rectangle(left, content.Top, Math.Max(1, right - left), content.Height), r);
            if (fill.Width > 0 && fill.Height > 0)
            {
                using (Brush b2 = new SolidBrush(RangeFill))
                    g.FillRectangle(b2, fill);
            }

            // Mark only the fixed start; the end is wherever the white playhead sits.
            Region old = g.Clip;
            g.SetClip(r);
            int startX = _view.XAt(_pendingStart);
            using (Pen p = new Pen(RangeEdge, 1.8f))
                g.DrawLine(p, startX, clip.Top, startX, clip.Bottom);
            g.Clip = old;
        }

        // Ranges already applied, i.e. marked for deletion. The overlay is red and
        // translucent so the frames under it stay readable; its edges are heavier than
        // the pending selection's and it has no grip, because an applied mark is not a
        // handle and cannot be dragged.
        private void DrawMarks(Graphics g, Rectangle r)
        {
            if (_marks.Count == 0) return;
            Rectangle content = ContentRect(r);
            Rectangle clip = ClipRect(r);
            Region old = g.Clip;
            g.SetClip(r);
            for (int i = 0; i < _marks.Count; i++)
            {
                MarkRange m = _marks[i];
                int left = _view.XAt(m.Start);
                int right = _view.XAt(m.End) - 1;
                if (right < 0 || left > r.Width) continue;
                Rectangle fill = Rectangle.Intersect(
                    new Rectangle(left, content.Top, Math.Max(1, right - left), content.Height), r);
                if (fill.Width > 0 && fill.Height > 0)
                {
                    using (Brush b = new SolidBrush(MarkFill))
                        g.FillRectangle(b, fill);
                }
                using (Pen p = new Pen(MarkEdge, 2f))
                {
                    g.DrawLine(p, left, clip.Top, left, clip.Bottom);
                    g.DrawLine(p, right, clip.Top, right, clip.Bottom);
                }
            }
            g.Clip = old;
        }

        // The one pointer of the strip: a white line over the whole height plus a
        // badge in the ruler band carrying the current time. The badge is what makes
        // "where am I" readable without looking down at the status line, which is
        // what a track you can actually edit has to give you.
        private void DrawPlayhead(Graphics g, Rectangle r)
        {
            int x = _view.XAt(_position);
            if (x < -8 || x > r.Width + 8) return;
            using (Pen p = new Pen(PlayheadColor, PlayheadWidth))
                g.DrawLine(p, x, 0, x, r.Height);

            string label = FormatBadgeTime(_position);
            SizeF ts = g.MeasureString(label, PlayheadFont);
            int bw = (int)Math.Ceiling(ts.Width) + 10;
            int bh = RulerH - 8;
            int bx = x - bw / 2;
            if (bx < 0) bx = 0;
            if (bx + bw > r.Width) bx = Math.Max(0, r.Width - bw);
            using (Brush b = new SolidBrush(PlayheadColor))
                g.FillRectangle(b, bx, 3, bw, bh);
            TextRenderer.DrawText(g, label, PlayheadFont,
                new Rectangle(bx, 3, bw, bh), PlayheadText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static readonly Font PlayheadFont = new Font("Consolas", 8f, FontStyle.Bold);
        private static readonly Color PlayheadText = Color.FromArgb(18, 20, 24);

        // Current position with tenths of a second: the strip is a scene-finding
        // surface, so the badge has to be finer than the whole-second ruler labels.
        private static string FormatBadgeTime(double t)
        {
            if (t < 0) t = 0;
            int total = (int)t;
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            int tenth = (int)Math.Round((t - total) * 10.0);
            if (tenth > 9) tenth = 9;
            if (h > 0)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0}:{1:00}:{2:00}.{3}", h, m, s, tenth);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:00}:{1:00}.{2}", m, s, tenth);
        }

        // --------------------------------------------------------------- input

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!_viewInitialized) { ResetView(); return; }
            // The initial scale is Width/Duration, so "the whole clip on one
            // screen" has to be recomputed when the width changes - otherwise
            // opening the editor would silently turn the opening view partial.
            if (_atInitial) { ResetView(); return; }
            OnViewportChanged();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            // The editor has no text inputs, so taking focus on hover is safe, and
            // it makes the wheel / Alt+wheel gestures work without a click first.
            Focus();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                // The white playhead is the only pointer now: a left click/drag always
                // moves it. There are no handles to grab, so the user never drags a
                // marker. While a range is being built its fill simply follows the
                // playhead (Position -> Invalidate repaints the pending fill).
                _scrubbing = true;
                Capture = true;
                Position = _view.TimeAt(e.X);
                if (_pendingActive) Invalidate();
            }
            else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                // Right/middle drag pans the view without moving the playhead.
                _panning = true;
                _panLastX = e.X;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_scrubbing)
            {
                Position = _view.TimeAt(e.X);
                if (_pendingActive) Invalidate();
            }
            else if (_panning)
            {
                int dx = e.X - _panLastX;
                if (dx != 0 && _view.PxPerSec > 0)
                {
                    ScrollBy(-dx / _view.PxPerSec);
                    _panLastX = e.X;
                }
            }
            else
            {
                Cursor = Cursors.Hand;
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_scrubbing) _scrubbing = false;
            }
            if ((e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) && _panning) _panning = false;
            if (!_scrubbing && !_panning) Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Ctrl+wheel = zoom anchored at the mouse (CapCut-like): the time under
            // the cursor stays under the cursor, wheel up stretches the strip,
            // wheel down squeezes it. Plain wheel = horizontal scroll.
            if ((ModifierKeys & Keys.Control) != 0)
            {
                double factor = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
                Zoom(factor, e.X);
            }
            else
            {
                double span = _view.ViewEnd - _view.ViewStart;
                ScrollBy(-(e.Delta / 120.0) * span * 0.15);
            }
            base.OnMouseWheel(e);
        }

        // ---------------------------------------------------------------- test hooks
        // --editortest has no physical mouse, so a playhead click/drag is delivered as
        // the very same window messages a real gesture produces. The handlers above are
        // therefore the real ones, not a shortcut around them. This moves the white
        // playhead to each time in turn (a press, moves, release), exactly like the user
        // dragging the one pointer this strip now has.
        internal void TestSeekTo(double[] times)
        {
            if (times == null || times.Length == 0) return;
            int y = Height / 2;
            int first = _view.XAt(times[0]);
            int last = first;
            SendMouse(ChromeApi.WmLButtonDown, ChromeApi.MkLButton, first, y);
            for (int i = 0; i < times.Length; i++)
            {
                last = _view.XAt(times[i]);
                SendMouse(ChromeApi.WmMouseMove, ChromeApi.MkLButton, last, y);
            }
            SendMouse(ChromeApi.WmLButtonUp, 0, last, y);
        }

        // TEMPORARY DIAGNOSTICS: the same gesture split into steps, so the
        // acceptance driver can pace the moves like a real fast hand does (one
        // move per timer tick) and the seek timer fires BETWEEN the moves.
        internal void TestScrubStart(double time)
        {
            if (!IsHandleCreated) return;
            int y = Height / 2;
            SendMouse(ChromeApi.WmLButtonDown, ChromeApi.MkLButton, _view.XAt(time), y);
        }

        internal void TestScrubMove(double time)
        {
            if (!IsHandleCreated) return;
            int y = Height / 2;
            SendMouse(ChromeApi.WmMouseMove, ChromeApi.MkLButton, _view.XAt(time), y);
        }

        internal void TestScrubEnd()
        {
            if (!IsHandleCreated) return;
            int y = Height / 2;
            SendMouse(ChromeApi.WmLButtonUp, 0, Width / 2, y);
        }

        private void SendMouse(int msg, int wParam, int x, int y)
        {
            if (!IsHandleCreated) return;
            long lParam = (long)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
            ChromeApi.SendMessage(Handle, msg, (IntPtr)wParam, (IntPtr)lParam);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _strip = null;
                _repaintTimer.Stop();
                _viewportTimer.Stop();
                _repaintTimer.Dispose();
                _viewportTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
