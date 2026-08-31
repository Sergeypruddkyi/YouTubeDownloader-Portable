using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YouTubeDownloader
{
    internal static class Theme
    {
        public const float BaseSize = 11.5F;

        public static readonly Font Regular = new Font("Segoe UI", BaseSize);
        public static readonly Font BoldStatus = new Font("Segoe UI", BaseSize, FontStyle.Bold);
        public static readonly Font Log = new Font("Consolas", 10.5F);
        public static readonly Font Big = new Font("Segoe UI", 13F);

        public static readonly Color Back = Color.FromArgb(30, 30, 30);
        public static readonly Color Input = Color.FromArgb(45, 45, 48);
        public static readonly Color Button = Color.FromArgb(51, 51, 55);
        public static readonly Color ButtonHover = Color.FromArgb(63, 63, 70);
        public static readonly Color Border = Color.FromArgb(63, 63, 70);
        public static readonly Color Light = Color.FromArgb(225, 225, 225);
        public static readonly Color Dim = Color.FromArgb(154, 154, 154);
        public static readonly Color Green = Color.FromArgb(108, 203, 108);
        public static readonly Color Red = Color.FromArgb(255, 107, 107);
        public static readonly Color Orange = Color.FromArgb(255, 184, 77);
        public static readonly Color Accent = Color.FromArgb(0, 120, 212);

        public static void StyleButton(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = ButtonHover;
            b.BackColor = Button;
            b.ForeColor = Light;
        }

        public static void StyleAccent(Button b)
        {
            StyleButton(b);
            b.BackColor = Accent;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(25, 135, 230);
        }

        public static void StyleInput(TextBox t)
        {
            t.BackColor = Input;
            t.ForeColor = Light;
            t.BorderStyle = BorderStyle.FixedSingle;
        }
    }

    public class MainForm : Form
    {
        private const int CollapsedHeight = 400;
        private const int ExpandedHeight = 593;

        private readonly Settings _settings = new Settings(AppPaths.SettingsPath);

        private TextBox txtUrl;
        private TextBox txtFolder;
        private TextBox txtLog;
        private Label lblUrl;
        private Label lblFolder;
        private Label lblQuality;
        private Label lblTitle;
        private Label lblYtStatus;
        private Label lblProgress;
        private Label lblInfo;
        private Label lblStatus;
        private ProgressBar pb;
        private Button btnPaste;
        private Button btnBrowse;
        private Button btnDownload;
        private Button btnCancel;
        private Button btnCheckUpdate;
        private Button btnOpenFolder;
        private Button btnLogToggle;
        private ComboBox cboLang;
        private ComboBox cboQuality;
        private Panel content;
        private DarkTitleBar titleBar;

        private readonly Timer _clipTimer = new Timer();
        private readonly Timer _titleTimer = new Timer();
        private bool _logVisible;
        private string _lastAutoUrl;
        private string _titleForUrl;
        private string _titleValue;
        private long? _titleSizeBytes;
        private int _titleSeq;
        private Process _proc;
        private volatile bool _downloading;
        private volatile bool _updateRunning;
        private bool _canceled;
        private bool _autoRetried;
        private string[] _lastArgs;
        private string _currentFile;
        private bool _alreadyDownloaded;
        private int _lastExitCode;
        private string _ytVersion = "";
        private bool _ytVersionFailed;
        private string _missingComponent;
        private bool _ffprobeMissing;
        private DownloadQuality _quality = DownloadQuality.BestAvailable;
        private DownloadQuality _lastQuality = DownloadQuality.BestAvailable;

        private Msg _statusKey = Msg.StatusReady;
        private object[] _statusArgs = new object[0];
        private Msg _infoKey = Msg.InfoPreparing;
        private object[] _infoArgs = new object[0];
        private string _infoRaw = "";
        private Msg? _progressKey;
        private string _progressRaw = "";

        public MainForm()
        {
            _settings.Load();
            L10n.SetFromSetting(_settings.Language);
            _quality = _settings.Quality;
            Text = "YouTube Downloader";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            FormBorderStyle = FormBorderStyle.None;
            Font = Theme.Regular;
            BackColor = Theme.Back;
            ClientSize = new Size(790, CollapsedHeight);
            MinimumSize = new Size(790, CollapsedHeight);
            StartPosition = FormStartPosition.CenterScreen;
            FormClosing += OnFormClosing;
            BuildUi();

            // Масштабирование под DPI монитора выполняется ДО создания хэндла:
            // после CreateHandle первый WM_NCCALCSIZE (borderless-chrome) искажает
            // ClientSize, и якоря контента снимают неверные дистанции
            _dpiScale = ChromeApi.GetDpiForWindowAt(Cursor.Position) / 96f;
            if (_dpiScale > 0.999f && _dpiScale < 1.001f) _dpiScale = 1f;
            if (_dpiScale != 1f)
            {
                Scale(new SizeF(_dpiScale, _dpiScale));
                MinimumSize = ScaledSize(790, CollapsedHeight);
                ClientSize = ScaledSize(790, CollapsedHeight);
            }
        }

        private void BuildUi()
        {
            content = new ChromePanel(this);
            content.Location = new Point(0, DarkTitleBar.BarHeight);
            content.Size = new Size(ClientSize.Width, ClientSize.Height - DarkTitleBar.BarHeight);
            content.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            content.BackColor = Theme.Back;

            lblUrl = new Label();
            lblUrl.Location = new Point(12, 17);
            lblUrl.Size = new Size(58, 24);
            lblUrl.ForeColor = Theme.Light;
            content.Controls.Add(lblUrl);

            txtUrl = new TextBox();
            txtUrl.Location = new Point(75, 15);
            txtUrl.Size = new Size(600, 29);
            txtUrl.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Theme.StyleInput(txtUrl);
            txtUrl.TextChanged += delegate { UpdateUrlStatus(); };
            content.Controls.Add(txtUrl);

            btnPaste = new Button();
            btnPaste.Location = new Point(680, 14);
            btnPaste.Size = new Size(98, 30);
            btnPaste.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            Theme.StyleButton(btnPaste);
            btnPaste.Click += delegate { PasteFromClipboard(); };
            content.Controls.Add(btnPaste);

            lblTitle = new Label();
            lblTitle.Location = new Point(75, 47);
            lblTitle.Size = new Size(600, 22);
            lblTitle.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            lblTitle.ForeColor = Theme.Dim;
            lblTitle.AutoEllipsis = true;
            content.Controls.Add(lblTitle);

            lblFolder = new Label();
            lblFolder.Location = new Point(12, 77);
            lblFolder.Size = new Size(58, 24);
            lblFolder.ForeColor = Theme.Light;
            content.Controls.Add(lblFolder);

            txtFolder = new TextBox();
            txtFolder.Location = new Point(75, 74);
            txtFolder.Size = new Size(530, 29);
            txtFolder.ReadOnly = true;
            txtFolder.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            txtFolder.BackColor = Theme.Input;
            txtFolder.ForeColor = Theme.Dim;
            txtFolder.BorderStyle = BorderStyle.FixedSingle;
            content.Controls.Add(txtFolder);

            btnBrowse = new Button();
            btnBrowse.Location = new Point(610, 73);
            btnBrowse.Size = new Size(80, 30);
            btnBrowse.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            Theme.StyleButton(btnBrowse);
            btnBrowse.Click += delegate { BrowseFolder(); };
            content.Controls.Add(btnBrowse);

            lblQuality = new Label();
            lblQuality.Location = new Point(12, 109);
            lblQuality.Size = new Size(84, 24);
            lblQuality.ForeColor = Theme.Light;
            content.Controls.Add(lblQuality);

            cboQuality = new ComboBox();
            cboQuality.DropDownStyle = ComboBoxStyle.DropDownList;
            cboQuality.Location = new Point(96, 106);
            cboQuality.Size = new Size(220, 29);
            cboQuality.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            cboQuality.BackColor = Theme.Input;
            cboQuality.ForeColor = Theme.Light;
            cboQuality.SelectedIndexChanged += delegate { OnQualityComboChanged(); };
            content.Controls.Add(cboQuality);

            lblYtStatus = new Label();
            lblYtStatus.Location = new Point(75, 140);
            lblYtStatus.Size = new Size(290, 22);
            lblYtStatus.ForeColor = Theme.Dim;
            content.Controls.Add(lblYtStatus);

            btnCheckUpdate = new Button();
            btnCheckUpdate.Location = new Point(460, 138);
            btnCheckUpdate.Size = new Size(220, 30);
            btnCheckUpdate.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            Theme.StyleButton(btnCheckUpdate);
            btnCheckUpdate.Click += async delegate { await RunUpdateFlowAsync(false); };
            content.Controls.Add(btnCheckUpdate);

            cboLang = new ComboBox();
            cboLang.DropDownStyle = ComboBoxStyle.DropDownList;
            cboLang.Items.Add("English");
            cboLang.Items.Add("Русский");
            cboLang.SelectedIndex = L10n.Current == Lang.Ru ? 1 : 0;
            cboLang.Location = new Point(688, 138);
            cboLang.Width = 90;
            cboLang.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            cboLang.BackColor = Theme.Input;
            cboLang.ForeColor = Theme.Light;
            cboLang.SelectedIndexChanged += delegate { OnLanguageComboChanged(); };
            content.Controls.Add(cboLang);

            btnDownload = new Button();
            btnDownload.Font = Theme.Big;
            btnDownload.Location = new Point(12, 174);
            btnDownload.Size = new Size(370, 52);
            btnDownload.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Theme.StyleAccent(btnDownload);
            btnDownload.Click += async delegate { await StartDownloadAsync(); };
            content.Controls.Add(btnDownload);
            AcceptButton = btnDownload;

            btnCancel = new Button();
            btnCancel.Location = new Point(392, 174);
            btnCancel.Size = new Size(120, 52);
            btnCancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnCancel.Enabled = false;
            Theme.StyleButton(btnCancel);
            btnCancel.Click += delegate { CancelDownload(); };
            content.Controls.Add(btnCancel);

            btnOpenFolder = new Button();
            btnOpenFolder.Location = new Point(522, 174);
            btnOpenFolder.Size = new Size(256, 52);
            btnOpenFolder.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnOpenFolder.Enabled = false;
            Theme.StyleButton(btnOpenFolder);
            btnOpenFolder.Click += delegate { OpenFolder(); };
            content.Controls.Add(btnOpenFolder);

            pb = new ProgressBar();
            pb.Location = new Point(12, 234);
            pb.Size = new Size(766, 24);
            pb.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            content.Controls.Add(pb);

            lblProgress = new Label();
            lblProgress.Location = new Point(12, 262);
            lblProgress.Size = new Size(766, 22);
            lblProgress.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            lblProgress.ForeColor = Theme.Light;
            content.Controls.Add(lblProgress);

            lblInfo = new Label();
            lblInfo.Location = new Point(12, 286);
            lblInfo.Size = new Size(640, 22);
            lblInfo.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            lblInfo.ForeColor = Theme.Dim;
            content.Controls.Add(lblInfo);

            btnLogToggle = new Button();
            btnLogToggle.Location = new Point(656, 284);
            btnLogToggle.Size = new Size(122, 30);
            btnLogToggle.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            Theme.StyleButton(btnLogToggle);
            btnLogToggle.Click += delegate { ToggleLog(); };
            content.Controls.Add(btnLogToggle);

            txtLog = new TextBox();
            txtLog.Location = new Point(12, 318);
            txtLog.Size = new Size(766, 196);
            txtLog.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Font = Theme.Log;
            txtLog.WordWrap = false;
            txtLog.Visible = false;
            txtLog.BackColor = Theme.Back;
            txtLog.ForeColor = Theme.Light;
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            content.Controls.Add(txtLog);

            lblStatus = new Label();
            lblStatus.Location = new Point(12, 519);
            lblStatus.Size = new Size(766, 22);
            lblStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lblStatus.Font = Theme.BoldStatus;
            lblStatus.AutoEllipsis = true;
            lblStatus.ForeColor = Theme.Dim;
            content.Controls.Add(lblStatus);

            Controls.Add(content);
            titleBar = new DarkTitleBar(this);
            Controls.Add(titleBar);

            // txtLog/lblStatus заданы в expanded-координатах; снапшотим их якоря
            // к expanded-контенту и возвращаем collapsed, иначе bottom-дистанции
            // снимаются с collapsed-высоты и статус оказывается вне окна
            content.Size = new Size(790, ExpandedHeight - DarkTitleBar.BarHeight);
            txtLog.SetBounds(12, 318, 766, 196);
            lblStatus.SetBounds(12, 519, 766, 22);
            content.Size = new Size(790, CollapsedHeight - DarkTitleBar.BarHeight);

            ApplyStrings();
            SetStatus(Msg.StatusReady, Theme.Dim);
            UpdateUrlStatus();
        }

        private void ApplyStrings()
        {
            lblUrl.Text = L10n.T(Msg.LabelUrl);
            lblFolder.Text = L10n.T(Msg.LabelFolder);
            lblQuality.Text = L10n.T(Msg.LabelQuality);
            ApplyQualityItems();
            btnPaste.Text = L10n.T(Msg.BtnPaste);
            btnBrowse.Text = L10n.T(Msg.BtnBrowse);
            btnCheckUpdate.Text = L10n.T(Msg.BtnCheckUpdate);
            btnDownload.Text = L10n.T(Msg.BtnDownload);
            btnCancel.Text = L10n.T(Msg.BtnCancel);
            btnOpenFolder.Text = L10n.T(Msg.BtnOpenFolder);
            btnLogToggle.Text = L10n.T(_logVisible ? Msg.BtnHideLog : Msg.BtnShowLog);
        }

        private void ApplyQualityItems()
        {
            cboQuality.BeginUpdate();
            cboQuality.Items.Clear();
            cboQuality.Items.Add(L10n.T(Msg.QualityBest));
            cboQuality.Items.Add(L10n.T(Msg.Quality1080));
            cboQuality.Items.Add(L10n.T(Msg.Quality720));
            cboQuality.Items.Add(L10n.T(Msg.Quality480));
            cboQuality.Items.Add(L10n.T(Msg.Quality360));
            cboQuality.Items.Add(L10n.T(Msg.QualityAudio));
            cboQuality.EndUpdate();
            cboQuality.SelectedIndex = QualityIndex(_quality);
        }

        private static int QualityIndex(DownloadQuality mode)
        {
            switch (mode)
            {
                case DownloadQuality.P1080: return 1;
                case DownloadQuality.P720: return 2;
                case DownloadQuality.P480: return 3;
                case DownloadQuality.P360: return 4;
                case DownloadQuality.AudioOnly: return 5;
                default: return 0;
            }
        }

        private static DownloadQuality ModeFromIndex(int index)
        {
            switch (index)
            {
                case 1: return DownloadQuality.P1080;
                case 2: return DownloadQuality.P720;
                case 3: return DownloadQuality.P480;
                case 4: return DownloadQuality.P360;
                case 5: return DownloadQuality.AudioOnly;
                default: return DownloadQuality.BestAvailable;
            }
        }

        private void OnQualityComboChanged()
        {
            DownloadQuality mode = ModeFromIndex(cboQuality.SelectedIndex);
            if (mode == _quality) return;
            _quality = mode;
            _settings.Quality = mode;
            _settings.Save();
            RestartTitleFetchForQuality();
        }

        private void RestartTitleFetchForQuality()
        {
            if (_downloading || _updateRunning) return;
            string t = txtUrl.Text.Trim();
            if (t.Length == 0 || !YouTubeUrl.IsValid(t)) return;
            _titleForUrl = null;
            _titleSizeBytes = null;
            lblTitle.Text = L10n.T(Msg.TitleFetching);
            lblTitle.ForeColor = Theme.Dim;
            _titleTimer.Stop();
            _titleTimer.Start();
        }

        private void OnLanguageComboChanged()
        {
            Lang sel = cboLang.SelectedIndex == 1 ? Lang.Ru : Lang.En;
            if (sel == L10n.Current) return;
            L10n.Set(sel);
            _settings.Language = L10n.ToSetting();
            _settings.Save();
            ApplyUiLanguage();
        }

        private void ApplyUiLanguage()
        {
            ApplyStrings();
            UpdateUrlStatus();
            if (_titleValue != null && _titleForUrl != null) RenderTitleLine();
            RenderYtStatus();
            RenderStatus();
            RenderInfo();
            RenderProgress();
        }

        private float _dpiScale = 1f;

        private Size ScaledSize(int width, int height)
        {
            return new Size((int)Math.Round(width * _dpiScale), (int)Math.Round(height * _dpiScale));
        }

        private void ToggleLog()
        {
            _logVisible = !_logVisible;
            txtLog.Visible = _logVisible;
            btnLogToggle.Text = L10n.T(_logVisible ? Msg.BtnHideLog : Msg.BtnShowLog);
            Size target = ScaledSize(790, _logVisible ? ExpandedHeight : CollapsedHeight);
            MinimumSize = target;
            ClientSize = target;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            string lf = _settings.LastFolder;
            if (_settings.HasChosenFolder && !string.IsNullOrEmpty(lf) && Directory.Exists(lf))
            {
                txtFolder.Text = lf;
            }
            else
            {
                SetStatus(Msg.StatusNoFolder, Theme.Dim);
            }

            _titleTimer.Interval = 800;
            _titleTimer.Tick += delegate
            {
                _titleTimer.Stop();
                TryFetchTitle(txtUrl.Text.Trim());
            };

            _missingComponent = AppPaths.MissingCoreComponent();
            if (_missingComponent != null)
            {
                RenderYtStatus();
                SetStatus(Msg.StatusMissingCannotDownload, Theme.Red, _missingComponent);
            }
            else
            {
                _ffprobeMissing = !AppPaths.FfprobePresent();
                if (_ffprobeMissing)
                {
                    lblYtStatus.Text = L10n.T(Msg.NoFfprobe);
                    lblYtStatus.ForeColor = Theme.Orange;
                }
                RefreshYtVersion();
            }

            ReadClipboardNow();
            _clipTimer.Interval = 1500;
            _clipTimer.Tick += delegate { PollClipboard(); };
            _clipTimer.Start();
        }

        private void RenderYtStatus()
        {
            if (_missingComponent != null)
            {
                lblYtStatus.Text = L10n.T(Msg.BundleIncomplete, _missingComponent);
                lblYtStatus.ForeColor = Theme.Red;
                return;
            }
            if (_ffprobeMissing)
            {
                lblYtStatus.Text = L10n.T(Msg.NoFfprobe);
                lblYtStatus.ForeColor = Theme.Orange;
                return;
            }
            if (!string.IsNullOrEmpty(_ytVersion))
            {
                lblYtStatus.Text = L10n.T(Msg.YtReady, _ytVersion);
                lblYtStatus.ForeColor = Theme.Green;
                return;
            }
            if (_ytVersionFailed)
            {
                lblYtStatus.Text = L10n.T(Msg.YtFailed);
                lblYtStatus.ForeColor = Theme.Red;
                return;
            }
            lblYtStatus.Text = L10n.T(Msg.YtPending);
            lblYtStatus.ForeColor = Theme.Dim;
        }

        private void ApplyYtVersion(string version)
        {
            _ytVersion = version;
            _ytVersionFailed = false;
            lblYtStatus.Text = L10n.T(Msg.YtReady, version);
            lblYtStatus.ForeColor = Theme.Green;
        }

        private void RefreshYtVersion()
        {
            lblYtStatus.Text = L10n.T(Msg.YtPending);
            lblYtStatus.ForeColor = Theme.Dim;
            YtDlpRunner.GetVersionAsync().ContinueWith(delegate(Task<YtDlpRunner.VersionResult> task)
            {
                YtDlpRunner.VersionResult r = task.Result;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (r.ExitCode == 0 && !string.IsNullOrEmpty(r.Version)) ApplyYtVersion(r.Version);
                    else
                    {
                        _ytVersionFailed = true;
                        lblYtStatus.Text = L10n.T(Msg.YtFailed);
                        lblYtStatus.ForeColor = Theme.Red;
                    }
                });
            });
        }

        private void SetStatus(Msg key, Color color, params object[] args)
        {
            _statusKey = key;
            _statusArgs = args != null ? args : new object[0];
            lblStatus.Text = L10n.T(key, _statusArgs);
            lblStatus.ForeColor = color;
        }

        private void RenderStatus()
        {
            lblStatus.Text = L10n.T(_statusKey, _statusArgs);
        }

        private void SetInfo(Msg key, params object[] args)
        {
            _infoKey = key;
            _infoArgs = args != null ? args : new object[0];
            _infoRaw = null;
            lblInfo.Text = L10n.T(key, _infoArgs);
        }

        private void SetInfoRaw(string text)
        {
            _infoRaw = text;
            lblInfo.Text = text;
        }

        private void RenderInfo()
        {
            lblInfo.Text = _infoRaw != null ? _infoRaw : L10n.T(_infoKey, _infoArgs);
        }

        private void SetProgressRaw(string text)
        {
            _progressKey = null;
            _progressRaw = text;
            lblProgress.Text = text;
        }

        private void SetProgressFinal(Msg key)
        {
            _progressKey = key;
            lblProgress.Text = L10n.T(key);
        }

        private void RenderProgress()
        {
            lblProgress.Text = _progressKey.HasValue ? L10n.T(_progressKey.Value) : _progressRaw;
        }

        private void SetBusy()
        {
            bool busy = _downloading || _updateRunning;
            btnDownload.Enabled = !busy;
            btnCheckUpdate.Enabled = !busy;
            btnBrowse.Enabled = !busy;
            btnPaste.Enabled = !busy;
            btnCancel.Enabled = _downloading;
            txtUrl.ReadOnly = _downloading;
        }

        private void AppendLog(string s)
        {
            if (txtLog.TextLength > 60000) txtLog.Clear();
            txtLog.AppendText(s + Environment.NewLine);
            txtLog.ScrollToCaret();
        }

        private string SafeClipboardText()
        {
            try { return Clipboard.GetText(); }
            catch (ExternalException) { return null; }
        }

        private void ReadClipboardNow()
        {
            string url = YouTubeUrl.ExtractFirst(SafeClipboardText());
            if (url != null) ApplyAutoUrl(url);
        }

        private void PollClipboard()
        {
            if (_downloading || _updateRunning) return;
            if (txtUrl.Focused) return;
            string url = YouTubeUrl.ExtractFirst(SafeClipboardText());
            if (url == null) return;
            string cur = txtUrl.Text.Trim();
            if (cur.Length == 0 || cur == _lastAutoUrl) ApplyAutoUrl(url);
        }

        private void ApplyAutoUrl(string url)
        {
            if (url == txtUrl.Text.Trim())
            {
                UpdateUrlStatus();
                return;
            }
            txtUrl.Text = url;
            _lastAutoUrl = url;
            UpdateUrlStatus();
            txtUrl.SelectionStart = txtUrl.Text.Length;
        }

        private void UpdateUrlStatus()
        {
            string t = txtUrl.Text.Trim();
            if (t.Length == 0)
            {
                lblTitle.Text = L10n.T(Msg.TitleWaiting);
                lblTitle.ForeColor = Theme.Dim;
                _titleForUrl = null;
                _titleValue = null;
                _titleSizeBytes = null;
                _titleTimer.Stop();
            }
            else if (YouTubeUrl.IsValid(t))
            {
                if (t == _titleForUrl && _titleValue != null)
                {
                    RenderTitleLine();
                }
                else
                {
                    lblTitle.Text = L10n.T(Msg.TitleFetching);
                    lblTitle.ForeColor = Theme.Dim;
                }
                _titleTimer.Stop();
                _titleTimer.Start();
            }
            else
            {
                lblTitle.Text = L10n.T(Msg.NotYouTube);
                lblTitle.ForeColor = Theme.Orange;
                _titleForUrl = null;
                _titleValue = null;
                _titleSizeBytes = null;
                _titleTimer.Stop();
            }
        }

        private void RenderTitleLine()
        {
            if (_titleValue == null)
            {
                lblTitle.Text = L10n.T(Msg.TitleWaiting);
                lblTitle.ForeColor = Theme.Dim;
                return;
            }
            string text;
            if (_titleSizeBytes.HasValue)
                text = L10n.T(Msg.InfoEstimatedSize, L10n.FormatSize(_titleSizeBytes.Value)) + "  ·  " + L10n.T(Msg.TitleIs, _titleValue);
            else
                text = L10n.T(Msg.TitleIs, _titleValue);
            lblTitle.Text = text;
            lblTitle.ForeColor = Theme.Light;
        }

        private void TryFetchTitle(string url)
        {
            if (_downloading || _updateRunning) return;
            if (!YouTubeUrl.IsValid(url)) return;
            if (url == _titleForUrl && _titleValue != null)
            {
                RenderTitleLine();
                return;
            }
            int seq = ++_titleSeq;
            lblTitle.Text = L10n.T(Msg.TitleFetching);
            lblTitle.ForeColor = Theme.Dim;
            DownloadQuality modeAtStart = _quality;
            Task.Run(delegate
            {
                YtDlpRunner.TitleSizeInfo info;
                bool ok = YtDlpRunner.TryGetTitleAndSize(modeAtStart, url, out info);
                BeginInvoke((MethodInvoker)delegate
                {
                    if (seq != _titleSeq) return;
                    if (ok && info != null && !string.IsNullOrEmpty(info.Title))
                    {
                        _titleForUrl = url;
                        _titleValue = info.Title;
                        _titleSizeBytes = info.SizeBytes;
                        RenderTitleLine();
                    }
                    else
                    {
                        _titleSizeBytes = null;
                        lblTitle.Text = L10n.T(Msg.TitleFailed);
                        lblTitle.ForeColor = Theme.Orange;
                    }
                });
            });
        }

        private void PasteFromClipboard()
        {
            string url = YouTubeUrl.ExtractFirst(SafeClipboardText());
            if (url != null)
            {
                txtUrl.Text = url;
                _lastAutoUrl = url;
                UpdateUrlStatus();
            }
            else
            {
                SetStatus(Msg.StatusNoUrlInClipboard, Theme.Orange);
            }
        }

        private void BrowseFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = L10n.T(Msg.FolderDlgDesc);
                string lf = txtFolder.Text;
                if (!string.IsNullOrEmpty(lf) && Directory.Exists(lf)) dlg.SelectedPath = lf;
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtFolder.Text = dlg.SelectedPath;
                    _settings.LastFolder = dlg.SelectedPath;
                    _settings.HasChosenFolder = true;
                    _settings.Save();
                }
            }
        }

        private void OpenFolder()
        {
            string folder = txtFolder.Text.Trim();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                try { Process.Start("explorer.exe", "\"" + folder + "\""); }
                catch { }
            }
        }

        private async Task StartDownloadAsync()
        {
            if (_downloading || _updateRunning) return;

            string url = txtUrl.Text.Trim();
            if (url.Length == 0)
            {
                MessageBox.Show(this, L10n.T(Msg.MsgNeedUrl), "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!YouTubeUrl.IsValid(url))
            {
                MessageBox.Show(this, L10n.T(Msg.MsgBadUrl), "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string folder = txtFolder.Text.Trim();
            if (folder.Length == 0)
            {
                BrowseFolder();
                folder = txtFolder.Text.Trim();
            }
            if (folder.Length == 0)
            {
                MessageBox.Show(this, L10n.T(Msg.MsgNoFolder), "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string missing = AppPaths.MissingCoreComponent();
            if (missing != null)
            {
                MessageBox.Show(this, L10n.T(Msg.MsgBundleBroken, missing), "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                SetStatus(Msg.StatusCreateFolderError, Theme.Red, ex.Message);
                return;
            }

            string[] args = YtDlpRunner.DownloadArgs(folder, url, _quality);
            await RunDownloadCoreAsync(args, true, _quality);
        }

        private async Task RunDownloadCoreAsync(string[] args, bool allowUpdateFlow, DownloadQuality mode)
        {
            _downloading = true;
            _canceled = false;
            _alreadyDownloaded = false;
            _currentFile = null;
            _lastArgs = args;
            _lastQuality = mode;
            SetBusy();

            pb.Value = 0;
            SetProgressRaw("");
            lblProgress.Font = Theme.Regular;
            lblProgress.ForeColor = Theme.Light;
            SetInfo(Msg.InfoPreparing);
            lblInfo.ForeColor = Theme.Dim;
            btnOpenFolder.Enabled = false;

            AppendLog("─────────────────────────────────────────────");
            AppendLog("$ yt.exe " + YtDlpRunner.FormatArgs(args));

            YtRunResult result = await YtDlpRunner.RunAsync(args, delegate(YtLine line) { OnYtLine(line); }, delegate(Process p) { _proc = p; });

            _downloading = false;
            _proc = null;
            SetBusy();

            if (_canceled)
            {
                SetInfoRaw("");
                SetStatus(Msg.StatusCanceled, Theme.Orange);
                return;
            }

            _lastExitCode = result.ExitCode;

            if (result.ExitCode == 0)
            {
                string name = _currentFile != null ? Path.GetFileName(_currentFile) : L10n.T(Msg.FileWord);
                SetProgressFinal(_alreadyDownloaded ? Msg.ProgressAlready : Msg.ProgressComplete);
                lblProgress.Font = Theme.BoldStatus;
                lblProgress.ForeColor = Theme.Green;
                SetInfoRaw(name);
                lblInfo.ForeColor = Theme.Light;
                pb.Value = 100;
                btnOpenFolder.Enabled = true;
                SetStatus(Msg.StatusDone, Theme.Green, name);
                return;
            }

            ErrorClassifier.Result cls = ErrorClassifier.Classify(result.Output, result.ExitCode);
            ShowErrorResult(cls, result.Output, allowUpdateFlow, mode);
        }

        private async void ShowErrorResult(ErrorClassifier.Result cls, string output, bool allowUpdateFlow, DownloadQuality mode)
        {
            string shortMsg;
            object shortArg;
            if (cls != null && cls.HintKey == Msg.HintFormatUnavailable && mode != DownloadQuality.BestAvailable)
            {
                shortMsg = L10n.T(Msg.HintQualityUnavailable);
                shortArg = Msg.HintQualityUnavailable;
            }
            else if (cls != null && cls.HintKey.HasValue)
            {
                shortMsg = L10n.T(cls.HintKey.Value);
                shortArg = cls.HintKey.Value;
            }
            else
            {
                shortMsg = L10n.T(Msg.ErrorUnknownYt, _lastExitCode);
                shortArg = new Sub(Msg.ErrorUnknownYt, _lastExitCode);
            }

            string detail = cls != null && cls.MatchedLine != null ? cls.MatchedLine : null;
            SetStatus(detail != null ? Msg.ErrorWithDetail : Msg.ErrorNoDetail, Theme.Red, shortArg, detail);

            if (cls != null && cls.Category == ErrorCategory.UpdateSuspect && allowUpdateFlow)
            {
                string yes = L10n.T(Msg.BtnYesCheck);
                string ans = PromptForm.Show(this, L10n.T(Msg.PromptVersionProblemTitle),
                    L10n.T(Msg.PromptVersionProblemMsg, shortMsg),
                    true, yes, L10n.T(Msg.BtnNo));
                if (ans == yes) await RunUpdateFlowAsync(true);
            }
        }

        private void CancelDownload()
        {
            if (!_downloading) return;
            _canceled = true;
            SetStatus(Msg.StatusStopping, Theme.Orange);
            Process p = _proc;
            if (p != null)
            {
                Task.Run(delegate { YtDlpRunner.KillTree(p); });
            }
        }

        private async Task RunUpdateFlowAsync(bool autoRetryAfterUpdate)
        {
            if (_downloading || _updateRunning) return;
            string missing = AppPaths.MissingCoreComponent();
            if (missing != null)
            {
                MessageBox.Show(this, L10n.T(Msg.MsgMissingPlain, missing), "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _updateRunning = true;
            SetBusy();
            SetStatus(Msg.StatusCheckingUpdate, Theme.Dim);

            try
            {
                string local = _ytVersion;
                if (string.IsNullOrEmpty(local))
                {
                    YtDlpRunner.VersionResult vr = await YtDlpRunner.GetVersionAsync();
                    local = vr.ExitCode == 0 ? vr.Version : null;
                }
                if (string.IsNullOrEmpty(local))
                {
                    MessageBox.Show(this, L10n.T(Msg.MsgLocalVersionFailed), L10n.T(Msg.CaptionUpdateCheck), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus(Msg.StatusVersionUndetected, Theme.Red);
                    return;
                }

                string latest = null;
                string netErr = null;
                try
                {
                    latest = await UpdateChecker.GetLatestVersionAsync();
                }
                catch (Exception ex)
                {
                    netErr = ex.Message;
                }

                if (latest == null)
                {
                    string useCurrent = L10n.T(Msg.BtnUseCurrent);
                    string ans = PromptForm.Show(this, L10n.T(Msg.CaptionUpdateCheck),
                        L10n.T(Msg.MsgCheckNetworkFail, netErr),
                        false, useCurrent, L10n.T(Msg.BtnTryLater));
                    if (ans == useCurrent)
                        SetStatus(Msg.StatusUsingCurrent, Theme.Dim, local);
                    else
                        SetStatus(Msg.StatusCheckPostponed, Theme.Dim);
                    return;
                }

                int cmp = UpdateChecker.CompareVersions(latest, local);
                if (cmp <= 0)
                {
                    MessageBox.Show(this, L10n.T(Msg.MsgUpToDate, local), L10n.T(Msg.CaptionUpdateCheck), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetStatus(Msg.StatusUpToDate, Theme.Dim, local);
                    return;
                }

                string update = L10n.T(Msg.BtnUpdate);
                string ans2 = PromptForm.Show(this, L10n.T(Msg.CaptionUpdateAvailable),
                    L10n.T(Msg.MsgUpdateAvailable, local, latest),
                    false, update, L10n.T(Msg.BtnCancel));
                if (ans2 != update)
                {
                    SetStatus(Msg.StatusUpdatePostponed, Theme.Dim);
                    return;
                }

                await PerformUpdateAsync(local, autoRetryAfterUpdate);
            }
            finally
            {
                _updateRunning = false;
                SetBusy();
            }
        }

        private async Task PerformUpdateAsync(string oldVersion, bool autoRetryAfterUpdate)
        {
            _updateRunning = true;
            SetBusy();
            try
            {
                SetStatus(Msg.StatusUpdating, Theme.Dim, oldVersion);
                AppendLog("$ yt.exe -U");
                YtRunResult r = await YtDlpRunner.RunAsync(YtDlpRunner.UpdateArgs(), delegate(YtLine line) { OnYtLine(line); }, delegate(Process p) { _proc = p; });

                YtDlpRunner.VersionResult vr = await YtDlpRunner.GetVersionAsync();
                bool ok = vr.ExitCode == 0 && UpdateChecker.CompareVersions(vr.Version, oldVersion) > 0;

                if (ok)
                {
                    ApplyYtVersion(vr.Version);
                    SetStatus(Msg.StatusYtUpdated, Theme.Green, oldVersion, vr.Version);
                    if (autoRetryAfterUpdate && !_autoRetried && _lastArgs != null)
                    {
                        _autoRetried = true;
                        AppendLog(L10n.T(Msg.StatusRetrying));
                        await RunDownloadCoreAsync(_lastArgs, false, _lastQuality);
                    }
                }
                else
                {
                    MessageBox.Show(this, L10n.T(Msg.MsgUpdateFailed), L10n.T(Msg.CaptionYtUpdate), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus(Msg.StatusUpdateNotPerformed, Theme.Red, oldVersion);
                }
            }
            finally
            {
                _updateRunning = false;
                SetBusy();
            }
        }

        private void OnYtLine(YtLine line)
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate { HandleYtLineUi(line); });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void HandleYtLineUi(YtLine line)
        {
            AppendLog(line.Text);

            Msg? phase = OutputParser.PhaseOf(line.Text);
            if (phase.HasValue && _currentFile == null) SetInfo(phase.Value);

            string dest = OutputParser.TryDestination(line.Text);
            if (dest != null)
            {
                _currentFile = dest;
                SetInfo(Msg.InfoDownloadingFile, Path.GetFileName(dest));
            }

            string merged = OutputParser.TryMergedFile(line.Text);
            if (merged != null)
            {
                _currentFile = merged;
                SetInfo(Msg.InfoMergingFile, Path.GetFileName(merged));
            }

            string extracted = OutputParser.TryExtractAudioDestination(line.Text);
            if (extracted != null)
            {
                _currentFile = extracted;
                SetInfo(Msg.InfoExtractingFile, Path.GetFileName(extracted));
            }

            if (OutputParser.IsAlreadyDownloaded(line.Text)) _alreadyDownloaded = true;

            double? pct = OutputParser.TryPercent(line.Text);
            if (pct.HasValue)
            {
                int v = (int)Math.Round(pct.Value);
                if (v > pb.Maximum) v = pb.Maximum;
                pb.Value = v;

                List<string> parts = new List<string>();
                parts.Add(pct.Value.ToString("0.0") + "%");
                string sp = OutputParser.TrySpeed(line.Text);
                if (sp != null && !sp.StartsWith("Unknown")) parts.Add(sp);
                string eta = OutputParser.TryEta(line.Text);
                if (eta != null) parts.Add("ETA " + eta);
                SetProgressRaw(string.Join("  ·  ", parts.ToArray()));
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyChromeStyles();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyChromeStyles();
            Size target = ScaledSize(790, CollapsedHeight);
            MinimumSize = target;
            ClientSize = target;
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
                if (titleBar != null && cpt.Y < titleBar.Height) { m.Result = (IntPtr)ChromeApi.HtCaption; return; }
                m.Result = (IntPtr)ChromeApi.HtClient;
                return;
            }
            base.WndProc(ref m);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_downloading && !_updateRunning) return;
            DialogResult r = MessageBox.Show(this,
                L10n.T(_downloading ? Msg.MsgExitDuringDownload : Msg.MsgExitDuringUpdate),
                "YouTube Downloader", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.No)
            {
                e.Cancel = true;
                return;
            }
            _canceled = true;
            Process p = _proc;
            if (p != null) YtDlpRunner.KillTreeNoWait(p);
        }
    }
}
