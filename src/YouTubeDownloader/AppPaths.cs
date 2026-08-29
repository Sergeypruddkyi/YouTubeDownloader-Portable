using System;
using System.IO;

namespace YouTubeDownloader
{
    public static class AppPaths
    {
        public static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string YtExe = Path.Combine(BaseDir, "yt.exe");
        public static readonly string DenoExe = Path.Combine(BaseDir, "deno.exe");
        public static readonly string FfmpegExe = Path.Combine(BaseDir, "ffmpeg.exe");
        public static readonly string FfprobeExe = Path.Combine(BaseDir, "ffprobe.exe");
        public static readonly string SettingsPath = Path.Combine(BaseDir, "settings.ini");

        public static string MissingCoreComponent()
        {
            if (!File.Exists(YtExe)) return "yt.exe";
            if (!File.Exists(FfmpegExe)) return "ffmpeg.exe";
            if (!File.Exists(DenoExe)) return "deno.exe";
            return null;
        }

        public static bool FfprobePresent()
        {
            return File.Exists(FfprobeExe);
        }
    }
}
