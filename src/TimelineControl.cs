using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    public class TimelineCutEventArgs : EventArgs
    {
        public double Time;
        public Cut DragCut;
        public bool Dragging;
    }

    public sealed class TimelineControl : Control
    {
        private const int PlayheadWidth = 2;
        private const int RulerH = 18;
        private const int SnapPx = 6;
        private const int EdgeHitPx = 5;
        private const double ZoomWindowMinFrames = 100.0;
        private const double MinThumbWidth = 80.0;
        private const double InitialViewSeconds = 30.0;

        // Thumbnail interval ladder (seconds per slot). A slot is chosen as the
        // smallest ladder value that is still >= MinThumbWidth pixels wide on
        // screen, so every thumbnail stays a distinguishable filmstrip frame at
        // any zoom level. Below the ladder floor the interval is continuous.
        private static readonly double[] SlotLadder = new double[]
        {
            1.0 / 60.0, 1.0 / 30.0, 1.0 / 15.0, 1.0 / 8.0, 1.0 / 4.0, 0.5,
            1.0, 2.0, 3.0, 5.0, 10.0, 15.0, 30.0, 60.0, 120.0, 300.0, 600.0, 1800.0, 3600.0
        };

        private double _duration = 1;
        private double _position;
        private double _viewStart;
        private double _viewEnd = 1;
        private double _slotSec = 1.0;
        private readonly List<double> _snapPoints = new List<double>();
        private readonly List<Cut> _cuts = new List<Cut>();

        private ThumbCache _cache;
        private ThumbRun _visibleRun;

        private enum DragMode { None, Pan, CutStart, CutEnd, CutWhole, RangeNew, ViewPan }
        private DragMode _drag = DragMode.None;
        private Cut _dragCut;
        private double _dragAnchorTime;
        private double _dragOrigStart;
        private double _dragOrigEnd;
        private bool _dragDirty;
        private bool _allowEdit = true;
        private bool _montage;
        private Cut _selectedCut;
        private bool _pendingActive;
        private double _pendingStart;
        private double _pendingEnd;
        private int _viewPanLastX;

        public event Action PositionChanged;
        public event Action ViewChanged;
        public event Action CutDragStarted;
        public event Action<Cut> CutCommitted;
        public event Action CutDragCanceled;
        public event Action SelectionChanged;
        public event Action<double, double> RangeCreated;

        public TimelineControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Input;
            Cursor = Cursors.Hand;
        }

        // When false (frame-finder strip) the control never draws, creates or
        // edits remove ranges; it only scrubs the playhead, scrolls and zooms.
        public bool AllowEdit
        {
            get { return _allowEdit; }
            set
            {
                _allowEdit = value;
                if (!value) { _selectedCut = null; _pendingActive = false; }
                Invalidate();
            }
        }

        // Montage style (top strip only): a CapCut-like time ruler on top that
        // doubles as a scrub zone, the filmstrip clip below it and a prominent
        // full-height playhead. When false the control renders exactly as before.
        public bool MontageStyle
        {
            get { return _montage; }
            set { _montage = value; Invalidate(); }
        }

        public Cut SelectedCut
        {
            get { return _selectedCut; }
        }

        public void SetSelectedCut(Cut cut)
        {
            if (cut != null && !_cuts.Contains(cut)) cut = null;
            if (ReferenceEquals(_selectedCut, cut)) return;
            _selectedCut = cut;
            Invalidate();
            Raise(SelectionChanged);
        }

        public double Duration
        {
            get { return _duration; }
            set
            {
                if (value <= 0) value = 1;
                _duration = value;
                // Initial view: a limited, readable window around 0:00, never
                // the whole file squeezed into the control width.
                double span = Math.Min(_duration, InitialViewSeconds);
                _viewStart = 0;
                _viewEnd = span;
                UpdateSlotSec();
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
                Raise(PositionChanged);
            }
        }

        public double ViewStart
        {
            get { return _viewStart; }
        }

        public double ViewEnd
        {
            get { return _viewEnd; }
        }

        public void SetCuts(IList<Cut> cuts)
        {
            _cuts.Clear();
            if (cuts != null)
                for (int i = 0; i < cuts.Count; i++) _cuts.Add(cuts[i]);
            if (_selectedCut != null && !_cuts.Contains(_selectedCut)) _selectedCut = null;
            Invalidate();
        }

        public void SetSnapPoints(IEnumerable<double> pts)
        {
            _snapPoints.Clear();
            if (pts != null) _snapPoints.AddRange(pts);
        }

        public void AttachCache(ThumbCache cache)
        {
            if (_cache != null) _cache.Changed -= OnCacheChanged;
            _cache = cache;
            _visibleRun = null;
            if (_cache != null) _cache.Changed += OnCacheChanged;
            Invalidate();
        }

        private void OnCacheChanged()
        {
            Invalidate();
        }

        private double PixelsPerSecond
        {
            get { return Math.Max(1e-6, Width / Math.Max(1e-6, _viewEnd - _viewStart)); }
        }

        private double TimeAt(int x)
        {
            return _viewStart + x / PixelsPerSecond;
        }

        private int XAt(double t)
        {
            return (int)Math.Round((t - _viewStart) * PixelsPerSecond);
        }

        private double ClampTime(double t)
        {
            if (t < 0) return 0;
            if (t > _duration) return _duration;
            return t;
        }

        private void Raise(Action a)
        {
            if (a != null) a();
        }

        public void Zoom(double factor, int anchorX)
        {
            double anchorT = TimeAt(anchorX);
            double span = (_viewEnd - _viewStart) / factor;
            double minSpan = _duration / (ZoomWindowMinFrames > 0 ? ZoomWindowMinFrames : 100.0);
            minSpan = Math.Max(minSpan, 1.0 / 1000.0);
            if (span > _duration) span = _duration;
            if (span < minSpan) span = minSpan;
            double frac = (anchorT - _viewStart) / Math.Max(1e-9, _viewEnd - _viewStart);
            double newStart = anchorT - frac * span;
            if (newStart < 0) newStart = 0;
            if (newStart + span > _duration) newStart = _duration - span;
            _viewStart = newStart;
            _viewEnd = newStart + span;
            UpdateVisibleRun();
            Invalidate();
            Raise(ViewChanged);
        }

        public void ResetZoom()
        {
            _viewStart = 0;
            _viewEnd = _duration;
            UpdateVisibleRun();
            Invalidate();
            Raise(ViewChanged);
        }

        public void ScrollBy(double deltaSeconds)
        {
            double span = _viewEnd - _viewStart;
            double s = _viewStart + deltaSeconds;
            if (s < 0) s = 0;
            if (s + span > _duration) s = _duration - span;
            if (Math.Abs(s - _viewStart) < 1e-9) return;
            _viewStart = s;
            _viewEnd = s + span;
            UpdateVisibleRun();
            Invalidate();
            Raise(ViewChanged);
        }

        public void EnsureVisible(double t, double marginSeconds)
        {
            if (t < _viewStart + marginSeconds)
                ScrollBy(t - _viewStart - marginSeconds);
            else if (t > _viewEnd - marginSeconds)
                ScrollBy(t - _viewEnd + marginSeconds);
        }

        private double ComputeSlotSec()
        {
            double pxPerSec = PixelsPerSecond;
            double minSlot = MinThumbWidth / Math.Max(1e-6, pxPerSec);
            for (int i = 0; i < SlotLadder.Length; i++)
            {
                if (SlotLadder[i] >= minSlot - 1e-9) return SlotLadder[i];
            }
            // Below the ladder floor: continuous interval, still >= MinThumbWidth.
            return minSlot;
        }

        private void UpdateSlotSec()
        {
            double s = ComputeSlotSec();
            if (s > 0 && !double.IsNaN(s) && !double.IsInfinity(s)) _slotSec = s;
        }

        private void UpdateVisibleRun()
        {
            if (_cache == null) return;
            // A hidden strip (legacy carrier while the FilmstripTimeline is the one
            // visible timeline) must not spend ffmpeg runs on thumbs nobody sees.
            if (!Visible) return;
            double span = _viewEnd - _viewStart;
            if (span <= 0 || double.IsInfinity(span) || double.IsNaN(span)) return;
            UpdateSlotSec();
            double slot = _slotSec;
            // Window = visible range padded by 2 slots on each side, aligned to
            // the global slot grid (i * slot) so thumb indexes are stable while
            // scrolling and matching thumbs re-use already decoded runs.
            double pad = slot * 2.0;
            double wStart = Math.Floor((_viewStart - pad) / slot) * slot;
            if (wStart < 0) wStart = 0;
            double wEnd = Math.Ceiling((_viewEnd + pad) / slot) * slot;
            if (wEnd > _duration) wEnd = _duration;
            double wDur = wEnd - wStart;
            if (wDur <= 0) wDur = Math.Min(slot, _duration);
            double fps = 1.0 / slot;
            ThumbRun run = _cache.RequestWindow(wStart, wDur, fps, ThumbHeight());
            if (!ReferenceEquals(run, _visibleRun))
            {
                // Keep the old run alive: its thumbs stay valid for scrolling
                // back; LRU eviction cleans them up when over budget.
                _visibleRun = run;
            }
        }

        private int ThumbHeight()
        {
            return Math.Max(24, Height - 4);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateVisibleRun();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Input);
            Rectangle r = ClientRectangle;
            if (r.Width < 2 || r.Height < 2) return;

            int thumbH = Math.Max(1, Height - 4 - (_montage ? RulerH : 0));
            DrawThumbs(g, r, thumbH, _montage ? RulerH : 0);
            if (_montage) DrawRuler(g, r);
            else DrawTimeGrid(g, r);
            if (_allowEdit)
            {
                DrawCuts(g, r, thumbH, _montage ? RulerH : 0);
                DrawKeepBar(g, r);
                if (_pendingActive) DrawPending(g, r);
            }
            DrawPlayhead(g, r);
            DrawBorder(g, r);
        }

        private void DrawThumbs(Graphics g, Rectangle r, int thumbH, int topInset)
        {
            if (_cache == null) return;
            ThumbRun run = _visibleRun;
            if (run == null)
            {
                UpdateVisibleRun();
                run = _visibleRun;
                if (run == null) return;
            }
            double runFps = Math.Max(1e-9, run.Fps);
            double secPerThumb = 1.0 / runFps;
            int first = (int)Math.Floor(_viewStart / secPerThumb);
            int last = (int)Math.Ceiling(_viewEnd / secPerThumb);
            if (first < 0) first = 0;

            for (int i = last; i >= first; i--)
            {
                double t = i * secPerThumb;
                if (t < run.Start - 1e-6 || t > run.Start + run.Duration + secPerThumb + 1e-6) continue;
                int k = (run.Id == 0) ? i : (int)Math.Round((t - run.Start) * runFps);
                if (k < 0) continue;
                Bitmap bmp;
                if (!_cache.TryGet(run, k, out bmp) || bmp == null)
                {
                    if (run.Id != 0 && _cache.Level0 != null)
                    {
                        int l0 = (int)Math.Floor(t * _cache.Level0.Fps);
                        _cache.TryGet(_cache.Level0, l0, out bmp);
                    }
                }
                if (bmp == null) continue;
                int x = XAt(t);
                int xNext = XAt(t + secPerThumb);
                int w = Math.Max(1, xNext - x);
                if (x + w < -4 || x > r.Width + 4) continue;
                Rectangle dst = new Rectangle(x, topInset + (r.Height - topInset - thumbH) / 2, w + 1, thumbH);
                lock (bmp)
                {
                    g.DrawImage(bmp, dst);
                }
            }
        }

        public static string FormatClock(double t)
        {
            if (t < 0) t = 0;
            int total = (int)Math.Floor(t + 0.0005);
            int ms = (int)Math.Round((t - total) * 1000.0);
            if (ms >= 1000) { ms = 0; total++; }
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            if (h > 0) return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:000}", h, m, s, ms);
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1:00}.{2:000}", m, s, ms);
        }

        // Montage ruler band on top: filled strip with time labels and ticks,
        // separated from the filmstrip clip by a border line.
        private void DrawRuler(Graphics g, Rectangle r)
        {
            using (Brush b = new SolidBrush(Theme.Back))
                g.FillRectangle(b, 0, 0, r.Width, RulerH - 1);
            using (Pen p = new Pen(Theme.Border, 1f))
                g.DrawLine(p, 0, RulerH - 1, r.Width, RulerH - 1);
        }

        private void DrawTimeGrid(Graphics g, Rectangle r)
        {
            double span = _viewEnd - _viewStart;
            if (span <= 0) return;
            double targetPx = 90;
            double raw = span * targetPx / Math.Max(1, r.Width);
            double[] steps = new[] { 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 60.0, 120.0, 300.0, 600.0, 900.0, 1800.0, 3600.0 };
            double step = steps[steps.Length - 1];
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] >= raw) { step = steps[i]; break; }
            }
            using (Pen pen = new Pen(Color.FromArgb(70, Theme.Light), 1f))
            using (Brush brush = new SolidBrush(Color.FromArgb(215, Theme.Light)))
            using (Brush chip = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
            using (Font f = new Font("Consolas", 7.5f))
            {
                double t0 = Math.Ceiling(_viewStart / step) * step;
                for (double t = t0; t <= _viewEnd + 1e-9; t += step)
                {
                    int x = XAt(t);
                    if (x < -40 || x > r.Width + 40) continue;
                    string label = FormatClock(t);
                    SizeF ts = g.MeasureString(label, f);
                    if (_montage)
                    {
                        // Labels and short ticks inside the top ruler band.
                        g.FillRectangle(chip, x + 1, 1, ts.Width + 4, RulerH - 6);
                        g.DrawString(label, f, brush, x + 2, 1);
                        g.DrawLine(pen, x, RulerH - 5, x, RulerH - 1);
                    }
                    else
                    {
                        g.DrawLine(pen, x, r.Height - 14, x, r.Height);
                        // Dark chip behind the label keeps it readable on both
                        // dark and bright filmstrip frames without hiding them.
                        g.FillRectangle(chip, x + 1, r.Height - 15, ts.Width + 4, 14);
                        g.DrawString(label, f, brush, x + 2, r.Height - 14);
                    }
                }
            }
        }

        private void DrawCuts(Graphics g, Rectangle r, int thumbH, int topInset)
        {
            for (int i = 0; i < _cuts.Count; i++)
            {
                Cut c = _cuts[i];
                int x1 = XAt(c.Start);
                int x2 = XAt(c.End);
                if (x2 < 0 || x1 > r.Width) continue;
                bool sel = ReferenceEquals(c, _selectedCut);
                Rectangle fill = new Rectangle(x1, topInset, Math.Max(1, x2 - x1), r.Height - topInset);
                using (Brush b = new SolidBrush(Color.FromArgb(sel ? 105 : 70, Theme.Red)))
                    g.FillRectangle(b, fill);
                using (Pen p = new Pen(sel ? Theme.Light : Theme.Red, sel ? 1.8f : 1.6f))
                {
                    g.DrawLine(p, x1, topInset, x1, r.Height);
                    g.DrawLine(p, x2 - 1, topInset, x2 - 1, r.Height);
                }
                bool dragged = ReferenceEquals(c, _dragCut) && _drag != DragMode.None;
                Color handle = dragged ? Theme.Orange : (sel ? Theme.Light : Theme.Red);
                using (Brush hb = new SolidBrush(handle))
                {
                    g.FillRectangle(hb, x1 - EdgeHitPx, topInset, EdgeHitPx, thumbH);
                    g.FillRectangle(hb, x2, topInset, EdgeHitPx, thumbH);
                }
            }
        }

        // Green segments on top = parts of the source that will REMAIN in the
        // output (complement of the remove ranges). Painted after the red
        // fills so it stays clearly visible over them.
        private void DrawKeepBar(Graphics g, Rectangle r)
        {
            double pos = 0;
            using (Brush b = new SolidBrush(Theme.Green))
            {
                for (int i = 0; i <= _cuts.Count; i++)
                {
                    double ks = pos;
                    double ke = i < _cuts.Count ? _cuts[i].Start : _duration;
                    if (i < _cuts.Count) pos = Math.Max(pos, _cuts[i].End);
                    if (ke > ks + 0.001)
                    {
                        int x1 = Math.Max(0, XAt(ks));
                        int x2 = Math.Min(r.Width, XAt(ke));
                        if (x2 > x1) g.FillRectangle(b, x1, 0, x2 - x1, 3);
                    }
                }
            }
        }

        // Range being created right now with the mouse (not committed yet).
        private void DrawPending(Graphics g, Rectangle r)
        {
            double s = Math.Min(_pendingStart, _pendingEnd);
            double e = Math.Max(_pendingStart, _pendingEnd);
            int x1 = XAt(s);
            int x2 = XAt(e);
            if (x2 < 0 || x1 > r.Width) return;
            Rectangle fill = new Rectangle(x1, 0, Math.Max(1, x2 - x1), r.Height);
            using (Brush b = new SolidBrush(Color.FromArgb(110, Theme.Orange)))
                g.FillRectangle(b, fill);
            using (Pen p = new Pen(Theme.Orange, 1.6f))
            {
                g.DrawLine(p, x1, 0, x1, r.Height);
                g.DrawLine(p, x2 - 1, 0, x2 - 1, r.Height);
            }
        }

        private void DrawPlayhead(Graphics g, Rectangle r)
        {
            int x = XAt(_position);
            using (Pen p = new Pen(Theme.Accent, PlayheadWidth))
                g.DrawLine(p, x, 0, x, r.Height);
            using (Brush b = new SolidBrush(Theme.Accent))
            {
                Point[] tri = new Point[]
                {
                    new Point(x - 5, 0), new Point(x + 5, 0), new Point(x, 7)
                };
                g.FillPolygon(b, tri);
            }
        }

        private void DrawBorder(Graphics g, Rectangle r)
        {
            using (Pen p = new Pen(Theme.Border, 1f))
                g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
        }

        private double Snap(double t)
        {
            double best = t;
            double bestPx = SnapPx / PixelsPerSecond;
            double bestDist = double.MaxValue;
            for (int i = 0; i < _snapPoints.Count; i++)
            {
                double d = Math.Abs(_snapPoints[i] - t);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = _snapPoints[i];
                }
            }
            if (bestDist <= bestPx) return best;
            if (Math.Abs(t) < bestPx) return 0;
            if (Math.Abs(t - _duration) < bestPx) return _duration;
            return t;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                double t = TimeAt(e.X);
                bool edge;
                bool isStart;
                Cut hit = HitCut(e.X, out edge, out isStart);
                if (hit != null && edge)
                {
                    _drag = isStart ? DragMode.CutStart : DragMode.CutEnd;
                    _dragCut = hit;
                    _dragOrigStart = hit.Start;
                    _dragOrigEnd = hit.End;
                    SetSelectedCut(hit);
                }
                else if (hit != null)
                {
                    _drag = DragMode.CutWhole;
                    _dragCut = hit;
                    _dragAnchorTime = t;
                    _dragOrigStart = hit.Start;
                    _dragOrigEnd = hit.End;
                    SetSelectedCut(hit);
                }
                else if (_allowEdit)
                {
                    // Click sets the playhead; dragging from empty space
                    // creates a new range that is committed on release.
                    _drag = DragMode.RangeNew;
                    _pendingStart = Snap(t);
                    _pendingEnd = _pendingStart;
                    _pendingActive = true;
                    Position = _pendingStart;
                }
                else
                {
                    _drag = DragMode.Pan;
                    _dragAnchorTime = t;
                    Position = Snap(t);
                }
                _dragDirty = false;
                if (_drag == DragMode.CutStart || _drag == DragMode.CutEnd || _drag == DragMode.CutWhole)
                {
                    if (CutDragStarted != null) CutDragStarted();
                }
                Capture = true;
            }
            else if (e.Button == MouseButtons.Right && _drag == DragMode.None)
            {
                // Right-drag pans the view without moving the playhead.
                _drag = DragMode.ViewPan;
                _viewPanLastX = e.X;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        private Cut HitCut(int x, out bool edge, out bool isStart)
        {
            edge = false;
            isStart = false;
            if (!_allowEdit) return null;
            int bestDist = int.MaxValue;
            Cut best = null;
            bool bestStart = false;
            for (int i = 0; i < _cuts.Count; i++)
            {
                Cut c = _cuts[i];
                int x1 = XAt(c.Start);
                int x2 = XAt(c.End);
                int dS = Math.Abs(x - x1);
                int dE = Math.Abs(x - x2);
                if (dS <= EdgeHitPx && dS < bestDist)
                {
                    bestDist = dS; best = c; bestStart = true;
                }
                if (dE <= EdgeHitPx && dE < bestDist)
                {
                    bestDist = dE; best = c; bestStart = false;
                }
            }
            if (best != null)
            {
                edge = true;
                isStart = bestStart;
                return best;
            }
            for (int i = 0; i < _cuts.Count; i++)
            {
                Cut c = _cuts[i];
                int x1 = XAt(c.Start);
                int x2 = XAt(c.End);
                if (x >= x1 && x <= x2)
                {
                    edge = false;
                    isStart = false;
                    return c;
                }
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag == DragMode.None)
            {
                bool edge;
                bool isStart;
                Cut hit = HitCut(e.X, out edge, out isStart);
                if (hit != null && edge) Cursor = Cursors.SizeWE;
                else if (hit != null) Cursor = Cursors.SizeAll;
                else if (_allowEdit) Cursor = Cursors.Cross;
                else Cursor = Cursors.Hand;
                base.OnMouseMove(e);
                return;
            }

            double t = TimeAt(e.X);
            if (_drag == DragMode.ViewPan)
            {
                double dx = e.X - _viewPanLastX;
                if (dx != 0)
                {
                    ScrollBy(-dx / PixelsPerSecond);
                    _viewPanLastX = e.X;
                }
                base.OnMouseMove(e);
                return;
            }
            if (_drag == DragMode.RangeNew)
            {
                _pendingEnd = Snap(t);
                // Live frame preview under the moving edge.
                Position = Snap(t);
                Invalidate();
                base.OnMouseMove(e);
                return;
            }
            if (_drag == DragMode.Pan)
            {
                Position = Snap(t);
                return;
            }
            if (_dragCut == null) return;

            if (_drag == DragMode.CutStart)
            {
                double s = Snap(t);
                double ce = _dragCut.End;
                if (ce - s < CutModel.MinCutLength)
                {
                    if (s < ce) s = ce - CutModel.MinCutLength;
                    else { s = _dragOrigStart; }
                }
                _dragCut.Start = Math.Max(0, Math.Min(s, _dragCut.End - CutModel.MinCutLength));
                _dragDirty = true;
            }
            else if (_drag == DragMode.CutEnd)
            {
                double s = _dragCut.Start;
                double en = Snap(t);
                if (en - s < CutModel.MinCutLength)
                {
                    if (en > s) en = s + CutModel.MinCutLength;
                    else { en = _dragOrigEnd; }
                }
                _dragCut.End = Math.Min(_duration, Math.Max(en, _dragCut.Start + CutModel.MinCutLength));
                _dragDirty = true;
            }
            else if (_drag == DragMode.CutWhole)
            {
                double delta = t - _dragAnchorTime;
                double len = _dragOrigEnd - _dragOrigStart;
                double ns = Snap(_dragOrigStart + delta);
                if (ns < 0) ns = 0;
                if (ns + len > _duration) ns = _duration - len;
                _dragCut.Start = ns;
                _dragCut.End = ns + len;
                _dragDirty = true;
            }
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_drag == DragMode.Pan && !_dragDirty)
            {
                // position already set on mouse-down
            }
            if (_drag == DragMode.CutStart || _drag == DragMode.CutEnd || _drag == DragMode.CutWhole)
            {
                // Commit only when the drag actually changed the cut bounds.
                // A drag that ends where it started must not produce a no-op
                // undo entry, so the open transaction is rolled back instead.
                bool changed = _dragCut != null &&
                    (Math.Abs(_dragCut.Start - _dragOrigStart) > 1e-9 ||
                     Math.Abs(_dragCut.End - _dragOrigEnd) > 1e-9);
                if (_dragDirty && changed && _dragCut != null && CutCommitted != null)
                    CutCommitted(_dragCut);
                else if (CutDragCanceled != null)
                    CutDragCanceled();
            }
            if (_drag == DragMode.RangeNew)
            {
                _pendingActive = false;
                double s = Math.Min(_pendingStart, _pendingEnd);
                double en = Math.Max(_pendingStart, _pendingEnd);
                // A tiny drag is just a click: the playhead already moved.
                if (en - s >= CutModel.MinCutLength && RangeCreated != null)
                    RangeCreated(s, en);
                Invalidate();
            }
            _drag = DragMode.None;
            _dragCut = null;
            _dragDirty = false;
            Capture = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        public bool IsDragging
        {
            get { return _drag != DragMode.None; }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                double factor = e.Delta > 0 ? 1.25 : 1.0 / 1.25;
                Zoom(factor, e.X);
            }
            else
            {
                double span = _viewEnd - _viewStart;
                ScrollBy(-(e.Delta / 120.0) * span * 0.15);
            }
            base.OnMouseWheel(e);
        }
    }
}
