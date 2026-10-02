using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YouTubeDownloader
{
    public class ThumbRun
    {
        public readonly long Id;
        public readonly double Start;
        public readonly double Duration;
        public readonly double Fps;
        public readonly int Height;
        // True for sampled runs: the thumbs are representative frames at arbitrary
        // times requested by the strip, not a uniform fps grid over [Start, Start+
        // Duration]. Indexes stay CELL indexes on the t = i / Fps grid, so the
        // existing TryGet / DrawStrip math keeps working unchanged.
        public readonly bool Sampled;
        public readonly Dictionary<int, Bitmap> Thumbs = new Dictionary<int, Bitmap>();
        public volatile bool Done;
        public volatile bool Canceled;
        public long LastTouch;
        internal Process Proc;

        public int ReadyCount
        {
            get { lock (Thumbs) { return Thumbs.Count; } }
        }

        internal ThumbRun(long id, double start, double duration, double fps, int height)
            : this(id, start, duration, fps, height, false)
        {
        }

        internal ThumbRun(long id, double start, double duration, double fps, int height, bool sampled)
        {
            Id = id;
            Start = start;
            Duration = duration;
            Fps = fps;
            Height = height;
            Sampled = sampled;
        }

        public bool Covers(double t)
        {
            return t >= Start - 1e-6 && t <= Start + Duration + 1e-6;
        }
    }

    public sealed class ThumbCache : IDisposable
    {
        private const int LruCap = 1400;

        private readonly string _file;
        private readonly System.ComponentModel.ISynchronizeInvoke _invoker;
        private readonly object _sync = new object();
        private readonly Dictionary<long, ThumbRun> _runs = new Dictionary<long, ThumbRun>();
        private long _nextId = 1;
        private int _totalThumbs;
        private bool _disposed;

        public event Action Changed;

        public ThumbCache(string file, System.ComponentModel.ISynchronizeInvoke invoker)
        {
            _file = file;
            _invoker = invoker;
        }

        public ThumbRun Level0 { get; private set; }

        public void StartLevel0(double duration, int height)
        {
            if (duration <= 0) return;
            double fps = 600.0 / duration;
            if (fps > 30.0) fps = 30.0;
            if (fps < 1.0 / 60.0) fps = 1.0 / 60.0;
            ThumbRun run = new ThumbRun(0, 0, duration, fps, height);
            lock (_sync)
            {
                if (Level0 != null) return;
                Level0 = run;
                _runs[0] = run;
            }
            StartReader(run, false);
        }

        public ThumbRun RequestWindow(double start, double duration, double fps, int height)
        {
            lock (_sync)
            {
                foreach (ThumbRun r in _runs.Values)
                {
                    if (r.Id == 0) continue;
                    if (r.Canceled) continue;
                    if (Math.Abs(r.Start - start) < 0.005 && Math.Abs(r.Duration - duration) < 0.05 && r.Fps >= fps - 1e-6)
                    {
                        r.LastTouch = Environment.TickCount;
                        return r;
                    }
                }
                ThumbRun run = new ThumbRun(_nextId++, start, duration, fps, height);
                _runs[run.Id] = run;
                StartReader(run, true);
                return run;
            }
        }

        // Seek-times packed into ONE ffmpeg process. Measured on AV1 720p60: a single
        // seek costs ~630 ms of which ~350 ms is just process start-up, so batching
        // several seeks into one process is far cheaper than one process per frame
        // (31 thumbs: 4 batched processes ~8.3 s vs 31 single processes ~19.5 s).
        private const int SampleBatch = 8;

        // Sampled-run entry point. `times` holds the times to sample in REQUEST
        // (priority) order; `indices[k]` is the strip cell that `times[k]` belongs
        // to. Cell i is expected to sit on the t = i / fps grid, which is exactly
        // the grid FilmstripTimeline paints with, so the strip is drawn by the
        // existing DrawStrip without any changes.
        public ThumbRun RequestSamples(double[] times, int[] indices, double duration, int height)
        {
            if (times == null || indices == null) return null;
            if (times.Length == 0 || times.Length != indices.Length) return null;
            if (duration <= 0) return null;

            int count = 0;
            for (int i = 0; i < indices.Length; i++)
                if (indices[i] + 1 > count) count = indices[i] + 1;
            if (count <= 0) return null;
            double fps = count / duration;

            lock (_sync)
            {
                foreach (ThumbRun r in _runs.Values)
                {
                    if (r.Id == 0 || r.Canceled || !r.Sampled) continue;
                    if (Math.Abs(r.Duration - duration) > 0.05) continue;
                    if (Math.Abs(r.Fps - fps) > 1e-9) continue;
                    r.LastTouch = Environment.TickCount;
                    return r;
                }
                ThumbRun run = new ThumbRun(_nextId++, 0, duration, fps, height, true);
                _runs[run.Id] = run;
                double[] t = (double[])times.Clone();
                int[] idx = (int[])indices.Clone();
                Task.Factory.StartNew(new Action(delegate { SampleReaderProc(run, t, idx); }), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return run;
            }
        }

        // Convenience overload for the plain case: `times` are already the cell grid
        // (times[i] == i * step), so the strip duration is times.Length * step.
        public ThumbRun RequestSamples(double[] times, int height)
        {
            if (times == null || times.Length < 2) return null;
            double step = times[1] - times[0];
            if (step <= 0) return null;
            int[] indices = new int[times.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            return RequestSamples(times, indices, times.Length * step, height);
        }

        public bool TryGet(ThumbRun run, int index, out Bitmap bmp)
        {
            bmp = null;
            if (run == null) return false;
            run.LastTouch = Environment.TickCount;
            lock (run.Thumbs)
            {
                return run.Thumbs.TryGetValue(index, out bmp);
            }
        }

        public void CancelRun(long runId)
        {
            ThumbRun run;
            lock (_sync)
            {
                if (!_runs.TryGetValue(runId, out run)) return;
            }
            if (run.Id == 0) return;
            run.Canceled = true;
            KillRun(run);
        }

        public void CancelAll()
        {
            List<ThumbRun> runs;
            lock (_sync) { runs = new List<ThumbRun>(_runs.Values); }
            foreach (ThumbRun r in runs)
            {
                if (r.Id == 0) continue;
                r.Canceled = true;
                KillRun(r);
            }
        }

        public void DropNonLevel0()
        {
            List<ThumbRun> drop;
            lock (_sync)
            {
                drop = new List<ThumbRun>();
                foreach (ThumbRun r in _runs.Values)
                    if (r.Id != 0) drop.Add(r);
                foreach (ThumbRun r in drop) _runs.Remove(r.Id);
            }
            foreach (ThumbRun r in drop)
            {
                r.Canceled = true;
                KillRun(r);
                DisposeRunThumbs(r);
            }
            FireChanged();
        }

        private void KillRun(ThumbRun run)
        {
            Process p = run.Proc;
            run.Proc = null;
            if (p != null)
            {
                YtDlpRunner.KillTreeNoWait(p);
                try { p.Dispose(); }
                catch { }
            }
        }

        private void DisposeRunThumbs(ThumbRun run)
        {
            lock (run.Thumbs)
            {
                foreach (Bitmap b in run.Thumbs.Values)
                {
                    try { b.Dispose(); }
                    catch { }
                }
                run.Thumbs.Clear();
            }
        }

        private void StartReader(ThumbRun run, bool seek)
        {
            Task.Factory.StartNew(new Action(delegate { ReaderProc(run, seek); }), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void ReaderProc(ThumbRun run, bool seek)
        {
            MemoryStream jpeg = new MemoryStream();
            List<Bitmap> batch = new List<Bitmap>();
            List<int> batchIndex = new List<int>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                string args = "";
                if (seek) args += "-ss " + Fmt(run.Start) + " -i " + YtDlpRunner.Quote(_file) + " -t " + Fmt(run.Duration);
                else args += "-i " + YtDlpRunner.Quote(_file);
                args += " -vf fps=" + FpsFmt(run.Fps) + ",scale=" + run.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":-2 -q:v 2 -f image2pipe -c:v mjpeg -nostats -";
                psi.Arguments = args;
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                Process p = new Process();
                p.StartInfo = psi;
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                lock (_sync)
                {
                    if (run.Canceled || _disposed || !_runs.ContainsKey(run.Id)) return;
                    run.Proc = p;
                }
                p.Start();
                p.BeginErrorReadLine();

                Stream outp = p.StandardOutput.BaseStream;
                byte[] buf = new byte[64 * 1024];
                int index = 0;
                bool inJpeg = false;
                int prev = -1;
                while (true)
                {
                    if (run.Canceled) break;
                    int n;
                    try { n = outp.Read(buf, 0, buf.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        int b = buf[i];
                        if (!inJpeg)
                        {
                            if (prev == 0xFF && b == 0xD8)
                            {
                                jpeg.WriteByte(0xFF);
                                jpeg.WriteByte(0xD8);
                                inJpeg = true;
                                prev = -1;
                                continue;
                            }
                        }
                        else
                        {
                            jpeg.WriteByte((byte)b);
                            if (prev == 0xFF && b == 0xD9)
                            {
                                byte[] bytes = jpeg.ToArray();
                                jpeg.SetLength(0);
                                inJpeg = false;
                                prev = -1;
                                Bitmap bmp = TryDecode(bytes);
                                if (bmp != null)
                                {
                                    batch.Add(bmp);
                                    batchIndex.Add(index);
                                }
                                index++;
                                if (batch.Count >= 24)
                                {
                                    Deliver(run, batch, batchIndex, false);
                                    batch = new List<Bitmap>();
                                    batchIndex = new List<int>();
                                }
                                continue;
                            }
                        }
                        prev = b;
                    }
                }
                Deliver(run, batch, batchIndex, true);
            }
            catch
            {
                foreach (Bitmap b in batch)
                {
                    try { b.Dispose(); }
                    catch { }
                }
                Deliver(run, null, null, true);
                return;
            }
            finally
            {
                jpeg.Dispose();
                Process p = run.Proc;
                if (p != null)
                {
                    try { if (!p.HasExited) YtDlpRunner.KillTreeNoWait(p); }
                    catch { }
                    try { p.Dispose(); }
                    catch { }
                    run.Proc = null;
                }
            }
        }

        // Walks the request plan in batches and hands every received frame to the UI
        // as soon as it exists, so the strip fills progressively instead of waiting
        // for a whole segment of video to finish decoding.
        private void SampleReaderProc(ThumbRun run, double[] times, int[] indices)
        {
            try
            {
                int pos = 0;
                while (pos < times.Length)
                {
                    if (run.Canceled || _disposed) break;
                    int n = Math.Min(SampleBatch, times.Length - pos);
                    double[] batchTimes = new double[n];
                    int[] batchIdx = new int[n];
                    Array.Copy(times, pos, batchTimes, 0, n);
                    Array.Copy(indices, pos, batchIdx, 0, n);

                    List<Bitmap> frames = RunSampleBatch(run, batchTimes);
                    if (frames != null && frames.Count == n)
                    {
                        Deliver(run, frames, new List<int>(batchIdx), false);
                    }
                    else
                    {
                        // The batch did not come back one-frame-per-time (a time past
                        // the end of the video makes its input yield nothing, which
                        // would silently shift every following index). Fall back to one
                        // process per time: a failure then only leaves a hole at its own
                        // index instead of corrupting the whole batch.
                        if (frames != null) DisposeBatch(frames);
                        for (int k = 0; k < n; k++)
                        {
                            if (run.Canceled || _disposed) break;
                            List<Bitmap> one = RunSampleBatch(run, new double[] { batchTimes[k] });
                            if (one == null) continue;
                            if (one.Count == 1)
                                Deliver(run, one, new List<int>(new int[] { batchIdx[k] }), false);
                            else
                                DisposeBatch(one);
                        }
                    }
                    pos += n;
                }
            }
            catch
            {
            }
            finally
            {
                Deliver(run, null, null, true);
            }
        }

        // One ffmpeg process -> one JPEG per requested time, in the requested order.
        // A single time uses a plain -frames:v 1 seek; several times are packed as one
        // seeked input each and concatenated, which is where the speed comes from:
        // process start-up is ~350 ms of the ~630 ms a lone seek costs.
        private List<Bitmap> RunSampleBatch(ThumbRun run, double[] times)
        {
            Process p = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                psi.Arguments = BuildSampleArgs(run, times);
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                p = new Process();
                p.StartInfo = psi;
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                lock (_sync)
                {
                    if (run.Canceled || _disposed || !_runs.ContainsKey(run.Id)) return null;
                    run.Proc = p;
                }
                p.Start();
                p.BeginErrorReadLine();

                List<byte[]> blobs = ReadJpegs(p.StandardOutput.BaseStream, times.Length, run);
                List<Bitmap> frames = new List<Bitmap>();
                for (int i = 0; i < blobs.Count; i++)
                {
                    Bitmap bmp = TryDecode(blobs[i]);
                    if (bmp == null)
                    {
                        DisposeBatch(frames);
                        return null;
                    }
                    frames.Add(bmp);
                }
                return frames;
            }
            catch
            {
                return null;
            }
            finally
            {
                Process proc = p;
                if (ReferenceEquals(run.Proc, proc)) run.Proc = null;
                if (proc != null)
                {
                    try { if (!proc.HasExited) YtDlpRunner.KillTreeNoWait(proc); }
                    catch { }
                    try { proc.Dispose(); }
                    catch { }
                }
            }
        }

        private string BuildSampleArgs(ThumbRun run, double[] times)
        {
            System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
            string height = run.Height.ToString(ci);
            string file = YtDlpRunner.Quote(_file);
            StringBuilder sb = new StringBuilder();
            sb.Append("-nostdin ");
            if (times.Length == 1)
            {
                sb.Append("-ss ").Append(Fmt(times[0])).Append(" -i ").Append(file);
                sb.Append(" -frames:v 1 -an -sn -dn -vf scale=").Append(height).Append(":-2 -q:v 3");
                sb.Append(" -f image2pipe -c:v mjpeg -nostats -");
                return sb.ToString();
            }
            for (int k = 0; k < times.Length; k++)
                sb.Append("-ss ").Append(Fmt(times[k])).Append(" -i ").Append(file).Append(' ');
            // One representative frame per input, concatenated in request order, so
            // the pipe yields exactly one JPEG per requested time in that order.
            // -fps_mode passthrough is REQUIRED: with the default fps mode concat
            // silently drops frames and the time -> JPEG mapping breaks.
            StringBuilder fc = new StringBuilder();
            for (int k = 0; k < times.Length; k++)
            {
                fc.Append('[').Append(k.ToString(ci)).Append(":v]trim=end_frame=1,setpts=PTS-STARTPTS,scale=")
                  .Append(height).Append(":-2[v").Append(k.ToString(ci)).Append("];");
            }
            for (int k = 0; k < times.Length; k++) fc.Append("[v").Append(k.ToString(ci)).Append(']');
            fc.Append("concat=n=").Append(times.Length.ToString(ci)).Append(":v=1:a=0[out]");
            sb.Append("-filter_complex ").Append(fc.ToString()).Append(' ');
            sb.Append("-map [out] -fps_mode passthrough -q:v 3 -an -sn -dn -f image2pipe -c:v mjpeg -nostats -");
            return sb.ToString();
        }

        // Reads up to maxFrames JPEG blobs (FF D8 .. FF D9) from an MJPEG pipe. Same
        // marker scan the streaming ReaderProc uses; kept separate so that path stays
        // untouched. Stops as soon as the expected number of frames has arrived.
        private static List<byte[]> ReadJpegs(Stream pipe, int maxFrames, ThumbRun run)
        {
            List<byte[]> blobs = new List<byte[]>();
            MemoryStream jpeg = new MemoryStream();
            byte[] buf = new byte[64 * 1024];
            bool inJpeg = false;
            int prev = -1;
            try
            {
                while (blobs.Count < maxFrames)
                {
                    if (run.Canceled) break;
                    int n;
                    try { n = pipe.Read(buf, 0, buf.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    for (int i = 0; i < n; i++)
                    {
                        int b = buf[i];
                        if (!inJpeg)
                        {
                            if (prev == 0xFF && b == 0xD8)
                            {
                                jpeg.WriteByte(0xFF);
                                jpeg.WriteByte(0xD8);
                                inJpeg = true;
                                prev = -1;
                                continue;
                            }
                        }
                        else
                        {
                            jpeg.WriteByte((byte)b);
                            if (prev == 0xFF && b == 0xD9)
                            {
                                blobs.Add(jpeg.ToArray());
                                jpeg.SetLength(0);
                                inJpeg = false;
                                prev = -1;
                                continue;
                            }
                        }
                        prev = b;
                    }
                }
            }
            finally
            {
                jpeg.Dispose();
            }
            return blobs;
        }

        private static Bitmap TryDecode(byte[] jpegBytes)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream(jpegBytes, false))
                {
                    return new Bitmap(ms);
                }
            }
            catch
            {
                return null;
            }
        }

        private void Deliver(ThumbRun run, List<Bitmap> batch, List<int> batchIndex, bool done)
        {
            if (_invoker != null && _invoker.InvokeRequired)
            {
                try { _invoker.BeginInvoke(new Action(delegate { ApplyDeliver(run, batch, batchIndex, done); }), null); }
                catch { ApplyDeliver(run, batch, batchIndex, done); }
                return;
            }
            ApplyDeliver(run, batch, batchIndex, done);
        }

        private void ApplyDeliver(ThumbRun run, List<Bitmap> batch, List<int> batchIndex, bool done)
        {
            if (_disposed)
            {
                if (batch != null) DisposeBatch(batch);
                return;
            }
            lock (_sync)
            {
                if (!_runs.ContainsKey(run.Id))
                {
                    if (batch != null) DisposeBatch(batch);
                    return;
                }
            }
            if (batch != null)
            {
                lock (run.Thumbs)
                {
                    for (int i = 0; i < batch.Count; i++)
                    {
                        Bitmap old;
                        if (run.Thumbs.TryGetValue(batchIndex[i], out old))
                        {
                            try { old.Dispose(); }
                            catch { }
                            _totalThumbs--;
                        }
                        run.Thumbs[batchIndex[i]] = batch[i];
                        _totalThumbs++;
                    }
                }
                EvictIfNeeded();
            }
            if (done)
            {
                if (run.Canceled)
                {
                    lock (_sync)
                    {
                        if (run.Id != 0) _runs.Remove(run.Id);
                    }
                    DisposeRunThumbs(run);
                }
                else
                {
                    run.Done = true;
                }
            }
            FireChanged();
        }

        private static void DisposeBatch(List<Bitmap> batch)
        {
            foreach (Bitmap b in batch)
            {
                try { b.Dispose(); }
                catch { }
            }
        }

        private void EvictIfNeeded()
        {
            if (_totalThumbs <= LruCap) return;
            List<KeyValuePair<ThumbRun, int>> candidates = new List<KeyValuePair<ThumbRun, int>>();
            lock (_sync)
            {
                foreach (ThumbRun r in _runs.Values)
                {
                    if (r.Id == 0) continue;
                    if (Environment.TickCount - r.LastTouch < 4000) continue;
                    lock (r.Thumbs)
                    {
                        foreach (KeyValuePair<int, Bitmap> kv in r.Thumbs)
                            candidates.Add(new KeyValuePair<ThumbRun, int>(r, kv.Key));
                    }
                }
            }
            while (_totalThumbs > LruCap && candidates.Count > 0)
            {
                int oldestIdx = -1;
                long oldestRun = -1;
                int oldestKey = -1;
                long oldestTouch = long.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    ThumbRun r = candidates[i].Key;
                    if (!_runs.ContainsKey(r.Id)) continue;
                    if (r.LastTouch < oldestTouch)
                    {
                        oldestTouch = r.LastTouch;
                        oldestRun = r.Id;
                        oldestIdx = i;
                        oldestKey = candidates[i].Value;
                    }
                }
                if (oldestIdx < 0) break;
                ThumbRun run;
                if (_runs.TryGetValue(oldestRun, out run))
                {
                    Bitmap bmp;
                    lock (run.Thumbs)
                    {
                        if (run.Thumbs.TryGetValue(oldestKey, out bmp))
                        {
                            run.Thumbs.Remove(oldestKey);
                            _totalThumbs--;
                        }
                    }
                    if (bmp != null)
                    {
                        try { bmp.Dispose(); }
                        catch { }
                    }
                }
                candidates.RemoveAt(oldestIdx);
            }
        }

        private void FireChanged()
        {
            Action h = Changed;
            if (h != null) h();
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string FpsFmt(double v)
        {
            return v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            List<ThumbRun> runs;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                runs = new List<ThumbRun>(_runs.Values);
            }
            foreach (ThumbRun r in runs)
            {
                r.Canceled = true;
                KillRun(r);
            }
            foreach (ThumbRun r in runs) DisposeRunThumbs(r);
            lock (_sync) { _runs.Clear(); }
            _totalThumbs = 0;
        }
    }
}
