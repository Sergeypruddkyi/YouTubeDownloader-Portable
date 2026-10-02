using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    // The window behind "Save frame (PNG)". It shows the REAL source-resolution
    // frame that FFmpeg just wrote to a temporary PNG - never the LibVLC preview,
    // never a screenshot of a window - so a 3840x2160 source is edited as
    // 3840x2160. The picture on screen is only a fitted VIEW of that frame: the
    // selection rectangle lives in source image pixels from the very first
    // moment, which is what comes back out on Save.
    //
    // The window owns neither ffmpeg nor the folder dialog. Save = OK and the
    // rectangle is returned; Cancel / Esc / X = Cancel and the caller drops the
    // temporary frame. The actual crop is done by EditorForm.
    public sealed class CropFrameForm : Form
    {
        // The PNG bytes stay in memory for the whole life of the window: the
        // bitmap is built from a MemoryStream that is never closed early, so the
        // temporary file on disk is NOT locked and the caller can still hand it
        // to ffmpeg (Save) or delete it (Cancel) after this form is gone.
        private readonly MemoryStream _pngStream;
        private readonly Bitmap _image;
        private readonly CropCanvas _canvas;
        private readonly Label _lblSelection;
        private readonly Button _btnSave;
        private readonly Button _btnCancel;
        // The shared custom chrome strip - the very same DarkTitleBar the main
        // window and the video editor put under their title.
        private DarkTitleBar _titleBar;

        // The selection in SOURCE image pixels (the coordinates ffmpeg gets).
        public Rectangle Selection { get { return _canvas.Selection; } }
        public Size SourceSize { get { return _canvas.SourceSize; } }

        private const int BarHeight = 50;

        public CropFrameForm(Form owner, string pngPath)
        {
            Text = L10n.T(Msg.EdCropTitle);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            // The SAME custom chrome as the main window and the video editor:
            // no standard Windows title bar at all (FormBorderStyle.None); the
            // top strip is the shared DarkTitleBar added at the end of this
            // constructor, and WndProc below provides our own resize borders,
            // caption drag and work-area-clamped maximize - the identical
            // mechanism MainForm / EditorForm use.
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = true;
            BackColor = Theme.Back;
            Font = Theme.Regular;
            // A very small selection still has to be grabbable, so the window may
            // not be shrunk to nothing; the picture itself can grow (MaximizeBox).
            MinimumSize = new Size(560, 400);

            // Built before the layout math below so the exact height of the shared
            // chrome strip (44 px) can be reserved for it; added to Controls at the
            // end of the constructor so it paints above everything else.
            _titleBar = new DarkTitleBar(this);
            int titleH = _titleBar.Height;

            byte[] bytes = File.ReadAllBytes(pngPath);
            _pngStream = new MemoryStream(bytes);
            _image = new Bitmap(_pngStream);

            // Window size follows the frame's aspect and the screen it opens on:
            // the frame is fitted into a normal desktop-sized share of the working
            // area, so a 4K frame is scaled down while a small frame never gets a
            // huge window around it. This used to be 85% of the screen (over
            // 2000x1200 on a 2560x1440 desktop for a 1280x720 frame) which felt
            // like fullscreen just to pick a rectangle; ~50% x 60% with a
            // desktop-friendly ceiling keeps the frame large enough to aim at
            // (a 1280x720 frame still fits 1:1 here) without swallowing the
            // desktop. Maximize is still allowed when extra precision is wanted.
            Rectangle wa = owner != null && !owner.IsDisposed
                ? Screen.FromControl(owner).WorkingArea
                : Screen.PrimaryScreen.WorkingArea;
            int maxW = (int)Math.Min(Math.Max(640, wa.Width * 0.50), 1440);
            int maxH = (int)Math.Min(Math.Max(440, wa.Height * 0.60), 900);
            // The 44 px chrome strip is reserved ABOVE the picture as well, so the
            // frame keeps exactly the view size it had before the custom chrome
            // and the ~900 px ceiling still covers the WHOLE window (canvas +
            // title strip + bottom bar).
            Size canvas = FitInside(_image.Size, maxW, maxH - BarHeight - titleH);
            canvas = new Size(Math.Max(480, canvas.Width), Math.Max(300, canvas.Height));
            ClientSize = new Size(canvas.Width, canvas.Height + BarHeight + titleH);

            _canvas = new CropCanvas();
            _canvas.Location = new Point(0, titleH);
            _canvas.Size = new Size(ClientSize.Width, ClientSize.Height - titleH - BarHeight);
            _canvas.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _canvas.SelectionChanged += delegate { UpdateSelectionLabel(); };
            Controls.Add(_canvas);
            _canvas.SetImage(_image);

            Panel bar = new Panel();
            bar.Location = new Point(0, ClientSize.Height - BarHeight);
            bar.Size = new Size(ClientSize.Width, BarHeight);
            bar.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            bar.BackColor = Theme.Back;
            Panel edge = new Panel();
            edge.Dock = DockStyle.Top;
            edge.Height = 1;
            edge.BackColor = Theme.Border;
            bar.Controls.Add(edge);
            Controls.Add(bar);

            // Selection readout in source pixels: the proof that what is being
            // cut is the frame's own coordinate system, not the scaled view.
            _lblSelection = new Label();
            _lblSelection.Location = new Point(12, 9);
            _lblSelection.Size = new Size(Math.Max(120, bar.Width - 300), 32);
            _lblSelection.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _lblSelection.ForeColor = Theme.Dim;
            _lblSelection.TextAlign = ContentAlignment.MiddleLeft;
            bar.Controls.Add(_lblSelection);

            _btnSave = new Button();
            _btnSave.Text = L10n.T(Msg.EdCropSave);
            _btnSave.Size = new Size(140, 32);
            _btnSave.Location = new Point(bar.Width - 12 - 110 - 8 - 140, 9);
            _btnSave.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Theme.StyleAccent(_btnSave);
            _btnSave.DialogResult = DialogResult.OK;
            bar.Controls.Add(_btnSave);

            _btnCancel = new Button();
            _btnCancel.Text = L10n.T(Msg.EdCancel);
            _btnCancel.Size = new Size(110, 32);
            _btnCancel.Location = new Point(bar.Width - 12 - 110, 9);
            _btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Theme.StyleButton(_btnCancel);
            _btnCancel.DialogResult = DialogResult.Cancel;
            bar.Controls.Add(_btnCancel);

            AcceptButton = _btnSave;
            CancelButton = _btnCancel;
            UpdateSelectionLabel();

            // Closed with the X (or Alt+F4): that is a Cancel - nothing is saved
            // and the caller deletes the temporary frame.
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (DialogResult == DialogResult.None) DialogResult = DialogResult.Cancel;
            };

            if (owner != null && !owner.IsDisposed) Owner = owner;

            // Last in z-order so the chrome strip paints above the picture; done
            // before the DPI scale below so it is scaled with everything else.
            Controls.Add(_titleBar);

            float k = ChromeApi.GetDpiForWindowAt(Cursor.Position) / 96f;
            if (k > 0.999f && k < 1.001f) k = 1f;
            if (k != 1f) Scale(new SizeF(k, k));
        }

        private static Size FitInside(Size img, int maxW, int maxH)
        {
            if (img.Width <= 0 || img.Height <= 0 || maxW <= 0 || maxH <= 0)
                return new Size(Math.Max(1, maxW), Math.Max(1, maxH));
            float s = Math.Min(maxW / (float)img.Width, maxH / (float)img.Height);
            return new Size(Math.Max(1, (int)Math.Round(img.Width * s)),
                Math.Max(1, (int)Math.Round(img.Height * s)));
        }

        private void UpdateSelectionLabel()
        {
            Rectangle r = _canvas.Selection;
            _lblSelection.Text = string.Format(CultureInfo.InvariantCulture,
                "{0} x {1} @ {2}, {3}   frame {4} x {5}",
                r.Width, r.Height, r.X, r.Y, _image.Width, _image.Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { if (_image != null) _image.Dispose(); } catch { }
                try { if (_pngStream != null) _pngStream.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- chrome
        // The window uses the SAME custom chrome as the main window and the
        // video editor (see MainForm.WndProc / EditorForm.WndProc): borderless,
        // the shared DarkTitleBar strip on top with its Min/Max/Close buttons,
        // our own resize borders, caption drag for moving the window by its top
        // strip, and maximize clamped to the monitor work area. Only the window
        // frame changed - the picture, the selection, Save/Cancel and the crop
        // logic are untouched.

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Same as EditorForm: the chrome styles need a live handle.
            ApplyChromeStyles();
        }

        private void ApplyChromeStyles()
        {
            int style = ChromeApi.GetWindowLong(Handle, ChromeApi.GwlStyle);
            int desired = style | 0x00040000 | 0x00C00000 | 0x00020000 | 0x00010000 | 0x00080000;
            if (desired != style)
            {
                ChromeApi.SetWindowLong(Handle, ChromeApi.GwlStyle, desired);
                ChromeApi.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ChromeApi.WmGetMinMaxInfo)
            {
                base.WndProc(ref m);
                IntPtr mon = ChromeApi.MonitorFromWindow(m.HWnd, ChromeApi.MonitorDefaultToNearest);
                ChromeApi.MonitorInfo mi = new ChromeApi.MonitorInfo();
                mi.CbSize = (uint)Marshal.SizeOf(typeof(ChromeApi.MonitorInfo));
                if (mon != IntPtr.Zero && ChromeApi.GetMonitorInfo(mon, ref mi))
                {
                    Marshal.WriteInt32(m.LParam, 8, mi.Work.Right - mi.Work.Left);
                    Marshal.WriteInt32(m.LParam, 12, mi.Work.Bottom - mi.Work.Top);
                    Marshal.WriteInt32(m.LParam, 16, mi.Work.Left);
                    Marshal.WriteInt32(m.LParam, 20, mi.Work.Top);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == ChromeApi.WmNcCalcSize && m.WParam != IntPtr.Zero)
            {
                // Zero non-client area = no standard Windows title bar at all.
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == ChromeApi.WmNcHitTest)
            {
                int sx = unchecked((short)((long)m.LParam & 0xFFFF));
                int sy = unchecked((short)(((long)m.LParam >> 16) & 0xFFFF));
                Point cpt = PointToClient(new Point(sx, sy));
                bool maximized = ChromeApi.IsZoomedStyle(m.HWnd);
                if (!maximized)
                {
                    int fw = ChromeApi.GetSystemMetrics(ChromeApi.SmCxSizeFrame) + ChromeApi.GetSystemMetrics(ChromeApi.SmCxPaddedBorder);
                    int fh = ChromeApi.GetSystemMetrics(ChromeApi.SmCySizeFrame) + ChromeApi.GetSystemMetrics(ChromeApi.SmCxPaddedBorder);
                    bool left = cpt.X < fw;
                    bool right = cpt.X >= ClientSize.Width - fw;
                    bool top = cpt.Y < fh;
                    bool bottom = cpt.Y >= ClientSize.Height - fh;
                    if (top && left) { m.Result = (IntPtr)ChromeApi.HtTopLeft; return; }
                    if (top && right) { m.Result = (IntPtr)ChromeApi.HtTopRight; return; }
                    if (bottom && left) { m.Result = (IntPtr)ChromeApi.HtBottomLeft; return; }
                    if (bottom && right) { m.Result = (IntPtr)ChromeApi.HtBottomRight; return; }
                    if (top) { m.Result = (IntPtr)ChromeApi.HtTop; return; }
                    if (bottom) { m.Result = (IntPtr)ChromeApi.HtBottom; return; }
                    if (left) { m.Result = (IntPtr)ChromeApi.HtLeft; return; }
                    if (right) { m.Result = (IntPtr)ChromeApi.HtRight; return; }
                }
                if (_titleBar != null && cpt.Y >= 0 && cpt.Y < _titleBar.Height)
                {
                    // Drag the window by the custom top strip - exactly like the editor.
                    m.Result = (IntPtr)ChromeApi.HtCaption;
                    return;
                }
                m.Result = (IntPtr)ChromeApi.HtClient;
                return;
            }
            base.WndProc(ref m);
        }

        // ---------------------------------------------------------------- canvas
        // The selection surface. Everything it stores is in SOURCE pixels; the
        // fitted view is recomputed on every resize, so resizing the window never
        // moves the selection - it only changes how big it is drawn.
        private sealed class CropCanvas : Control
        {
            private const int West = 1;
            private const int North = 2;
            private const int East = 4;
            private const int South = 8;
            private const int MoveMode = 16;
            // Grab zones and handles are measured on SCREEN pixels and converted
            // to source pixels, so they stay the same size on a scaled-down 4K
            // frame and on an upscaled small one.
            private const int GrabPx = 8;
            private const int MinSelection = 16;

            private Bitmap _bmp;
            private Size _src = Size.Empty;
            private Rectangle _sel;
            private float _scale = 1f;
            private PointF _offset = PointF.Empty;
            private int _mode;
            private PointF _downSrc;
            private Rectangle _downSel;

            public event EventHandler SelectionChanged;

            public Rectangle Selection { get { return _sel; } }
            public Size SourceSize { get { return _src; } }

            public CropCanvas()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Color.Black;
                TabStop = false;
            }

            public void SetImage(Bitmap bmp)
            {
                _bmp = bmp;
                _src = bmp == null ? Size.Empty : bmp.Size;
                // First selection: the WHOLE frame, as the UX requires.
                _sel = new Rectangle(Point.Empty, _src);
                Refit();
                Invalidate();
            }

            private void Refit()
            {
                if (_src.Width <= 0 || _src.Height <= 0
                    || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                {
                    _scale = 1f;
                    _offset = PointF.Empty;
                    return;
                }
                float sx = ClientSize.Width / (float)_src.Width;
                float sy = ClientSize.Height / (float)_src.Height;
                _scale = Math.Min(sx, sy);
                if (_scale <= 0f) _scale = 1f;
                _offset = new PointF(
                    (ClientSize.Width - _src.Width * _scale) / 2f,
                    (ClientSize.Height - _src.Height * _scale) / 2f);
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                Refit();
                Invalidate();
            }

            private Rectangle DisplayRect(Rectangle src)
            {
                int w = (int)Math.Round(src.Width * _scale);
                int h = (int)Math.Round(src.Height * _scale);
                if (w < 1) w = 1;
                if (h < 1) h = 1;
                return new Rectangle(
                    (int)Math.Round(src.X * _scale + _offset.X),
                    (int)Math.Round(src.Y * _scale + _offset.Y), w, h);
            }

            private PointF ToSource(int x, int y)
            {
                float s = _scale <= 0f ? 1f : _scale;
                return new PointF((x - _offset.X) / s, (y - _offset.Y) / s);
            }

            private float ToleranceSource()
            {
                float s = _scale <= 0f ? 1f : _scale;
                return GrabPx / s;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(BackColor);
                if (_bmp == null || _src.Width <= 0) return;

                Rectangle img = DisplayRect(new Rectangle(Point.Empty, _src));
                g.DrawImage(_bmp, img);

                Rectangle sel = DisplayRect(_sel);
                // Everything of the picture outside the selection is dimmed; the
                // letterbox around the fitted picture stays black anyway.
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(165, 0, 0, 0)))
                {
                    if (sel.Top > img.Top)
                        g.FillRectangle(dim, img.Left, img.Top, img.Width, sel.Top - img.Top);
                    if (sel.Bottom < img.Bottom)
                        g.FillRectangle(dim, img.Left, sel.Bottom, img.Width, img.Bottom - sel.Bottom);
                    if (sel.Left > img.Left)
                        g.FillRectangle(dim, img.Left, sel.Top, sel.Left - img.Left, sel.Height);
                    if (sel.Right < img.Right)
                        g.FillRectangle(dim, sel.Right, sel.Top, img.Right - sel.Right, sel.Height);
                }

                using (Pen accent = new Pen(Theme.Accent, 2f))
                    g.DrawRectangle(accent, sel);
                using (Pen light = new Pen(Color.White, 1f))
                    g.DrawRectangle(light, Rectangle.Inflate(sel, -1, -1));
                using (SolidBrush fill = new SolidBrush(Color.White))
                using (Pen edge = new Pen(Theme.Accent, 1f))
                {
                    Point[] handles = HandlePoints(sel);
                    for (int i = 0; i < handles.Length; i++)
                    {
                        Rectangle h = new Rectangle(handles[i].X - 4, handles[i].Y - 4, 8, 8);
                        g.FillRectangle(fill, h);
                        g.DrawRectangle(edge, h);
                    }
                }
            }

            private static Point[] HandlePoints(Rectangle r)
            {
                int mx = r.Left + r.Width / 2;
                int my = r.Top + r.Height / 2;
                return new Point[]
                {
                    new Point(r.Left, r.Top), new Point(mx, r.Top), new Point(r.Right, r.Top),
                    new Point(r.Right, my), new Point(r.Right, r.Bottom),
                    new Point(mx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, my)
                };
            }

            // 0 = nothing under the cursor, MoveMode = inside the selection,
            // otherwise a bitmask of the edges/corners to drag.
            private int HitTest(PointF s)
            {
                float tol = ToleranceSource();
                float x0 = _sel.X, y0 = _sel.Y, x1 = _sel.Right, y1 = _sel.Bottom;
                bool left = Math.Abs(s.X - x0) <= tol;
                bool right = Math.Abs(s.X - x1) <= tol;
                bool top = Math.Abs(s.Y - y0) <= tol;
                bool bottom = Math.Abs(s.Y - y1) <= tol;

                if (left && top) return West | North;
                if (right && top) return East | North;
                if (left && bottom) return West | South;
                if (right && bottom) return East | South;
                if (top && s.X >= x0 - tol && s.X <= x1 + tol) return North;
                if (bottom && s.X >= x0 - tol && s.X <= x1 + tol) return South;
                if (left && s.Y >= y0 - tol && s.Y <= y1 + tol) return West;
                if (right && s.Y >= y0 - tol && s.Y <= y1 + tol) return East;
                if (s.X > x0 && s.X < x1 && s.Y > y0 && s.Y < y1) return MoveMode;
                return 0;
            }

            private static Cursor CursorFor(int mode)
            {
                switch (mode)
                {
                    case West | North:
                    case East | South: return Cursors.SizeNWSE;
                    case East | North:
                    case West | South: return Cursors.SizeNESW;
                    case North:
                    case South: return Cursors.SizeNS;
                    case West:
                    case East: return Cursors.SizeWE;
                    case MoveMode: return Cursors.SizeAll;
                    default: return Cursors.Default;
                }
            }

            private int MinSize()
            {
                if (_src.Width <= 0 || _src.Height <= 0) return 1;
                return Math.Min(MinSelection, Math.Min(_src.Width, _src.Height));
            }

            private static int Clamp(int v, int lo, int hi)
            {
                if (hi < lo) hi = lo;
                if (v < lo) v = lo;
                if (v > hi) v = hi;
                return v;
            }

            private Rectangle ApplyDrag(Rectangle start, int mode, int dx, int dy)
            {
                if (mode == MoveMode)
                {
                    return new Rectangle(
                        Clamp(start.X + dx, 0, _src.Width - start.Width),
                        Clamp(start.Y + dy, 0, _src.Height - start.Height),
                        start.Width, start.Height);
                }
                int min = MinSize();
                int x0 = start.X, y0 = start.Y, x1 = start.Right, y1 = start.Bottom;
                if ((mode & West) != 0) x0 = Clamp(start.X + dx, 0, x1 - min);
                if ((mode & North) != 0) y0 = Clamp(start.Y + dy, 0, y1 - min);
                if ((mode & East) != 0) x1 = Clamp(start.Right + dx, x0 + min, _src.Width);
                if ((mode & South) != 0) y1 = Clamp(start.Bottom + dy, y0 + min, _src.Height);
                return Rectangle.FromLTRB(x0, y0, x1, y1);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || _src.Width <= 0) return;
                Focus();
                int mode = HitTest(ToSource(e.X, e.Y));
                if (mode == 0) return;
                _mode = mode;
                _downSrc = ToSource(e.X, e.Y);
                _downSel = _sel;
                Capture = true;
                Cursor = CursorFor(mode);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_src.Width <= 0) return;
                PointF s = ToSource(e.X, e.Y);
                if (_mode == 0)
                {
                    Cursor = CursorFor(HitTest(s));
                    return;
                }
                int dx = (int)Math.Round(s.X - _downSrc.X);
                int dy = (int)Math.Round(s.Y - _downSrc.Y);
                Rectangle next = ApplyDrag(_downSel, _mode, dx, dy);
                if (next != _sel)
                {
                    _sel = next;
                    if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _mode = 0;
                if (Capture) Capture = false;
                Cursor = CursorFor(HitTest(ToSource(e.X, e.Y)));
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                _mode = 0;
            }
        }
    }
}
