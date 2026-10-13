using System;
using System.Collections.Generic;
using System.Drawing;

namespace YouTubeDownloader
{
    // Stage 1 thumbnail store for the filmstrip timeline: memory only.
    //
    // The store holds a DENSE TEMPORAL SEQUENCE of small frames: one thumbnail
    // every ThumbStore.BaseDt seconds of the media, addressed by a grid index
    // (index i is the frame at t = i * BaseDt). It is NOT a "representative frame
    // per screen cell" structure — the timeline viewport selects which slice of
    // this sequence it draws, so the on-screen strip is always a run of
    // consecutive frames of the visible time range.
    //
    // Concurrency contract (important):
    //   * Put() is only ever called on the UI thread (ThumbService marshals every
    //     delivery through ISynchronizeInvoke). Eviction therefore never disposes
    //     a Bitmap that the paint path could be using.
    //   * TryGet() is the paint-path read: memory only, no I/O, no decode, no
    //     blocking work of any kind.
    //   * Has()/HasRange() are safe from worker threads (used to skip redundant
    //     span-scan jobs).
    public sealed class ThumbStore : IDisposable
    {
        // Temporal grid of the base level: 2 thumbnails per second of media.
        public const double BaseDt = 0.5;

        private sealed class Entry
        {
            public Bitmap Bmp;
            public long Touch;
        }

        private readonly Dictionary<int, Entry> _frames = new Dictionary<int, Entry>();
        private readonly object _sync = new object();
        private readonly int _maxFrames;
        // Ascending snapshot of the resident indexes, rebuilt lazily after the set of
        // resident indexes changes. It is what makes "the nearest frame I have" a
        // binary search instead of a walk over the whole store.
        private int[] _sorted;
        private bool _sortedDirty = true;
        private long _clock;
        private int _protectFrom = int.MaxValue;
        private int _protectTo = int.MinValue;
        private bool _disposed;

        public ThumbStore(int maxFrames)
        {
            if (maxFrames < 32) maxFrames = 32;
            _maxFrames = maxFrames;
        }

        public int MaxFrames
        {
            get { return _maxFrames; }
        }

        public int Count
        {
            get { lock (_sync) { return _frames.Count; } }
        }

        // Frames in this index range are never evicted: they are what the viewport
        // is showing right now. Called by the timeline on every viewport change.
        public void ProtectRange(int fromIndex, int toIndex)
        {
            lock (_sync)
            {
                if (fromIndex > toIndex)
                {
                    _protectFrom = int.MaxValue;
                    _protectTo = int.MinValue;
                }
                else
                {
                    _protectFrom = fromIndex;
                    _protectTo = toIndex;
                }
            }
        }

        public bool Has(int index)
        {
            lock (_sync) { return _frames.ContainsKey(index); }
        }

        // True only when every index in the inclusive range is already resident.
        public bool HasRange(int fromIndex, int toIndex)
        {
            if (fromIndex > toIndex) return true;
            lock (_sync)
            {
                for (int i = fromIndex; i <= toIndex; i++)
                    if (!_frames.ContainsKey(i)) return false;
                return true;
            }
        }

        // True only when every one of the given grid indexes is already resident.
        // The sampled pass addresses a SPARSE set of times - one per strip cell - so
        // the contiguous HasRange above cannot answer whether the strip is covered.
        public bool HasAll(int[] indexes, int count)
        {
            if (indexes == null || count <= 0) return true;
            if (count > indexes.Length) count = indexes.Length;
            lock (_sync)
            {
                for (int i = 0; i < count; i++)
                    if (!_frames.ContainsKey(indexes[i])) return false;
                return true;
            }
        }

        // Paint-path read. Touches the LRU so whatever is on screen stays resident
        // even while a background scan keeps flooding the store.
        public bool TryGet(int index, out Bitmap bmp)
        {
            bmp = null;
            lock (_sync)
            {
                Entry e;
                if (!_frames.TryGetValue(index, out e)) return false;
                e.Touch = ++_clock;
                bmp = e.Bmp;
                return bmp != null;
            }
        }

        // The nearest frame that IS resident, in either direction and with no radius
        // limit. The strip paints this while the exact frame of a cell is still being
        // decoded: a cell must never fall back to showing the bare surface, because
        // that reads as a hole in the filmstrip. Ties go to the earlier frame, which
        // keeps the choice deterministic and monotone across neighbouring cells (so
        // the fallback never jumps backwards while walking the strip).
        //
        // Paint-path read: memory only, no I/O, no decode. The returned frame is
        // touched, so the frame a whole group of cells is currently standing in for
        // cannot be evicted underneath them.
        public bool TryGetNearest(int index, out int foundIndex, out Bitmap bmp)
        {
            foundIndex = -1;
            bmp = null;
            lock (_sync)
            {
                if (_frames.Count == 0) return false;
                int[] keys = SortedLocked();

                int pos = Array.BinarySearch(keys, index);
                if (pos < 0) pos = ~pos;               // first key greater than index
                int best = -1;
                if (pos < keys.Length) best = keys[pos];
                if (pos > 0)
                {
                    int left = keys[pos - 1];
                    if (best < 0 || (index - left) <= (best - index)) best = left;
                }
                if (best < 0) return false;

                Entry e = _frames[best];
                e.Touch = ++_clock;
                foundIndex = best;
                bmp = e.Bmp;
                return bmp != null;
            }
        }

        // Rebuilt only when the SET of resident indexes changed; replacing a frame's
        // bitmap does not move its key.
        private int[] SortedLocked()
        {
            if (!_sortedDirty && _sorted != null) return _sorted;
            int[] keys = new int[_frames.Count];
            _frames.Keys.CopyTo(keys, 0);
            Array.Sort(keys);
            _sorted = keys;
            _sortedDirty = false;
            return _sorted;
        }

        // Delivery path. UI thread only (see the concurrency contract above).
        public void Put(int index, Bitmap bmp)
        {
            if (bmp == null) return;
            lock (_sync)
            {
                if (_disposed)
                {
                    try { bmp.Dispose(); }
                    catch { }
                    return;
                }
                Entry old;
                if (_frames.TryGetValue(index, out old))
                {
                    if (ReferenceEquals(old.Bmp, bmp))
                    {
                        old.Touch = ++_clock;
                        return;
                    }
                    _frames.Remove(index);
                    try { if (old.Bmp != null) old.Bmp.Dispose(); }
                    catch { }
                }
                Entry e = new Entry();
                e.Bmp = bmp;
                e.Touch = ++_clock;
                _frames[index] = e;
                _sortedDirty = true;
                if (_frames.Count > _maxFrames) EvictLocked();
            }
        }

        // Least-recently-used sweep over everything outside the protected range.
        // Runs in bulk with hysteresis so that a long background scan does not pay
        // for a sweep on every single delivered frame.
        private void EvictLocked()
        {
            int target = _maxFrames - Math.Max(16, _maxFrames / 10);
            List<KeyValuePair<int, Entry>> candidates = new List<KeyValuePair<int, Entry>>();
            foreach (KeyValuePair<int, Entry> kv in _frames)
            {
                if (kv.Key >= _protectFrom && kv.Key <= _protectTo) continue;
                candidates.Add(kv);
            }
            if (candidates.Count == 0) return;
            candidates.Sort(delegate(KeyValuePair<int, Entry> a, KeyValuePair<int, Entry> b)
            {
                return a.Value.Touch.CompareTo(b.Value.Touch);
            });
            int need = _frames.Count - target;
            for (int i = 0; i < candidates.Count && need > 0; i++, need--)
            {
                Entry e = candidates[i].Value;
                _frames.Remove(candidates[i].Key);
                _sortedDirty = true;
                try { if (e.Bmp != null) e.Bmp.Dispose(); }
                catch { }
            }
        }

        public void Clear()
        {
            List<Entry> all = new List<Entry>();
            lock (_sync)
            {
                foreach (Entry e in _frames.Values) all.Add(e);
                _frames.Clear();
                _sortedDirty = true;
            }
            for (int i = 0; i < all.Count; i++)
            {
                try { if (all[i].Bmp != null) all[i].Bmp.Dispose(); }
                catch { }
            }
        }

        public void Dispose()
        {
            lock (_sync) { _disposed = true; }
            Clear();
        }
    }
}
