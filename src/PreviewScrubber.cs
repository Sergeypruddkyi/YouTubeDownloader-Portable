using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;

namespace YouTubeDownloader
{
    // What kind of frame the preview is being handed.
    public enum PreviewFrameKind
    {
        // A filmstrip thumbnail used as an instant stand-in. Owned by the CONSUMER:
        // it comes from the consumer's own ThumbnailProvider and the consumer must
        // dispose it when it is replaced.
        Thumbnail,
        // A real decoded frame taken from the scrubber cache. Owned by the SCRUBBER:
        // the consumer must never dispose it.
        Cached,
        // A real decoded frame produced for the current request. Owned by the SCRUBBER.
        Exact
    }

    // Fast preview scrubbing for the editor.
    //
    // The problem it solves: the old path ran a brand new ffmpeg process for every
    // preview seek (kill + spawn + seek + decode), and an in-flight decode made the
    // editor drop every newer playhead position, so the preview could end up on an
    // obsolete frame and the final MouseUp position was often never requested.
    //
    // Model:
    //   * ONE worker thread owns ONE FrameDecoder - no process per mouse move;
    //   * every Request() bumps a generation; the worker always serves the NEWEST
    //     generation and results of superseded requests are never published as the
    //     current frame (latest request wins, no queue of stale jobs);
    //   * a small LRU cache of decoded frames around the playhead serves repeat
    //     scrubbing without touching ffmpeg;
    //   * an instant fallback (cache, then the filmstrip thumbnails supplied by the
    //     consumer) is published synchronously on the caller's thread inside
    //     Request(), so the preview follows the mouse at mouse rate while the exact
    //     frame is still being decoded;
    //   * moving forward reuses the live decoder through StepForward() instead of
    //     restarting it, as long as that is estimated to be cheaper than a seek.
    //
    // Threading contract:
    //   * Request()/RequestFinal() are called on the UI thread;
    //   * FrameReady is raised on the invoker's thread (the UI thread);
    //   * bitmaps handed out as Cached/Exact are owned by this object and stay valid
    //     until the next publish plus one acknowledgement, so the consumer must not
    //     dispose them and must not keep them past the next FrameReady.
    public sealed class PreviewScrubber : IDisposable
    {
        private const double Eps = 0.002;
        // No requests for this long: release the decoder session (keeps the cache).
        private const int IdleKillMs = 5000;
        // How far from the requested time a cached frame may be and still be shown
        // as an instant stand-in while the exact frame is decoded.
        private const double FallbackWindow = 0.5;
        // A superseded decoded frame is still worth showing when it is this close to
        // the newest requested position: it is sharper than a thumbnail and visually
        // right, while a frame from an abandoned region is never published.
        private const double StalePublishTolerance = 0.35;
        // Hard cap on a forward walk, independent of the cost estimate.
        private const int MaxStepFrames = 300;
        // Cost model seeds, refined at runtime by measurement. A forward step reads one
        // frame off an already running ffmpeg (a pipe read plus one bitmap), it is not
        // comparable to starting a process, so the seed is deliberately small and the
        // measured value takes over after a few steps.
        private const double SeedStepMs = 4.0;
        private const double SeedSeekMs = 600.0;
        private const double SeedProxySeekMs = 40.0;
        private const int MaxCacheEntries = 48;
        private const long MaxCacheBytes = 96L * 1024 * 1024;
        private const int MinCacheEntries = 6;
        private const int MaxProxyCacheEntries = 24;
        private const int ProxyHeight = 480;
        private const int ProxyFps = 15;
        // TASK-005: the full-clip proxy takes ~45 s to build (Test.mp4), so the
        // scrubber first builds a small head segment. It uses the exact same
        // encoding, only limited in time, and becomes usable after ~3 s; the full
        // file is built right afterwards in the same background thread and then
        // replaces the head segment. No second timeline, no new architecture.
        private const double EarlyProxySec = 30.0;

        private sealed class Entry
        {
            public Bitmap Bmp;
            public double Pts;
            public long Touch;
            public bool Pinned;
        }

        private readonly string _file;
        private readonly int _width;
        private readonly int _height;
        private readonly double _duration;
        private readonly ISynchronizeInvoke _invoker;
        private readonly long _frameBytes;

        private readonly object _sync = new object();
        private readonly Dictionary<long, Entry> _cache = new Dictionary<long, Entry>();
        private readonly List<Entry> _retiring = new List<Entry>();
        private Entry _pinned;
        private long _cacheBytes;
        private long _clock;

        private FrameDecoder _decoder;
        private Thread _worker;
        private long _generation;
        private long _servedGeneration;
        private int _active;
        private double _target;
        private bool _targetFinal;
        private volatile bool _disposed;

        // Optional H.264 480p / 15 fps stand-in used ONLY while the playhead is
        // moving. Built in two phases in the background (TASK-005): a 30-second
        // head segment first, usable after a few seconds for positions inside
        // it, then the full clip, which replaces the head segment. _proxyValidTo
        // is the timeline length the CURRENT proxy decoder actually covers;
        // requests past it fall through to the original decoder until the full
        // file is ready. MouseUp still decodes the original.
        private readonly Dictionary<long, Entry> _proxyCache = new Dictionary<long, Entry>();
        private long _proxyCacheBytes;
        private long _proxyFrameBytes = 1;
        private FrameDecoder _proxyDecoder;
        private Thread _proxyThread;
        private Process _proxyProc;
        private string _proxyPath;
        // The head-segment file while it is live (TASK-005): tracked separately
        // because _proxyPath already points at the full file being built, so
        // Dispose() would otherwise orphan it.
        private string _earlyProxyPath;
        private volatile bool _proxyReady;
        // Guarded by _sync (volatile is illegal on double).
        private double _proxyValidTo;
        private volatile bool _proxyFailed;
        private long _earlyProxyBuildMs = -1;
        private long _proxyBuildMs = -1;
        private long _proxyBytes;
        private int _proxyW;
        private int _proxyH;
        private double _lastProxySeekMs = -1;
        private double _lastOrigSeekMs = -1;

        // Cost model: measured, not guessed.
        private double _stepMs = SeedStepMs;
        private double _seekMs = SeedSeekMs;
        private double _proxyStepMs = SeedStepMs;
        private double _proxySeekMs = SeedProxySeekMs;

        // UI-thread state for the "better frame wins" publication gate.
        private double _uiTarget;
        private double _uiDisplayedPts;
        private bool _uiHaveDisplayed;
        private Bitmap _uiDisplayedBmp;
        private PreviewFrameKind _uiDisplayedKind;
        private long _lastAppliedGen = -1;

        // Diagnostics.
        private int _requests;
        private int _decodesStarted;
        private int _stepsForward;
        private int _cacheHits;
        private int _abandoned;
        private int _stalePublished;
        private int _processStarts;
        private int _decodeErrors;
        private int _thumbFrames;
        private int _exactFrames;
        private int _cachedFrames;

        public PreviewScrubber(string file, int width, int height, double duration, ISynchronizeInvoke invoker)
            : this(file, width, height, duration, invoker, true)
        {
        }

        // buildProxy=false is used when the editor's embedded player is the active
        // preview: the H.264 stand-in is only ever shown by the FFmpeg scrub
        // preview, so building it (a whole-clip 480p transcode that decodes every
        // source frame) would burn the CPU for minutes after every open without
        // anything ever displaying it. With the prebuilt player preview the
        // scrubber keeps its worker, its cache and its exact-frame decoding, so
        // the FFmpeg preview stays available as the fallback.
        public PreviewScrubber(string file, int width, int height, double duration, ISynchronizeInvoke invoker, bool buildProxy)
        {
            _file = file;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _duration = duration > 0 ? duration : 0;
            _invoker = invoker;
            long fb = (long)_width * _height * 4;
            if (fb > 128L * 1024 * 1024) fb = 128L * 1024 * 1024;
            _frameBytes = fb;

            // Constructing the decoder allocates its frame buffer but starts no
            // process: ffmpeg is only spawned by the first StartAt().
            _decoder = new FrameDecoder(_file, _width, _height);

            _worker = new Thread(new ThreadStart(WorkerProc));
            _worker.IsBackground = true;
            _worker.Name = "preview-scrub";
            _worker.Start();

            _proxyFailed = !buildProxy;
            if (buildProxy) StartProxyBuild();
        }

        // Supplies a thumbnail for an instant stand-in. MUST return a freshly
        // allocated bitmap (the consumer owns it) or null. Called on the UI thread.
        public Func<double, Bitmap> ThumbnailProvider { get; set; }

        public bool ThumbnailFallbackEnabled { get; set; }

        // Raised on the invoker's thread for every frame the consumer should show.
        public event Action<Bitmap, double, PreviewFrameKind> FrameReady;

        public int Requests { get { return _requests; } }
        public int DecodesStarted { get { return _decodesStarted; } }
        public int StepsForward { get { return _stepsForward; } }
        public int CacheHits { get { return _cacheHits; } }
        public int DecodesAbandoned { get { return _abandoned; } }
        public int StalePublished { get { return _stalePublished; } }
        public int ProcessStarts { get { return _processStarts; } }
        public int DecodeErrors { get { return _decodeErrors; } }
        public int ThumbFrames { get { return _thumbFrames; } }
        public int ExactFrames { get { return _exactFrames; } }
        public int CachedFrames { get { return _cachedFrames; } }
        // Timestamp of the frame currently on screen, and whether anything has been put
        // there yet. Lets a caller tell whether the preview already shows the frame for
        // a given position. UI thread only.
        public double DisplayedPts { get { return _uiDisplayedPts; } }
        public bool HasDisplayed { get { return _uiHaveDisplayed; } }

        public int CachedEntries { get { lock (_sync) { return _cache.Count; } } }
        public bool ProxyReady { get { return _proxyReady; } }
        // How far into the clip the current proxy decoder reaches. Before the
        // full file is ready this is the head segment (TASK-005); requests past
        // it are served from the original until the swap.
        public double ProxyValidTo { get { lock (_sync) { return _proxyValidTo; } } }
        public bool ProxyFailed { get { return _proxyFailed; } }
        public long EarlyProxyBuildMs { get { return _earlyProxyBuildMs; } }
        public long ProxyBuildMs { get { return _proxyBuildMs; } }
        public long ProxyBytes { get { return _proxyBytes; } }
        public double LastProxySeekMs { get { return _lastProxySeekMs; } }
        public double LastOrigSeekMs { get { return _lastOrigSeekMs; } }

        // True when every request issued so far has been served and no decode is in
        // flight. The UI uses it to know when the preview has caught up.
        public bool IsIdle
        {
            get { lock (_sync) { return !_disposed && _generation == _servedGeneration && _active == 0; } }
        }

        // Tells the scrubber that the caller has put this frame on screen itself (the
        // editor does that for the opening frame and for the step buttons, which use
        // their own decoder). Without it the scrubber would treat the screen as empty
        // and could replace a sharp frame with an instant thumbnail stand-in.
        public void NoteDisplayed(double pts)
        {
            if (_disposed) return;
            _uiTarget = pts;
            _uiDisplayedPts = pts;
            _uiHaveDisplayed = true;
            _uiDisplayedBmp = null;
        }

        public void Request(double t)
        {
            RequestCore(t, false);
        }

        // MouseUp / explicit exact seek: a new generation that must win.
        public void RequestFinal(double t)
        {
            RequestCore(t, true);
        }

        private void RequestCore(double t, bool final)
        {
            if (_disposed) return;
            if (t < 0) t = 0;
            if (_duration > 0 && t > _duration) t = _duration;

            _uiTarget = t;
            Interlocked.Increment(ref _requests);

            lock (_sync)
            {
                if (_disposed) return;
                _generation++;
                _target = t;
                _targetFinal = final;
                Monitor.Pulse(_sync);
            }

            // Instant visual feedback, synchronously on the caller's thread: memory
            // lookups only, no decode, no ffmpeg. This is what makes the preview
            // follow the mouse while the worker is still decoding the exact frame.
            if (!final) ShowFallback(t);
        }

        // ------------------------------------------------------------------ worker

        private void WorkerProc()
        {
            while (true)
            {
                long gen;
                double target;
                bool final;
                if (!TakeRequest(out gen, out target, out final))
                {
                    if (_disposed) return;
                    ReleaseIdleSession();
                    continue;
                }
                try { Serve(gen, target, final); }
                catch { Interlocked.Increment(ref _decodeErrors); }
                finally
                {
                    lock (_sync) { _active--; Monitor.PulseAll(_sync); }
                }
            }
        }

        private bool TakeRequest(out long gen, out double target, out bool final)
        {
            gen = 0;
            target = 0;
            final = false;
            lock (_sync)
            {
                if (_disposed) return false;
                if (_generation == _servedGeneration) Monitor.Wait(_sync, IdleKillMs);
                if (_disposed) return false;
                if (_generation == _servedGeneration) return false;
                _servedGeneration = _generation;
                gen = _generation;
                target = _target;
                final = _targetFinal;
                _active++;
                return true;
            }
        }

        private void Serve(long gen, double target, bool final)
        {
            if (target < 0) target = 0;
            if (_duration > 0 && target > _duration) target = _duration;

            if (final)
            {
                ServeOriginal(gen, target, true);
                return;
            }

            if (_proxyReady)
            {
                ServeProxy(gen, target);
                return;
            }

            ServeOriginal(gen, target, false);
        }

        private void ServeOriginal(long gen, double target, bool final)
        {
            Entry hit = FindNearest(_cache, target, ExactTolerance(_decoder));
            if (hit != null)
            {
                Interlocked.Increment(ref _cacheHits);
                Publish(hit, PreviewFrameKind.Exact, gen, final);
                return;
            }

            Entry walked;
            if (TryForwardWalk(_decoder, false, gen, target, out walked))
            {
                if (walked != null) Publish(walked, PreviewFrameKind.Exact, gen, final);
                return;
            }

            if (!IsCurrent(gen)) return;

            Stopwatch sw = Stopwatch.StartNew();
            DecodedFrame f = _decoder.StartAt(target);
            sw.Stop();
            _lastOrigSeekMs = sw.Elapsed.TotalMilliseconds;
            ObserveSeekCost(sw.Elapsed.TotalMilliseconds);
            _processStarts = _decoder.ProcessStarts;
            Interlocked.Increment(ref _decodesStarted);
            if (f == null)
            {
                Interlocked.Increment(ref _decodeErrors);
                return;
            }
            Entry e = Insert(_cache, f, _frameBytes, MaxCacheEntries, MaxCacheBytes, MinCacheEntries);
            if (e == null) return;
            PublishOrAbandon(e, gen, final, PreviewFrameKind.Exact);
        }

        private void ServeProxy(long gen, double target)
        {
            FrameDecoder d = _proxyDecoder;
            if (d == null) return;

            // TASK-005: the head segment covers only the start of the clip. Past
            // it the original serves, exactly as if no proxy existed yet.
            double validTo;
            lock (_sync) { validTo = _proxyValidTo; }
            if (target > validTo + Eps)
            {
                ServeOriginal(gen, target, false);
                return;
            }

            Entry hit = FindNearest(_proxyCache, target, ExactTolerance(d));
            if (hit != null)
            {
                Interlocked.Increment(ref _cacheHits);
                Publish(hit, PreviewFrameKind.Cached, gen, false);
                return;
            }

            Entry walked;
            if (TryForwardWalk(d, true, gen, target, out walked))
            {
                if (walked != null) Publish(walked, PreviewFrameKind.Cached, gen, false);
                return;
            }

            if (!IsCurrent(gen)) return;

            Stopwatch sw = Stopwatch.StartNew();
            DecodedFrame f = d.StartAt(target);
            sw.Stop();
            _lastProxySeekMs = sw.Elapsed.TotalMilliseconds;
            ObserveProxySeekCost(sw.Elapsed.TotalMilliseconds);
            Interlocked.Increment(ref _decodesStarted);
            if (f == null)
            {
                Interlocked.Increment(ref _decodeErrors);
                return;
            }
            Entry e = Insert(_proxyCache, f, _proxyFrameBytes, MaxProxyCacheEntries, MaxCacheBytes, 4);
            if (e == null) return;
            PublishOrAbandon(e, gen, false, PreviewFrameKind.Cached);
        }

        private void PublishOrAbandon(Entry e, long gen, bool final, PreviewFrameKind kind)
        {
            if (IsCurrent(gen)) Publish(e, kind, gen, final);
            else
            {
                Interlocked.Increment(ref _abandoned);
                if (Math.Abs(e.Pts - CurrentTarget()) <= StalePublishTolerance)
                {
                    Interlocked.Increment(ref _stalePublished);
                    Publish(e, PreviewFrameKind.Cached, gen, false);
                }
            }
        }

        // Continue the live decoder forward instead of killing ffmpeg and seeking
        // again. Returns true when the walk handled the request (either it reached
        // the target, or it stopped because a newer request superseded it).
        private bool TryForwardWalk(FrameDecoder decoder, bool proxy, long gen, double target, out Entry result)
        {
            result = null;
            if (decoder == null || !decoder.HasSession) return false;

            double last = decoder.LastPts;
            if (last < 0) return false;
            double gap = target - last;
            if (gap <= Eps) return false;                    // same frame or backwards

            double frameDur = decoder.EstimatedFrameDuration;
            if (frameDur <= 0 || frameDur > 1.0) frameDur = proxy ? 1.0 / ProxyFps : 1.0 / 30.0;

            double stepMs = proxy ? _proxyStepMs : _stepMs;
            double seekMs = proxy ? _proxySeekMs : _seekMs;
            double frames = gap / frameDur;
            if (frames > MaxStepFrames) return false;
            if (frames * stepMs > seekMs) return false;     // seeking is cheaper

            while (true)
            {
                if (!IsCurrent(gen)) return true;             // superseded: stop now
                Stopwatch sw = Stopwatch.StartNew();
                DecodedFrame f = decoder.StepForward();
                sw.Stop();
                if (f == null) return false;                  // session died: fall back to a seek
                if (proxy) ObserveProxyStepCost(sw.Elapsed.TotalMilliseconds);
                else ObserveStepCost(sw.Elapsed.TotalMilliseconds);
                Interlocked.Increment(ref _stepsForward);

                Entry e = proxy
                    ? Insert(_proxyCache, f, _proxyFrameBytes, MaxProxyCacheEntries, MaxCacheBytes, 4)
                    : Insert(_cache, f, _frameBytes, MaxCacheEntries, MaxCacheBytes, MinCacheEntries);
                if (f.PtsTime >= target - Eps)
                {
                    result = e;
                    return true;
                }
                double remaining = (target - f.PtsTime) / frameDur;
                if (remaining * stepMs > seekMs) return false;   // no longer worth it
            }
        }

        private bool IsCurrent(long gen)
        {
            lock (_sync) { return !_disposed && _generation == gen; }
        }

        private double CurrentTarget()
        {
            lock (_sync) { return _target; }
        }

        // A cached frame only counts as "the frame for this time" when it is within a
        // fraction of one frame duration. The upper clamp is not cosmetic: with a bogus
        // (huge) frame duration this test would degenerate into "any frame within a
        // minute is fine" and Serve() would hand back a frame from a completely
        // different part of the clip without ever decoding the requested one.
        private double ExactTolerance(FrameDecoder decoder)
        {
            double frameDur = decoder != null ? decoder.EstimatedFrameDuration : 0;
            if (frameDur <= 0 || frameDur > 1.0) frameDur = 1.0 / 30.0;
            double tol = frameDur * 0.6;
            if (tol > 0.05) tol = 0.05;
            if (tol < 0.005) tol = 0.005;
            return tol;
        }

        // True when a frame's timestamp is within one frame duration of the playhead,
        // i.e. it is the frame the playhead is actually sitting on. A frame cut from a
        // different part of the clip is farther away than that and can never pass.
        private bool WithinOneFrame(double pts, double target)
        {
            FrameDecoder d = _decoder;
            double frameDur = d != null ? d.EstimatedFrameDuration : 0;
            if (frameDur <= 0 || frameDur > 1.0) frameDur = 1.0 / 30.0;
            return Math.Abs(pts - target) <= frameDur + 1e-6;
        }

        private void ObserveStepCost(double ms)
        {
            if (ms <= 0) return;
            _stepMs = _stepMs * 0.7 + ms * 0.3;
            if (_stepMs < 0.2) _stepMs = 0.2;
            if (_stepMs > 5000) _stepMs = 5000;
        }

        private void ObserveSeekCost(double ms)
        {
            if (ms <= 0) return;
            _seekMs = _seekMs * 0.7 + ms * 0.3;
            if (_seekMs < 1) _seekMs = 1;
            if (_seekMs > 20000) _seekMs = 20000;
        }

        private void ObserveProxyStepCost(double ms)
        {
            if (ms <= 0) return;
            _proxyStepMs = _proxyStepMs * 0.7 + ms * 0.3;
            if (_proxyStepMs < 0.2) _proxyStepMs = 0.2;
            if (_proxyStepMs > 2000) _proxyStepMs = 2000;
        }

        private void ObserveProxySeekCost(double ms)
        {
            if (ms <= 0) return;
            _proxySeekMs = _proxySeekMs * 0.7 + ms * 0.3;
            if (_proxySeekMs < 1) _proxySeekMs = 1;
            if (_proxySeekMs > 5000) _proxySeekMs = 5000;
        }

        private void ReleaseIdleSession()
        {
            FrameDecoder d = _decoder;
            if (d != null && d.HasSession) d.ReleaseSession();
            FrameDecoder p = _proxyDecoder;
            if (p != null && p.HasSession) p.ReleaseSession();
        }

        // ------------------------------------------------------------------- cache

        private static long Key(double pts)
        {
            return (long)Math.Round(pts * 1000.0);
        }

        private Entry FindNearest(Dictionary<long, Entry> cache, double t, double tolerance)
        {
            Entry best = null;
            double bestDist = tolerance + 1e-9;
            lock (_sync)
            {
                foreach (KeyValuePair<long, Entry> kv in cache)
                {
                    Entry e = kv.Value;
                    if (e.Bmp == null) continue;
                    double d = Math.Abs(e.Pts - t);
                    if (d <= bestDist) { bestDist = d; best = e; }
                }
                if (best != null) best.Touch = ++_clock;
            }
            return best;
        }

        // Takes ownership of f.Image.
        private Entry Insert(Dictionary<long, Entry> cache, DecodedFrame f, long frameBytes, int maxEntries, long maxBytes, int minEntries)
        {
            if (f == null || f.Image == null) return null;
            long key = Key(f.PtsTime);
            lock (_sync)
            {
                if (_disposed)
                {
                    try { f.Image.Dispose(); } catch { }
                    return null;
                }
                Entry old;
                if (cache.TryGetValue(key, out old) && old.Bmp != null)
                {
                    if (old.Pinned)
                    {
                        old.Touch = ++_clock;
                        try { f.Image.Dispose(); } catch { }
                        return old;
                    }
                    try { old.Bmp.Dispose(); } catch { }
                    if (ReferenceEquals(cache, _proxyCache)) _proxyCacheBytes -= frameBytes;
                    else _cacheBytes -= frameBytes;
                    cache.Remove(key);
                }
                Entry e = new Entry();
                e.Bmp = f.Image;
                e.Pts = f.PtsTime;
                e.Touch = ++_clock;
                cache[key] = e;
                if (ReferenceEquals(cache, _proxyCache)) _proxyCacheBytes += frameBytes;
                else _cacheBytes += frameBytes;
                EvictLocked(cache, frameBytes, maxEntries, maxBytes, minEntries);
                return e;
            }
        }

        private void EvictLocked(Dictionary<long, Entry> cache, long frameBytes, int maxEntries, long maxBytes, int minEntries)
        {
            long perEntry = frameBytes > 0 ? frameBytes : 1;
            int byBytes = (int)(maxBytes / perEntry);
            if (byBytes < minEntries) byBytes = minEntries;
            if (byBytes < maxEntries) maxEntries = byBytes;
            bool proxy = ReferenceEquals(cache, _proxyCache);

            while (cache.Count > maxEntries || (proxy ? _proxyCacheBytes : _cacheBytes) > maxBytes)
            {
                Entry victim = null;
                foreach (KeyValuePair<long, Entry> kv in cache)
                {
                    Entry e = kv.Value;
                    if (e.Pinned) continue;
                    if (victim == null || e.Touch < victim.Touch) victim = e;
                }
                if (victim == null) return;
                long key = Key(victim.Pts);
                Entry cur;
                if (cache.TryGetValue(key, out cur) && ReferenceEquals(cur, victim)) cache.Remove(key);
                else continue;
                try { if (victim.Bmp != null) victim.Bmp.Dispose(); } catch { }
                if (proxy)
                {
                    _proxyCacheBytes -= perEntry;
                    if (_proxyCacheBytes < 0) _proxyCacheBytes = 0;
                }
                else
                {
                    _cacheBytes -= perEntry;
                    if (_cacheBytes < 0) _cacheBytes = 0;
                }
            }
        }

        private void ClearCache()
        {
            List<Entry> all = new List<Entry>();
            lock (_sync)
            {
                foreach (KeyValuePair<long, Entry> kv in _cache) all.Add(kv.Value);
                foreach (KeyValuePair<long, Entry> kv in _proxyCache) all.Add(kv.Value);
                _cache.Clear();
                _proxyCache.Clear();
                _cacheBytes = 0;
                _proxyCacheBytes = 0;
                _retiring.Clear();
                _pinned = null;
            }
            for (int i = 0; i < all.Count; i++)
            {
                try { if (all[i].Bmp != null) all[i].Bmp.Dispose(); }
                catch { }
            }
        }

        // ----------------------------------------------------------------- publish

        // force: publish even when the very same bitmap is already on screen. Used
        // for the final (MouseUp) request so the consumer always gets a definite
        // "this is the exact frame for this position" notification.
        private void Publish(Entry e, PreviewFrameKind kind, long gen, bool force)
        {
            if (e == null || e.Bmp == null) return;
            PinForPublish(e);

            if (_invoker != null && _invoker.InvokeRequired)
            {
                try
                {
                    _invoker.BeginInvoke(new Action(delegate { ApplyCandidate(e.Bmp, e.Pts, kind, gen, force); }), null);
                    return;
                }
                catch
                {
                    AcknowledgeDisplayed();
                    return;
                }
            }
            ApplyCandidate(e.Bmp, e.Pts, kind, gen, force);
        }

        private void PinForPublish(Entry e)
        {
            lock (_sync)
            {
                if (!ReferenceEquals(_pinned, e) && _pinned != null)
                {
                    // Keep the previously shown bitmap alive until the consumer has
                    // acknowledged that it replaced it on screen.
                    _retiring.Add(_pinned);
                    _pinned.Pinned = false;
                }
                e.Pinned = true;
                _pinned = e;
            }
        }

        // Runs on the UI thread. The gate that implements "an obsolete frame never
        // overwrites a newer preview": a candidate is shown only when it is at least
        // as close to the CURRENT playhead as what is already displayed. A frame from
        // an abandoned region is therefore rejected, while a sharper frame for (or
        // near) the current position always wins.
        //
        // Distance alone is not enough, because a stand-in is labelled with the time
        // the caller ASKED for, not with the time of the frame it was cut from: a
        // thumbnail published for t has distance 0 to the playhead while the real
        // frame for t sits up to one frame duration behind it. Judged by distance
        // alone that thumbnail beats the exact frame, which is exactly the "fallback
        // sticks after the mouse stops" defect. Two rules fix it: a stand-in never
        // blocks the exact frame for the position it stands for, and the final
        // (MouseUp) request always wins.
        private void ApplyCandidate(Bitmap bmp, double pts, PreviewFrameKind kind, long gen, bool force)
        {
            if (_disposed || bmp == null) { AcknowledgeDisplayed(); return; }

            double target = _uiTarget;
            double curDist = _uiHaveDisplayed ? Math.Abs(_uiDisplayedPts - target) : double.MaxValue;
            double newDist = Math.Abs(pts - target);

            bool wins;
            if (force)
            {
                // Final request: the frame for the position the user stopped at must
                // reach the screen no matter what is displayed.
                wins = true;
            }
            else if (kind == PreviewFrameKind.Exact && _uiHaveDisplayed
                     && _uiDisplayedKind != PreviewFrameKind.Exact && WithinOneFrame(pts, target))
            {
                // The sharp frame for the position the playhead is on supersedes the
                // blurry stand-in that was shown for that same position.
                wins = true;
            }
            else
            {
                wins = newDist <= curDist + 1e-6;
            }

            // A result from an older generation must never displace a newer one, not
            // even the forced final frame: it could land after a still newer request
            // has already been started.
            if (wins && gen >= 0 && gen < _lastAppliedGen) wins = false;

            if (wins && !ReferenceEquals(bmp, _uiDisplayedBmp))
            {
                _uiDisplayedPts = pts;
                _uiDisplayedBmp = bmp;
                _uiDisplayedKind = kind;
                _uiHaveDisplayed = true;
                if (gen >= 0) _lastAppliedGen = gen;
                if (kind == PreviewFrameKind.Exact) Interlocked.Increment(ref _exactFrames);
                else if (kind == PreviewFrameKind.Cached) Interlocked.Increment(ref _cachedFrames);
                else Interlocked.Increment(ref _thumbFrames);

                Action<Bitmap, double, PreviewFrameKind> h = FrameReady;
                if (h != null)
                {
                    try { h(bmp, pts, kind); }
                    catch { }
                }
            }
            AcknowledgeDisplayed();
        }

        // The consumer has replaced the on-screen image: anything retired earlier is
        // now unreferenced and may be evicted.
        public void AcknowledgeDisplayed()
        {
            lock (_sync)
            {
                for (int i = 0; i < _retiring.Count; i++)
                {
                    if (_retiring[i] != null) _retiring[i].Pinned = false;
                }
                _retiring.Clear();
                EvictLocked(_cache, _frameBytes, MaxCacheEntries, MaxCacheBytes, MinCacheEntries);
                EvictLocked(_proxyCache, _proxyFrameBytes, MaxProxyCacheEntries, MaxCacheBytes, 4);
            }
        }

        private void ShowFallback(double t)
        {
            if (_proxyReady)
            {
                Entry p = FindNearest(_proxyCache, t, FallbackWindow);
                if (p != null)
                {
                    Interlocked.Increment(ref _cacheHits);
                    ApplyCandidate(p.Bmp, p.Pts, PreviewFrameKind.Cached, -1, false);
                    return;
                }
                // Proxy is the live source during a drag. Do not stretch a 72 px
                // filmstrip thumbnail over it once that source exists.
                return;
            }
            Entry e = FindNearest(_cache, t, FallbackWindow);
            if (e != null)
            {
                Interlocked.Increment(ref _cacheHits);
                ApplyCandidate(e.Bmp, e.Pts, PreviewFrameKind.Cached, -1, false);
                return;
            }
            if (!ThumbnailFallbackEnabled || ThumbnailProvider == null) return;
            Bitmap b = null;
            try { b = ThumbnailProvider(t); }
            catch { b = null; }
            if (b == null) return;
            ApplyCandidate(b, t, PreviewFrameKind.Thumbnail, -1, false);
        }

        private void StartProxyBuild()
        {
            _proxyThread = new Thread(new ThreadStart(BuildProxyProc));
            _proxyThread.IsBackground = true;
            _proxyThread.Name = "preview-proxy";
            _proxyThread.Start();
        }

        // Allocates a fresh temp path for one proxy file and tracks it in
        // _proxyPath BEFORE the build starts, so Dispose() cleans even a
        // half-written file. Returns null when already disposed.
        private string NewProxyPath()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "YouTubeDownloader-Net10");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "proxy-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N") + ".mp4");
                lock (_sync)
                {
                    if (_disposed) return null;
                    _proxyPath = path;
                }
                return path;
            }
            catch { return null; }
        }

        // Runs one ffmpeg transcode. limitSec > 0 caps the output length (the
        // head segment); 0 means the whole clip. True on a usable file.
        // Never touches _proxyFailed: the caller decides what a failure means.
        private bool BuildProxyFile(string path, double limitSec)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfmpegExe;
                psi.Arguments = "-hide_banner -y -i " + YtDlpRunner.Quote(_file)
                    + " -map 0:v:0 -an -sn -dn -vf scale=" + _proxyW.ToString(CultureInfo.InvariantCulture)
                    + ":" + _proxyH.ToString(CultureInfo.InvariantCulture) + ",fps=" + ProxyFps.ToString(CultureInfo.InvariantCulture)
                    + " -c:v libx264 -preset ultrafast -tune zerolatency -crf 28 -pix_fmt yuv420p"
                    + " -x264-params keyint=" + ProxyFps.ToString(CultureInfo.InvariantCulture)
                    + ":min-keyint=" + ProxyFps.ToString(CultureInfo.InvariantCulture)
                    + ":scenecut=0"
                    + (limitSec > 0 ? " -t " + limitSec.ToString("0", CultureInfo.InvariantCulture) : "")
                    + " " + YtDlpRunner.Quote(path);
                psi.WorkingDirectory = AppPaths.BaseDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                psi.RedirectStandardOutput = true;

                Process p = new Process();
                p.StartInfo = psi;
                lock (_sync)
                {
                    if (_disposed) return false;
                    _proxyProc = p;
                }
                Stopwatch sw = Stopwatch.StartNew();
                p.Start();
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                p.WaitForExit();
                sw.Stop();
                _proxyBuildMs = sw.ElapsedMilliseconds;
                lock (_sync) { _proxyProc = null; }

                if (_disposed)
                {
                    TryDelete(path);
                    return false;
                }
                if (p.ExitCode != 0 || !File.Exists(path)) return false;
                FileInfo fi = new FileInfo(path);
                _proxyBytes = fi.Length;
                if (_proxyBytes <= 0) return false;
                return true;
            }
            catch { return false; }
        }

        // Opens a decoder on a freshly built proxy file, warms it with one
        // sample seek and publishes it. validTo is the timeline length this
        // file covers (head segment or whole clip).
        private bool AttachProxyDecoder(string path, double sampleAt, double validTo)
        {
            FrameDecoder dec = new FrameDecoder(path, _proxyW, _proxyH);
            Stopwatch seekSw = Stopwatch.StartNew();
            DecodedFrame sample = dec.StartAt(sampleAt);
            seekSw.Stop();
            _lastProxySeekMs = seekSw.Elapsed.TotalMilliseconds;
            if (sample != null)
                Insert(_proxyCache, sample, _proxyFrameBytes, MaxProxyCacheEntries, MaxCacheBytes, 4);
            lock (_sync)
            {
                if (_disposed)
                {
                    dec.Dispose();
                    TryDelete(path);
                    return false;
                }
                _proxyDecoder = dec;
                _proxyPath = path;
                _proxyValidTo = validTo;
                _proxyReady = true;
                Monitor.PulseAll(_sync);
            }
            return true;
        }

        private void BuildProxyProc()
        {
            string early = null;
            try
            {
                int h = ProxyHeight;
                if (h > _height && _height > 0) h = _height;
                int w = (int)Math.Round(_width * (double)h / Math.Max(1, _height));
                if (w < 2) w = 2;
                if ((w & 1) != 0) w++;
                if ((h & 1) != 0) h++;
                _proxyW = w;
                _proxyH = h;
                long fb = (long)w * h * 4;
                if (fb < 1) fb = 1;
                _proxyFrameBytes = fb;

                // Short clips fit into one pass: same flow as before, no split.
                if (_duration <= 0 || _duration <= EarlyProxySec)
                {
                    string only = NewProxyPath();
                    if (only == null) return;
                    if (!BuildProxyFile(only, 0))
                    {
                        _proxyFailed = true;
                        TryDeleteRetried(only, 3, 200);
                        return;
                    }
                    if (!AttachProxyDecoder(only, _duration > 2 ? _duration * 0.5 : 0, _duration))
                        return;
                    WriteProxyDiag();
                    return;
                }

                // Phase 1 (TASK-005): the head segment. Same encoding, limited in
                // time: scrubbing the start of the clip is fast after a few
                // seconds instead of after the whole ~45 s build.
                early = NewProxyPath();
                if (early == null) return;
                if (BuildProxyFile(early, EarlyProxySec))
                {
                    _earlyProxyBuildMs = _proxyBuildMs;
                    // Kept even if phase 2 below fails: the head stays usable
                    // instead of falling back to AV1 for the start of the clip.
                    lock (_sync) { _earlyProxyPath = early; }
                    AttachProxyDecoder(early, EarlyProxySec * 0.5, EarlyProxySec);
                    WriteProxyDiag();
                }
                else
                {
                    TryDeleteRetried(early, 3, 200);
                    early = null;
                }
                if (_disposed) return;

                // Phase 2: the full clip, replacing the head segment.
                string full = NewProxyPath();
                if (full == null) return;
                if (!BuildProxyFile(full, 0))
                {
                    if (!_proxyReady) _proxyFailed = true;
                    TryDeleteRetried(full, 3, 200);
                    return;
                }
                FrameDecoder prev;
                lock (_sync) { prev = _proxyDecoder; }
                if (!AttachProxyDecoder(full, _duration > 2 ? _duration * 0.5 : 0, _duration))
                {
                    TryDeleteRetried(full, 3, 200);
                    return;
                }
                // The full file is live: stop the replaced head session first so
                // no dead ffmpeg keeps burning CPU, then drop its file.
                if (prev != null)
                {
                    try { prev.ReleaseSession(); } catch { }
                    try { prev.Dispose(); } catch { }
                }
                lock (_sync) { _earlyProxyPath = null; }
                if (!TryDeleteRetried(early, 5, 250))
                {
                    // A serve was still reading it (Windows cannot delete an
                    // open file); Dispose() gets a second chance at close.
                    lock (_sync) { _earlyProxyPath = early; }
                }
                early = null;
                WriteProxyDiag();
            }
            catch
            {
                if (!_proxyReady) _proxyFailed = true;
                // Delete only what never went live: a live head segment must
                // survive, it may be serving scrub requests right now.
                if (early != null && !_proxyReady) TryDelete(early);
            }
        }

        private void WriteProxyDiag()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "YouTubeDownloader-Net10");
                Directory.CreateDirectory(dir);
                string line = string.Format(CultureInfo.InvariantCulture,
                    "ready={0} fail={1} earlyMs={2} buildMs={3} validTo={4:N1} bytes={5} {6}x{7} proxySeekMs={8:N1} origSeekMs={9:N1} path={10}\r\n",
                    _proxyReady, _proxyFailed, _earlyProxyBuildMs, _proxyBuildMs, _proxyValidTo, _proxyBytes, _proxyW, _proxyH,
                    _lastProxySeekMs, _lastOrigSeekMs, _proxyPath ?? "");
                File.WriteAllText(Path.Combine(dir, "proxy-last.txt"), line);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        // Windows cannot delete a file a dying ffmpeg still has open (the kill
        // is async), so one shot is not enough at the swap/Dispose points.
        private static bool TryDeleteRetried(string path, int attempts, int delayMs)
        {
            if (string.IsNullOrEmpty(path)) return true;
            for (int i = 0; i <= attempts; i++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    File.Delete(path);
                    return true;
                }
                catch { }
                try { Thread.Sleep(delayMs); }
                catch { }
            }
            return false;
        }

        public void Dispose()
        {
            FrameDecoder d;
            FrameDecoder pd;
            Process pp;
            string path;
            string epath;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                d = _decoder;
                _decoder = null;
                pd = _proxyDecoder;
                _proxyDecoder = null;
                pp = _proxyProc;
                path = _proxyPath;
                epath = _earlyProxyPath;
                _earlyProxyPath = null;
                _generation++;
                Monitor.PulseAll(_sync);
            }
            if (pp != null)
            {
                try { YtDlpRunner.KillTreeNoWait(pp); } catch { }
            }
            // Release the decoder first: that unblocks a worker sitting in a pipe
            // read, so the join below cannot hang on a long decode.
            if (d != null) d.Dispose();
            if (pd != null) pd.Dispose();
            if (_worker != null)
            {
                try { _worker.Join(2000); }
                catch { }
                _worker = null;
            }
            if (_proxyThread != null)
            {
                try { _proxyThread.Join(2000); }
                catch { }
                _proxyThread = null;
            }
            ClearCache();
            // Retried: the async taskkill above may still hold these files.
            TryDeleteRetried(path, 3, 200);
            TryDeleteRetried(epath, 3, 200);
        }
    }
}
