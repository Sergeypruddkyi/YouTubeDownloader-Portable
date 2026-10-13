using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace YouTubeDownloader
{
    public class MediaInfo
    {
        public bool Ok;
        public string Error;
        public double Duration;
        public bool HasVideo;
        public bool HasAudio;
        public string VideoCodec;
        public string VideoPixFmt;
        public string AudioCodec;
        public int Width;
        public int Height;
        // Exact video frame rate from r_frame_rate (e.g. 60000/1001 → 59.94).
        // 0 means unknown; callers should fall back to a safe default (e.g. 30).
        public double VideoFps;

        public bool IsAudioOnly
        {
            get { return Ok && HasAudio && !HasVideo; }
        }
    }

    public static class MediaProbe
    {
        private class StreamInfo
        {
            public int Index = -1;
            public string Type;
            public string Codec;
            public string PixFmt;
            public int Width;
            public int Height;
            public double Duration;
            public bool AttachedPic;
            public string RFrameRate;
        }

        public static MediaInfo Probe(string path)
        {
            MediaInfo info = new MediaInfo();
            if (!AppPaths.FfprobePresent())
            {
                info.Error = "ffprobe.exe not found";
                return info;
            }
            string output = RunFlat(path);
            if (output == null)
            {
                info.Error = "ffprobe failed to run";
                return info;
            }

            List<StreamInfo> streams = new List<StreamInfo>();
            double formatDuration = 0;
            StreamInfo cur = null;
            string[] lines = output.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string keyPath = line.Substring(0, eq);
                string val = line.Substring(eq + 1).Trim();
                if (val.Length >= 2 && val[0] == '"' && val[val.Length - 1] == '"')
                    val = val.Substring(1, val.Length - 2);

                string[] parts = keyPath.Split('.');
                if (parts.Length >= 3 && string.Equals(parts[0], "streams", StringComparison.Ordinal)
                    && string.Equals(parts[1], "stream", StringComparison.Ordinal))
                {
                    int idx;
                    if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out idx)) continue;
                    if (cur == null || cur.Index != idx)
                    {
                        cur = FindOrAdd(streams, idx);
                    }
                    string key = parts[parts.Length - 1];
                    if (parts.Length == 4 && string.Equals(key, "index", StringComparison.Ordinal))
                    {
                        cur.Index = ParseInt(val, idx);
                    }
                    else if (string.Equals(key, "codec_type", StringComparison.Ordinal))
                    {
                        cur.Type = val;
                    }
                    else if (string.Equals(key, "codec_name", StringComparison.Ordinal))
                    {
                        cur.Codec = val;
                    }
                    else if (string.Equals(key, "pix_fmt", StringComparison.Ordinal))
                    {
                        cur.PixFmt = val;
                    }
                    else if (string.Equals(key, "width", StringComparison.Ordinal))
                    {
                        cur.Width = ParseInt(val, 0);
                    }
                    else if (string.Equals(key, "height", StringComparison.Ordinal))
                    {
                        cur.Height = ParseInt(val, 0);
                    }
                    else if (string.Equals(key, "duration", StringComparison.Ordinal))
                    {
                        cur.Duration = ParseDouble(val);
                    }
                    else if (string.Equals(key, "r_frame_rate", StringComparison.Ordinal))
                    {
                        cur.RFrameRate = val;
                    }
                    else if (parts.Length == 5 && string.Equals(key, "attached_pic", StringComparison.Ordinal)
                        && string.Equals(parts[3], "disposition", StringComparison.Ordinal))
                    {
                        cur.AttachedPic = ParseInt(val, 0) == 1;
                    }
                }
                else if (parts.Length == 2 && string.Equals(parts[0], "format", StringComparison.Ordinal)
                    && string.Equals(parts[1], "duration", StringComparison.Ordinal))
                {
                    formatDuration = ParseDouble(val);
                }
            }

            StreamInfo video = null;
            StreamInfo audio = null;
            for (int i = 0; i < streams.Count; i++)
            {
                StreamInfo s = streams[i];
                if (string.Equals(s.Type, "video", StringComparison.Ordinal) && !s.AttachedPic && video == null) video = s;
                if (string.Equals(s.Type, "audio", StringComparison.Ordinal) && audio == null) audio = s;
            }

            info.HasVideo = video != null;
            info.HasAudio = audio != null;
            if (video != null)
            {
                info.VideoCodec = video.Codec;
                info.VideoPixFmt = video.PixFmt;
                info.Width = video.Width;
                info.Height = video.Height;
                if (video.Duration > info.Duration) info.Duration = video.Duration;
                if (!string.IsNullOrEmpty(video.RFrameRate))
                    info.VideoFps = HybridCut.ParseFps(video.RFrameRate);
            }
            if (audio != null)
            {
                info.AudioCodec = audio.Codec;
                if (audio.Duration > info.Duration) info.Duration = audio.Duration;
            }
            if (formatDuration > info.Duration) info.Duration = formatDuration;

            if (!info.HasVideo && !info.HasAudio)
            {
                info.Error = "no streams";
                return info;
            }
            if (info.HasVideo && (info.Width <= 0 || info.Height <= 0))
            {
                info.Error = "video stream has no size";
                return info;
            }
            if (info.Duration <= 0)
            {
                info.Error = "duration unknown";
                return info;
            }
            info.Ok = true;
            return info;
        }

        private static StreamInfo FindOrAdd(List<StreamInfo> streams, int idx)
        {
            for (int i = 0; i < streams.Count; i++)
            {
                if (streams[i].Index == idx) return streams[i];
            }
            StreamInfo s = new StreamInfo();
            s.Index = idx;
            streams.Add(s);
            return s;
        }

        private static string RunFlat(string path)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = AppPaths.FfprobeExe;
                psi.Arguments = YtDlpRunner.FormatArgs(new[]
                {
                    "-v", "error", "-print_format", "flat", "-show_format", "-show_streams", path
                });
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
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            lock (errBuf) { if (errBuf.Length < 4000) errBuf.AppendLine(e.Data); }
                        }
                    };
                    p.BeginErrorReadLine();
                    string so = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); }
                        catch { }
                        return null;
                    }
                    if (p.ExitCode != 0) return null;
                    return so;
                }
            }
            catch
            {
                return null;
            }
        }

        private static int ParseInt(string s, int fallback)
        {
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        private static double ParseDouble(string s)
        {
            double v;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }
    }
}
