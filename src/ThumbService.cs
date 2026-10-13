using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace YouTubeDownloader
{
    // Stage 1 thumbnail producer for the filmstrip timeline.
    //
    // There are exactly TWO primitives, and the choice between them is made by how
    // far apart in time the strip's cells are:
    //
    //   * SPAN SCAN - one ffmpeg process walks a contiguous stretch of the media in
    //     decode order and emits a JPEG every `dt` seconds of media as it goes
    //     (sequential decode, no per-thumbnail seek). It is the cheap primitive
    //     WHILE the cells are closer together than the grid it produces: at deep
    //     zoom 37 cells cover a few seconds, so almost every decoded frame is one
    //     the strip paints. Used for the viewport demand at fine zoom.
    //
    //   * SAMPLED PASS - N seeked inputs packed into ONE ffmpeg process, yielding
    //     exactly one frame per requested time (see BuildSampleArgs). It is the
    //     cheap primitive when the cells are FAR apart: at the whole-clip view 37
    //     cells are ~14 s apart, and a span scan would have to decode all 508 s of
    //     the media to produce the same 37 frames. Used for the whole-clip overview
    //     and for the viewport demand at coarse zoom.
    //
    // Both are produced off the UI thread; the only UI-thread work is storing the
    // finished Bitmaps and raising Changed (coalesced by the timeline control).
    public sealed class ThumbService : IDisposable
    {
        // Frames per UI marshaling once the strip is already showing something.
        private const int DeliveryBatch = 8;
        // Hard cap on how long a partial batch may sit undelivered.
        private const int DeliveryMaxDelayMs = 120;
        // How long the base scan sleeps while a viewport scan is running.
        private const int BaseYieldSleepMs = 25;
        // Frames of slack before a forward viewport request is treated as a jump
        // that deserves its own scan instead of waiting for the base pass.
        private const int BaseSkipMargin = 20;

        // Seek times packed into ONE sampled process. 8 is deliberately well below
        // the point where ffmpeg's concat gives up: measured on Test.mp4 (AV1 720p60),
        // batches of 8, 9, 10 and 12 inputs all return exactly one frame per time,
        // while 13 or more fail with "Invalid pts (0) <= last (0)" and "Conversion
        // failed!" after producing only 12 frames. The per-time fallback in
        // SampleChunkRun covers a batch that ever comes back short.
        private const int SampleBatch = 8;

        // Overview workers. Measured on Test.mp4 (508 s, 37 samples, batch 8):
        // 1 worker 7.9 s, 2 workers 5.8 s, 3 workers 5.5 s, 4 workers 5.7 s. The
        // cost is dominated by the seek and the machine saturates past two
        // processes, so two is where more workers stop paying for themselves - and
        // it leaves CPU for the preview scrubber and the UI.
        private const int OverviewWorkers = 2;

        private sealed class DemandJob
        {
            public int FirstIndex;
            public int Count;
            // Non-null for a SAMPLED job: the exact grid times to sample and the
            // store index each one belongs to. Null for a span-scan job.
            public double[] SampleTimes;
            public int[] SampleIndexes;
            public long Generation;
            public bool Prefetch;
            public volatile bool Canceled;
            public volatile Process Proc;

            public int LastIndex
            {
                get { return FirstIndex + Count - 1; }
            }

            public bool Overlaps(int from, int to)
            {
                return FirstIndex <= to && from <= LastIndex;
            }
        }

        // Frames produced for one batch of sample times, together with the store
        // index each frame belongs to. A chunk is never partial: a batch that comes
        // back short is redone one time per process.
        private sealed class SampleChunk
        {
            public readonly List<Bitmap> Frames = new List<Bitmap>();
            public readonly List<int> Indexes = new List<int>();
        }

        private readonly string _file;
        private readonly System.ComponentModel.ISynchronizeInvoke _invoker;
        private readonly double _duration;
        private readonly int _height;
        private readonly ThumbStore _store;

        private readonly object _sync = new object();
        private Thread _baseThread;
        private Thread _demandThread;
        private DemandJob _running;
        private DemandJob _pendingVisible;
        private DemandJob _pendingPrefetch;
        private long _generation;

        // Every sampled ffmpeg process currently alive, so Dispose can end them
        // without waiting for a batch to finish on its own.
        private readonly List<Process> _sampleProcs = new List<Process>();

        // The overview owns the machine while it runs. Demand requests are not
        // dropped, they are parked until it commits, so the same region is never
        // decoded twice and the initial view is never filled in twice.
        private volatile bool _overviewStarted;
        private volatile bool _overviewPending;
        private volatile bool _overviewDone;

        private volatile bool _disposed;
        // True while a viewport scan owns the machine: the base scan stops reading
        // its pipe (ffmpeg then blocks on the write and its decode pauses), which
        // implements the VISIBLE > PREFETCH > BASE priority without extra processes.
        private volatile bool _demandActive;
        private volatile Process _baseProc;
        private volatile bool _baseStarted;
        private volatile bool _baseDone;
        // Highest index the base scan has already produced (or is about to). Used
        // to avoid starting a second decode of a region the base pass is already
        // walking through.
        private volatile int _baseFrontier;
        private int _framesDelivered;

        // Raised on the UI thread after a delivery has been stored.
        public event Action Changed;

        // Raised on the UI thread while the overview runs (0..100) and exactly once
        // when it is over, whether it succeeded or not. The host uses them to drive
        // the existing progress/status line of the editor.
        public event Action<int> OverviewProgress;
        public event Action OverviewDone;

        public ThumbService(string file, System.ComponentModel.ISynchronizeInvoke invoker, double duration, int height)
        {
            _file = file;
            _invoker = invoker;
            _duration = duration > 0 ? duration : 0;
            _height = height < 16 ? 16 : height;
            // A frame is decoded at 78x44 (~13 KB). 900 of them is ~12 MB, which is
            // comfortably more than a screenful of filmstrip plus prefetch plus
            // recent history - the same budget the store had when the cells were
            // 128x72 and it held 900 of them.
            _store = new ThumbStore(900);
            _demandThread = new Thread(new ThreadStart(DemandProc));
            _demandThread.IsBackground = true;
            _demandThread.Name = "thumb-demand";
            _demandThread.Start();
        }

        public ThumbStore Store
        {
            get { return _store; }
        }

        public int BaseFrameCount
        {
            get { return _duration <= 0 ? 0 : (int)Math.Ceiling(_duration / ThumbStore.BaseDt); }
        }

        public int FramesDelivered
        {
            get { return _framesDelivered; }
        }

        public bool BaseScanDone
        {
            get { return _baseDone; }
        }

        public bool BaseScanRunning
        {
            get { return _baseStarted && !_baseDone && !_disposed; }
        }

        // Lowest-priority pass over the whole file. Started once, right after the
        // media is loaded. It is never cancelled by viewport requests: it just
        // yields while they run.
        //
        // It is deliberately NOT started by the editor any more: the opening view is
        // the whole clip on one screen, which the sampled overview below serves with
        // ~37 frames instead of a full-file decode. Detail is loaded on demand when
        // the user zooms in (see RequestViewport / RequestCellSamples).
        public void StartBaseScan()
        {
            if (_disposed) return;
            lock (_sync)
            {
                if (_baseStarted) return;
                _baseStarted = true;
            }
            _baseThread = new Thread(new ThreadStart(BaseScanProc));
            _baseThread.IsBackground = true;
            _baseThread.Name = "thumb-base";
            _baseThread.Start();
        }

        public bool IsOverviewDone
        {
            get { return _overviewDone; }
        }

        public bool OverviewRunning
        {
            get { return _overviewStarted && !_overviewDone && !_disposed; }
        }

        // The whole-clip overview: the media as a complete visual map, produced once
        // right after opening and delivered INCREMENTALLY, batch by batch, so the
        // strip starts filling the moment the first frames land instead of staying
        // empty until the whole pass is over. `times[k]` is the exact grid time of
        // store index `indexes[k]`.
        //
        // Returns false when there is nothing to do (no plan, or a pass was already
        // started); in that case OverviewDone is NOT raised and the caller is free to
        // go ready immediately.
        public bool StartOverview(double[] times, int[] indexes)
        {
            if (_disposed) return false;
            if (times == null || indexes == null) return false;
            if (times.Length == 0 || times.Length != indexes.Length) return false;
            lock (_sync)
            {
                if (_overviewStarted) return false;
                _overviewStarted = true;
                _overviewPending = true;
            }
            double[] t = (double[])times.Clone();
            int[] idx = (int[])indexes.Clone();
            Thread th = new Thread(new ThreadStart(delegate { OverviewProc(t, idx); }));
            th.IsBackground = true;
            th.Name = "thumb-overview";
            th.Start();
            return true;
        }

        // Overview body. The plan is cut into SampleBatch-sized chunks; each chunk is
        // one packed ffmpeg process, and OverviewWorkers chunks run at a time.
        //
        // Every chunk is DELIVERED THE MOMENT IT IS READY. The strip therefore fills in
        // progressively - the first frames of the opening view are on screen within a
        // fraction of a second - instead of the user staring at an empty track until
        // the slowest batch of the whole pass has finished. Order does not matter for
        // correctness: a cell paints its own frame once it exists and the nearest
        // resident frame until then, so an out-of-order arrival can never open a hole.
        private void OverviewProc(double[] times, int[] indexes)
        {
            try
            {
                int batches = (times.Length + SampleBatch - 1) / SampleBatch;
                object gate = new object();
                int nextBatch = 0;
                int batchesDone = 0;
                int workers = Math.Min(OverviewWorkers, batches);
                Thread[] pool = new Thread[workers];
                for (int w = 0; w < workers; w++)
                {
                    pool[w] = new Thread(new ThreadStart(delegate
                    {
                        while (true)
                        {
                            if (_disposed) return;
                            int b;
                            lock (gate)
                            {
                                if (nextBatch >= batches) return;
                                b = nextBatch++;
                            }
                            int first = b * SampleBatch;
                            int n = Math.Min(SampleBatch, times.Length - first);
                            double[] bt = new double[n];
                            int[] bi = new int[n];
                            Array.Copy(times, first, bt, 0, n);
                            Array.Copy(indexes, first, bi, 0, n);
                            SampleChunk chunk = SampleChunkRun(bt, bi, null);
                            if (_disposed)
                            {
                                if (chunk != null) DisposeFrames(chunk.Frames);
                                return;
                            }
                            if (chunk != null && chunk.Frames.Count > 0)
                                Deliver(chunk.Frames, chunk.Indexes);
                            // Demand may run again as soon as the first frames exist:
                            // parking it until the whole pass is over would make an
                            // immediate zoom wait for frames it does not need.
                            _overviewPending = false;
                            int pct;
                            lock (gate)
                            {
                                batchesDone++;
                                pct = batchesDone * 100 / batches;
                            }
                            ReportProgress(pct);
                        }
                    }));
                    pool[w].IsBackground = true;
                    pool[w].Name = "thumb-overview-" + w;
                    pool[w].Start();
                }
                for (int w = 0; w < workers; w++)
                {
                    try { pool[w].Join(); }
                    catch { }
                }
            }
            catch
            {
            }
            finally
            {
                _overviewPending = false;
                _overviewDone = true;
                ReportOverviewDone();
            }
        }

        // Viewport request in the SAMPLED form: `times[k]` is the exact grid time of
        // store index `indexes[k]`, and together they are precisely what the visible
        // cells of the strip paint. Used when the cells are farther apart in time than
        // the 2 fps grid, where seeking the cells directly costs O(cells) while a span
        // scan would cost O(seconds of media in the window).
        public void RequestCellSamples(double[] times, int[] indexes)
        {
            if (_disposed) return;
            if (times == null || indexes == null) return;
            if (times.Length == 0 || times.Length != indexes.Length) return;
            if (BaseFrameCount <= 0) return;
            if (_store.HasAll(indexes, indexes.Length)) return;

            DemandJob job = new DemandJob();
            job.SampleTimes = (double[])times.Clone();
            job.SampleIndexes = (int[])indexes.Clone();
            job.FirstIndex = indexes[0];
            job.Count = indexes.Length;
            lock (_sync)
            {
                _generation++;
                job.Generation = _generation;
                _pendingVisible = job;
                // A sampled request is a complete answer for the viewport on its own;
                // it supersedes any span-scan padding queued a moment earlier.
                _pendingPrefetch = null;
                Monitor.Pulse(_sync);
            }
        }

        // Viewport request: `visibleFrom..visibleTo` is what the user is looking at
        // right now, `prefetchFrom..prefetchTo` is the padded range around it (the
        // movement direction is already encoded in those bounds by the caller).
        public void RequestViewport(int visibleFrom, int visibleTo, int prefetchFrom, int prefetchTo)
        {
            if (_disposed) return;
            int total = BaseFrameCount;
            if (total <= 0) return;
            visibleFrom = ClampIndex(visibleFrom, total);
            visibleTo = ClampIndex(visibleTo, total);
            prefetchFrom = ClampIndex(prefetchFrom, total);
            prefetchTo = ClampIndex(prefetchTo, total);
            if (visibleFrom > visibleTo) return;

            DemandJob running;
            lock (_sync)
            {
                _generation++;
                running = _running;
                _pendingVisible = MakeJob(visibleFrom, visibleTo, false, _generation);
                _pendingPrefetch = MakeJob(prefetchFrom, prefetchTo, true, _generation);
                Monitor.Pulse(_sync);
            }
            // A stale job is only interrupted when the new view does not overlap it
            // at all (a jump). For an overlapping view the running scan is allowed
            // to finish: cancelling it on every scroll step would starve the strip.
            if (running != null && !running.Overlaps(visibleFrom, visibleTo))
                running.Canceled = true;
        }

        private DemandJob MakeJob(int from, int to, bool prefetch, long generation)
        {
            if (from > to) return null;
            // Avoid decoding twice what the base pass is already walking through.
            // A viewport scan is skipped only when the base scan is going to cover
            // the range very soon anyway, i.e. the request sits at/behind its
            // frontier (or just barely ahead of it). Two cases still start a scan:
            //   * a real jump forward, far beyond the frontier - waiting for the
            //     base pass would mean tens of seconds of placeholders;
            //   * a range entirely behind the frontier whose frames are no longer
            //     resident (evicted) - the base pass will never come back to it.
            if (_baseStarted && !_baseDone)
            {
                bool aheadOfScan = from > _baseFrontier + BaseSkipMargin;
                bool willBeProduced = to > _baseFrontier;
                if (!aheadOfScan && willBeProduced) return null;
            }
            if (_store.HasRange(from, to)) return null;
            DemandJob job = new DemandJob();
            job.FirstIndex = from;
            job.Count = to - from + 1;
            job.Prefetch = prefetch;
            job.Generation = generation;
            return job;
        }

        private static int ClampIndex(int index, int total)
        {
            if (index < 0) return 0;
            if (index > total - 1) return total - 1;
            return index;
        }

        private void BaseScanProc()
        {
            try
            {
                RunSpan(0, BaseFrameCount, null);
            }
            catch
            {
            }
            finally
            {
                _baseDone = true;
            }
        }

        private void DemandProc()
        {
            while (true)
            {
                if (_disposed) return;
                DemandJob job = null;
                lock (_sync)
                {
                    if (_pendingVisible != null)
                    {
                        job = _pendingVisible;
                        _pendingVisible = null;
                    }
                    else if (_pendingPrefetch != null)
                    {
                        job = _pendingPrefetch;
                        _pendingPrefetch = null;
                    }
                    else
                    {
                        Monitor.Wait(_sync, 200);
                        continue;
                    }
                    _running = job;
                }
                _demandActive = true;
                try
                {
                    // The overview owns the machine while it runs. Demand requests are
                    // not dropped, they wait: the opening view is being produced right
                    // now, and running a second decode for the same region would only
                    // slow both down. The wait is bounded by the overview itself,
                    // which commits within seconds.
                    while (_overviewPending && !_disposed)
                        Thread.Sleep(BaseYieldSleepMs);

                    if (!job.Canceled && !_disposed)
                    {
                        if (job.SampleTimes != null)
                        {
                            // Sampled demand: run it unless the overview (or an
                            // earlier request) has already stored every cell.
                            if (!_store.HasAll(job.SampleIndexes, job.SampleIndexes.Length))
                            {
                                SampleChunk chunk = SampleChunkRun(job.SampleTimes, job.SampleIndexes, job);
                                if (chunk.Frames.Count > 0) Deliver(chunk.Frames, chunk.Indexes);
                            }
                        }
                        else if (!_store.HasRange(job.FirstIndex, job.LastIndex))
                        {
                            RunSpan(job.FirstIndex, job.Count, job);
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    _demandActive = false;
                    lock (_sync)
                    {
                        if (ReferenceEquals(_running, job)) _running = null;
                    }
                }
            }
        }

        // One sequential ffmpeg pass over [firstIndex*dt, (firstIndex+count)*dt).
        // The k-th JPEG read from the pipe is stored at index firstIndex + k, which
        // is exact because the caller keeps firstIndex on the global time grid.
        private void RunSpan(int firstIndex, int count, DemandJob job)
        {
            if (count <= 0) return;
            double dt = ThumbStore.BaseDt;
            double t0 = firstIndex * dt;
            double span = count * dt;

            Process proc = null;
            MemoryStream jpeg = new MemoryStream();
            List<Bitmap> batch = new List<Bitmap>();
            List<int> batchIndex = new List<int>();
            Stopwatch clock = Stopwatch.StartNew();
            long lastFlush = 0;
            int delivered = 0;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                psi.Arguments = BuildSpanArgs(t0, span, dt);
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                proc = new Process();
                proc.StartInfo = psi;
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                if (job != null) job.Proc = proc;
                else _baseProc = proc;
                if (_disposed) return;
                proc.Start();
                proc.BeginErrorReadLine();

                Stream outp = proc.StandardOutput.BaseStream;
                byte[] buf = new byte[64 * 1024];
                bool inJpeg = false;
                int prev = -1;

                while (delivered < count)
                {
                    if (_disposed) break;
                    if (job != null && job.Canceled) break;
                    if (job == null && _demandActive)
                    {
                        Thread.Sleep(BaseYieldSleepMs);
                        continue;
                    }

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
                                    batchIndex.Add(firstIndex + delivered);
                                    delivered++;
                                    Interlocked.Increment(ref _framesDelivered);
                                    if (job == null) _baseFrontier = firstIndex + delivered;
                                }
                                // The very first frames go out one by one so the
                                // strip shows something immediately; after that they
                                // are batched to keep UI marshaling cheap.
                                int flushSize = delivered <= DeliveryBatch ? 1 : DeliveryBatch;
                                if (batch.Count >= flushSize
                                    || (batch.Count > 0 && clock.ElapsedMilliseconds - lastFlush >= DeliveryMaxDelayMs))
                                {
                                    Deliver(batch, batchIndex);
                                    batch = new List<Bitmap>();
                                    batchIndex = new List<int>();
                                    lastFlush = clock.ElapsedMilliseconds;
                                }
                                if (delivered >= count) break;
                                continue;
                            }
                        }
                        prev = b;
                    }
                }
                if (batch.Count > 0) Deliver(batch, batchIndex);
                batch = new List<Bitmap>();
                batchIndex = new List<int>();
            }
            catch
            {
            }
            finally
            {
                jpeg.Dispose();
                DisposeFrames(batch);
                Kill(proc);
                if (job != null) job.Proc = null;
                else _baseProc = null;
            }
        }

        // The one and only ffmpeg invocation shape used for thumbnails: a
        // sequential decode of a contiguous stretch, scaled down, streamed as MJPEG
        // on stdout. `t0` is always aligned to the global grid, so frame order maps
        // 1:1 onto store indexes.
        private string BuildSpanArgs(double t0, double span, double dt)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            StringBuilder sb = new StringBuilder();
            sb.Append("-nostdin ");
            if (t0 > 1e-6) sb.Append("-ss ").Append(t0.ToString("0.000000", ci)).Append(' ');
            sb.Append("-i ").Append(YtDlpRunner.Quote(_file));
            sb.Append(" -t ").Append(span.ToString("0.000000", ci));
            sb.Append(" -vf fps=").Append((1.0 / dt).ToString("0.######", ci));
            sb.Append(",scale=").Append(_height.ToString(ci)).Append(":-2");
            sb.Append(" -q:v 4 -an -sn -dn -f image2pipe -c:v mjpeg -nostats -");
            return sb.ToString();
        }

        // ------------------------------------------------------- sampled pass
        //
        // Same primitive as the cache's sampled runs, moved here so the filmstrip
        // has one producer: N seeked inputs in ONE process, one representative frame
        // per requested time, concatenated in request order.

        // A chunk of the plan, run as packed batches. A batch that does not come back
        // one-frame-per-time is redone one time per process, so a single failure can
        // only leave a hole at its own index instead of shifting every following one
        // (a time past the end of the media makes its input yield nothing).
        private SampleChunk SampleChunkRun(double[] times, int[] indexes, DemandJob job)
        {
            SampleChunk chunk = new SampleChunk();
            if (times.Length == 0) return chunk;
            if (job != null && job.Canceled) return chunk;

            List<Bitmap> frames = RunSampleBatch(times, job);
            if (frames != null && frames.Count == times.Length)
            {
                chunk.Frames.AddRange(frames);
                chunk.Indexes.AddRange(indexes);
                return chunk;
            }
            if (frames != null) DisposeFrames(frames);

            for (int k = 0; k < times.Length; k++)
            {
                if (_disposed) break;
                if (job != null && job.Canceled) break;
                List<Bitmap> one = RunSampleBatch(new double[] { times[k] }, job);
                if (one == null) continue;
                if (one.Count == 1)
                {
                    chunk.Frames.Add(one[0]);
                    chunk.Indexes.Add(indexes[k]);
                }
                else
                {
                    DisposeFrames(one);
                }
            }
            return chunk;
        }

        // One ffmpeg process -> one JPEG per requested time, in the requested order,
        // or null when the process did not deliver exactly that. A single time is a
        // plain seek; several times are packed as one seeked input each and
        // concatenated, which is where the speed comes from: process start-up is
        // ~350 ms of the ~630 ms a lone seek costs.
        private List<Bitmap> RunSampleBatch(double[] times, DemandJob job)
        {
            Process proc = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                psi.Arguments = BuildSampleArgs(times);
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                proc = new Process();
                proc.StartInfo = psi;
                proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                if (_disposed) return null;
                if (job != null) job.Proc = proc;
                if (!RegisterSampleProc(proc)) return null;
                proc.Start();
                proc.BeginErrorReadLine();

                List<byte[]> blobs = ReadJpegs(proc.StandardOutput.BaseStream, times.Length, job);
                if (blobs.Count != times.Length) return null;
                List<Bitmap> frames = new List<Bitmap>(blobs.Count);
                for (int i = 0; i < blobs.Count; i++)
                {
                    Bitmap bmp = TryDecode(blobs[i]);
                    if (bmp == null)
                    {
                        DisposeFrames(frames);
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
                UnregisterSampleProc(proc);
                if (job != null && ReferenceEquals(job.Proc, proc)) job.Proc = null;
                Kill(proc);
            }
        }

        // The packed form: `times.Length` seeked inputs, one trimmed frame each,
        // concatenated in request order. -fps_mode passthrough is REQUIRED: with the
        // default fps mode concat silently drops frames and the time -> JPEG mapping
        // breaks. Batches are kept at SampleBatch because 13+ inputs make ffmpeg fail
        // with duplicate timestamps out of concat.
        private string BuildSampleArgs(double[] times)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            string file = YtDlpRunner.Quote(_file);
            string height = _height.ToString(ci);
            StringBuilder sb = new StringBuilder();
            sb.Append("-nostdin ");
            if (times.Length == 1)
            {
                sb.Append("-ss ").Append(times[0].ToString("0.000000", ci)).Append(" -i ").Append(file);
                sb.Append(" -frames:v 1 -an -sn -dn -vf scale=").Append(height).Append(":-2 -q:v 3");
                sb.Append(" -f image2pipe -c:v mjpeg -nostats -");
                return sb.ToString();
            }
            for (int k = 0; k < times.Length; k++)
                sb.Append("-ss ").Append(times[k].ToString("0.000000", ci)).Append(" -i ").Append(file).Append(' ');
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
        // marker scan RunSpan uses; stops as soon as the expected number has arrived.
        private List<byte[]> ReadJpegs(Stream pipe, int maxFrames, DemandJob job)
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
                    if (_disposed) break;
                    if (job != null && job.Canceled) break;
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

        // Every sampled process is tracked so Dispose can end it instead of leaving
        // it blocked on a pipe nobody reads.
        private bool RegisterSampleProc(Process proc)
        {
            lock (_sync)
            {
                if (_disposed) return false;
                _sampleProcs.Add(proc);
                return true;
            }
        }

        private void UnregisterSampleProc(Process proc)
        {
            if (proc == null) return;
            lock (_sync) { _sampleProcs.Remove(proc); }
        }

        private void ReportProgress(int percent)
        {
            Action<int> h = OverviewProgress;
            if (h == null || _disposed) return;
            if (_invoker != null && _invoker.InvokeRequired)
            {
                try { _invoker.BeginInvoke(new Action(delegate { h(percent); }), null); }
                catch { }
                return;
            }
            h(percent);
        }

        private void ReportOverviewDone()
        {
            Action h = OverviewDone;
            if (h == null) return;
            if (_invoker != null && _invoker.InvokeRequired)
            {
                try { _invoker.BeginInvoke(new Action(delegate { h(); }), null); }
                catch { }
                return;
            }
            h();
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

        private static void DisposeFrames(List<Bitmap> frames)
        {
            if (frames == null) return;
            for (int i = 0; i < frames.Count; i++)
            {
                try { if (frames[i] != null) frames[i].Dispose(); }
                catch { }
            }
            frames.Clear();
        }

        // Hand finished frames to the UI thread. The store is mutated only there,
        // which is what makes eviction safe against the paint path.
        private void Deliver(List<Bitmap> frames, List<int> indices)
        {
            if (frames == null || frames.Count == 0) return;
            if (_disposed)
            {
                DisposeFrames(frames);
                return;
            }
            if (_invoker != null && _invoker.InvokeRequired)
            {
                try
                {
                    _invoker.BeginInvoke(new Action(delegate { Apply(frames, indices); }), null);
                    return;
                }
                catch
                {
                    // The host window is gone: drop the frames instead of touching
                    // the store from a worker thread.
                    DisposeFrames(frames);
                    return;
                }
            }
            Apply(frames, indices);
        }

        private void Apply(List<Bitmap> frames, List<int> indices)
        {
            if (_disposed)
            {
                DisposeFrames(frames);
                return;
            }
            for (int i = 0; i < frames.Count; i++)
            {
                _store.Put(indices[i], frames[i]);
            }
            frames.Clear();
            indices.Clear();
            Action h = Changed;
            if (h != null) h();
        }

        private static void Kill(Process proc)
        {
            if (proc == null) return;
            try { if (!proc.HasExited) YtDlpRunner.KillTreeNoWait(proc); }
            catch { }
            try { proc.Dispose(); }
            catch { }
        }

        public void Dispose()
        {
            Process baseProc;
            DemandJob running;
            List<Process> samples;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                running = _running;
                _pendingVisible = null;
                _pendingPrefetch = null;
                samples = new List<Process>(_sampleProcs);
                Monitor.PulseAll(_sync);
            }
            if (running != null) running.Canceled = true;
            baseProc = _baseProc;
            Kill(baseProc);
            if (running != null) Kill(running.Proc);
            // Sampled batches are killed rather than waited for: a worker blocked on a
            // pipe nobody reads would otherwise leave its ffmpeg alive for good.
            for (int i = 0; i < samples.Count; i++) Kill(samples[i]);
            // Threads are background: they must never hold the process open.
            _store.Dispose();
        }
    }
}
