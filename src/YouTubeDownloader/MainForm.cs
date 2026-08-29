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
    public class MainForm : Form
    {
        private readonly Settings _settings = new Settings(AppPaths.SettingsPath);

        private TextBox txtUrl;
        private TextBox txtFolder;
        private TextBox txtLog;
        private Label lblUrlStatus;
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

        private readonly Timer _clipTimer = new Timer();
        private string _lastAutoUrl;
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
            ClientSize = new Size(790, 505);
            MinimumSize = new Size(700, 480);
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
            Controls.Add(l1);

            txtUrl = new TextBox();
            txtUrl.Location = new Point(75, 14);
            txtUrl.Size = new Size(600, 23);
            txtUrl.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            txtUrl.TextChanged += delegate { UpdateUrlStatus(); };
            Controls.Add(txtUrl);

            btnPaste = new Button();
            btnPaste.Text = "Вставить";
            btnPaste.Location = new Point(680, 13);
            btnPaste.Size = new Size(98, 25);
            btnPaste.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnPaste.Click += delegate { PasteFromClipboard(); };
            Controls.Add(btnPaste);

            lblUrlStatus = new Label();
            lblUrlStatus.Location = new Point(75, 40);
            lblUrlStatus.Size = new Size(600, 16);
            lblUrlStatus.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            lblUrlStatus.ForeColor = Color.Gray;
            Controls.Add(lblUrlStatus);

            Label l2 = new Label();
            l2.Text = "Папка:";
            l2.Location = new Point(12, 69);
            l2.Size = new Size(58, 20);
            Controls.Add(l2);

            txtFolder = new TextBox();
            txtFolder.Location = new Point(75, 66);
            txtFolder.Size = new Size(530, 23);
            txtFolder.ReadOnly = true;
            txtFolder.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            txtFolder.BackColor = SystemColors.Control;
            Controls.Add(txtFolder);

            btnBrowse = new Button();
            btnBrowse.Text = "Обзор…";
            btnBrowse.Location = new Point(610, 65);
            btnBrowse.Size = new Size(80, 25);
            btnBrowse.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnBrowse.Click += delegate { BrowseFolder(); };
            Controls.Add(btnBrowse);

            lblYtStatus = new Label();
            lblYtStatus.Location = new Point(75, 95);
            lblYtStatus.Size = new Size(360, 20);
            lblYtStatus.ForeColor = Color.Gray;
            Controls.Add(lblYtStatus);

            btnCheckUpdate = new Button();
            btnCheckUpdate.Text = "Проверить обновление yt-dlp";
            btnCheckUpdate.Location = new Point(445, 92);
            btnCheckUpdate.Size = new Size(233, 26);
            btnCheckUpdate.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnCheckUpdate.Click += async delegate { await RunUpdateFlowAsync(false); };
            Controls.Add(btnCheckUpdate);

            btnDownload = new Button();
            btnDownload.Text = "СКАЧАТЬ";
            btnDownload.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnDownload.Location = new Point(12, 128);
            btnDownload.Size = new Size(370, 44);
            btnDownload.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            btnDownload.Click += async delegate { await StartDownloadAsync(); };
            Controls.Add(btnDownload);
            AcceptButton = btnDownload;

            btnCancel = new Button();
            btnCancel.Text = "Отмена";
            btnCancel.Location = new Point(392, 128);
            btnCancel.Size = new Size(120, 44);
            btnCancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnCancel.Enabled = false;
            btnCancel.Click += delegate { CancelDownload(); };
            Controls.Add(btnCancel);

            btnOpenFolder = new Button();
            btnOpenFolder.Text = "Открыть папку";
            btnOpenFolder.Location = new Point(522, 128);
            btnOpenFolder.Size = new Size(256, 44);
            btnOpenFolder.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnOpenFolder.Enabled = false;
            btnOpenFolder.Click += delegate { OpenFolder(); };
            Controls.Add(btnOpenFolder);

            pb = new ProgressBar();
            pb.Location = new Point(12, 184);
            pb.Size = new Size(766, 22);
            pb.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(pb);

            lblProgress = new Label();
            lblProgress.Location = new Point(12, 210);
            lblProgress.Size = new Size(766, 16);
            lblProgress.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(lblProgress);

            lblInfo = new Label();
            lblInfo.Location = new Point(12, 228);
            lblInfo.Size = new Size(766, 16);
            lblInfo.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(lblInfo);

            txtLog = new TextBox();
            txtLog.Location = new Point(12, 250);
            txtLog.Size = new Size(766, 224);
            txtLog.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Font = new Font("Consolas", 9F);
            txtLog.WordWrap = false;
            Controls.Add(txtLog);

            lblStatus = new Label();
            lblStatus.Location = new Point(12, 479);
            lblStatus.Size = new Size(766, 18);
            lblStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            lblStatus.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            lblStatus.AutoEllipsis = true;
            lblStatus.ForeColor = Color.Gray;
            lblStatus.Text = "Готово.";
            Controls.Add(lblStatus);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _settings.Load();
            string lf = _settings.LastFolder;
            if (!string.IsNullOrEmpty(lf)) txtFolder.Text = lf;

            string missing = AppPaths.MissingCoreComponent();
            if (missing != null)
            {
                lblYtStatus.Text = "Комплект неполный: не найден " + missing;
                lblYtStatus.ForeColor = Color.Firebrick;
                SetStatus("В папке приложения отсутствует " + missing + ". Скачать невозможно.", Color.Firebrick);
            }
            else if (!AppPaths.FfprobePresent())
            {
                lblYtStatus.Text = "ffprobe.exe не найден (не критично)";
                lblYtStatus.ForeColor = Color.DarkOrange;
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

        private void RefreshYtVersion()
        {
            lblYtStatus.Text = "yt-dlp: определение версии…";
            lblYtStatus.ForeColor = Color.Gray;
            Task.Run(delegate
            {
                int code;
                string v = YtDlpRunner.GetVersionText(out code);
                BeginInvoke((MethodInvoker)delegate
                {
                    if (code == 0 && !string.IsNullOrEmpty(v))
                    {
                        _ytVersion = v;
                        lblYtStatus.Text = "yt-dlp: " + v + " — готов";
                        lblYtStatus.ForeColor = Color.Green;
                    }
                    else
                    {
                        lblYtStatus.Text = "yt-dlp: не удалось определить версию";
                        lblYtStatus.ForeColor = Color.Firebrick;
                    }
                });
            });
        }

        private void SetStatus(string text, Color color)
        {
            lblStatus.Text = text;
            lblStatus.ForeColor = color;
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
                lblUrlStatus.ForeColor = Color.Gray;
            }
            else if (YouTubeUrl.IsValid(t))
            {
                lblUrlStatus.Text = "YouTube-ссылка распознана";
                lblUrlStatus.ForeColor = Color.Green;
            }
            else
            {
                lblUrlStatus.Text = "Не похоже на YouTube-ссылку";
                lblUrlStatus.ForeColor = Color.DarkOrange;
            }
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
                SetStatus("В буфере обмена не найдена YouTube-ссылка.", Color.DarkOrange);
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
                SetStatus("Ошибка: не удалось создать папку — " + ex.Message, Color.Firebrick);
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

            btnDownload.Enabled = false;
            btnCheckUpdate.Enabled = false;
            btnBrowse.Enabled = false;
            btnPaste.Enabled = false;
            txtUrl.ReadOnly = true;
            btnCancel.Enabled = true;
            btnOpenFolder.Enabled = false;
            pb.Value = 0;
            lblProgress.Text = "";
            lblInfo.Text = "Подготовка…";

            AppendLog("─────────────────────────────────────────────");
            AppendLog("$ yt.exe " + YtDlpRunner.FormatArgs(args));

            YtRunResult result = await YtDlpRunner.RunAsync(args, delegate(YtLine line) { OnYtLine(line); }, delegate(Process p) { _proc = p; });

            _downloading = false;
            _proc = null;
            btnDownload.Enabled = true;
            btnCheckUpdate.Enabled = true;
            btnBrowse.Enabled = true;
            btnPaste.Enabled = true;
            txtUrl.ReadOnly = false;
            btnCancel.Enabled = false;

            if (_canceled)
            {
                lblInfo.Text = "";
                SetStatus("Скачивание отменено пользователем.", Color.DarkOrange);
                return;
            }

            _lastExitCode = result.ExitCode;

            if (result.ExitCode == 0)
            {
                string name = _currentFile != null ? Path.GetFileName(_currentFile) : "файл";
                if (_alreadyDownloaded) SetStatus("Этот файл уже скачан ранее: " + name, Color.Green);
                else SetStatus("Готово: " + name, Color.Green);
                pb.Value = 100;
                btnOpenFolder.Enabled = true;
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
            SetStatus("Ошибка: " + shortMsg + (detail != null ? " [" + detail + "]" : ""), Color.Firebrick);

            if (cls != null && cls.Category == ErrorCategory.UpdateSuspect && allowUpdateFlow)
            {
                string ans = PromptForm.Show(this, "Возможная проблема версии",
                    "Скачивание завершилось ошибкой.\n\n" + shortMsg + "\n\nПричина обозначена как «возможная проблема версии yt-dlp» — точно установить её нельзя.\n\nПроверить наличие обновления yt-dlp?",
                    true, "Да, проверить", "Нет");
                if (ans == "Да, проверить") await RunUpdateFlowAsync(true);
            }
        }

        private void CancelDownload()
        {
            if (!_downloading) return;
            _canceled = true;
            SetStatus("Остановка…", Color.DarkOrange);
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
            btnCheckUpdate.Enabled = false;
            btnDownload.Enabled = false;
            SetStatus("Проверка обновления yt-dlp…", Color.Gray);

            try
            {
                string local = _ytVersion;
                if (string.IsNullOrEmpty(local))
                {
                    int c;
                    local = YtDlpRunner.GetVersionText(out c);
                }
                if (string.IsNullOrEmpty(local))
                {
                    MessageBox.Show(this, "Не удалось определить версию установленного yt.exe.", "Проверка обновления", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetStatus("Ошибка: версия yt-dlp не определена.", Color.Firebrick);
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
                        false, "Использовать текущую версию", "Попробовать позже");
                    if (ans == "Использовать текущую версию")
                        SetStatus("Используется текущая версия yt-dlp " + local + ".", Color.Gray);
                    else
                        SetStatus("Проверка обновления отложена.", Color.Gray);
                    return;
                }

                int cmp = UpdateChecker.CompareVersions(latest, local);
                if (cmp <= 0)
                {
                    MessageBox.Show(this, "Установлена актуальная версия yt-dlp (" + local + ").\nОшибка, скорее всего, не связана с версией.", "Проверка обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetStatus("yt-dlp " + local + " — актуален.", Color.Gray);
                    return;
                }

                string ans2 = PromptForm.Show(this, "Доступно обновление yt-dlp",
                    "Установлена версия: " + local + "\nДоступна версия: " + latest + "\n\nОбновить сейчас?",
                    false, "Обновить", "Отмена");
                if (ans2 != "Обновить")
                {
                    SetStatus("Обновление отложено.", Color.Gray);
                    return;
                }

                await PerformUpdateAsync(local, autoRetryAfterUpdate);
            }
            finally
            {
                _updateRunning = false;
                btnCheckUpdate.Enabled = true;
                btnDownload.Enabled = true;
            }
        }

        private async Task PerformUpdateAsync(string oldVersion, bool autoRetryAfterUpdate)
        {
            _updateRunning = true;
            btnCheckUpdate.Enabled = false;
            btnDownload.Enabled = false;
            try
            {
                SetStatus("Обновление yt-dlp " + oldVersion + "…", Color.Gray);
                AppendLog("$ yt.exe -U");
                YtRunResult r = await YtDlpRunner.RunAsync(YtDlpRunner.UpdateArgs(), delegate(YtLine line) { OnYtLine(line); }, delegate(Process p) { _proc = p; });

                int c;
                string now = YtDlpRunner.GetVersionText(out c);
                bool ok = c == 0 && UpdateChecker.CompareVersions(now, oldVersion) > 0;

                if (ok)
                {
                    _ytVersion = now;
                    lblYtStatus.Text = "yt-dlp: " + now + " — готов";
                    SetStatus("yt-dlp обновлён: " + oldVersion + " → " + now + ".", Color.Green);
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
                    SetStatus("Ошибка: обновление yt-dlp не выполнено (осталась версия " + oldVersion + ").", Color.Firebrick);
                }
            }
            finally
            {
                _updateRunning = false;
                btnCheckUpdate.Enabled = true;
                btnDownload.Enabled = true;
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
                if (v < pb.Minimum) v = pb.Minimum;
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
            if (p != null) YtDlpRunner.KillTree(p);
        }
    }
}
