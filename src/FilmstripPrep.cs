using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YouTubeDownloader
{
    // CapCut-style filmstrip preparation: the WHOLE clip is prepared once, up
    // front, as ONE seamless horizontal strip of small proxy frames; the UI is
    // shown nothing until the strip is complete, and then it appears whole.
    //
    //   PREPARED  ->  CACHED  ->  SHOWN WHOLE
    //
    // Generation is ONE ffmpeg process decoding the media sequentially in
    // decode order (no per-thumbnail seeking):
    //
    //   ffmpeg -i clip -vf "fps=<count>/<duration>,scale=-2:<h>" -f rawvideo -
    //
    // The frame count follows the physical WIDTH of the clip on screen (one
    // proxy frame per a few pixels of strip), never the number of seconds of
    // media. The finished strip is written to a small disk cache keyed by the
    // exact source file (path + size + mtime), so reopening the same video is
    // instant.
    internal static class FilmstripPrep
    {
        // Proxy frame height: exactly the filmstrip track height.
        public const int FrameHeight = 44;
        // One proxy frame covers this many pixels of the strip at the opening
        // scale. For the 28:24 test clip (strip ~636 px wide) this yields ~140
        // samples - what the visible strip can actually show.
        public const int PixelsPerFrame = 4;
        private const int MinFrames = 32;
        private const int MaxFrames = 512;
        private const uint Magic = 0x50534659;      // "YFSP" little-endian
        private const int Version = 2;
        private const int HeaderBytes = 16;

        // Last Generate() wall time and which pass produced the strip.
        // "cache" is not set here (the caller loaded the file); "key" is the
        // keyframe-only pass; "fps" is the slow full-decode fallback.
        public static int LastElapsedMs;
        public static string LastMode = "";

        // One prepared filmstrip: a byte buffer of BGR24 proxy frames, evenly
        // spread over [0, DurationSec]. Immutable after preparation; the paint
        // path draws over the shared buffer (no per-frame copies).
        public sealed class Prepared
        {
            public byte[] Bytes;                    // Count * FrameBytes, BGR24
            public int Count;
            public int FrameWidth;
            public int FrameHeight;
            public double DurationSec;

            public int FrameBytes
            {
                get { return FrameWidth * FrameHeight * 3; }
            }

            // Time interval frame i covers: [i*dur/N, (i+1)*dur/N).
            public double FrameStart(int i)
            {
                return Count > 0 ? DurationSec * i / Count : 0;
            }

            public double FrameEnd(int i)
            {
                return Count > 0 ? DurationSec * (i + 1) / Count : DurationSec;
            }

            // The frame that covers time t.
            public int IndexAt(double t)
            {
                if (Count <= 0 || DurationSec <= 0) return 0;
                int i = (int)(t / DurationSec * Count);
                if (i < 0) i = 0;
                if (i >= Count) i = Count - 1;
                return i;
            }
        }

        // How many proxy frames the clip needs at the given strip width.
        public static int PlanFrames(double durationSec, int clipWidthPx)
        {
            if (durationSec <= 0) return 0;
            int n = clipWidthPx > 0
                ? (clipWidthPx + PixelsPerFrame - 1) / PixelsPerFrame
                : (int)Math.Ceiling(durationSec / 12.0);
            if (n < MinFrames) n = MinFrames;
            if (n > MaxFrames) n = MaxFrames;
            return n;
        }

        // A GDI+ view of one frame over the shared buffer: 24bpp, no pixel
        // copy, so painting never allocates. The strip outlives every view by
        // design (the timeline holds it for the whole editing session).
        public static Bitmap FrameView(Prepared strip, int i)
        {
            if (strip == null || strip.Bytes == null || strip.Count <= 0) return null;
            if (i < 0) i = 0;
            if (i >= strip.Count) i = strip.Count - 1;
            GCHandle pin = PinShared.Pin(strip.Bytes);
            IntPtr scan0 = pin.AddrOfPinnedObject();
            IntPtr p = new IntPtr(scan0.ToInt64() + (long)i * strip.FrameBytes);
            return new Bitmap(strip.FrameWidth, strip.FrameHeight, strip.FrameWidth * 3,
                PixelFormat.Format24bppRgb, p);
        }

        // One shared pin per buffer: pinning anew for every view would cost a
        // GC handle each paint; the buffer is long-lived anyway.
        private static class PinShared
        {
            private static byte[] _pinned;
            private static GCHandle _handle;
            private static readonly object Gate = new object();

            public static GCHandle Pin(byte[] bytes)
            {
                lock (Gate)
                {
                    if (!ReferenceEquals(_pinned, bytes) || !_handle.IsAllocated)
                    {
                        if (_handle.IsAllocated) _handle.Free();
                        _pinned = bytes;
                        _handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    }
                    return _handle;
                }
            }
        }

        // The cache file of this exact source video version (path+size+mtime).
        public static string CachePathFor(string file)
        {
            FileInfo fi = new FileInfo(file);
            string key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            string hash;
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                StringBuilder sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                hash = sb.ToString();
            }
            string name = Path.GetFileNameWithoutExtension(fi.Name);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 24) name = name.Substring(0, 24);
            string dir = Path.Combine(Path.GetTempPath(), "YouTubeDownloader", "filmstrip");
            return Path.Combine(dir, name + "_" + hash + ".fs1");
        }

        // Reads a previously prepared strip; null when there is none or it is
        // stale (bad header/size). One eager read - the files are ~1-8 MB.
        public static Prepared TryLoad(string cachePath, double durationSec)
        {
            try
            {
                if (!File.Exists(cachePath)) return null;
                using (FileStream fs = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < HeaderBytes) return null;
                    byte[] header = new byte[HeaderBytes];
                    if (ReadExact(fs, header, 0, HeaderBytes) < HeaderBytes) return null;
                    uint magic = BitConverter.ToUInt32(header, 0);
                    int version = BitConverter.ToInt32(header, 4);
                    int count = BitConverter.ToInt32(header, 8);
                    int frameWidth = BitConverter.ToInt32(header, 12);
                    if (magic != Magic || version != Version) return null;
                    if (count < 1 || count > 4096 || frameWidth < 8 || frameWidth > 512) return null;
                    long frameBytes = (long)frameWidth * FrameHeight * 3;
                    if (fs.Length != HeaderBytes + frameBytes * count) return null;
                    byte[] bytes = new byte[frameBytes * count];
                    if (ReadExact(fs, bytes, 0, bytes.Length) < bytes.Length) return null;
                    Prepared p = new Prepared();
                    p.Bytes = bytes;
                    p.Count = count;
                    p.FrameWidth = frameWidth;
                    p.FrameHeight = FrameHeight;
                    p.DurationSec = durationSec;
                    return p;
                }
            }
            catch { return null; }
        }

        private static int ReadExact(Stream s, byte[] buf, int off, int len)
        {
            int total = 0;
            while (total < len)
            {
                int r;
                try { r = s.Read(buf, off + total, len - total); }
                catch { break; }
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        // Generates the strip with ONE sequential ffmpeg pass and saves it into
        // the cache via a temp file, so a killed run never leaves a half store
        // that looks valid. Returns null on any failure; the caller then keeps
        // its neutral "preparing" state.
        //
        // Fast path: decode KEYFRAMES only (-discard:v nokey). That covers the
        // whole duration as a scene map without decoding every P/B frame - the
        // difference on AV1 60 fps Test.mp4 is ~1 s vs ~80 s. If the GOP is so
        // sparse the map would be empty, fall back to the fps sampler (full
        // sequential decode, still no per-thumbnail seeking).
        public static Prepared Generate(string file, double durationSec, int frames, int srcWidth, int srcHeight)
        {
            LastElapsedMs = 0;
            LastMode = "";
            if (frames < 1 || durationSec <= 0) return null;
            int frameWidth = SourceThumbWidth(srcWidth, srcHeight);
            if (frameWidth < 8) return null;
            Stopwatch sw = Stopwatch.StartNew();
            Prepared p = GenerateFromKeys(file, durationSec, frames, frameWidth);
            if (p != null)
            {
                LastMode = "key";
                LastElapsedMs = (int)sw.ElapsedMilliseconds;
                return p;
            }
            p = GenerateFromFps(file, durationSec, frames, frameWidth);
            LastMode = p != null ? "fps" : "";
            LastElapsedMs = (int)sw.ElapsedMilliseconds;
            return p;
        }

        // Keyframe-only pass: one process, sequential, no seeking. The GOP
        // already samples scene changes; extra keys above MaxFrames are
        // subsampled evenly so the buffer stays small.
        private static Prepared GenerateFromKeys(string file, double durationSec, int planned, int frameWidth)
        {
            string vf = "scale=" + frameWidth.ToString(CultureInfo.InvariantCulture)
                + ":" + FrameHeight.ToString(CultureInfo.InvariantCulture)
                + ":flags=fast_bilinear,setsar=1";
            string args = "-hide_banner -loglevel error -an -discard:v nokey -i \"" + file + "\""
                + " -vf \"" + vf + "\" -pix_fmt bgr24 -fps_mode passthrough -f rawvideo -";
            byte[] data;
            int got;
            if (!RunRawPass(args, frameWidth, MaxFrames, out data, out got)) return null;
            // Accept any keyframe map that covers the clip. A 10 s GOP on a
            // 28 min file is ~170 thumbs; even a coarser GOP is still a scene
            // map. Rejecting it and falling through to fps= would decode every
            // frame (~80 s) for no extra visual information at this width.
            if (got < 8) return null;
            if (got > MaxFrames) data = Subsample(data, got, frameWidth * FrameHeight * 3, MaxFrames, out got);
            return FinishStrip(file, data, got, frameWidth, durationSec);
        }

        // Slow fallback: fps sampler still walks the file once, no seeking,
        // but it decodes every frame. Used only when keyframes are too sparse.
        private static Prepared GenerateFromFps(string file, double durationSec, int frames, int frameWidth)
        {
            string fps = (frames / durationSec).ToString("R", CultureInfo.InvariantCulture);
            string vf = "fps=" + fps
                + ",scale=" + frameWidth.ToString(CultureInfo.InvariantCulture)
                + ":" + FrameHeight.ToString(CultureInfo.InvariantCulture)
                + ":flags=fast_bilinear,setsar=1";
            string args = "-hide_banner -loglevel error -an -i \"" + file + "\""
                + " -vf \"" + vf + "\" -pix_fmt bgr24 -f rawvideo -";
            byte[] data;
            int got;
            if (!RunRawPass(args, frameWidth, frames, out data, out got)) return null;
            if (got < 1 || got < frames / 2) return null;
            if (got > frames) data = Subsample(data, got, frameWidth * FrameHeight * 3, frames, out got);
            if (got < frames)
            {
                byte[] cut = new byte[(long)got * frameWidth * FrameHeight * 3];
                Array.Copy(data, cut, cut.Length);
                data = cut;
            }
            return FinishStrip(file, data, got, frameWidth, durationSec);
        }

        private static Prepared FinishStrip(string file, byte[] data, int count, int frameWidth, double durationSec)
        {
            int need = count * frameWidth * FrameHeight * 3;
            if (data == null || count < 1 || data.Length < need) return null;
            if (data.Length != need)
            {
                byte[] exact = new byte[need];
                Array.Copy(data, exact, need);
                data = exact;
            }
            string tmpPath = null;
            Prepared p = new Prepared();
            p.Bytes = data;
            p.Count = count;
            p.FrameWidth = frameWidth;
            p.FrameHeight = FrameHeight;
            p.DurationSec = durationSec;
            SaveCache(CachePathFor(file), p, ref tmpPath);
            try { if (tmpPath != null && File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
            return p;
        }

        // Reads packed BGR24 frames from one ffmpeg stdout. The buffer grows
        // so a keyframe pass covering the WHOLE duration is never truncated
        // to the opening of the clip. Hard cap 4096 (cache header limit).
        private static bool RunRawPass(string args, int frameWidth, int maxFrames, out byte[] data, out int got)
        {
            data = null;
            got = 0;
            Process proc = null;
            try
            {
                int frameBytes = frameWidth * FrameHeight * 3;
                proc = StartFfmpeg(args);
                Stream s = proc.StandardOutput.BaseStream;
                int cap = Math.Max(64, Math.Max(maxFrames, MinFrames));
                if (cap > 4096) cap = 4096;
                byte[] buf = new byte[(long)frameBytes * cap];
                int off = 0;
                for (;;)
                {
                    if (off + frameBytes > buf.Length)
                    {
                        int next = cap * 2;
                        if (next > 4096) next = 4096;
                        if (next <= cap) break;
                        byte[] grown = new byte[(long)frameBytes * next];
                        Array.Copy(buf, grown, off);
                        buf = grown;
                        cap = next;
                    }
                    int read = ReadExact(s, buf, off, frameBytes);
                    if (read < frameBytes) break;
                    off += read;
                    got++;
                }
                // Drain leftover so ffmpeg cannot block on a full pipe.
                byte[] drain = new byte[frameBytes];
                while (ReadExact(s, drain, 0, frameBytes) >= frameBytes) { }
                try { proc.WaitForExit(120000); } catch { }
                if (got < 1) return false;
                data = buf;
                return true;
            }
            catch { return false; }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(); } catch { }
            }
        }

        private static byte[] Subsample(byte[] src, int got, int frameBytes, int target, out int count)
        {
            count = target;
            if (target < 1 || got <= target) { count = got; return src; }
            byte[] dst = new byte[(long)target * frameBytes];
            for (int i = 0; i < target; i++)
            {
                int k = target == 1 ? 0 : (int)((long)i * (got - 1) / (target - 1));
                Array.Copy(src, (long)k * frameBytes, dst, (long)i * frameBytes, frameBytes);
            }
            return dst;
        }

        // Proxy width for the given media aspect at FrameHeight, rounded down
        // to an even number (ffmpeg-safe strides).
        public static int SourceThumbWidth(int srcW, int srcH)
        {
            if (srcW <= 0 || srcH <= 0) return 76;      // 16:9-ish, 4-aligned BGR24 stride
            int w = (int)((long)srcW * FrameHeight / Math.Max(1, srcH));
            if (w < 8) w = 8;
            if (w > 512) w = 512;
            // Multiple of 4 so packed BGR24 stride (w*3) is 4-byte aligned for
            // GDI+ Bitmap(scan0, stride) without a per-frame copy.
            return w & ~3;
        }

        private static Process StartFfmpeg(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = AppPaths.FfmpegExe;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process proc = Process.Start(psi);
            // stderr must be drained or ffmpeg blocks once the pipe fills.
            Thread errDrain = new Thread(new ThreadStart(delegate
            {
                try { proc.StandardError.ReadToEnd(); } catch { }
            }));
            errDrain.IsBackground = true;
            errDrain.Start();
            return proc;
        }

        private static void SaveCache(string cachePath, Prepared p, ref string tmpPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                tmpPath = cachePath + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp";
                using (FileStream fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] header = new byte[HeaderBytes];
                    BitConverter.GetBytes(Magic).CopyTo(header, 0);
                    BitConverter.GetBytes(Version).CopyTo(header, 4);
                    BitConverter.GetBytes(p.Count).CopyTo(header, 8);
                    BitConverter.GetBytes(p.FrameWidth).CopyTo(header, 12);
                    fs.Write(header, 0, header.Length);
                    fs.Write(p.Bytes, 0, p.Bytes.Length);
                }
                if (File.Exists(cachePath)) File.Delete(cachePath);
                File.Move(tmpPath, cachePath);
                tmpPath = null;
            }
            catch { }
        }
    }
}
