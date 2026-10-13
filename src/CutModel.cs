using System;
using System.Collections.Generic;

namespace YouTubeDownloader
{
    public struct TrimRegion
    {
        public double Start;
        public double End;

        public TrimRegion(double start, double end)
        {
            Start = start;
            End = end;
        }

        public double Length
        {
            get { return End - Start; }
        }
    }

    public class Cut
    {
        public double Start;
        public double End;

        public Cut(double start, double end)
        {
            Start = start;
            End = end;
        }

        public double Length
        {
            get { return End - Start; }
        }

        public Cut Clone()
        {
            return new Cut(Start, End);
        }
    }

    public class CutModel
    {
        public const double MinCutLength = 0.05;

        private readonly double _duration;
        private readonly List<Cut> _cuts = new List<Cut>();
        private readonly List<List<Cut>> _undo = new List<List<Cut>>();
        private readonly List<List<Cut>> _redo = new List<List<Cut>>();
        private List<Cut> _txSnapshot;
        private bool _txOpen;

        public CutModel(double duration)
        {
            _duration = Math.Max(0, duration);
        }

        public double Duration
        {
            get { return _duration; }
        }

        public int CutCount
        {
            get { return _cuts.Count; }
        }

        public IList<Cut> Cuts
        {
            get { return _cuts.AsReadOnly(); }
        }

        public bool CanUndo
        {
            get { return _undo.Count > 0; }
        }

        public bool CanRedo
        {
            get { return _redo.Count > 0; }
        }

        public double RemovedDuration
        {
            get
            {
                double sum = 0;
                for (int i = 0; i < _cuts.Count; i++) sum += _cuts[i].Length;
                return sum;
            }
        }

        public void BeginTransaction()
        {
            _txSnapshot = Snapshot(_cuts);
            _txOpen = true;
        }

        public void Commit()
        {
            if (!_txOpen) return;
            bool changed = !CutsEqual(_txSnapshot, _cuts);
            _txOpen = false;
            if (changed)
            {
                // Undo-стек хранит состояние ДО изменения (см. AddCut/DeleteCut),
                // поэтому при Commit пушится предтранзакционный снимок,
                // а не текущее состояние, иначе Undo после Commit ничего не откатывает.
                PushUndo(_txSnapshot);
                _redo.Clear();
            }
            _txSnapshot = null;
        }

        public void Rollback()
        {
            if (!_txOpen) return;
            _txOpen = false;
            _cuts.Clear();
            _cuts.AddRange(_txSnapshot);
            _txSnapshot = null;
        }

        public void Normalize()
        {
            for (int i = 0; i < _cuts.Count; i++)
            {
                Cut c = _cuts[i];
                if (c.Start < 0) c.Start = 0;
                if (c.End > _duration) c.End = _duration;
            }
            _cuts.RemoveAll(delegate(Cut c) { return c.End - c.Start < MinCutLength * 0.5; });
            _cuts.Sort(delegate(Cut a, Cut b)
            {
                if (a.Start < b.Start) return -1;
                if (a.Start > b.Start) return 1;
                return 0;
            });
            MergeOverlaps();
        }

        public bool AddCut(double start, double end)
        {
            List<Cut> before = Snapshot(_cuts);
            ClampAndInsert(start, end);
            if (!CutsEqual(before, _cuts))
            {
                if (!_txOpen) PushUndo(before);
                _redo.Clear();
                return true;
            }
            return false;
        }

        public bool DeleteCut(Cut cut)
        {
            List<Cut> before = Snapshot(_cuts);
            for (int i = 0; i < _cuts.Count; i++)
            {
                if (ReferenceEquals(_cuts[i], cut))
                {
                    _cuts.RemoveAt(i);
                    if (!_txOpen) PushUndo(before);
                    _redo.Clear();
                    return true;
                }
            }
            return false;
        }

        public bool DeleteCutAt(int index)
        {
            if (index < 0 || index >= _cuts.Count) return false;
            List<Cut> before = Snapshot(_cuts);
            _cuts.RemoveAt(index);
            if (!_txOpen) PushUndo(before);
            _redo.Clear();
            return true;
        }

        public bool ClearCuts()
        {
            if (_cuts.Count == 0) return false;
            List<Cut> before = Snapshot(_cuts);
            _cuts.Clear();
            if (!_txOpen) PushUndo(before);
            _redo.Clear();
            return true;
        }

        public bool SetCutBounds(Cut cut, double start, double end)
        {
            List<Cut> before = Snapshot(_cuts);
            int idx = _cuts.IndexOf(cut);
            if (idx < 0) return false;
            _cuts.RemoveAt(idx);
            bool ok = ClampAndInsert(start, end);
            if (!ok)
            {
                _cuts.Clear();
                _cuts.AddRange(before);
                return false;
            }
            if (!CutsEqual(before, _cuts)) return true;
            _cuts.Clear();
            _cuts.AddRange(before);
            return false;
        }

        public bool MoveCut(Cut cut, double delta)
        {
            return SetCutBounds(cut, cut.Start + delta, cut.End + delta);
        }

        public List<TrimRegion> KeepRegions()
        {
            List<TrimRegion> regions = new List<TrimRegion>();
            double pos = 0;
            for (int i = 0; i < _cuts.Count; i++)
            {
                double s = Clamp(_cuts[i].Start);
                double e = Clamp(_cuts[i].End);
                if (e <= s) continue;
                if (s > pos + 0.001) regions.Add(new TrimRegion(pos, s));
                pos = Math.Max(pos, e);
            }
            if (pos < _duration - 0.001) regions.Add(new TrimRegion(pos, _duration));
            List<TrimRegion> result = new List<TrimRegion>();
            for (int i = 0; i < regions.Count; i++)
                if (regions[i].Length > 0.001) result.Add(regions[i]);
            return result;
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            List<Cut> cur = Snapshot(_cuts);
            List<Cut> prev = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _cuts.Clear();
            _cuts.AddRange(prev);
            _redo.Add(cur);
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            List<Cut> cur = Snapshot(_cuts);
            List<Cut> next = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _cuts.Clear();
            _cuts.AddRange(next);
            _undo.Add(cur);
        }

        private bool ClampAndInsert(double start, double end)
        {
            if (start < 0) start = 0;
            if (end > _duration) end = _duration;
            if (end - start < MinCutLength) return false;
            _cuts.Add(new Cut(start, end));
            _cuts.Sort(delegate(Cut a, Cut b)
            {
                if (a.Start < b.Start) return -1;
                if (a.Start > b.Start) return 1;
                return 0;
            });
            MergeOverlaps();
            return true;
        }

        private void MergeOverlaps()
        {
            for (int i = 0; i < _cuts.Count - 1; )
            {
                Cut a = _cuts[i];
                Cut b = _cuts[i + 1];
                if (b.Start <= a.End)
                {
                    if (b.End > a.End) a.End = b.End;
                    _cuts.RemoveAt(i + 1);
                }
                else
                {
                    i++;
                }
            }
        }

        private double Clamp(double t)
        {
            if (t < 0) return 0;
            if (t > _duration) return _duration;
            return t;
        }

        private static List<Cut> Snapshot(List<Cut> cuts)
        {
            List<Cut> copy = new List<Cut>(cuts.Count);
            for (int i = 0; i < cuts.Count; i++) copy.Add(cuts[i].Clone());
            return copy;
        }

        private static bool CutsEqual(List<Cut> a, List<Cut> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (Math.Abs(a[i].Start - b[i].Start) > 1e-9) return false;
                if (Math.Abs(a[i].End - b[i].End) > 1e-9) return false;
            }
            return true;
        }

        private void PushUndo(List<Cut> snapshot)
        {
            _undo.Add(snapshot);
            while (_undo.Count > 100) _undo.RemoveAt(0);
        }
    }
}
