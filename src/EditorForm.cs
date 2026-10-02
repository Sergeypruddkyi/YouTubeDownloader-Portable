using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibVLCSharp.WinForms;

namespace YouTubeDownloader
{
    public sealed class EditorForm : Form
    {
        // The editor window is sized around the TWO things that have to be big: the
        // preview frame and the timeline track. The old 1000x570 window could not
        // hold both - a working track needs real height, and the preview may not be
        // shrunk to pay for it, so the window grew instead.
        private const int W = 1180;
        private const int H = 694;

        // Height of the preview's own transport strip (the row that holds Play,
        // docked directly under the VideoView and above the filmstrip).
        private const int PlayBarHeight = 42;

        // Every folder-choosing dialog of the editor keeps its OWN last folder
        // (Open / Save frame) under its own settings key, so a folder picked in
        // one dialog can never move another one. MainForm hands its shared
        // Settings instance in, so both windows write the same settings.ini.
        private readonly Settings _settings;

        private string _file;
        private MediaInfo _media;
        private string _fatalError;
        // File handed to the constructor that OnShown() still has to open: the
        // window is shown first, the opening work runs on the already visible
        // window (see the constructor and OnShown).
        private string _pendingFile;

        private PictureBox _preview;
        // The central preview area is now a container holding BOTH the embedded
        // LibVLC VideoView (the primary preview, per TASK 2 / UX contract) and the
        // original _preview PictureBox kept as a live fallback (frame stepping,
        // Save frame, image preview, or if LibVLC fails to initialize).
        private Panel _previewHost;
        // Player transport strip: docked INSIDE the preview host, directly under
        // the VideoView and above the filmstrip. Our Play/Pause lives here, not in
        // Cutter's action rows, so the preview owns its own single transport button
        // (CapCut-style) without turning the preview into a VLC player.
        private Panel _playBar;
        private VideoView _videoView;
        private LibVlcPreview _libVlc;
        private volatile bool _seekFromPlayer;
        private FilmstripTimeline _filmstrip;
        private Label _lblInfo;
        private Label _lblStatus;
        private ProgressBar _pb;
        private Button _btnStepBack;
        private Button _btnStepFwd;
        private Button _btnCut;
        private Button _btnCancel;
        private Button _btnSaveFrame;
        private Button _btnPlayPause;
        private Button _btnSaveVideo;
        private Button _btnOpenInPlayer;
        private Button _btnOpen;
        private Button _btnOpenImage;
        private Button _btnClose;
        private Panel _zone;
        private DarkTitleBar _titleBar;
        private static readonly Color ZoneBack = Color.FromArgb(22, 22, 24);

        // The preview request throttle. Scrubbing is NOT debounced: a trailing debounce
        // is restarted by every mouse move and therefore never fires while the mouse is
        // moving, which is precisely when the preview has to follow the playhead. The
        // first move is handed to the scrubber immediately, later ones are limited to
        // one per this interval, and the final position is always requested on MouseUp.
        private const int PreviewThrottleMs = 55;

        // Scrubbing goes through PreviewScrubber: one worker thread, one ffmpeg process
        // reused across moves, latest request wins. _decoder stays as the decoder for
        // the step buttons and for the frame shown when the editor opens.
        private PreviewScrubber _scrubber;
        private Bitmap _shownBmp;
        private bool _shownOwned;
        private volatile bool _throttlePending;
        private long _lastPreviewRequestMs;

        private FrameDecoder _decoder;
        private FilmstripPrep.Prepared _strip;
        private volatile bool _stripLoading;
        private int _stripGen;
        private int _stripPrepMs = -1;
        private string _stripPrepMode = "";
        private DecodedFrame _currentFrame;
        private CutModel _model;
        private volatile bool _busy;
        private volatile bool _cancelRequested;
        private Process _jobProc;
        private double _lastPts;
        private bool _lastPtsValid;
        private readonly System.Windows.Forms.Timer _previewThrottle;
        private string _lastFfmpegErrLine;
        private volatile bool _uiClosing;
        // Path of the file the last successful "Save video (trim)" produced; read by
        // the smoke test to prove the button really writes a playable result.
        private string _lastSavedPath;
        private bool _suppressDialogs;
        // Duration of one video frame (= 1 / fps). Computed from the probed r_frame_rate
        // so that Prev/Next frame buttons step by exactly one frame regardless of fps.
        // Falls back to 1/30 if the file has no video stream or fps could not be probed.
        private double _frameDuration = 1.0 / 30.0;

        // The status line doubles as the position clock, so a fresh notice would be
        // wiped by the next clock refresh. While a notice is held the clock stands
        // still; any explicit message or a playhead move ends the hold immediately.
        private const int StatusNoticeMs = 6000;
        private int _statusHoldUntil;
        private int _progressPeak = -1;

        public EditorForm(string file)
            : this(file, null)
        {
        }

        // The Settings instance is supplied by MainForm so that the editor's own
        // folder memories (Open / Save frame) are written into the very same
        // settings.ini the main window uses. Called without one (tests), the
        // editor keeps its own instance and only reads the file.
        public EditorForm(string file, Settings settings)
        {
            _settings = settings ?? new Settings(AppPaths.SettingsPath);
            if (settings == null) _settings.Load();
            Text = "YouTube Downloader";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            FormBorderStyle = FormBorderStyle.None;
            // The editor shares the main window's chrome (DarkTitleBar + borderless
            // window) and its minimize/maximize behaviour, so it keeps the standard
            // Minimize / Maximize / Close buttons just like the main window.
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Back;
            Font = Theme.Regular;
            KeyPreview = true;
            FormClosing += OnFormClosingInner;

            _previewThrottle = new System.Windows.Forms.Timer();
            _previewThrottle.Interval = PreviewThrottleMs;
            _previewThrottle.Tick += OnPreviewThrottleTick;

            BuildUi();
            EditTiming.Mark("EditorForm.BuildUi done");

            // The window must exist BEFORE the video engine and the media probe
            // run: both resolve from disk (the native libvlc tree, ffprobe.exe)
            // and on a cold first run they were the one thing that used to keep
            // the user staring at a window that had not appeared yet. OnShown()
            // continues here, with the window already on screen.
            _pendingFile = file;
            SetStatusRaw(L10n.T(Msg.EdStatusStarting), Theme.Dim);
            UpdateButtons();

            float k = ChromeApi.GetDpiForWindowAt(Cursor.Position) / 96f;
            if (k > 0.999f && k < 1.001f) k = 1f;
            if (k != 1f) Scale(new SizeF(k, k));
        }

        private void BuildUi()
        {
            ClientSize = new Size(W, H);

            _lblInfo = new Label();
            _lblInfo.Location = new Point(12, 44);
            _lblInfo.Size = new Size(W - 24, 20);
            _lblInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _lblInfo.ForeColor = Theme.Light;
            Controls.Add(_lblInfo);

            // The central preview: a host panel that contains BOTH the embedded
            // LibVLC VideoView (primary, per TASK 2) and the original PictureBox
            // as a fallback. VideoView is on top and fills the area; the PictureBox
            // stays underneath (hidden when LibVLC is active) so the existing
            // frame pipeline (step, Save frame, image, scrub fallback) still works.
            _previewHost = new Panel();
            _previewHost.Location = new Point(12, 70);
            _previewHost.Size = new Size(W - 24, 400);
            _previewHost.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _previewHost.BackColor = Color.Black;

            _preview = new PictureBox();
            _preview.Dock = DockStyle.Fill;
            _preview.BackColor = Color.Black;
            _preview.SizeMode = PictureBoxSizeMode.Zoom;
            _previewHost.Controls.Add(_preview);

            // The transport strip is docked to the BOTTOM of the preview host, so
            // it is always directly under the video picture and always above the
            // filmstrip, at every window size - the player's own Play sits there.
            _playBar = new Panel();
            _playBar.Dock = DockStyle.Bottom;
            _playBar.Height = PlayBarHeight;
            _playBar.BackColor = ZoneBack;
            _playBar.Resize += delegate { CenterPlayButton(); };
            _previewHost.Controls.Add(_playBar);

            _videoView = new VideoView();
            _videoView.Dock = DockStyle.Fill;
            _videoView.BackColor = Color.Black;
            _videoView.Visible = false;
            _previewHost.Controls.Add(_videoView);
            // The video fills whatever the bottom-docked transport strip leaves.
            _videoView.BringToFront();
            Controls.Add(_previewHost);

            // Timeline zone: ruler + clip header + continuous filmstrip + marks,
            // all drawn by the single FilmstripTimeline. No zoom column, no empty
            // track around a small block: the clip fills the zone width.
            _zone = new Panel();
            _zone.Location = new Point(12, 478);
            _zone.Size = new Size(W - 24, FilmstripTimeline.TotalTimelineHeight);
            _zone.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _zone.BackColor = ZoneBack;
            Panel zoneEdge = new Panel();
            zoneEdge.Dock = DockStyle.Top;
            zoneEdge.Height = 1;
            zoneEdge.BackColor = Theme.Border;
            _zone.Controls.Add(zoneEdge);
            Controls.Add(_zone);

            // The ONE timeline of the editor: continuous time-based filmstrip.
            _filmstrip = new FilmstripTimeline();
            _filmstrip.Location = new Point(0, 0);
            _filmstrip.Size = new Size(W - 24, FilmstripTimeline.TotalTimelineHeight);
            _filmstrip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _filmstrip.PositionChanged += delegate { OnFilmstripPositionChanged(); };
            _filmstrip.ViewChanged += delegate { UpdateButtons(); };
            // The scrub is over when the button comes up. That is the moment the exact
            // frame for the final position has to be asked for, once, so that nothing
            // can supersede it: the old code lost it whenever a decode happened to be
            // in flight (the position was dropped by the _busy gate and never retried).
            _filmstrip.MouseUp += delegate { OnFilmstripScrubEnded(); };
            _zone.Controls.Add(_filmstrip);

            // Compact bottom action panel: the actions that actually write something
            // out on the first row (trim, then the range confirmation pair, then the
            // window buttons), frame stepping plus status/progress on the second.
            _btnSaveVideo = AddButton(12, 604, 222, Msg.EdSaveVideo, delegate { OnSaveVideo(); });
            Theme.StyleAccent(_btnSaveVideo);
            // Cut / Cancel only act on the pending selection of the filmstrip; the
            // video is not touched until Save video (trim) is pressed.
            _btnCut = AddButton(242, 604, 96, Msg.EdCut, delegate { OnCut(); });
            _btnCancel = AddButton(346, 604, 96, Msg.EdCancel, delegate { OnCancelRange(); });
            _btnStepBack = AddButton(12, 644, 100, Msg.EdStepBack, delegate { StepBy(false); });
            _btnStepFwd = AddButton(120, 644, 100, Msg.EdStepFwd, delegate { StepBy(true); });
            _btnSaveFrame = AddButton(228, 644, 176, Msg.EdSaveFrame, delegate { OnSaveFrame(); });
            // Play/Pause: the UX contract's transport for the embedded player. It is
            // NOT part of Cutter's bottom action rows - it lives in the preview's own
            // transport strip, directly under the VideoView (see _playBar). Play
            // always starts from the CURRENT playhead position, never from the start
            // of the file; while playing the white playhead follows the actual
            // playback time.
            _btnPlayPause = new Button();
            _btnPlayPause.Size = new Size(112, 30);
            Theme.StyleButton(_btnPlayPause);
            _btnPlayPause.Tag = "Play";
            _btnPlayPause.Text = "Play";
            _btnPlayPause.Click += delegate { OnPlayPause(); };
            _playBar.Controls.Add(_btnPlayPause);
            CenterPlayButton();
            // "Open in player": opens the last successfully saved trimmed file with the
            // OS default handler for its type (no player is hardcoded, no association is
            // touched). It stays disabled until a Save video (trim) succeeds. English
            // label only for now - localization is a separate task, so it is not routed
            // through L10n and carries its literal text as its Tag.
            _btnOpenInPlayer = AddButton(450, 604, 156, "Open in player", delegate { OnOpenInPlayer(); });
            // Not routed through ApplyStrings/L10n: keep its literal English text.
            _btnOpenInPlayer.Text = "Open in player";
            // The two open actions are named explicitly, one per media kind, and
            // they sit in one column: Open Video keeps the existing video flow
            // (and its own OpenFolder), Open Image opens a still image in the very
            // same preview (its own ImageFolder). Both are wide enough for their
            // fully spelled-out labels - "Open Video…" alone measures ~104 px and
            // the Russian "Открыть изображение…" ~185 px, so the old 100 px button
            // would have cut the text off.
            _btnOpen = AddButton(W - 310, 604, 200, Msg.EdOpenFile, delegate { OnOpenFile(); });
            _btnOpenImage = AddButton(W - 310, 644, 200, Msg.EdOpenImage, delegate { OnOpenImage(); });
            _btnClose = AddButton(W - 102, 604, 90, Msg.EdClose, delegate { Close(); });
            _btnSaveVideo.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnCut.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnSaveFrame.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnStepBack.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnStepFwd.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnOpenInPlayer.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnOpen.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnOpenImage.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            _lblStatus = new Label();
            // Position line sits right after the Play button in the frame-action
            // row ([Prev][Next][SaveFrame][Play][Position]) so the playback clock
            // reads in the natural control order, clear of the buttons.
            _lblStatus.Location = new Point(520, 650);
            // As wide as the room left of the Open Image button allows: that button
            // owns the right edge (W - 310 .. W - 110) one row below the status line,
            // so the line ends 8 px before it instead of running underneath it. The
            // usual notices (Ready, position, frame/image saved) fit this width.
            _lblStatus.Size = new Size(W - 310 - 520 - 8, 18);
            _lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _lblStatus.ForeColor = Theme.Dim;
            Controls.Add(_lblStatus);

            // The progress line of the save: 0 % when the job starts, filling while
            // ffmpeg works, left at 100 % once the result is on disk. It sits in the
            // free space of the action row - the status line below it stays readable -
            // and stays hidden at rest, so it is never just an empty white box.
            _pb = new ProgressBar();
            // Shifted right of the new "Open in player" button (which occupies x=450..606
            // on this row) so the two never overlap; the bar is hidden at rest anyway.
            _pb.Location = new Point(616, 612);
            _pb.Size = new Size(164, 16);
            _pb.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _pb.Visible = false;
            Controls.Add(_pb);

            _titleBar = new DarkTitleBar(this);
            Controls.Add(_titleBar);

            ApplyStrings();
        }

        private Button AddButton(int x, int y, int w, object text, EventHandler onClick)
        {
            Button b = new Button();
            b.Location = new Point(x, y);
            b.Size = new Size(w, 32);
            Theme.StyleButton(b);
            b.Tag = text;
            b.Click += onClick;
            Controls.Add(b);
            return b;
        }

        private void ApplyStrings()
        {
            _btnStepBack.Text = L10n.T(Msg.EdStepBack);
            _btnStepFwd.Text = L10n.T(Msg.EdStepFwd);
            _btnCut.Text = L10n.T(Msg.EdCut);
            _btnCancel.Text = L10n.T(Msg.EdCancel);
            _btnSaveFrame.Text = L10n.T(Msg.EdSaveFrame);
            _btnSaveVideo.Text = L10n.T(Msg.EdSaveVideo);
            _btnOpen.Text = L10n.T(Msg.EdOpenFile);
            _btnOpenImage.Text = L10n.T(Msg.EdOpenImage);
            _btnClose.Text = L10n.T(Msg.EdClose);
        }

        private static string TextOf(object tag)
        {
            if (tag is Msg) return L10n.T((Msg)tag);
            return tag as string ?? "";
        }

        // ------------------------------------------------------------ LibVLC preview
        // One embedded player for the whole editor. VideoView sits in the central
        // preview host; the old PictureBox preview remains as the fallback. The
        // timeline playhead is the single source of position (UX contract): moving
        // it seeks the player (latest-wins), and during playback the player's own
        // position drives the white playhead and the status clock.

        private void InitLibVlc()
        {
            if (_videoView == null) return;
            _libVlc = new LibVlcPreview(_videoView);
            _libVlc.Initialize();
            _libVlc.PlayingPositionChanged += OnLibVlcPlayingPosition;
            _libVlc.EndReached += OnLibVlcEndReached;
        }

        // The Play button is centered in the preview's own transport strip. The
        // strip is docked to the bottom of the preview host, so the button always
        // sits directly under the video picture and above the filmstrip, at every
        // window size - it is never inside the VideoView, never after the
        // timeline and never part of Cutter's action rows.
        private void CenterPlayButton()
        {
            if (_playBar == null || _btnPlayPause == null) return;
            int x = (_playBar.ClientSize.Width - _btnPlayPause.Width) / 2;
            int y = (_playBar.ClientSize.Height - _btnPlayPause.Height) / 2;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            _btnPlayPause.Location = new Point(x, y);
        }

        // The embedded LibVLC player is the active preview exactly while it holds
        // a media: that is the condition under which the playhead drives the
        // player (seek / playhead following) instead of the FFmpeg preview.
        private bool LibVlcPreviewActive
        {
            get { return _libVlc != null && _libVlc.IsInitialized && _libVlc.HasMedia; }
        }

        // Seek the player to the playhead whenever the playhead moves. The player
        // applies only the newest requested position (latest-wins), so fast scrubbing
        // never queues stale seeks that would then play one after another.
        private void LibVlcSeekToPlayhead()
        {
            if (_libVlc == null || !_libVlc.IsInitialized || _filmstrip == null) return;
            if (_seekFromPlayer)
            {
                // TEMPORARY DIAGNOSTICS: a playhead move that is NOT seeked back to
                // the player (by design). Should happen exactly once per player
                // position event; a burst of these means the flag is misbehaving.
                VlcDiag.Write(VlcDiag.NextSeq() + " SWALLOW playheadMove st=" + _libVlc.StateText
                    + " playhead=" + _filmstrip.Position.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
                _seekFromPlayer = false;
                return;
            }
            _libVlc.Seek(_filmstrip.Position);
        }

        // Play/Pause transport. Play always starts from the current playhead;
        // Pause keeps the position; repeated Play continues from there.
        private void OnPlayPause()
        {
            if (_libVlc == null || !_libVlc.IsInitialized || _media == null) return;
            if (!_libVlc.HasMedia)
            {
                // No media yet (or LibVLC not ready): fall back to the FFmpeg
                // scrubber so the button is never a dead control.
                if (_scrubber != null) _scrubber.RequestFinal(_filmstrip.Position);
                return;
            }
            bool nowPlaying = _libVlc.TogglePlayPause(_filmstrip.Position);
            _btnPlayPause.Text = nowPlaying ? "Pause" : "Play";
            if (nowPlaying) _videoView.Visible = true;
        }

        // While playing, the player is the source of the actual current time: the
        // white playhead and the status clock follow it. A playhead move from the
        // user immediately ends this (OnFilmstripPositionChanged calls LibVlcSeek).
        private void OnLibVlcPlayingPosition(double sec)
        {
            if (_filmstrip == null || _media == null) return;
            // TEMPORARY DIAGNOSTICS: while PAUSED every player position event yanks
            // the playhead to the DECODED position - legal after a seek (decode
            // snapped to a frame boundary), fatal if it lands on an older seek.
            if (!_libVlc.IsPlaying)
            {
                double was = _filmstrip.Position;
                if (Math.Abs(was - sec) > 0.25)
                    VlcDiag.Write(VlcDiag.NextSeq() + " PAUSED-PC decoded=" + sec.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                        + " playheadWas=" + was.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            }
            // Mark this playhead move as coming FROM the player, so the
            // PositionChanged handler does not seek the player back to where it
            // already is. The flag is cleared in finally: when the assignment is
            // clamped away (position unchanged, so PositionChanged does not fire)
            // a stale flag would otherwise swallow the NEXT real user seek - the
            // first drag of the playhead after opening would do nothing.
            _seekFromPlayer = true;
            try { _filmstrip.Position = sec; }
            finally { _seekFromPlayer = false; }
            UpdateTimeLabel();
            _btnPlayPause.Text = "Pause";
        }

        private void OnLibVlcEndReached()
        {
            if (_filmstrip == null) return;
            _btnPlayPause.Text = "Play";
            // Leave the playhead at the end; a re-open or seek resumes from there.
        }

        // Loads (or replaces) the media shown by THIS editor window. Called by
        // the constructor and by Open... so opening another file always replaces
        // the media in the current, single EditorForm and never spawns a second
        // editor window.
        private void LoadMedia(string file)
        {
            EditTiming.Mark("LoadMedia begin: " + file);
            _previewThrottle.Stop();
            _throttlePending = false;
            _filmstrip.Position = 0;
            DisposeEditor();
            EditTiming.Mark("LoadMedia: previous session disposed");
            // Reset the embedded player for the new media: stop, hide the VideoView
            // so the fallback PictureBox is the visible preview again until LibVLC
            // reopens the file successfully.
            if (_libVlc != null)
            {
                _libVlc.Stop();
                if (_videoView != null) _videoView.Visible = false;
                _btnPlayPause.Text = "Play";
            }
            bool shownWasCurrent = _currentFrame != null && ReferenceEquals(_currentFrame.Image, _shownBmp);
            if (_currentFrame != null && _currentFrame.Image != null)
            {
                try { _currentFrame.Image.Dispose(); }
                catch { }
            }
            _currentFrame = null;
            // _shownBmp is either a scrubber frame (already freed by DisposeEditor), the
            // frame just disposed above, or a thumbnail copy this form owns. Only the
            // last one still needs freeing here.
            if (_shownOwned && _shownBmp != null && !shownWasCurrent)
            {
                try { _shownBmp.Dispose(); }
                catch { }
            }
            _shownBmp = null;
            _shownOwned = false;
            _preview.Image = null;
            _lastPts = 0;
            _lastPtsValid = false;
            _lastFfmpegErrLine = null;
            // A newly loaded file has no saved trim yet, so the "Open in player" button
            // must start disabled and must not point at a previous file's result.
            _lastSavedPath = null;
            _pb.Value = 0;
            _filmstrip.SetPreparedStrip(null);

            // Restore controls that ShowFatal / ShowAudioOnlyState may have
            // hidden for the previously loaded media.
            _preview.Visible = true;
            if (_previewHost != null) _previewHost.Visible = true;
            _filmstrip.Visible = true;
            _zone.Visible = true;
            _btnSaveFrame.Visible = true;
            _btnSaveVideo.Visible = true;
            _btnStepBack.Visible = true;
            _btnStepFwd.Visible = true;
            _btnCut.Visible = true;
            _btnCancel.Visible = true;
            _pb.Visible = false;
            _lblInfo.ForeColor = Theme.Light;

            _file = file;
            _fatalError = null;
            _media = null;
            _model = null;
            if (!AppPaths.FfprobePresent())
            {
                _fatalError = L10n.T(Msg.EdNoFfprobe);
            }
            else
            {
                long probeStart = EditTiming.NowMs;
                _media = MediaProbe.Probe(file);
                EditTiming.Mark("LoadMedia: MediaProbe.Probe (ffprobe)", EditTiming.NowMs - probeStart);
                if (!_media.Ok) _fatalError = L10n.T(Msg.EdErrorProbe) + " " + (_media.Error ?? "");
                if (_media.Ok && _media.VideoFps > 0)
                    _frameDuration = 1.0 / _media.VideoFps;
                else
                    _frameDuration = 1.0 / 30.0;
            }

            if (_fatalError != null)
            {
                ShowFatal();
                return;
            }

            Text = L10n.T(Msg.EdTitle, Path.GetFileName(file));
            _model = new CutModel(_media.Duration);
            if (_media.IsAudioOnly)
            {
                ShowAudioOnlyState();
            }
            else
            {
                BeginOpen();
            }
        }

        private void ShowFatal()
        {
            _lblInfo.Text = _fatalError;
            _lblInfo.ForeColor = Theme.Red;
            _preview.Visible = false;
            if (_previewHost != null) _previewHost.Visible = false;
            _filmstrip.Visible = false;
            _zone.Visible = false;
            _btnSaveFrame.Enabled = false;
            _btnSaveVideo.Enabled = false;
            _btnStepBack.Enabled = false;
            _btnStepFwd.Enabled = false;
            _btnCut.Enabled = false;
            _btnCancel.Enabled = false;
            _lblStatus.Text = "";
            _pb.Visible = false;
            MessageBox.Show(this, _fatalError, "YouTube Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void ShowAudioOnlyState()
        {
            _preview.Visible = false;
            if (_previewHost != null) _previewHost.Visible = false;
            _filmstrip.Visible = false;
            _zone.Visible = false;
            _btnSaveFrame.Visible = false;
            _btnSaveVideo.Visible = false;
            _btnStepBack.Visible = false;
            _btnStepFwd.Visible = false;
            _btnCut.Visible = false;
            _btnCancel.Visible = false;
            _pb.Visible = false;
            _lblStatus.Text = "";
            _lblInfo.Text = L10n.T(Msg.EdAudioOnly);
            _lblInfo.ForeColor = Theme.Orange;
        }

        private void BeginOpen()
        {
            EditTiming.Mark("BeginOpen begin");
            _busy = true;
            UpdateButtons();
            SetStatusRaw(L10n.T(Msg.EdStatusOpening), Theme.Dim);
            // Open the file in the embedded LibVLC player. It plays in the central
            // VideoView; the FFmpeg opening frame below still runs for the fallback
            // (frame stepping / Save frame / image preview) and for the timeline.
            if (_libVlc != null && _libVlc.IsInitialized && _videoView != null)
            {
                double start = _filmstrip != null ? _filmstrip.Position : 0;
                EditTiming.Mark("BeginOpen: VideoView handle created=" + _videoView.IsHandleCreated
                    + " visible=" + _videoView.Visible);
                long openStart = EditTiming.NowMs;
                bool opened = _libVlc.OpenFile(_file, start, _media != null ? _media.VideoCodec : null);
                EditTiming.Mark("BeginOpen: LibVlc.OpenFile", EditTiming.NowMs - openStart);
                _videoView.Visible = opened;
                // Only show Play as ready if we actually have a media open.
                if (opened) _btnPlayPause.Enabled = true;
            }
            FrameDecoder dec = new FrameDecoder(_file, _media.Width, _media.Height);
            Task<DecodedFrame> task = Task.Factory.StartNew(new Func<DecodedFrame>(delegate
            {
                return dec.StartAt(0);
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            task.ContinueWith(new Action<Task<DecodedFrame>>(delegate(Task<DecodedFrame> task2)
            {
                DecodedFrame f = null;
                try { f = task2.Result; }
                catch { }
                if (IsDisposed || _uiClosing)
                {
                    if (f != null && f.Image != null) f.Image.Dispose();
                    dec.Dispose();
                    return;
                }
                if (f == null)
                {
                    dec.Dispose();
                    _busy = false;
                    UpdateButtons();
                    SetStatusRaw(L10n.T(Msg.EdErrorDecode) + " ffmpeg", Theme.Red);
                    MessageBox.Show(this, L10n.T(Msg.EdErrorDecode) + " ffmpeg", "YouTube Downloader",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Close();
                    return;
                }
                _decoder = dec;
                _currentFrame = f;
                _lastPts = f.PtsTime >= 0 ? f.PtsTime : 0;
                _lastPtsValid = true;
                ShowFrame(f.Image, true);
                EditTiming.Mark("BeginOpen: first ffmpeg frame shown");
                _filmstrip.Duration = _media.Duration;
                _filmstrip.Caption = Path.GetFileName(_file) + "    " + FormatClock(_media.Duration);
                // The filmstrip is prepared as ONE whole strip off the UI thread
                // (cache first, one sequential ffmpeg pass otherwise) and handed
                // to the timeline only when complete: the user never watches the
                // strip fill in piece by piece.
                _filmstrip.SetPreparedStrip(null);
                _stripLoading = true;
                PrepareStripInBackground();

                // The preview scrubber: one worker thread and one reused ffmpeg process
                // for a whole drag, replacing the old one-process-per-playhead-move
                // path. The step buttons keep using _decoder above.
                // Its H.264 stand-in is built ONLY for the FFmpeg preview: while the
                // embedded LibVLC player is the active preview the playhead never
                // asks the scrubber for a frame, so the whole-clip 480p transcode
                // (a full-CPU pass that can run for minutes on 4K sources) would
                // never be displayed - it is not started at all.
                long scrubStart = EditTiming.NowMs;
                bool libVlcPreview = LibVlcPreviewActive;
                _scrubber = new PreviewScrubber(_file, _media.Width, _media.Height, _media.Duration, this, !libVlcPreview);
                EditTiming.Mark("BeginOpen: PreviewScrubber created (proxy build=" + (!libVlcPreview).ToString() + ")",
                    EditTiming.NowMs - scrubStart);
                _scrubber.ThumbnailFallbackEnabled = true;
                _scrubber.ThumbnailProvider = GrabThumbnail;
                _scrubber.FrameReady += OnPreviewFrame;
                // The opening frame is already on screen and is not ours to replace.
                _scrubber.NoteDisplayed(_lastPts);

                UpdateInfoLine();
                _filmstrip.TestResetRanges();

                FinishOpen();
                EditTiming.Mark("BeginOpen: FinishOpen (editor usable)");
            }), TaskScheduler.FromCurrentSynchronizationContext());
        }

        // CapCut-style preparation: one background pass produces the ENTIRE
        // filmstrip (proxy frames sampled by the strip's physical width), the
        // result is cached on disk, and only the finished strip reaches the UI.
        // Until then the timeline stays in its neutral state; there is no
        // progressive filling to watch.
        private void PrepareStripInBackground()
        {
            string file = _file;
            double duration = _media != null ? _media.Duration : 0;
            int srcW = _media != null ? _media.Width : 0;
            int srcH = _media != null ? _media.Height : 0;
            int frames = _filmstrip != null ? _filmstrip.PlanStripFrames() : 0;
            if (frames <= 0 && duration > 0)
            {
                // Layout may not have a Width yet; still plan from the known
                // editor track so preparation cannot silently skip the strip.
                int track = _filmstrip != null && _filmstrip.Width > 0 ? _filmstrip.Width : (W - 24);
                int clipW = (int)Math.Round(track * 0.55);
                if (clipW < 1) clipW = 1;
                frames = FilmstripPrep.PlanFrames(duration, clipW);
            }
            _stripGen++;
            int gen = _stripGen;
            _stripLoading = true;
            _stripPrepMs = -1;
            _stripPrepMode = "";
            EditTiming.Mark("PrepareStrip: started (frames=" + frames.ToString() + ", duration=" + duration.ToString("0.0") + ")");
            Task<FilmstripPrep.Prepared> task = Task.Factory.StartNew(new Func<FilmstripPrep.Prepared>(delegate
            {
                if (duration <= 0 || frames <= 0) return null;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                FilmstripPrep.Prepared cached = FilmstripPrep.TryLoad(FilmstripPrep.CachePathFor(file), duration);
                if (StripCacheValid(cached, duration, frames))
                {
                    _stripPrepMode = "cache";
                    _stripPrepMs = (int)sw.ElapsedMilliseconds;
                    EditTiming.Mark("PrepareStrip: finished from cache", _stripPrepMs);
                    return cached;
                }
                FilmstripPrep.Prepared built = FilmstripPrep.Generate(file, duration, frames, srcW, srcH);
                _stripPrepMode = FilmstripPrep.LastMode;
                _stripPrepMs = FilmstripPrep.LastElapsedMs;
                EditTiming.Mark("PrepareStrip: ffmpeg pass done (" + (_stripPrepMode ?? "") + ")", _stripPrepMs);
                return built;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            task.ContinueWith(new Action<Task<FilmstripPrep.Prepared>>(delegate(Task<FilmstripPrep.Prepared> t)
            {
                FilmstripPrep.Prepared strip = null;
                try { strip = task.Result; }
                catch { }
                // Another file was opened meanwhile: this result belongs to the past.
                if (gen != _stripGen) return;
                _stripLoading = false;
                EditTiming.Mark("PrepareStrip: strip handed to the timeline");
                if (IsDisposed || _uiClosing)
                {
                    return;     // the strip buffer is pure managed memory; no disposal needed
                }
                _strip = strip;
                if (_filmstrip != null) _filmstrip.SetPreparedStrip(strip);
                UpdateButtons();
                UpdateTimeLabel();
            }), TaskScheduler.FromCurrentSynchronizationContext());
        }

        // Sanity gate for the cache: the loaded strip must cover the whole
        // duration of the media actually being opened, at roughly the planned
        // sample count (fps rounding may shrink it a little).
        private static bool StripCacheValid(FilmstripPrep.Prepared cached, double duration, int planned)
        {
            if (cached == null || cached.Count <= 0 || cached.Bytes == null) return false;
            if (Math.Abs(cached.DurationSec - duration) >= 0.001) return false;
            if ((cached.FrameWidth & 3) != 0) return false;
            if (planned > 0 && cached.Count < planned / 2) return false;
            return true;
        }

        // Media is open and the editor is usable. The filmstrip itself stays in
        // the neutral "preparing" state until the whole strip is ready.
        private void FinishOpen()
        {
            HideProgress();
            _busy = false;
            UpdateButtons();
            if (_stripLoading)
                SetStatusRaw(L10n.T(Msg.EdStatusOverview), Theme.Dim);
            else
                SetStatusRaw(L10n.T(Msg.EdStatusReady), Theme.Dim);
        }

        private void UpdateInfoLine()
        {
            if (_media == null || _model == null) return;
            _lblInfo.Text = L10n.T(Msg.EdInfoLine,
                _media.Width + "×" + _media.Height,
                _media.VideoCodec ?? "",
                L10n.FormatDurationShort(_media.Duration),
                _model.CutCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                L10n.FormatDurationShort(_model.RemovedDuration),
                L10n.FormatDurationShort(_media.Duration - _model.RemovedDuration));
        }

        private void UpdateButtons()
        {
            bool ready = !_busy && _fatalError == null && _model != null;
            // A confirmed range is what makes the trim possible: the button means
            // "at least one red range will be physically removed", nothing else.
            bool hasMarks = _filmstrip != null && _filmstrip.MarkedCount > 0;
            SetEnabled(_btnStepBack, ready && _decoder != null, false);
            SetEnabled(_btnStepFwd, ready && _decoder != null, false);
            // Cut is the playhead-driven mark action and is always available while the
            // media is ready: press once to set Start at the playhead, press again to
            // set End. Cancel only means something while a range is being built, so it
            // is enabled exactly then.
            SetEnabled(_btnCut, ready, false);
            // Cancel is the undo of the last marking action, so it is on exactly while
            // there is something to undo: a range being built, or at least one
            // committed red range that can still be taken back one press at a time.
            // With nothing left it is visibly off.
            SetEnabled(_btnCancel, ready && _filmstrip.HasUndoable, false);
            SetEnabled(_btnSaveVideo, ready && hasMarks, true);
            SetEnabled(_btnSaveFrame, ready && _preview.Image != null, false);
            SetEnabled(_btnOpen, !_busy, false);
            SetEnabled(_btnOpenImage, !_busy, false);
            // Play/Pause is the LibVLC transport: available while the embedded
            // player holds a media and the editor is ready.
            SetEnabled(_btnPlayPause, ready && LibVlcPreviewActive, false);
            // Enabled only after a Save video (trim) has produced a file, and never while
            // a job is running. It always points at the latest saved result via
            // _lastSavedPath, which OnSaveVideo refreshes on every successful save.
            SetEnabled(_btnOpenInPlayer, !_busy && _lastSavedPath != null, false);
            SetEnabled(_btnClose, true, false);
        }

        // A switched-off button has to look switched off. Theme.StyleButton paints
        // every state with the same colours, so a disabled button was previously
        // indistinguishable from a live one - exactly the wrong signal for a button
        // whose whole meaning is "there is something to remove now".
        private static void SetEnabled(Button b, bool on, bool accent)
        {
            if (b == null) return;
            b.Enabled = on;
            Color back = on ? (accent ? Theme.Accent : Theme.Button) : Color.FromArgb(40, 40, 43);
            Color fore = on ? Theme.Light : Color.FromArgb(110, 110, 110);
            if (b.BackColor != back) b.BackColor = back;
            if (b.ForeColor != fore) b.ForeColor = fore;
            b.FlatAppearance.MouseOverBackColor = on
                ? (accent ? Color.FromArgb(25, 135, 230) : Theme.ButtonHover)
                : back;
        }

        private void SetStatusRaw(string text, Color color)
        {
            // An explicit message always wins over a held notice.
            _statusHoldUntil = 0;
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        // Wrap-safe "the deadline has not passed yet".
        private bool StatusNoticeHeld
        {
            get { return unchecked(Environment.TickCount - _statusHoldUntil) < 0; }
        }

        // Hands the playhead position to the scrubber. The scrubber keeps only the
        // newest request, answers it from its own frame cache or from the filmstrip
        // thumbnails synchronously, and decodes the exact frame in the background, so
        // the preview follows the mouse instead of waiting for a decode to finish.
        private void RequestPreview(double t)
        {
            if (_scrubber == null) return;
            _throttlePending = true;
            if (_previewThrottle.Enabled) return;              // a pass is already due
            long now = Environment.TickCount;
            if (now - _lastPreviewRequestMs >= PreviewThrottleMs)
            {
                _lastPreviewRequestMs = now;
                _throttlePending = false;
                _scrubber.Request(t);
                return;
            }
            _previewThrottle.Start();
        }

        private void OnPreviewThrottleTick(object sender, EventArgs e)
        {
            _previewThrottle.Stop();
            if (!_throttlePending || _scrubber == null) return;
            _throttlePending = false;
            _lastPreviewRequestMs = Environment.TickCount;
            // The newest position, not the one that armed the timer.
            if (_filmstrip != null) _scrubber.Request(_filmstrip.Position);
        }

        // MouseUp on the filmstrip: the scrub is over, so ask for the exact frame for
        // the final position. Nothing can supersede this request.
        private void OnFilmstripScrubEnded()
        {
            _previewThrottle.Stop();
            _throttlePending = false;
            if (_scrubber == null) return;
            _lastPreviewRequestMs = Environment.TickCount;
            _scrubber.RequestFinal(_filmstrip.Position);
        }

        // A frame for the preview. Thumbnails are owned by this form (they are private
        // copies of filmstrip thumbnails); decoded frames stay owned by the scrubber.
        private void OnPreviewFrame(Bitmap bmp, double pts, PreviewFrameKind kind)
        {
            if (IsDisposed || _uiClosing || _scrubber == null) return;
            ShowFrame(bmp, kind == PreviewFrameKind.Thumbnail);
            if (kind != PreviewFrameKind.Thumbnail)
            {
                // A real decoded frame: remember where the preview actually is, so that
                // stepping continues from what the user is looking at.
                _lastPts = pts;
                _lastPtsValid = true;
                // The step decoder has no idea about this position any more, so drop its
                // session: the next step then re-seeks instead of continuing from
                // wherever that decoder happened to stop.
                if (!_busy && _decoder != null && _decoder.HasSession) _decoder.ReleaseSession();
            }
            UpdateButtons();
            UpdateTimeLabel();
        }

        // The single place that decides what is on screen and who has to free it. The
        // scrubber owns the frames it publishes and keeps handing them out of its cache,
        // so this form must never dispose one; the frames produced by our own _decoder
        // and the thumbnail copies are ours to free.
        private void ShowFrame(Bitmap bmp, bool ownedByForm)
        {
            if (bmp == null || IsDisposed) return;
            if (ReferenceEquals(bmp, _shownBmp)) return;
            Bitmap old = _shownBmp;
            bool oldOwned = _shownOwned;
            _shownBmp = bmp;
            _shownOwned = ownedByForm;
            _preview.Image = bmp;
            if (old != null && oldOwned)
            {
                try { old.Dispose(); }
                catch { }
            }
        }

        // Instant stand-in for the preview: the prepared filmstrip frame covering
        // the requested time. Memory only, no decode, which is what lets the
        // preview react to a mouse move immediately while the exact frame is
        // still being decoded.
        private Bitmap GrabThumbnail(double t)
        {
            FilmstripPrep.Prepared strip = _strip;
            if (strip == null || strip.Count <= 0 || strip.Bytes == null) return null;
            int index = strip.IndexAt(t);
            try
            {
                // The strip owns the shared buffer and the paint path draws views
                // over it, so hand the preview a private copy.
                using (Bitmap src = FilmstripPrep.FrameView(strip, index))
                {
                    return src != null ? new Bitmap(src) : null;
                }
            }
            catch { return null; }
        }

        private void StepBy(bool forward)
        {
            if (_busy || _decoder == null) return;
            _busy = true;
            UpdateButtons();
            SetStatusRaw(L10n.T(Msg.EdStatusDecoding), Theme.Dim);

            // Capture current position and frame duration for the background thread.
            double currentPos = _lastPtsValid ? _lastPts : 0;
            double frameDur = _frameDuration;
            double duration = _media != null ? _media.Duration : 0;

            Task<DecodedFrame> task = Task.Factory.StartNew(new Func<DecodedFrame>(delegate
            {
                if (forward)
                {
                    // Try to read the next frame from a live ffmpeg session first —
                    // that is always exact and costs nothing (no new process needed).
                    DecodedFrame f = _decoder.StepForward();
                    if (f != null) return f;
                    // Session is dead (closed after scrubbing, or never started after seek).
                    // Seek to currentPos + 1 frame so the decoder lands on the exact next frame.
                    double target = currentPos + frameDur;
                    if (duration > 0) target = Math.Min(target, duration);
                    return _decoder.StartAt(target);
                }
                else
                {
                    // Step back by exactly one frame duration from the current position.
                    // Using the measured r_frame_rate is more reliable than the visited-list
                    // heuristic, which may be empty after a scrub or a seek.
                    double target = currentPos - frameDur;
                    if (target < 0) target = 0;
                    return _decoder.StartAt(target);
                }
            }), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            task.ContinueWith(new Action<Task<DecodedFrame>>(delegate(Task<DecodedFrame> tt)
            {
                FinishFrameTask(tt);
            }), TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void FinishFrameTask(Task<DecodedFrame> task)
        {
            DecodedFrame f = null;
            try { f = task.Result; }
            catch { }
            if (IsDisposed || _uiClosing)
            {
                if (f != null && f.Image != null) f.Image.Dispose();
                return;
            }
            _busy = false;
            if (f == null)
            {
                UpdateButtons();
                SetStatusRaw(L10n.T(Msg.EdErrorDecode), Theme.Red);
                return;
            }
            // Free the previous step frame unless it is the one currently on screen: in
            // that case ShowFrame() below owns the disposal.
            if (_currentFrame != null && _currentFrame.Image != null
                && !ReferenceEquals(_currentFrame.Image, f.Image)
                && !ReferenceEquals(_currentFrame.Image, _shownBmp))
            {
                try { _currentFrame.Image.Dispose(); }
                catch { }
            }
            _currentFrame = f;
            ShowFrame(f.Image, true);
            _lastPts = f.PtsTime >= 0 ? f.PtsTime : _lastPts;
            _lastPtsValid = true;
            if (_scrubber != null) _scrubber.NoteDisplayed(_lastPts);
            _filmstrip.Position = _lastPts;
            // Keep the playhead on screen when it is moved from outside (frame
            // stepping / seek); while scrubbing the user is already pointing at it.
            if (!_filmstrip.IsScrubbing) _filmstrip.EnsureVisible(_lastPts);
            UpdateButtons();
            UpdateTimeLabel();
            SetStatusRaw(L10n.T(Msg.EdStatusReady), Theme.Dim);
        }

        private void UpdateTimeLabel()
        {
            if (StatusNoticeHeld) return;      // a fresh notice stays readable
            if (_stripLoading)
            {
                SetStatusRaw(L10n.T(Msg.EdStatusOverview), Theme.Dim);
                return;
            }
            string t = FormatClock(_filmstrip.Position);
            SetStatusRaw(L10n.T(Msg.EdPosition) + " " + t + " / " + FormatClock(_media.Duration), Theme.Light);
        }

        // Cut: the single playhead-driven "mark point" action.
        //   * first press  -> fixes the range Start at the current white playhead;
        //   * second press -> fixes the End at the current playhead and commits the red
        //     range, ready for a fresh Start at the next playhead spot.
        // The user never drags a marker: they move the playhead, press to set Start,
        // move the playhead again, press to set End.
        // Nothing is processed here - no FFmpeg, no file is written, no backend is
        // involved; the strip keeps the mark in memory only.
        // A red range is a no-cut zone, so a Cut is possible only in free space: a Start
        // on a red range is refused, and a span that would cross a red range is refused
        // at the End press. Both refusals leave every existing red range untouched and
        // say so on the status line.
        private void OnCut()
        {
            if (_busy || _media == null) return;
            FilmstripTimeline.MarkResult result = _filmstrip.ApplyCut();
            UpdateButtons();
            switch (result)
            {
                case FilmstripTimeline.MarkResult.Committed:
                    // A red range was just created: report the running totals. Held
                    // briefly like the other notices so a frame still being decoded
                    // cannot wipe it; released the moment the playhead moves.
                    SetStatusRaw(L10n.T(Msg.EdRangeApplied,
                        _filmstrip.MarkedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        L10n.FormatDurationShort(_filmstrip.MarkedSeconds)), Theme.Orange);
                    _statusHoldUntil = Environment.TickCount + StatusNoticeMs;
                    break;
                case FilmstripTimeline.MarkResult.StartPlaced:
                    // A Start was just placed at the playhead; the End is set on the
                    // next Cut after the user moves the playhead. The notice is held
                    // briefly so a frame still being decoded cannot wipe it, and it is
                    // released the moment the playhead moves (OnPositionSource).
                    SetStatusRaw(L10n.T(Msg.EdRangeStartSet), Theme.Orange);
                    _statusHoldUntil = Environment.TickCount + StatusNoticeMs;
                    break;
                case FilmstripTimeline.MarkResult.RejectedInMark:
                    // The playhead sits on a red range: no Start, no pending Cut, and
                    // no existing red range was changed.
                    SetStatusRaw(L10n.T(Msg.EdCutBlockedInMark), Theme.Red);
                    _statusHoldUntil = Environment.TickCount + StatusNoticeMs;
                    break;
                case FilmstripTimeline.MarkResult.RejectedOverlap:
                    // The End would make the span eat into a red range: nothing is
                    // created, nothing is trimmed to fit. The armed Start is kept, so
                    // the user can move the playhead and press Cut again.
                    SetStatusRaw(L10n.T(Msg.EdCutBlockedOverlap), Theme.Red);
                    _statusHoldUntil = Environment.TickCount + StatusNoticeMs;
                    break;
                default:
                    // RejectedTooShort: the span is smaller than the minimum, the strip
                    // kept the armed Start. Leave the status as it was.
                    break;
            }
        }

        // Cancel is the undo of the last marking action, in the order the marks were
        // made: a range being built is dropped first (and the committed red ranges are
        // not touched), otherwise the NEWEST red range is removed. One press removes at
        // most one thing - it never clears everything - and the red ranges that stay
        // keep their exact times.
        private void OnCancelRange()
        {
            if (_busy || _media == null) return;
            FilmstripTimeline.CancelResult result = _filmstrip.CancelOrUndo();
            if (result == FilmstripTimeline.CancelResult.Nothing) return;
            UpdateButtons();
            if (result == FilmstripTimeline.CancelResult.PendingDropped)
            {
                SetStatusRaw(L10n.T(Msg.EdRangeCanceled), Theme.Dim);
                return;
            }
            // A red range was taken back: report the new totals, so the counters on
            // screen always match what the strip is actually showing.
            SetStatusRaw(L10n.T(Msg.EdRangeUndone,
                _filmstrip.MarkedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                L10n.FormatDurationShort(_filmstrip.MarkedSeconds)), Theme.Orange);
            _statusHoldUntil = Environment.TickCount + StatusNoticeMs;
        }

        // The single filmstrip drives the preview: refresh the clock and ask
        // the scrubber for the frame at the new playhead time. When the embedded
        // LibVLC player is active it is seeked instead (latest-wins), and the
        // FFmpeg scrubber stays as the fallback for stepping/frame extraction.
        private void OnFilmstripPositionChanged()
        {
            if (_filmstrip == null) return;
            // Moving the playhead means the user is done reading the notice.
            _statusHoldUntil = 0;
            UpdateTimeLabel();
            // If LibVLC is the active preview, seek it; otherwise keep the old
            // FFmpeg scrub path.
            if (LibVlcPreviewActive)
            {
                LibVlcSeekToPlayhead();
                return;
            }
            RequestPreview(_filmstrip.Position);
        }

        private static string FormatClock(double t)
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

        // Save frame keeps its OWN last folder: the folder dialog is still shown
        // every time, but it starts in the folder the previous Save frame used.
        // Open and Save video (trim) have their own places, so this choice can
        // never move them. The generated PNG name is unchanged.
        //
        // What changed: the button no longer writes the frame immediately. It
        // decodes the REAL source-resolution frame at the playhead into a temp
        // PNG (never the preview, never a screenshot), opens the crop window on
        // that file, and only then - with the rectangle known - shows the same
        // folder dialog it always had and cuts the rectangle out of that very
        // same temp PNG. Cancel (in the crop window or in the dialog) writes
        // nothing; the temp PNG is deleted on every path out of here.
        private void OnSaveFrame()
        {
            if (_busy || string.IsNullOrEmpty(_file) || !File.Exists(_file)) return;
            double pos = _filmstrip != null ? _filmstrip.Position : 0;

            string temp = NewTempFramePath();
            string error;
            if (!ExtractSourceFrame(_file, temp, pos, out error))
            {
                TryDeleteTempFrame(temp);
                ReportFrameError(error);
                return;
            }

            try
            {
                Rectangle sel = Rectangle.Empty;
                using (CropFrameForm crop = new CropFrameForm(this, temp))
                {
                    if (crop.ShowDialog(this) != DialogResult.OK) return;
                    sel = crop.Selection;
                }
                if (sel.Width < 1 || sel.Height < 1) return;

                string folder;
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "Select folder to save frame";
                    dlg.UseDescriptionForTitle = true;
                    string lastFrameFolder = _settings.FrameFolder;
                    if (!string.IsNullOrEmpty(lastFrameFolder) && Directory.Exists(lastFrameFolder))
                        dlg.SelectedPath = lastFrameFolder;
                    if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath))
                        return;
                    folder = dlg.SelectedPath;
                    _settings.FrameFolder = folder;
                    _settings.Save();
                }

                string outPath = GenerateUniquePngPath(folder, _file, pos);
                if (ExtractCroppedPng(temp, outPath, sel, out error))
                {
                    string name = Path.GetFileName(outPath);
                    SetStatusRaw(L10n.T(Msg.EdStatusFrameSaved) + ": " + name, Theme.Green);
                }
                else
                {
                    ReportFrameError(error);
                }
            }
            finally
            {
                TryDeleteTempFrame(temp);
            }
        }

        private void ReportFrameError(string error)
        {
            SetStatusRaw("Error saving frame: " + error, Theme.Red);
            if (!_suppressDialogs)
            {
                MessageBox.Show(this, L10n.T(Msg.EdErrorSave) + " " + error, "YouTube Downloader",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // One temp PNG per Save frame attempt, in the app's own temp folder (the
        // same folder SelfTest uses), so deleting it can never touch anything
        // else and nothing is ever left inside the portable dist folder.
        private static string NewTempFramePath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "YouTubeDownloader");
            try { Directory.CreateDirectory(dir); }
            catch { dir = Path.GetTempPath(); }
            return Path.Combine(dir, "cropframe_" + Guid.NewGuid().ToString("N") + ".png");
        }

        private static void TryDeleteTempFrame(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string FormatFrameTime(double t)
        {
            if (t < 0) t = 0;
            int total = (int)Math.Floor(t + 0.0005);
            int ms = (int)Math.Round((t - total) * 1000.0);
            if (ms >= 1000) { ms = 0; total++; }
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            return string.Format(CultureInfo.InvariantCulture, "{0:00}-{1:00}-{2:00}.{3:000}", h, m, s, ms);
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalid.Length; i++)
                name = name.Replace(invalid[i], '_');
            return name;
        }

        private static string GenerateUniquePngPath(string folder, string videoFile, double timeSec)
        {
            string videoName = Path.GetFileNameWithoutExtension(videoFile);
            if (string.IsNullOrEmpty(videoName)) videoName = "frame";
            videoName = SanitizeFileName(videoName);
            string timeStr = FormatFrameTime(timeSec);
            string baseName = videoName + "_" + timeStr;
            string candidate = Path.Combine(folder, baseName + ".png");
            int counter = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(folder, string.Format(CultureInfo.InvariantCulture, "{0}_{1:000}.png", baseName, counter));
                counter++;
            }
            return candidate;
        }

        private bool ExtractSourceFrame(string input, string output, double timeSec, out string error)
        {
            string[] args = new string[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", timeSec.ToString("0.000000", CultureInfo.InvariantCulture),
                "-i", input,
                "-frames:v", "1",
                "-update", "1",
                "-y", output
            };
            return RunFfmpegCapture(args, output, 30000, "extracting frame", out error);
        }

        // The Save-frame crop: cut the selected rectangle (SOURCE pixels, as the
        // crop window returned them) out of the real frame that was decoded into
        // the temp PNG. No rescaling, no video file, no preview involved.
        private bool ExtractCroppedPng(string input, string output, Rectangle sel, out string error)
        {
            return RunFfmpegCapture(TrimJob.BuildCropPngArgs(input, output, sel), output, 30000,
                "cropping frame", out error);
        }

        // The one place that starts ffmpeg for a single-frame job: capture its
        // stderr, wait with a timeout, kill on timeout, and turn a failure into a
        // message. Frame extract and crop both go through it, so there is no
        // second copy of the process wiring to drift out of sync.
        private bool RunFfmpegCapture(string[] args, string outputMustExist, int timeoutMs, string what, out string error)
        {
            error = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                psi.Arguments = YtDlpRunner.FormatArgs(args);
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);

                using (Process p = Process.Start(psi))
                {
                    StringBuilder errBuf = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) lock (errBuf) { if (errBuf.Length < 4000) errBuf.AppendLine(e.Data); }
                    };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    bool finished = p.WaitForExit(timeoutMs);
                    if (!finished)
                    {
                        try { p.Kill(); } catch { }
                        error = "Timeout " + what;
                        return false;
                    }
                    if (p.ExitCode != 0 || !File.Exists(outputMustExist))
                    {
                        error = errBuf.ToString().Trim();
                        if (string.IsNullOrEmpty(error)) error = "ffmpeg exited with code " + p.ExitCode;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // Open starts in the last folder a source video was opened from (its own
        // settings key, so a Save frame folder can never move it), and the dialog
        // is told to restore the process directory so the folder it was left in
        // cannot leak into any later dialog either.
        private void OnOpenFile()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Video|*.mp4;*.mkv;*.webm;*.mov;*.avi;*.m4v;*.ts|All files|*.*";
                dlg.RestoreDirectory = true;
                string lastOpenFolder = _settings.OpenFolder;
                if (!string.IsNullOrEmpty(lastOpenFolder) && Directory.Exists(lastOpenFolder))
                    dlg.InitialDirectory = lastOpenFolder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                RememberOpenFolder(dlg.FileName);
                // Replace the media of THIS editor window in place; keep a
                // single editor instead of closing/reopening the form.
                LoadMedia(dlg.FileName);
            }
        }

        // Only the Open dialog writes the Open folder; it is the folder of the
        // video that was just chosen.
        private void RememberOpenFolder(string file)
        {
            string dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            _settings.OpenFolder = dir;
            _settings.Save();
        }

        // Open Image shows a still image in the editor's own preview. It is NOT
        // the frame flow: it never reads or writes FrameFolder (that one belongs
        // to Save frame (PNG)) and never touches the video OpenFolder - it keeps
        // its own ImageFolder, so no dialog can move another one's folder. The
        // image goes through the very same preview the frames use, so no second
        // viewer is introduced: scrubbing or stepping the video replaces it again.
        private void OnOpenImage()
        {
            if (_busy) return;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Image|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*";
                dlg.RestoreDirectory = true;
                string lastImageFolder = _settings.ImageFolder;
                if (!string.IsNullOrEmpty(lastImageFolder) && Directory.Exists(lastImageFolder))
                    dlg.InitialDirectory = lastImageFolder;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string dir = Path.GetDirectoryName(dlg.FileName);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _settings.ImageFolder = dir;
                    _settings.Save();
                }
                ShowImage(dlg.FileName);
            }
        }

        // The file is read into a Bitmap of its own, so no file handle stays open
        // and the preview owns (and disposes) exactly what it shows - the same
        // contract every other frame in the preview follows.
        private void ShowImage(string path)
        {
            Bitmap bmp;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image img = Image.FromStream(fs))
                {
                    bmp = new Bitmap(img);
                }
            }
            catch (Exception ex)
            {
                SetStatusRaw(L10n.T(Msg.EdErrorProbe) + " " + ex.Message, Theme.Red);
                Notify(L10n.T(Msg.EdErrorProbe) + " " + ex.Message, MessageBoxIcon.Error);
                return;
            }
            // ShowFatal / ShowAudioOnlyState hide the preview; an image the user
            // has just picked has to be visible anyway.
            if (!_preview.Visible) _preview.Visible = true;
            ShowFrame(bmp, true);
            SetStatusRaw(L10n.T(Msg.EdStatusImageOpened, Path.GetFileName(path)), Theme.Green);
            _statusHoldUntil = Environment.TickCount + StatusNoticeMs;
        }

        // Opens the last successfully saved trimmed file with the user's default handler
        // for that file type. It uses the existing _lastSavedPath (the exact result of
        // Save video (trim)) instead of searching the folder, and hands the path to the
        // Windows shell via UseShellExecute so no specific player is hardcoded and no file
        // association is changed. Success needs no dialog; a launch failure is reported
        // through the editor's own status line (and the shared error notice).
        private void OnOpenInPlayer()
        {
            string path = _lastSavedPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                // Literal English text, matching the English-only button; localization of
                // this feature is a separate task, so no L10n entry is added here.
                SetStatusRaw("Failed to open in player.", Theme.Red);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                SetStatusRaw("Failed to open in player: " + ex.Message, Theme.Red);
                Notify("Failed to open in player: " + ex.Message, MessageBoxIcon.Error);
            }
        }

        // The real trim: every range the user confirmed with Cut is physically cut
        // out of the source, and the surviving parts are concatenated into ONE new
        // file by ffmpeg. The source is never written to and never replaced; the result
        // lands next to it as "<name>_trimmed.mp4" (" (2)", " (3)" ... if taken).
        private void OnSaveVideo()
        {
            if (_busy || _media == null) return;

            // The ranges to remove are exactly the applied (red) ones on the strip.
            List<TrimRegion> cuts = new List<TrimRegion>();
            for (int i = 0; i < _filmstrip.MarkedCount; i++)
                cuts.Add(new TrimRegion(_filmstrip.MarkedStart(i), _filmstrip.MarkedEnd(i)));
            if (cuts.Count == 0)
            {
                Notify(L10n.T(Msg.EdNothingToSave), MessageBoxIcon.Information);
                return;
            }

            // Normalise (clamp / sort / merge overlaps) and derive what is left to keep.
            List<TrimRegion> regions = TrimJob.ComputeKeepRegions(cuts, _media.Duration);
            if (regions.Count == 0)
            {
                Notify(L10n.T(Msg.EdNothingLeft), MessageBoxIcon.Information);
                return;
            }

            string outPath;
            try { outPath = TrimJob.SuggestTrimmedPath(_file); }
            catch (Exception ex)
            {
                Notify(L10n.T(Msg.EdErrorMove) + " " + ex.Message, MessageBoxIcon.Error);
                return;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "YouTubeDownloader");
            try { Directory.CreateDirectory(tempDir); }
            catch (Exception ex)
            {
                Notify(L10n.T(Msg.EdErrorMove) + " " + ex.Message, MessageBoxIcon.Error);
                return;
            }
            string tempFile = Path.Combine(tempDir, "edit_" + Process.GetCurrentProcess().Id + "_" + DateTime.Now.Ticks + ".mp4");
            bool hasAudio = _media.HasAudio;
            string[] args = TrimJob.BuildTrimArgs(_file, tempFile, regions, hasAudio);
            double expected = TrimJob.ExpectedOutputDuration(regions);

            _busy = true;
            _cancelRequested = false;
            _lastFfmpegErrLine = null;
            _lastSavedPath = null;
            UpdateButtons();
            ShowProgress();
            SetStatusRaw(L10n.T(Msg.EdStatusTrimming) + " 0%", Theme.Dim);

            StringBuilder errTail = new StringBuilder();
            Task<TrimOutcome> task = Task.Factory.StartNew(new Func<TrimOutcome>(delegate
            {
                return RunSaveWithHybrid(_file, tempFile, regions, hasAudio, expected, args, errTail);
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            task.ContinueWith(new Action<Task<TrimOutcome>>(delegate(Task<TrimOutcome> tt)
            {
                TrimOutcome outcome = null;
                try { outcome = tt.Result; }
                catch (Exception ex) { outcome = new TrimOutcome { Error = ex.Message }; }
                _jobProc = null;
                _busy = false;
                if (outcome.Canceled)
                {
                    TryDeleteTemp(tempFile);
                    HideProgress();
                    UpdateButtons();
                    SetStatusRaw(L10n.T(Msg.EdStatusTrimCanceled), Theme.Orange);
                    return;
                }
                if (!outcome.Ok)
                {
                    // The original is untouched; only the half-written temp is dropped.
                    TryDeleteTemp(tempFile);
                    HideProgress();
                    UpdateButtons();
                    SetStatusRaw(L10n.T(Msg.EdErrorTrim), Theme.Red);
                    Notify(L10n.T(Msg.EdErrorTrim) + "\n" + (outcome.Error ?? errTail.ToString()), MessageBoxIcon.Error);
                    return;
                }
                // Never replace the source, and never silently overwrite an earlier
                // result: if the name got taken while ffmpeg ran, take the next free one.
                bool moved = false;
                string moveErr = null;
                try
                {
                    if (File.Exists(outPath)) outPath = TrimJob.SuggestTrimmedPath(_file);
                    File.Move(tempFile, outPath);
                    moved = true;
                }
                catch (Exception ex) { moveErr = ex.Message; }
                if (!moved)
                {
                    TryDeleteTemp(tempFile);
                    HideProgress();
                    UpdateButtons();
                    SetStatusRaw(L10n.T(Msg.EdErrorMove), Theme.Red);
                    Notify(L10n.T(Msg.EdErrorMove) + " " + moveErr, MessageBoxIcon.Error);
                    return;
                }
                _lastSavedPath = outPath;
                UpdateButtons();
                // The bar is the visible record of the save: it stays at 100 % while the
                // result is on disk. Success is reported in the editor's own status line,
                // not in a system message box, and with the file name only - the result
                // always lands next to the source.
                CompleteProgress();
                SetStatusRaw(L10n.T(Msg.EdStatusTrimSaved, Path.GetFileName(outPath)), Theme.Green);
                _statusHoldUntil = Environment.TickCount + StatusNoticeMs;
            }), TaskScheduler.FromCurrentSynchronizationContext());
        }

        // The progress line of a save: hidden at rest, 0 % -> filling -> 100 % on disk.
        private void ShowProgress()
        {
            _pb.Value = 0;
            _pb.Visible = true;
        }

        private void CompleteProgress()
        {
            _pb.Value = 100;
            _pb.Visible = true;
        }

        private void HideProgress()
        {
            _pb.Value = 0;
            _pb.Visible = false;
        }

        // The smoke test drives the very same button, so it must be able to keep the
        // modal error dialogs from blocking an automated run. The success path needs no
        // dialog at all - it reports through the status line.
        private void Notify(string text, MessageBoxIcon icon)
        {
            if (_suppressDialogs) return;
            MessageBox.Show(this, text, "YouTube Downloader", MessageBoxButtons.OK, icon);
        }

        private class TrimOutcome
        {
            public bool Ok;
            public bool Canceled;
            public string Error;
        }

        private TrimOutcome RunSaveWithHybrid(string input, string tempFile,
            List<TrimRegion> regions, bool hasAudio, double expectedDuration,
            string[] fullArgs, StringBuilder errTail)
        {
            // Hybrid first: stream-copy всё с keyframe, re-encode только головы.
            // Любая проблема → тихий fallback на проверенный полный encode.
            HybridCut.Plan plan = null;
            try
            {
                string vcodec = _media != null ? _media.VideoCodec : null;
                double fps = HybridCut.ProbeFps(input);
                plan = HybridCut.TryPlan(input, regions, vcodec, fps,
                    _media != null ? _media.Duration : 0, hasAudio);
            }
            catch (Exception ex)
            {
                lock (errTail) { errTail.AppendLine("hybrid plan: " + ex.Message); }
                plan = null;
            }
            if (plan != null && plan.Eligible)
            {
                HybridCutRunner.LastPlan = plan;
                StringBuilder herr = new StringBuilder();
                HybridCutRunner.StepResult hr = HybridCutRunner.TryHybrid(input, tempFile, plan,
                    hasAudio,
                    delegate { return _cancelRequested; },
                    delegate(double p)
                    {
                        int pct = (int)Math.Round(100.0 * p);
                        if (pct < 0) pct = 0;
                        if (pct > 100) pct = 100;
                        BeginInvokeSafe(delegate
                        {
                            if (_cancelRequested) return;
                            _pb.Value = pct;
                            SetStatusRaw(L10n.T(Msg.EdStatusTrimming) + " " + pct + "%", Theme.Dim);
                        });
                    },
                    herr);
                if (hr.Ok)
                {
                    TrimOutcome ok = new TrimOutcome();
                    ok.Ok = true;
                    return ok;
                }
                if (hr.Canceled)
                {
                    TrimOutcome c = new TrimOutcome();
                    c.Canceled = true;
                    return c;
                }
                lock (errTail) { errTail.Append(herr.ToString()); errTail.AppendLine("hybrid failed (" + hr.Error + "), fallback to full encode"); }
                TryDeleteTemp(tempFile);
            }
            else if (plan != null)
            {
                lock (errTail) { errTail.AppendLine("hybrid skipped: " + plan.Reason); }
            }
            return RunTrimProcess(fullArgs, expectedDuration, errTail);
        }

        private TrimOutcome RunTrimProcess(string[] args, double expectedDuration, StringBuilder errTail)
        {
            TrimOutcome outcome = new TrimOutcome();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                psi.Arguments = YtDlpRunner.FormatArgs(args);
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);

                Process p = new Process();
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    double? t = TrimJob.TryProgressOutTimeUs(e.Data);
                    if (t.HasValue && expectedDuration > 0)
                    {
                        int pct = (int)Math.Round(100.0 * t.Value / expectedDuration);
                        if (pct < 0) pct = 0;
                        if (pct > 100) pct = 100;
                        BeginInvokeSafe(delegate
                        {
                            if (_cancelRequested) return;
                            _pb.Value = pct;
                            SetStatusRaw(L10n.T(Msg.EdStatusTrimming) + " " + pct + "%", Theme.Dim);
                        });
                    }
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (errTail)
                    {
                        if (errTail.Length > 4000) return;
                        errTail.AppendLine(e.Data);
                    }
                    _lastFfmpegErrLine = e.Data;
                };

                _jobProc = p;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(200))
                {
                    if (_cancelRequested)
                    {
                        YtDlpRunner.KillTree(p);
                        p.WaitForExit(5000);
                        outcome.Canceled = true;
                        break;
                    }
                }
                if (!outcome.Canceled && p.ExitCode != 0)
                {
                    outcome.Error = "ffmpeg exit code " + p.ExitCode;
                    lock (errTail)
                    {
                        if (errTail.Length == 0 && _lastFfmpegErrLine != null) errTail.AppendLine(_lastFfmpegErrLine);
                    }
                }
                else
                {
                    outcome.Ok = true;
                }
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
                return outcome;
            }
        }

        private void BeginInvokeSafe(MethodInvoker action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static void TryDeleteTemp(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private void OnFormClosingInner(object sender, FormClosingEventArgs e)
        {
            if (_jobProc != null && !_cancelRequested)
            {
                if (MessageBox.Show(this, L10n.T(Msg.EdMsgExitBusy), "YouTube Downloader",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
                _cancelRequested = true;
                YtDlpRunner.KillTreeNoWait(_jobProc);
                e.Cancel = true;
                return;
            }
            _uiClosing = true;
            _previewThrottle.Stop();
            DisposeEditor();
        }

        private void DisposeEditor()
        {
            if (_scrubber != null)
            {
                // The scrubber owns every decoded frame it published, including the one
                // currently on screen. Forget that reference so this form never touches
                // a bitmap the scrubber is about to free.
                _scrubber.FrameReady -= OnPreviewFrame;
                _scrubber.Dispose();
                _scrubber = null;
                if (!_shownOwned) _shownBmp = null;
            }
            if (_decoder != null)
            {
                _decoder.Dispose();
                _decoder = null;
            }
            if (_strip != null)
            {
                _strip = null;      // pure managed buffer, GC handles the rest
            }
            _stripLoading = false;
            _stripGen++;            // any in-flight preparation belongs to the past
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            _previewThrottle.Dispose();
            // Dispose the single embedded LibVLC player on final close: Stop,
            // release Media, dispose MediaPlayer and LibVLC. No leftover decoder
            // or process survives the editor.
            if (_libVlc != null)
            {
                try { _libVlc.Dispose(); }
                catch { }
                _libVlc = null;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            EditTiming.Mark("EditorForm shown (VideoView handle created="
                + (_videoView != null && _videoView.IsHandleCreated) + ")");
            // The window is on screen NOW. Paint it (the status line included)
            // before the opening work starts, so a cold first open shows a window
            // that fills itself in rather than nothing at all.
            Update();

            string file = _pendingFile;
            _pendingFile = null;
            if (file != null)
            {
                // One embedded LibVLC player for THIS editor window: brought up
                // here, reused across Open / Close / Reopen, released on close.
                // The engine behind it is process-wide (see
                // LibVlcPreview.EnsureEngine) and is normally already warm from
                // RuntimeWarmup. If libvlc is unavailable the editor silently
                // falls back to the FFmpeg preview.
                InitLibVlc();
                EditTiming.Mark("EditorForm.InitLibVlc done (libvlc runtime ready)");

                LoadMedia(file);
                EditTiming.Mark("EditorForm.LoadMedia done");
            }

            // Shown is raised only once the editor is loaded: that is the order the
            // test harnesses and MainForm rely on (they used to get it for free
            // because the constructor did the loading).
            base.OnShown(e);
            EditTiming.StartUiProbe(this);
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
            // The editor uses the SAME window chrome as the main window (see
            // MainForm.WndProc): borderless, resizable, minimizable/maximizable,
            // with the same resize borders and caption hit-testing, and the
            // maximize bounds clamped to the monitor work area.
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
                if (_titleBar != null && cpt.Y >= 0 && cpt.Y < _titleBar.Height)
                {
                    m.Result = (IntPtr)ChromeApi.HtCaption;
                    return;
                }
                m.Result = (IntPtr)ChromeApi.HtClient;
                return;
            }
            base.WndProc(ref m);
        }

        // ---------------------------------------------------------------- test hooks
        // Used only by the editor smoke test (--editortest). They drive the very same
        // code path a mouse drag takes - position change, throttled request, MouseUp
        // final request - so the wiring can be checked without a real mouse.

        internal void TestDriveScrub(double[] positions)
        {
            if (positions == null) return;
            for (int i = 0; i < positions.Length; i++)
            {
                _filmstrip.Position = positions[i];
                OnFilmstripScrubEnded();
                Application.DoEvents();
            }
        }

        // Moves the white playhead through the filmstrip's own mouse messages, the same
        // way a real click/drag would, so the smoke test positions the playhead exactly
        // like the user before pressing Cut to set Start / End.
        internal void TestSeekTo(double time)
        {
            _filmstrip.TestSeekTo(new double[] { time });
            Application.DoEvents();
        }

        // TEMPORARY DIAGNOSTICS: a paced, real-mouse-shaped scrub. The acceptance
        // driver presses once, then delivers ONE move per timer tick (30-40 ms
        // apart, exactly how fast a hand can move) and releases at the end. Unlike
        // TestSeekTo (all moves back to back), the seek timer fires BETWEEN the
        // moves here, so a seek is applied while the next move is already coming -
        // the interleaving a real fast drag produces.
        internal void TestScrubStart(double time) { _filmstrip.TestScrubStart(time); }
        internal void TestScrubMove(double time) { _filmstrip.TestScrubMove(time); }
        internal void TestScrubEnd() { _filmstrip.TestScrubEnd(); Application.DoEvents(); }

        // TEMPORARY DIAGNOSTICS: the full-speed burst - press + ALL moves back to
        // back through the filmstrip's real mouse-message path, release at the
        // end. Stresses latest-wins: requests arrive far faster than the seek
        // timer can fire.
        internal void TestScrubBurst(double[] times)
        {
            if (times == null || times.Length == 0) return;
            _filmstrip.TestSeekTo(times);
            Application.DoEvents();
        }

        // TEMPORARY DIAGNOSTICS: live vout count / input seekability, the facts
        // that decide whether a seek can produce a new picture at all.
        internal int TestLibVlcVoutCount
        {
            get { return _libVlc != null ? _libVlc.VoutCount : -1; }
        }
        internal int TestLibVlcSeekable
        {
            get { return _libVlc != null ? _libVlc.SeekableState : -1; }
        }

        // The Start that has been placed for the range being built (NaN semantics are
        // avoided: while nothing is being built this equals the current playhead).
        internal double TestRangeStart
        {
            get { return _filmstrip.RangeStart; }
        }

        // The live End of the range being built, i.e. the current playhead.
        internal double TestRangeEnd
        {
            get { return _filmstrip.RangeEnd; }
        }

        // True while a range is being built (a Start is placed, waiting for End).
        internal bool TestRangeBuilding
        {
            get { return _filmstrip.HasRange; }
        }

        // Cut / Cancel go through the very same button click handlers a real mouse
        // click reaches, so the smoke test exercises the wired behaviour, not a copy.
        // Cut is the two-stage mark action: first press sets Start, second sets End.
        internal void TestCut()
        {
            if (_btnCut != null && _btnCut.Enabled) _btnCut.PerformClick();
            Application.DoEvents();
        }

        internal void TestCancelRange()
        {
            if (_btnCancel != null && _btnCancel.Enabled) _btnCancel.PerformClick();
            Application.DoEvents();
        }

        // Zoom goes through the very same buttons a mouse click reaches, so the smoke
        // test exercises the wired control, not a copy of its action. The button is
        // waited for because its enabled state follows the view, which is only known
        // once the strip has painted itself for the first time.
        // The zoom buttons are gone from the UI; the smoke test drives the very
        // same view-scale operations of the filmstrip the buttons used to reach.
        internal void TestZoomOut()
        {
            if (_filmstrip == null || !_filmstrip.CanZoomOut) return;
            _filmstrip.ZoomOut();
            Application.DoEvents();
        }

        internal void TestZoomIn()
        {
            if (_filmstrip == null || !_filmstrip.CanZoomIn) return;
            _filmstrip.ZoomIn();
            Application.DoEvents();
        }

        internal void TestZoomFit()
        {
            if (_filmstrip == null) return;
            _filmstrip.ZoomToFit();
            Application.DoEvents();
        }

        private static bool TestWaitEnabled(Button b, int timeoutMs = 5000)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (b != null && !b.Enabled && sw.ElapsedMilliseconds < timeoutMs)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            return b != null && b.Enabled;
        }

        // View-scale diagnostics: read-only, and all of them are VIEW state, never
        // media state - which is exactly what the zoom checks have to prove.
        internal double TestPixelsPerSecond
        {
            get { return _filmstrip != null ? _filmstrip.PixelsPerSecond : 0; }
        }

        // "Fit" as a concept is gone: the strip opens at a fixed scale and the
        // clip is never squeezed into the window width, so there is no fit
        // scale to report.
        internal double TestZoomLevel
        {
            get { return _filmstrip != null ? _filmstrip.ZoomLevel : 0; }
        }

        internal double TestViewStart
        {
            get { return _filmstrip != null ? _filmstrip.ViewStart : 0; }
        }

        internal double TestViewEnd
        {
            get { return _filmstrip != null ? _filmstrip.ViewEnd : 0; }
        }

        internal int TestTimelineWidth
        {
            get { return _filmstrip != null ? _filmstrip.Width : 0; }
        }

        internal bool TestCanZoomOut
        {
            get { return _filmstrip != null && _filmstrip.CanZoomOut; }
        }

        internal bool TestCanZoomIn
        {
            get { return _filmstrip != null && _filmstrip.CanZoomIn; }
        }

        // The real playhead time on the timeline (seconds), independent of any zoom.
        internal double TestPosition
        {
            get { return _filmstrip != null ? _filmstrip.Position : double.NaN; }
        }

        internal double TestDuration
        {
            get { return _filmstrip != null ? _filmstrip.Duration : double.NaN; }
        }

        // True when the whole clip (00:00 .. end) is inside the visible window, i.e.
        // the timeline is in "the entire movie on one screen" mode.
        internal bool TestWholeClipVisible
        {
            get
            {
                if (_filmstrip == null) return false;
                return _filmstrip.ViewStart <= 0.05
                    && _filmstrip.ViewEnd >= _filmstrip.Duration - 0.05;
            }
        }

        // ------------------------------------------------------- overview diagnostics
        // The strip is either fully prepared (one whole filmstrip) or not shown at
        // all, so the diagnostics read the same binary state.
        internal bool TestOverviewDone
        {
            get { return _strip != null && _strip.Count > 0; }
        }

        internal bool TestOverviewRunning
        {
            get { return _stripLoading; }
        }

        internal int TestVisibleCells
        {
            get
            {
                if (_filmstrip == null) return 0;
                int visible, ready;
                _filmstrip.VisibleCoverage(out visible, out ready);
                return visible;
            }
        }

        internal int TestVisibleCellsReady
        {
            get
            {
                if (_filmstrip == null) return 0;
                int visible, ready;
                _filmstrip.VisibleCoverage(out visible, out ready);
                return ready;
            }
        }

        internal int TestVisibleCellsMissing
        {
            get
            {
                int visible = TestVisibleCells;
                return visible - TestVisibleCellsReady;
            }
        }

        internal int TestStoreCount
        {
            get { return _strip != null ? _strip.Count : 0; }
        }

        // Wall time of the last strip prepare (cache hit or Generate).
        internal int TestStripPrepMs
        {
            get { return _stripPrepMs; }
        }

        internal string TestStripPrepMode
        {
            get { return _stripPrepMode ?? ""; }
        }

        // Frames the strip plan asked for (physical width / proxy step).
        internal int TestDenseFrameCount
        {
            get { return _filmstrip != null ? _filmstrip.PlanStripFrames() : 0; }
        }

        internal string TestOverviewDiag
        {
            get
            {
                int visible = TestVisibleCells;
                int ready = TestVisibleCellsReady;
                return "cells=" + visible + " ready=" + ready
                    + " store=" + TestStoreCount + "/" + TestDenseFrameCount
                    + " strip=" + (_stripLoading ? "loading" : (TestOverviewDone ? "ready" : "none"))
                    + " prep=" + _stripPrepMode + " " + _stripPrepMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";
            }
        }

        internal int TestMarkedCount
        {
            get { return _filmstrip.MarkedCount; }
        }

        // "Save video (trim)" carries meaning through its enabled state alone: it is on
        // exactly when at least one applied range would be physically removed.
        internal bool TestSaveEnabled
        {
            get { return _btnSaveVideo != null && _btnSaveVideo.Enabled; }
        }

        internal bool TestSuppressDialogs
        {
            set { _suppressDialogs = value; }
        }

        // What the editor's own status line currently reads - the success of a trim is
        // reported there, in the bottom area, and not in a system dialog.
        internal string TestStatusText
        {
            get { return _lblStatus != null ? _lblStatus.Text : null; }
        }

        // Clicks the real button - the same handler a mouse click reaches - and pumps
        // the UI until the trim job is over, so the caller can inspect what it wrote.
        internal string TestClickSaveVideo(int timeoutMs)
        {
            if (_btnSaveVideo == null || !_btnSaveVideo.Enabled) return null;
            _progressPeak = -1;
            _btnSaveVideo.PerformClick();
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Application.DoEvents();
                if (_pb != null && _pb.Visible && _pb.Value > _progressPeak) _progressPeak = _pb.Value;
                if (!_busy && _lastSavedPath != null) break;
                if (!_busy && sw.ElapsedMilliseconds > 500) break;   // job ended without a result
                Thread.Sleep(20);
            }
            return _lastSavedPath;
        }

        // Highest value the progress line reached while the job ran (-1 = never shown).
        internal int TestProgressPeak
        {
            get { return _progressPeak; }
        }

        internal int TestProgressValue
        {
            get { return _pb != null ? _pb.Value : -1; }
        }

        internal bool TestProgressVisible
        {
            get { return _pb != null && _pb.Visible; }
        }

        internal void TestStepForward()
        {
            StepBy(true);
            Application.DoEvents();
        }

        internal void TestStepBackward()
        {
            StepBy(false);
            Application.DoEvents();
        }

        internal bool TestSaveFrameEnabled
        {
            get { return _btnSaveFrame != null && _btnSaveFrame.Enabled; }
        }

        internal bool TestClickSaveFrame(string folder, out string savedPath, out string error)
        {
            savedPath = null;
            error = null;
            if (_busy || string.IsNullOrEmpty(_file) || !File.Exists(_file))
            {
                error = "busy or invalid file";
                return false;
            }
            double pos = _filmstrip != null ? _filmstrip.Position : 0;
            string outPath = GenerateUniquePngPath(folder, _file, pos);
            bool ok = ExtractSourceFrame(_file, outPath, pos, out error);
            if (ok)
            {
                savedPath = outPath;
                string name = Path.GetFileName(outPath);
                SetStatusRaw(L10n.T(Msg.EdStatusFrameSaved) + ": " + name, Theme.Green);
            }
            return ok;
        }

        // Non-interactive twin of the crop flow for --selftest: the same temp
        // frame extract, the same crop ffmpeg call and the same always-delete
        // cleanup, with the rectangle handed in instead of drawn in the window.
        // The temp path is returned so the test can prove the file is gone.
        internal bool TestCropFrame(string folder, Rectangle sel, out string savedPath,
            out string tempPath, out string error)
        {
            savedPath = null;
            tempPath = NewTempFramePath();
            if (_busy || string.IsNullOrEmpty(_file) || !File.Exists(_file))
            {
                error = "busy or invalid file";
                TryDeleteTempFrame(tempPath);
                return false;
            }
            double pos = _filmstrip != null ? _filmstrip.Position : 0;
            try
            {
                if (!ExtractSourceFrame(_file, tempPath, pos, out error)) return false;
                string outPath = GenerateUniquePngPath(folder, _file, pos);
                if (!ExtractCroppedPng(tempPath, outPath, sel, out error)) return false;
                savedPath = outPath;
                error = null;
                return true;
            }
            finally
            {
                TryDeleteTempFrame(tempPath);
            }
        }

        internal double TestMarkedStart(int index)
        {
            return _filmstrip.MarkedStart(index);
        }

        internal double TestMarkedEnd(int index)
        {
            return _filmstrip.MarkedEnd(index);
        }

        internal string TestMarksDiag
        {
            get
            {
                int n = _filmstrip.MarkedCount;
                if (n == 0) return "(нет)";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(_filmstrip.MarkedStart(i).ToString("N3", System.Globalization.CultureInfo.InvariantCulture));
                    sb.Append('-');
                    sb.Append(_filmstrip.MarkedEnd(i).ToString("N3", System.Globalization.CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        // Test hook: leave the editor in the state a freshly opened file starts in, so a
        // following visual (real mouse) run starts from a known picture.
        internal void TestResetRanges()
        {
            _filmstrip.TestResetRanges();
            Application.DoEvents();
        }

        internal bool TestScrubberIdle
        {
            get { return _scrubber == null || _scrubber.IsIdle; }
        }

        // True once the media is open and the editor is actually usable. The opening
        // frame pass runs in the background and is deliberately NOT part of this: the
        // strip is a working track from the first paint, so the editor must not report
        // itself busy while frames are still streaming in.
        internal bool TestReady
        {
            get { return _scrubber != null && !_busy; }
        }

        internal bool TestBusy
        {
            get { return _busy; }
        }

        // TEMPORARY DIAGNOSTICS: rerun the full open path on the SAME file, so the
        // acceptance can drive the "Open -> PAUSED -> immediately fast-drag the
        // playhead" repro window without a second process.
        internal void TestReopen()
        {
            if (string.IsNullOrEmpty(_file) || !File.Exists(_file)) return;
            LoadMedia(_file);
        }

        internal double TestShownPts
        {
            get { return _scrubber != null ? _scrubber.DisplayedPts : double.NaN; }
        }

        internal int TestProcessStarts
        {
            get { return _scrubber != null ? _scrubber.ProcessStarts : -1; }
        }

        internal string TestScrubDiag
        {
            get
            {
                if (_scrubber == null) return "no scrubber";
                return "req=" + _scrubber.Requests
                    + " decodes=" + _scrubber.DecodesStarted
                    + " steps=" + _scrubber.StepsForward
                    + " procs=" + _scrubber.ProcessStarts
                    + " cacheHits=" + _scrubber.CacheHits
                    + " err=" + _scrubber.DecodeErrors
                    + " abandoned=" + _scrubber.DecodesAbandoned
                    + " idle=" + _scrubber.IsIdle
                    + " proxy=" + (_scrubber.ProxyReady ? "ready" : (_scrubber.ProxyFailed ? "fail" : "build"))
                    + " proxyMs=" + _scrubber.ProxyBuildMs
                    + " proxyBytes=" + _scrubber.ProxyBytes
                    + " proxySeek=" + _scrubber.LastProxySeekMs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + " origSeek=" + _scrubber.LastOrigSeekMs.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
                    + " pos=" + (_filmstrip != null ? _filmstrip.Position.ToString("N3", System.Globalization.CultureInfo.InvariantCulture) : "n/a");
            }
        }

        internal bool TestHasPreviewFrame
        {
            get { return _preview != null && _preview.Image != null; }
        }

        // ------------------------------------------------- LibVLC player test hooks
        // Expose the embedded player's live state so the verification harness can
        // assert the TASK 2.4 UX contract (no autoplay, first frame, seek without
        // playback, play-from-current-position) on the real editor.

        internal bool TestLibVlcActive
        {
            get { return LibVlcPreviewActive; }
        }

        internal bool TestLibVlcPlaying
        {
            get { return _libVlc != null && _libVlc.IsPlaying; }
        }

        internal double TestLibVlcPositionMs
        {
            get
            {
                if (_libVlc == null || !_libVlc.HasMedia) return double.NaN;
                try { return _libVlc.PositionMs; }
                catch { return double.NaN; }
            }
        }

        // Exact MediaPlayer state name (Paused / Playing / Stopped / Ended ...):
        // acceptance evidence for which transport state the playhead is driving.
        internal string TestLibVlcState
        {
            get { return _libVlc != null ? _libVlc.StateText : "no-player"; }
        }

        // Evidence for the "VideoView is display-only" part of the UX contract:
        // the embedded VideoView must host NO child controls, i.e. no VLC
        // transport controls, no VLC seek bar, no Full/Ratio/fullscreen buttons
        // and no second timeline. -1 means the player was never built.
        internal int TestVideoViewChildControlCount
        {
            get { return _videoView != null ? _videoView.Controls.Count : -1; }
        }

        // Drives the real Play/Pause button handler (the same one a click reaches).
        internal void TestPlayPause()
        {
            if (_btnPlayPause == null) return;
            OnPlayPause();
            Application.DoEvents();
        }

        // Reopens a file through the exact Open Video... path (LoadMedia), so the
        // harness can verify a close/reopen cycle: the player gets new media, shows
        // the first frame, is PAUSED and the playhead is back at 0.
        internal void TestReopen(string path)
        {
            LoadMedia(path);
            Application.DoEvents();
        }

        // Screen rectangles of the three parts of the preview UX, so the acceptance
        // run can prove where Play physically is: between the video picture and the
        // filmstrip, never inside the video and never after the timeline.
        internal Rectangle TestVideoViewScreenRect
        {
            get { return _videoView != null ? _videoView.RectangleToScreen(_videoView.ClientRectangle) : Rectangle.Empty; }
        }

        internal Rectangle TestPlayButtonScreenRect
        {
            get { return _btnPlayPause != null ? _btnPlayPause.RectangleToScreen(_btnPlayPause.ClientRectangle) : Rectangle.Empty; }
        }

        internal Rectangle TestFilmstripScreenRect
        {
            get { return _filmstrip != null ? _filmstrip.RectangleToScreen(_filmstrip.ClientRectangle) : Rectangle.Empty; }
        }

        internal bool TestVideoViewVisible
        {
            get { return _videoView != null && _videoView.Visible && !_videoView.IsDisposed; }
        }

        internal bool TestPlayButtonEnabled
        {
            get { return _btnPlayPause != null && _btnPlayPause.Enabled; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left:
                    StepBy(false);
                    return true;
                case Keys.Right:
                    StepBy(true);
                    return true;
                case Keys.Prior:
                    _filmstrip.ScrollBy(-(_filmstrip.ViewEnd - _filmstrip.ViewStart) * 0.8);
                    return true;
                case Keys.Next:
                    _filmstrip.ScrollBy((_filmstrip.ViewEnd - _filmstrip.ViewStart) * 0.8);
                    return true;
                // Ctrl+Z / Ctrl+Y / Delete used to drive the old CutModel-based cut
                // path and the hidden TimelineControl. Both are gone: marks live in
                // the FilmstripTimeline and are undone one at a time with the Cancel
                // button, so these keys intentionally have no binding now.
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
