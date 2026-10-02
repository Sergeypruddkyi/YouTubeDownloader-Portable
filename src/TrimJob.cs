using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace YouTubeDownloader
{
    public static class TrimJob
    {
        public static readonly string VideoCodecArgs = "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p";
        public static readonly string AudioCodecArgs = "-c:a aac -b:a 160k";

        // Shortest span that may be handed to ffmpeg. A trim=start=X:end=Y with Y-X
        // at or below zero is rejected by ffmpeg and aborts the whole job, so such
        // spans are dropped while the ranges are still plain numbers.
        public const double MinSegmentSeconds = 0.05;

        // The one canonical form of "ranges the user wants removed": inside
        // [0, duration], sorted by start, overlaps and neighbours merged, no empty
        // spans. Everything downstream (keep-regions, ffmpeg args) works on this.
        public static List<TrimRegion> NormalizeCuts(IList<TrimRegion> cuts, double duration)
        {
            List<TrimRegion> result = new List<TrimRegion>();
            if (cuts == null || duration <= 0) return result;

            List<TrimRegion> work = new List<TrimRegion>();
            for (int i = 0; i < cuts.Count; i++)
            {
                double s = cuts[i].Start;
                double e = cuts[i].End;
                if (double.IsNaN(s) || double.IsNaN(e)) continue;
                if (s < 0) s = 0;
                if (e > duration) e = duration;
                if (e - s < MinSegmentSeconds) continue;
                work.Add(new TrimRegion(s, e));
            }
            work.Sort(delegate(TrimRegion a, TrimRegion b)
            {
                if (a.Start < b.Start) return -1;
                if (a.Start > b.Start) return 1;
                return 0;
            });

            for (int i = 0; i < work.Count; i++)
            {
                TrimRegion r = work[i];
                if (result.Count > 0 && r.Start <= result[result.Count - 1].End)
                {
                    // TrimRegion is a struct: the merged end has to be written back.
                    TrimRegion last = result[result.Count - 1];
                    if (r.End > last.End) result[result.Count - 1] = new TrimRegion(last.Start, r.End);
                    continue;
                }
                result.Add(r);
            }
            return result;
        }

        // What survives: the complement of the removed ranges inside [0, duration].
        // These are the segments that get physically cut out of the source and then
        // concatenated into the single result file.
        public static List<TrimRegion> ComputeKeepRegions(IList<TrimRegion> cuts, double duration)
        {
            List<TrimRegion> keep = new List<TrimRegion>();
            if (duration <= 0) return keep;
            List<TrimRegion> removed = NormalizeCuts(cuts, duration);
            double pos = 0;
            for (int i = 0; i < removed.Count; i++)
            {
                double s = removed[i].Start;
                double e = removed[i].End;
                if (s - pos >= MinSegmentSeconds) keep.Add(new TrimRegion(pos, s));
                if (e > pos) pos = e;
            }
            if (duration - pos >= MinSegmentSeconds) keep.Add(new TrimRegion(pos, duration));
            return keep;
        }

        // The result never replaces the source and never silently overwrites an
        // earlier result: video.mp4 -> video_trimmed.mp4 -> video_trimmed (2).mp4
        // The container stays MP4, because that is what the encode pipeline writes.
        public static string SuggestTrimmedPath(string sourcePath)
        {
            string dir = Path.GetDirectoryName(sourcePath);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrEmpty(baseName)) baseName = "video";

            string candidate = Path.Combine(dir, baseName + "_trimmed.mp4");
            int n = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(dir, baseName + "_trimmed (" + n + ").mp4");
                n++;
            }
            return candidate;
        }

        public static List<TrimRegion> SanitizeRegions(List<TrimRegion> regions)
        {
            List<TrimRegion> result = new List<TrimRegion>();
            if (regions == null) return result;
            for (int i = 0; i < regions.Count; i++)
            {
                TrimRegion r = regions[i];
                if (r.End - r.Start < 0.001) continue;
                if (result.Count > 0 && r.Start < result[result.Count - 1].End) continue;
                result.Add(r);
            }
            return result;
        }

        public static string BuildFilterComplex(List<TrimRegion> regions, bool hasAudio)
        {
            StringBuilder sb = new StringBuilder();
            List<string> labels = new List<string>();
            for (int i = 0; i < regions.Count; i++)
            {
                TrimRegion r = regions[i];
                sb.Append("[0:v]trim=start=").Append(Fmt(r.Start)).Append(":end=").Append(Fmt(r.End))
                  .Append(",setpts=PTS-STARTPTS[v").Append(i).Append("];");
                if (hasAudio)
                    sb.Append("[0:a]atrim=start=").Append(Fmt(r.Start)).Append(":end=").Append(Fmt(r.End))
                      .Append(",asetpts=PTS-STARTPTS[a").Append(i).Append("];");
                labels.Add("[v" + i + "]");
                if (hasAudio) labels.Add("[a" + i + "]");
            }
            sb.Append(string.Join("", labels.ToArray()))
              .Append("concat=n=").Append(regions.Count).Append(":v=1:a=").Append(hasAudio ? 1 : 0)
              .Append("[v]");
            if (hasAudio) sb.Append("[a]");
            return sb.ToString();
        }

        public static string[] BuildTrimArgs(string input, string output, List<TrimRegion> regions, bool hasAudio)
        {
            List<string> args = new List<string>();
            args.Add("-hide_banner");
            args.Add("-i");
            args.Add(input);
            args.Add("-filter_complex");
            args.Add(BuildFilterComplex(regions, hasAudio));
            args.Add("-map");
            args.Add("[v]");
            if (hasAudio)
            {
                args.Add("-map");
                args.Add("[a]");
            }
            args.Add("-sn");
            args.Add("-dn");
            foreach (string a in VideoCodecArgs.Split(' ')) args.Add(a);
            if (hasAudio)
                foreach (string a in AudioCodecArgs.Split(' ')) args.Add(a);
            args.Add("-movflags");
            args.Add("+faststart");
            args.Add("-progress");
            args.Add("pipe:1");
            args.Add("-nostats");
            args.Add("-y");
            args.Add(output);
            return args.ToArray();
        }

        public static string[] BuildPngArgs(string input, string output, double timeSec)
        {
            return new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-ss", Fmt(timeSec),
                "-i", input,
                "-frames:v", "1",
                "-y", output
            };
        }

        // The Save-frame crop: cut the selected rectangle out of the ALREADY
        // decoded frame (the temp PNG the crop window was shown). W:H:X:Y are
        // SOURCE image pixels - exactly the numbers the crop window returns - so
        // ffmpeg only cuts, it never rescales and never touches the video.
        public static string[] BuildCropPngArgs(string input, string output, Rectangle rect)
        {
            string crop = string.Format(CultureInfo.InvariantCulture, "crop={0}:{1}:{2}:{3}",
                rect.Width, rect.Height, rect.X, rect.Y);
            return new[]
            {
                "-hide_banner", "-loglevel", "error",
                "-i", input,
                "-vf", crop,
                "-frames:v", "1",
                "-update", "1",
                "-y", output
            };
        }

        public static double ExpectedOutputDuration(List<TrimRegion> regions)
        {
            double sum = 0;
            for (int i = 0; i < regions.Count; i++) sum += regions[i].End - regions[i].Start;
            return sum;
        }

        public static double? TryProgressOutTimeUs(string line)
        {
            if (line == null) return null;
            if (!line.StartsWith("out_time_us=", StringComparison.Ordinal)) return null;
            long us;
            if (!long.TryParse(line.Substring(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out us)) return null;
            return us / 1000000.0;
        }

        public static bool IsProgressEnd(string line)
        {
            return line != null && string.Equals(line.Trim(), "progress=end", StringComparison.Ordinal);
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.000000", CultureInfo.InvariantCulture);
        }
    }
}
