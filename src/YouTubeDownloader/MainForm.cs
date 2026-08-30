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

        public static readonly Font BoldStatus = new Font("Segoe UI", 10F, FontStyle.Bold);
        public static readonly Font Regular = new Font("Segoe UI", 9F);

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
        private const string AnsCheckYes = "Да, проверить";
        private const string AnsNo = "Нет";
        private const string AnsUseCurrent = "Использовать текущую версию";
        private const string AnsTryLater = "Попробовать позже";
        private const string AnsUpdate = "Обновить";
        private const string AnsCancel = "Отмена";

        private const int CollapsedHeight = 300;
        private const int ExpandedHeight = 508;

        private readonly Settings _settings = new Settings(AppPaths.SettingsPath);

        private TextBox txtUrl;
        private TextBox txtFolder;
        private TextBox txtLog;
        private Label lblUrlStatus;
        private Label lblYtStatus;
        private Label lblTitle;
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

        private readonly Timer _clipTimer = new Timer();
        private readonly Timer _titleTimer = new Timer();
        private bool _logVisible;
        private string _lastAutoUrl;
        private string _lastTitleUrl;
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

        public MainForm()
        {
            Text = "YouTube Downloader";
            Font = new Font("Segoe UI", 9F);
            BackColor = Theme.Back;
            ClientSize = new Size(790, CollapsedHeight);
            MinimumSize = new Size(700, 320);
            StartPosition = FormStartPosition.CenterScreen;
            FormClosing += OnFormClosing;
            BuildUi();
        }

        private void BuildUi()
        {
            Label l1 = new Label();
            l1.Text = "Ссылка:";
            l1.Location = new Point(12, 17);
            l1.Size = new Size(58, 20);
            l1.ForeColor = Theme.Light;
            Controls.Add(l1);

            txtUrl = new TextBox();
            txtUrl.Location = new Point(75, 14);
            txtUrl.Size = new Size(600, 23);
            txtUrl.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Theme.StyleInput(txtUrl);
            txtUrl.TextChanged += delegate { UpdateUrlStatus(); };
            Controls.Add(txtUrl);

            btnPaste = new Button();
            btnPaste.Text = "Вставить";
            btnPaste.Location = new Point(680, 13);
            btnPaste.Size = new Size(98, 25);
            btnPaste.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            Theme.StyleButton(btnPaste);
            btnPaste.Click += delegate { PasteFromClipboard(); };
            Controls.Add(btnPaste);

            lblUrlStatus = new Label();
            lblUrlStatus.Location = new Point(75, 40);
            lblUrlStatus.Size = new Size(600, 16);
            lblUrlStatus.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            lblUrlStatus.ForeColor = Theme.Dim;
            lblUrlStatus.Text = "Ожидание ссылки — скопируйте URL YouTube или введите вручную";
            Controls.Add(lblUrlStatus);

            lblTitle = new Label();
            lblTitle.Location = new Point(75, 57);
            lblTitle.Size = new Size(600, 15);
            lblTitle.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            lblTitle.ForeColor = Theme.Light;
            lblTitle.AutoEllipsis = true;
            Controls.Add(lblTitle);

            Label l2 = new Label();
            l2.Text = "Папка:";
            l2.Location = new Point(12, 78);
            l2.Size = new Size(58, 20);
            l2.ForeColor = Theme.Light;
            Controls.Add(l2);

            txtFolder = new TextBox();
            txtFolder.Location = new Point(75, 75);
            txtFolder.Size = new Size(530, 23);
            txtFolder.ReadOnly = true;
            txtFolder.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            txtFolder.BackColor = Theme.Input;
            txtFolder.ForeColor = Theme.Dim;
            txtFolder.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(txtFolder);

            btnBrowse = new Button();
            btnBrowse.Text = "Обзор…";
            btnBrowse.Location = new Point(610, 74);
            btnBrowse.Size = new Size(80, 25);
            btnBrowse.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            Theme.StyleButton(btnBrowse);
            btnBrowse.Click += delegate { BrowseFolder(); };
            Controls.Add(btnBrowse);

            lblYtStatus = new Label();
            lblYtStatus.Location = new Point(75, 104);
            lblYtStatus.Size = new Size(360, 20);
            lblYtStatus.ForeColor = Theme.Dim;
            Controls.Add(lblYtStatus);

            btnCheckUpdate = new Button();
            btnCheckUpdate.Text = "Проверить обновление yt-dlp";
            btnCheckUpdate.Location = new Point(445, 101);
            btnCheckUpdate.Size = new Size(233, 26);
            btnCheckUpdate.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            Theme.StyleButton(btnCheckUpdate);
            btnCheckUpdate.Click += async delegate { await RunUpdateFlowAsync(false); };
            Controls.Add(btnCheckUpdate);

            btnDownload = new Button();
            btnDownload.Text = "СКАЧАТЬ";
            btnDownload.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnDownload.Location = new Point(12, 135);
            btnDownload.Size = new Size(370, 44);
            btnDownload.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Theme.StyleAccent(btnDownload);
            btnDownload.Click += async delegate { await StartDownloadAsync(); };
            Controls.Add(btnDownload);
            AcceptButton = btnDownload;

            btnCancel = new Button();
            btnCancel.Text = "Отмена";
            btnCancel.Location = new Point(392, 135);
            btnCancel.Size = new Size(120, 44);
            btnCancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnCancel.Enabled = false;
            Theme.StyleButton(btnCancel);
            btnCancel.Click += delegate { CancelDownload(); };
            Controls.Add(btnCancel);

            btnOpenFolder = new Button();
            btnOpenFolder.Text = "Открыть папку";
            btnOpenFolder.Location = new Point(522, 135);
            btnOpenFolder.Size = new Size(256, 44);
            btnOpenFolder.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnOpenFolder.Enabled = false;
            Theme.StyleButton(btnOpenFolder);
            btnOpenFolder.Click += delegate { OpenFolder(); };
            Controls.Add(btnOpenFolder);

            pb = new ProgressBar();
            pb.Location = new Point(12, 196);
            pb.Size = new Size(766, 22);
            pb.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(pb);

            lblProgress = new Label();
            lblProgress.Location = new Point(12, 222);
            lblProgress.Size = new Size(766, 16);
            lblProgress.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            lblProgress.ForeColor = Theme.Light;
            Controls.Add(lblProgress);

            lblInfo = new Label();
            lblInfo.Location = new Point(12, 240);
            lblInfo.Size = new Size(640, 16);
            lblInfo.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            lblInfo.ForeColor = Theme.Dim;
            Controls.Add(lblInfo);

            btnLogToggle = new Button();
            btnLogToggle.Text = "Показать лог";
            btnLogToggle.Location = new Point(656, 238);
            btnLogToggle.Size = new Size(122, 25);
            btnLogToggle.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            Theme.StyleButton(btnLogToggle);
            btnLogToggle.Click += delegate { ToggleLog(); };
            Controls.Add(btnLogToggle);

            txtLog = new TextBox();
            txtLog.Location = new Point(12, 268);
            txtLog.Size = new Size(766, 207);
            txtLog.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.WordWrap = false;
            txtLog.Visible = false;
            txtLog.BackColor = Theme.Back;
            txtLog.ForeColor = Theme.Light;
            txtLog.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(txtLog);

            lblStatus = new Label();
            lblStatus.Location = new Point(12, 483);
            lblStatus.Size = new Size(766, 18);
            lblStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lblStatus.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblStatus.AutoEllipsis = true;
            lblStatus.ForeColor = Theme.Dim;
            lblStatus.Text = "Готово.";
            Controls.Add(lblStatus);
        }

        private void ToggleLog()
        {
            _logVisible = !_logVisible;
            txtLog.Visible = _logVisible;
            btnLogToggle.Text = _logVisible ? "Скрыть лог" : "Показать лог";
            MinimumSize = _logVisible ? new Size(700, 520) : new Size(700, 320);
            ClientSize = _logVisible ? new Size(790, ExpandedHeight) : new Size(790, CollapsedHeight);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _settings.Load();
            string lf = _settings.LastFolder;
            if (_settings.HasChosenFolder && !string.IsNullOrEmpty(lf))
            {
                txtFolder.Text = lf;
            }
            else
            {
                SetStatus("Папка назначения не выбрана — нажмите «Обзор…» перед первым скачиванием.", Theme.Dim);
            }

            _titleTimer.Interval = 800;
            _titleTimer.Tick += delegate
            {
                _titleTimer.Stop();
                TryFetchTitle(txtUrl.Text.Trim());
            };

            string missing = AppPaths.MissingCoreComponent();
            if (missing != null)
            {
                lblYtStatus.Text = "Комплект неполный: не найден " + missing;
                lblYtStatus.ForeColor = Theme.Red;
                SetStatus("В папке приложения отсутствует " + missing + ". Скачать невозможно.", Theme.Red);
            }
            else if (!AppPaths.FfprobePresent())
            {
                lblYtStatus.Text = "ffprobe.exe не найден (не критично)";
                lblYtStatus.ForeColor = Theme.Orange;
                RefreshYtVersion();
            }
            else
            {
                RefreshYtVersion();
            }

            ReadClipboardNow();
            _clipTimer.Interval = 1500;
            _clipTimer.Tick += delegate { PollClipboard(); };
            _clipTimer.Start();
        }

        private void ApplyYtVersion(string version)
        {
            _ytVersion = version;
            lblYtStatus.Text = "yt-dlp: " + version + " — готов";
            lblYtStatus.ForeColor = Theme.Green;
        }

        private void RefreshYtVersion()
        {
            lblYtStatus.Text = "yt-dlp: определение версии…";
            lblYtStatus.ForeColor = Theme.Dim;
            YtDlpRunner.GetVersionAsync().ContinueWith(delegate(Task<YtDlpRunner.VersionResult> task)
            {
                YtDlpRunner.VersionResult r = task.Result;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (r.ExitCode == 0 && !string.IsNullOrEmpty(r.Version)) ApplyYtVersion(r.Version);
                    else
                    {
                        lblYtStatus.Text = "yt-dlp: не удалось определить версию";
                        lblYtStatus.ForeColor = Theme.Red;
                    }
                });
            });
        }

        private void SetStatus(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;
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
                lblUrlStatus.Text = "Ожидание ссылки — скопируйте URL YouTube или введите вручную";
                lblUrlStatus.ForeColor = Theme.Dim;
                lblTitle.Text = "";
                _titleTimer.Stop();
            }
            else if (YouTubeUrl.IsValid(t))
            {
                lblUrlStatus.Text = "YouTube-ссылка распознана";
                lblUrlStatus.ForeColor = Theme.Green;
                _titleTimer.Stop();
                _titleTimer.Start();
            }
            else
            {
                lblUrlStatus.Text = "Не похоже на YouTube-ссылку";
                lblUrlStatus.ForeColor = Theme.Orange;
                lblTitle.Text = "";
                _titleTimer.Stop();
            }
        }

        private void TryFetchTitle(string url)
        {
            if (_downloading || _updateRunning) return;
            if (!YouTubeUrl.IsValid(url)) return;
            if (url == _lastTitleUrl) return;
            _lastTitleUrl = url;
            int seq = ++_titleSeq;
            lblTitle.Text = "Получение названия…";
            lblTitle.ForeColor = Theme.Dim;
            Task.Run(delegate
            {
                string title;
                bool ok = YtDlpRunner.TryGetTitle(url, out title);
                BeginInvoke((MethodInvoker)delegate
                {
                    if (seq != _titleSeq) return;
                    if (ok && !string.IsNullOrEmpty(title))
                    {
                        lblTitle.Text = "Название: " + title;
                        lblTitle.ForeColor = Theme.Light;
                    }
                    else
                    {
                        lblTitle.Text = "Название получить не удалось (не критично для скачивания)";
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
                SetStatus("В буфере обмена не найдена YouTube-ссылка.", Theme.Orange);
            }
        }

        private void BrowseFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Выберите папку для скачивания видео";
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
                MessageBox.Show(this, "Введите или скопируйте ссылку YouTube.", "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!YouTubeUrl.IsValid(url))
            {
                MessageBox.Show(this, "Поле не содержит распознанной YouTube-ссылки. Проверьте ссылку.", "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                MessageBox.Show(this, "Не выбрана папка назначения.", "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string missing = AppPaths.MissingCoreComponent();
            if (missing != null)
            {
                MessageBox.Show(this, "В папке приложения отсутствует " + missing + ". Комплект повреждён.", "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                SetStatus("Ошибка: не удалось создать папку — " + ex.Message, Theme.Red);
                return;
            }

            string[] args = YtDlpRunner.DownloadArgs(folder, url);
            await RunDownloadCoreAsync(args, true);
        }

        private async Task RunDownloadCoreAsync(string[] args, bool allowUpdateFlow)
        {
            _downloading = true;
            _canceled = false;
            _alreadyDownloaded = false;
            _currentFile = null;
            _lastArgs = args;
            SetBusy();

            pb.Value = 0;
            lblProgress.Text = "";
            lblProgress.Font = Theme.Regular;
            lblProgress.ForeColor = Theme.Light;
            lblInfo.Text = "Подготовка…";
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
                lblInfo.Text = "";
                SetStatus("Скачивание отменено пользователем.", Theme.Orange);
                return;
            }

            _lastExitCode = result.ExitCode;

            if (result.ExitCode == 0)
            {
                string name = _currentFile != null ? Path.GetFileName(_currentFile) : "файл";
                lblProgress.Text = _alreadyDownloaded ? "✓ ФАЙЛ УЖЕ БЫЛ СКАЧАН" : "✓ СКАЧИВАНИЕ ЗАВЕРШЕНО";
                lblProgress.Font = Theme.BoldStatus;
                lblProgress.ForeColor = Theme.Green;
                lblInfo.Text = name;
                lblInfo.ForeColor = Theme.Light;
                pb.Value = 100;
                btnOpenFolder.Enabled = true;
                SetStatus("Готово: " + name, Theme.Green);
                return;
            }

            ErrorClassifier.Result cls = ErrorClassifier.Classify(result.Output, result.ExitCode);
            ShowErrorResult(cls, result.Output, allowUpdateFlow);
        }

        private async void ShowErrorResult(ErrorClassifier.Result cls, string output, bool allowUpdateFlow)
        {
            string shortMsg;
            if (cls != null && cls.Hint != null) shortMsg = cls.Hint;
            else shortMsg = "Неизвестная ошибка yt-dlp (код " + _lastExitCode + ").";

            string detail = cls != null && cls.MatchedLine != null ? cls.MatchedLine : null;
            SetStatus("Ошибка: " + shortMsg + (detail != null ? " [" + detail + "]" : ""), Theme.Red);

            if (cls != null && cls.Category == ErrorCategory.UpdateSuspect && allowUpdateFlow)
            {
                string ans = PromptForm.Show(this, "Возможная проблема версии",
                    "Скачивание завершилось ошибкой.\n\n" + shortMsg + "\n\nПричина обозначена как «возможная проблема версии yt-dlp» — точно установить её нельзя.\n\nПроверить наличие обновления yt-dlp?",
                    true, AnsCheckYes, AnsNo);
                if (ans == AnsCheckYes) await RunUpdateFlowAsync(true);
            }
        }

        private void CancelDownload()
        {
            if (!_downloading) return;
            _canceled = true;
            SetStatus("Остановка…", Theme.Orange);
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
                MessageBox.Show(this, "В папке приложения отсутствует " + missing + ".", "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _updateRunning = true;
            SetBusy();
            SetStatus("Проверка обновления yt-dlp…", Theme.Dim);

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
                    MessageBox.Show(this, "Не удалось определить версию установленного yt.exe.", "Проверка обновления", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus("Ошибка: версия yt-dlp не определена.", Theme.Red);
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
                    string ans = PromptForm.Show(this, "Проверка обновления",
                        "Не удалось проверить обновление yt-dlp.\nПричина: " + netErr + "\n\nЭто не означает, что yt-dlp устарел.",
                        false, AnsUseCurrent, AnsTryLater);
                    if (ans == AnsUseCurrent)
                        SetStatus("Используется текущая версия yt-dlp " + local + ".", Theme.Dim);
                    else
                        SetStatus("Проверка обновления отложена.", Theme.Dim);
                    return;
                }

                int cmp = UpdateChecker.CompareVersions(latest, local);
                if (cmp <= 0)
                {
                    MessageBox.Show(this, "Установлена актуальная версия yt-dlp (" + local + ").\nОшибка, скорее всего, не связана с версией.", "Проверка обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetStatus("yt-dlp " + local + " — актуален.", Theme.Dim);
                    return;
                }

                string ans2 = PromptForm.Show(this, "Доступно обновление yt-dlp",
                    "Установлена версия: " + local + "\nДоступна версия: " + latest + "\n\nОбновить сейчас?",
                    false, AnsUpdate, AnsCancel);
                if (ans2 != AnsUpdate)
                {
                    SetStatus("Обновление отложено.", Theme.Dim);
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
                SetStatus("Обновление yt-dlp " + oldVersion + "…", Theme.Dim);
                AppendLog("$ yt.exe -U");
                YtRunResult r = await YtDlpRunner.RunAsync(YtDlpRunner.UpdateArgs(), delegate(YtLine line) { OnYtLine(line); }, delegate(Process p) { _proc = p; });

                YtDlpRunner.VersionResult vr = await YtDlpRunner.GetVersionAsync();
                bool ok = vr.ExitCode == 0 && UpdateChecker.CompareVersions(vr.Version, oldVersion) > 0;

                if (ok)
                {
                    ApplyYtVersion(vr.Version);
                    SetStatus("yt-dlp обновлён: " + oldVersion + " → " + vr.Version + ".", Theme.Green);
                    if (autoRetryAfterUpdate && !_autoRetried && _lastArgs != null)
                    {
                        _autoRetried = true;
                        AppendLog("Повторная попытка скачивания после обновления…");
                        await RunDownloadCoreAsync(_lastArgs, false);
                    }
                }
                else
                {
                    MessageBox.Show(this, "Обновление не удалось. Подробности — в логе.", "Обновление yt-dlp", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus("Ошибка: обновление yt-dlp не выполнено (осталась версия " + oldVersion + ").", Theme.Red);
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

            string phase = OutputParser.PhaseOf(line.Text);
            if (phase != null && _currentFile == null) lblInfo.Text = phase;

            string dest = OutputParser.TryDestination(line.Text);
            if (dest != null)
            {
                _currentFile = dest;
                lblInfo.Text = "Скачивание: " + Path.GetFileName(dest);
            }

            string merged = OutputParser.TryMergedFile(line.Text);
            if (merged != null)
            {
                _currentFile = merged;
                lblInfo.Text = "Склейка: " + Path.GetFileName(merged);
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
                lblProgress.Text = string.Join("  ·  ", parts.ToArray());
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_downloading && !_updateRunning) return;
            DialogResult r = MessageBox.Show(this,
                _downloading ? "Идёт скачивание. Прервать и выйти?" : "Идёт обновление yt-dlp. Прервать и выйти?",
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
