using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeDownloader
{
    public class DecodedFrame
    {
        public Bitmap Image;
        public double PtsTime;
        public int SessionIndex;
    }

    public sealed class FrameDecoder : IDisposable
    {
        // ffmpeg's input seek (-ss before -i) is already frame accurate: it jumps to
        // the preceding keyframe and decodes forward to the requested time. The
        // lookback therefore buys no accuracy, it only makes ffmpeg hand out that
        // many extra seconds of frames which have to be read and thrown away again.
        // Kept small as a rounding guard.
        private const double SeekLookback = 0.05;
        private const double Eps = 0.002;

        private readonly string _file;
        private readonly int _width;
        private readonly int _height;
        private readonly long _frameBytes;
        private readonly byte[] _buf;
        private readonly List<double> _visited = new List<double>();

        private Process _proc;
        private Stream _pipe;
        private readonly object _sync = new object();
        private readonly List<double> _ptsQueue = new List<double>();
        private bool _stderrEof;
        private int _sessionIndex;
        private double _lastDelivered = -1;
        private bool _disposed;
        private int _processStarts;

        private static readonly Regex RxPtsTime = new Regex(@"pts_time:(?<t>[\d.]+)", RegexOptions.Compiled);

        public FrameDecoder(string file, int width, int height)
        {
            _file = file;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            long bytes = (long)_width * _height * 4;
            if (bytes > 128 * 1024 * 1024) bytes = 128 * 1024 * 1024;
            _frameBytes = bytes;
            _buf = new byte[_frameBytes];
        }

        public bool HasSession
        {
            get { lock (_sync) { return _proc != null; } }
        }

        public double LastPts
        {
            get { return _lastDelivered; }
        }

        // How many ffmpeg processes this decoder has spawned. Diagnostic only: the
        // scrubbing work needs it to prove that a drag no longer restarts ffmpeg for
        // every playhead move.
        public int ProcessStarts
        {
            get { return _processStarts; }
        }

        // Measured frame duration, taken from the timestamps of the frames already
        // read in this session. Used by the preview scrubber to decide whether
        // stepping forward is cheaper than seeking.
        public double EstimatedFrameDuration
        {
            get { return EstimateStep(); }
        }

        // Drops the live session (and its ffmpeg process) but keeps the decoder
        // usable: the next StartAt/StepForward starts a fresh one. Used to release
        // the process when scrubbing goes idle.
        public void ReleaseSession()
        {
            lock (_sync)
            {
                if (_disposed) return;
                KillCurrentNoThrow();
            }
        }

        public DecodedFrame StartAt(double timeSec)
        {
            lock (_sync)
            {
                if (_disposed) return null;
                KillCurrentNoThrow();
                if (!StartProcess(timeSec)) return null;
            }
            return ReadUntil(timeSec);
        }

        public DecodedFrame StepForward()
        {
            bool alive;
            lock (_sync) { alive = _proc != null; }
            if (!alive) return null;
            return ReadOneFrame();
        }

        public DecodedFrame StepBackward()
        {
            double target = PreviousVisitedTime(_lastDelivered);
            if (target < 0) target = 0;
            return StartAt(target);
        }

        private double PreviousVisitedTime(double current)
        {
            lock (_sync)
            {
                double best = -1;
                for (int i = 0; i < _visited.Count; i++)
                {
                    double t = _visited[i];
                    if (t < current - Eps && t > best) best = t;
                }
                if (best >= 0) return best;
                if (current > 0)
                {
                    double step = EstimateStep();
                    return Math.Max(0, current - step);
                }
                return -1;
            }
        }

        private double EstimateStep()
        {
            lock (_sync)
            {
                if (_visited.Count >= 2)
                {
                    double sum = 0;
                    int n = 0;
                    for (int i = _visited.Count - 1; i > 0 && n < 16; i--)
                    {
                        double d = _visited[i] - _visited[i - 1];
                        // Only consecutive frames count. A larger gap is not a frame
                        // duration, it is a hole in the sequence (a retry or a seek),
                        // and averaging it in would inflate the estimate wildly.
                        if (d > 1e-6 && d <= 1.0) { sum += d; n++; }
                    }
                    if (n > 0) return sum / n;
                }
            }
            return 1.0 / 30.0;
        }

        // Decodes forward from the seek point to the requested time. The frames passed
        // on the way are read and dropped WITHOUT being turned into bitmaps: only the
        // frame that was actually asked for is converted. A 60 fps source with a seek
        // that starts a fraction of a second early used to mean ~30 throw-away bitmaps
        // per seek, each one a 3.7 MB allocation plus a 3.7 MB row-by-row copy.
        private DecodedFrame ReadUntil(double target)
        {
            return ReadUntil(target, true);
        }

        private DecodedFrame ReadUntil(double target, bool allowRetry)
        {
            double lastPts = -1;
            while (true)
            {
                double pts;
                int index;
                if (!ReadOneRaw(out pts, out index)) break;
                lastPts = pts;
                if (target <= 0 || pts >= target - Eps) return BuildFrame(pts, index);
            }

            // The stream ended before the requested time: a seek at or past the last
            // frame, or a short read. Retry once, aimed at the last frame we saw, so
            // the caller gets a picture instead of nothing.
            if (allowRetry && lastPts >= 0 && target > lastPts + Eps)
            {
                bool restarted;
                lock (_sync)
                {
                    if (_disposed) return null;
                    KillCurrentNoThrow();
                    restarted = StartProcess(lastPts);
                }
                if (restarted) return ReadUntil(lastPts, false);
            }
            return null;
        }

        private bool StartProcess(double seekTo)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                // -copyts is MANDATORY here, not cosmetic. Without it ffmpeg rebases
                // the output timestamps to zero at the seek point, so showinfo reports
                // 0, 0.0166, 0.0333 ... for a seek at 99.5 s. ReadUntil() compares
                // those timestamps against an ABSOLUTE timeline position, so it could
                // never see the target frame and decoded the whole rest of the file
                // instead. With -copyts the first frame is reported at 99.5 s and the
                // walk stops where it should.
                psi.Arguments = "-copyts -ss " + Fmt(Math.Max(0, seekTo - SeekLookback))
                    + " -i " + YtDlpRunner.Quote(_file)
                    + " -vf showinfo -map 0:v:0 -an -sn -dn"
                    + " -f rawvideo -pix_fmt bgra -";
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardErrorEncoding = new UTF8Encoding(false);

                _ptsQueue.Clear();
                _stderrEof = false;
                _sessionIndex = 0;
                // The visited list is the basis for the measured frame duration, so it
                // must not survive a seek: mixing the timestamp of the frame before the
                // seek (say 0) with the first frame after it (say 100) makes the decoder
                // believe a frame lasts 100 seconds. That poisons every cost estimate
                // and every "is this frame close enough" tolerance downstream.
                _visited.Clear();

                Process p = new Process();
                p.StartInfo = psi;
                p.ErrorDataReceived += OnStdErrLine;
                p.Start();
                p.BeginErrorReadLine();
                _pipe = p.StandardOutput.BaseStream;
                _proc = p;
                _processStarts++;
                return true;            }
            catch
            {
                _proc = null;
                _pipe = null;
                return false;
            }
        }

        private void OnStdErrLine(object sender, DataReceivedEventArgs e)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(sender, _proc)) return;
                if (e.Data == null)
                {
                    _stderrEof = true;
                    return;
                }
                Match m = RxPtsTime.Match(e.Data);
                if (!m.Success) return;
                double t;
                if (!double.TryParse(m.Groups["t"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out t)) return;
                _ptsQueue.Add(t);
            }
        }

        private DecodedFrame ReadOneFrame()
        {
            double pts;
            int index;
            if (!ReadOneRaw(out pts, out index)) return null;
            return BuildFrame(pts, index);
        }

        // Reads one whole frame off the pipe together with its pts, WITHOUT allocating
        // a bitmap. The pixels stay in the shared _buf, so a following BuildFrame()
        // converts the frame that was just read.
        private bool ReadOneRaw(out double pts, out int index)
        {
            pts = -1;
            index = 0;

            Stream pipe;
            lock (_sync) { pipe = _pipe; }
            if (pipe == null) return false;

            byte[] buf = _buf;
            int offset = 0;
            try
            {
                while (offset < _frameBytes)
                {
                    int n = pipe.Read(buf, offset, (int)(_frameBytes - offset));
                    if (n <= 0) break;
                    offset += n;
                }
            }
            catch
            {
                return false;
            }
            if (offset < _frameBytes) return false;

            index = Interlocked.Increment(ref _sessionIndex);
            pts = WaitPts(index - 1);
            NoteDelivered(pts);
            return true;
        }

        // Converts the frame currently held in _buf. Must be called on the same thread
        // that performed the matching ReadOneRaw(), before any further read.
        private DecodedFrame BuildFrame(double pts, int index)
        {
            byte[] buf = _buf;
            Bitmap bmp = null;
            try
            {
                bmp = new Bitmap(_width, _height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Rectangle rect = new Rectangle(0, 0, _width, _height);
                System.Drawing.Imaging.BitmapData bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < _height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(buf, y * _width * 4, bd.Scan0 + y * bd.Stride, _width * 4);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
            }
            catch
            {
                if (bmp != null) bmp.Dispose();
                return null;
            }

            DecodedFrame f = new DecodedFrame();
            f.Image = bmp;
            f.PtsTime = pts;
            f.SessionIndex = index;
            return f;
        }

        private void NoteDelivered(double pts)
        {
            lock (_sync)
            {
                _lastDelivered = pts >= 0 ? pts : _lastDelivered;
                if (_lastDelivered < 0) _lastDelivered = 0;
                if (_visited.Count == 0 || _visited[_visited.Count - 1] < _lastDelivered - Eps)
                {
                    _visited.Add(_lastDelivered);
                    while (_visited.Count > 256) _visited.RemoveAt(0);
                }
            }
        }

        private double WaitPts(int queueIndex)
        {
            for (int waited = 0; waited < 3000; waited += 20)
            {
                lock (_sync)
                {
                    if (_ptsQueue.Count > queueIndex) return _ptsQueue[queueIndex];
                    if (_stderrEof && _ptsQueue.Count <= queueIndex) break;
                }
                Thread.Sleep(20);
            }
            lock (_sync)
            {
                if (_ptsQueue.Count > queueIndex) return _ptsQueue[queueIndex];
            }
            return -1;
        }

        private void KillCurrentNoThrow()
        {
            Process p = _proc;
            Stream pipe = _pipe;
            _proc = null;
            _pipe = null;

            // Order matters. The process has to be stopped FIRST: disposing the pipe
            // while ffmpeg is still writing to it is what produced the visible
            // "ERROR_NO_DATA" (Win32 232 / 0x800700e8) failures. Once the process is
            // gone nothing can write into the closed handle any more.
            if (p != null)
            {
                try { p.ErrorDataReceived -= OnStdErrLine; }
                catch { }
                YtDlpRunner.KillTreeNoWait(p);
                try { p.Dispose(); }
                catch { }
            }
            if (pipe != null)
            {
                try { pipe.Dispose(); }
                catch { }
            }
        }

        private static string Fmt(double sec)
        {
            if (sec < 0) sec = 0;
            return sec.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                KillCurrentNoThrow();
            }
        }
    }
}
