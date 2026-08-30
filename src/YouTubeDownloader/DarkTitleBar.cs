using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    internal static class ChromeApi
    {
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public const int SmCxSizeFrame = 32;
        public const int SmCySizeFrame = 33;
        public const int SmCxPaddedBorder = 92;

        public const int WmNcCalcSize = 0x83;
        public const int WmNcHitTest = 0x84;
        public const int WmGetMinMaxInfo = 0x24;

        public const int HtClient = 1;
        public const int HtCaption = 2;
        public const int HtLeft = 10;
        public const int HtRight = 11;
        public const int HtTop = 12;
        public const int HtTopLeft = 13;
        public const int HtTopRight = 14;
        public const int HtBottom = 15;
        public const int HtBottomLeft = 16;
        public const int HtBottomRight = 17;
        public const int HtTransparent = -1;

        public const int WsMaximize = 0x01000000;
        public const int GwlStyle = -16;
        public const uint MonitorDefaultToNearest = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct ChromePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ChromeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo
        {
            public uint CbSize;
            public ChromeRect Monitor;
            public ChromeRect Work;
            public uint Flags;
        }

        public static bool IsZoomedStyle(IntPtr hwnd)
        {
            return (GetWindowLong(hwnd, GwlStyle) & WsMaximize) != 0;
        }
    }

    public class ChromePanel : Panel
    {
        private readonly Form _form;

        public ChromePanel(Form form)
        {
            _form = form;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ChromeApi.WmNcHitTest && _form.WindowState != FormWindowState.Maximized)
            {
                int sx = unchecked((short)((long)m.LParam & 0xFFFF));
                int sy = unchecked((short)(((long)m.LParam >> 16) & 0xFFFF));
                Rectangle wr = _form.RectangleToScreen(_form.ClientRectangle);
                int fw = ChromeApi.GetSystemMetrics(ChromeApi.SmCxSizeFrame) + ChromeApi.GetSystemMetrics(ChromeApi.SmCxPaddedBorder);
                int fh = ChromeApi.GetSystemMetrics(ChromeApi.SmCySizeFrame) + ChromeApi.GetSystemMetrics(ChromeApi.SmCxPaddedBorder);
                bool edge = sx < wr.Left + fw || sx >= wr.Right - fw || sy < wr.Top + fh || sy >= wr.Bottom - fh;
                if (edge)
                {
                    m.Result = (IntPtr)ChromeApi.HtTransparent;
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    public class DarkTitleBar : Panel
    {
        public const int BarHeight = 36;
        public const int FrameWidth = 8;

        private readonly Form _form;
        private readonly Button _minBtn;
        private readonly Button _maxBtn;
        private readonly Button _closeBtn;
        private readonly Font _glyphFont = new Font("Segoe MDL2 Assets", 9.5F);
        private const string GlyphMin = "\uE921";
        private const string GlyphMax = "\uE922";
        private const string GlyphRestore = "\uE923";
        private const string GlyphClose = "\uE8BB";

        public DarkTitleBar(Form form)
        {
            _form = form;
            BackColor = Theme.Back;
            DoubleBuffered = true;

            _minBtn = MakeButton(GlyphMin, "Minimize", delegate { _form.WindowState = FormWindowState.Minimized; });
            _maxBtn = MakeButton(GlyphMax, "Maximize", delegate
            {
                if (_form.WindowState == FormWindowState.Maximized) _form.WindowState = FormWindowState.Normal;
                else _form.WindowState = FormWindowState.Maximized;
            });
            _closeBtn = MakeButton(GlyphClose, "Close", delegate { _form.Close(); });
            _closeBtn.MouseEnter += delegate { _closeBtn.BackColor = Color.FromArgb(196, 43, 28); };
            _closeBtn.MouseLeave += delegate { _closeBtn.BackColor = Theme.Back; };
            Controls.Add(_minBtn);
            Controls.Add(_maxBtn);
            Controls.Add(_closeBtn);
            PositionButtons();

            Dock = DockStyle.Top;
            Height = BarHeight;

            form.SizeChanged += delegate { UpdateMaxGlyph(); };
        }

        private Button MakeButton(string glyph, string accName, EventHandler onClick)
        {
            Button b = new Button();
            b.Text = glyph;
            b.Font = _glyphFont;
            b.AccessibleName = accName;
            b.Size = new Size(46, BarHeight);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Theme.Back;
            b.ForeColor = Theme.Light;
            b.TabStop = false;
            b.MouseEnter += delegate { if (b != _closeBtn) b.BackColor = Theme.ButtonHover; };
            b.MouseLeave += delegate { if (b != _closeBtn) b.BackColor = Theme.Back; };
            b.Click += onClick;
            return b;
        }

        private void PositionButtons()
        {
            if (_closeBtn == null || _maxBtn == null || _minBtn == null) return;
            _closeBtn.Location = new Point(Width - 46, 0);
            _maxBtn.Location = new Point(Width - 92, 0);
            _minBtn.Location = new Point(Width - 138, 0);
        }

        private void UpdateMaxGlyph()
        {
            if (_maxBtn == null) return;
            _maxBtn.Text = _form.WindowState == FormWindowState.Maximized ? GlyphRestore : GlyphMax;
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            PositionButtons();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_form.Icon != null) e.Graphics.DrawIcon(_form.Icon, new Rectangle(10, (BarHeight - 16) / 2, 16, 16));
            TextRenderer.DrawText(e.Graphics, _form.Text, Theme.Regular,
                new Rectangle(34, 0, Width - 160, BarHeight), Theme.Light,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ChromeApi.WmNcHitTest)
            {
                m.Result = (IntPtr)ChromeApi.HtTransparent;
                return;
            }
            base.WndProc(ref m);
        }
    }
}
