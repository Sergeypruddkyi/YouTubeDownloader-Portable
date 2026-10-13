# Prune-LibVlc.ps1 — minimize the bundled LibVLC runtime for the Cutter.
#
# The app ships VideoLAN.LibVLC.Windows 3.0.24, whose targets copy ALL three
# architectures (win-x64, win-x86, win-arm64) plus the full VLC plugin tree.
# The Cutter only ever plays local MP4 files (H.264 / AV1 via dav1d) into a
# VideoView, so almost the whole tree is dead weight.
#
# The keep-list below is the audit result (see .agent/libvlc-minimize/audit.md):
# every kept plugin was either observed in the actual libvlc module trail
# (spike/LibVlcSpike.PluginAudit, --verbose=2, H.264 and AV1 runs) or is cheap
# insurance for a documented fallback path. Everything NOT in the keep-list
# inside libvlc\<arch>\plugins is deleted (keep-list, fail-closed).
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File Prune-LibVlc.ps1 [-LibVlcDir <path to libvlc folder>]
# Default -LibVlcDir: <repo>\dist\YouTubeDownloader\libvlc
#
# Idempotent: safe to run repeatedly (publish runs it after every publish).

param(
    [string]$LibVlcDir = ""
)

$ErrorActionPreference = "Stop"

if ($LibVlcDir -eq "") {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $LibVlcDir = Join-Path $repoRoot "dist\YouTubeDownloader\libvlc"
}

if (-not (Test-Path (Join-Path $LibVlcDir "win-x64\libvlccore.dll"))) {
    Write-Host "Prune-LibVlc: '$LibVlcDir' does not look like a bundled libvlc tree - nothing to do."
    exit 0
}

function Get-TreeStats([string]$path) {
    if (-not (Test-Path $path)) { return @{ MB = 0; Files = 0; Dirs = 0 } }
    $files = Get-ChildItem -LiteralPath $path -Recurse -File
    $bytes = ($files | Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes) { $bytes = 0 }
    @{
        MB    = [math]::Round($bytes / 1MB, 1)
        Files = $files.Count
        Dirs  = (Get-ChildItem -LiteralPath $path -Recurse -Directory).Count
    }
}

$before = Get-TreeStats $LibVlcDir
Write-Host ("Prune-LibVlc: before  = {0} MB, {1} files, {2} dirs" -f $before.MB, $before.Files, $before.Dirs)

# 1. Whole architectures we never publish or load (process is win-x64,
#    LibVLCSharp Core.Initialize probes libvlc\win-x64 only).
foreach ($arch in @("win-x86", "win-arm64")) {
    $p = Join-Path $LibVlcDir $arch
    if (Test-Path $p) {
        Remove-Item -LiteralPath $p -Recurse -Force
        Write-Host "Prune-LibVlc: removed architecture $arch"
    }
}

$x64 = Join-Path $LibVlcDir "win-x64"

# 2. Developer import libraries: never loaded by the runtime.
Get-ChildItem -LiteralPath $x64 -File -Filter *.lib | ForEach-Object {
    Write-Host "Prune-LibVlc: removed $($_.Name)"
    $_.Delete()
}

# 3. Data folders of removed features.
foreach ($dir in @("lua", "hrtfs")) {
    $p = Join-Path $x64 $dir
    if (Test-Path $p) {
        Remove-Item -LiteralPath $p -Recurse -Force
        Write-Host "Prune-LibVlc: removed data folder $dir"
    }
}

# 4. Plugin keep-list (relative to win-x64). Everything else in plugins\ is removed.
$keep = @(
    # access: opening the local file
    "access\libfilesystem_plugin.dll",

    # stream filters auto-inserted by the core for local reads
    "stream_filter\libcache_read_plugin.dll",
    "stream_filter\librecord_plugin.dll",

    # MP4 container (app contract: downloads are merged to mp4) + mkv/Matroska:
    # WebM files ARE demuxed by the merged avformat inside avcodec (video plays),
    # but avformat returns EOF for a seek to the EXACT end of a webm, so the
    # fast-drag-to-end preview froze on the old frame (user-file acceptance
    # failure 2026-10-02). The native mkv demuxer delivers the final frame.
    "demux\libmp4_plugin.dll",
    "demux\libmkv_plugin.dll",

    # decoders: avcodec (H264/VP9/MPEG4 video + ALL audio it knows), dav1d (AV1 pin),
    # d3d11va (hw accel observed in the H264 trail)
    "codec\libavcodec_plugin.dll",
    "codec\libdav1d_plugin.dll",
    "codec\libd3d11va_plugin.dll",
    # opus: the ONLY Opus decoder in this build. The bundled FFmpeg inside
    # avcodec has dedicated-module codecs disabled ("avcodec: codec not found
    # (Opus Audio)"), so WebM (VP9+Opus) previews were silent without it.
    "codec\libopus_plugin.dll",

    # video output stack: D3D11 + embed window + inhibit (all in the trail),
    # D3D9 as the documented fallback if D3D11 cannot initialize
    "video_output\libdirect3d11_plugin.dll",
    "video_output\libdirect3d9_plugin.dll",
    "video_output\libdrawable_plugin.dll",
    "video_output\libwinhibit_plugin.dll",
    "d3d11\libdirect3d11_filters_plugin.dll",
    "d3d9\libdirect3d9_filters_plugin.dll",

    # video converter chain (kept whole: cryptic failures if a converter is
    # missing; swscale/chain/yuvp are in the actual trail)
    "video_chroma",

    # rotation metadata on phone videos (cheap insurance)
    "video_filter\libtransform_plugin.dll",

    # SPU text renderer observed in the trail
    "text_renderer\libfreetype_plugin.dll",

    # metadata reader observed in the trail
    "meta_engine\libtaglib_plugin.dll",

    # audio output + the aout chain pieces observed in the trail or needed for
    # non-stereo / non-standard-rate audio
    "audio_output\libmmdevice_plugin.dll",
    "audio_output\libwasapi_plugin.dll",
    "audio_filter\libscaletempo_plugin.dll",
    "audio_filter\libugly_resampler_plugin.dll",
    "audio_filter\libspeex_resampler_plugin.dll",
    "audio_filter\libaudio_format_plugin.dll",
    "audio_filter\libtrivial_channel_mixer_plugin.dll",
    "audio_filter\libsimple_channel_mixer_plugin.dll",
    "audio_mixer\libfloat_mixer_plugin.dll",
    "audio_mixer\libinteger_mixer_plugin.dll",

    # used by libvlc itself at startup (observed: keystore "memory",
    # logger "console")
    "keystore\libmemory_keystore_plugin.dll",
    "logger\libconsole_logger_plugin.dll"
)

$pluginsRoot = Join-Path $x64 "plugins"
$keepSet = @{}
foreach ($k in $keep) {
    if ($k -eq "video_chroma") {
        $chromaDir = Join-Path $pluginsRoot "video_chroma"
        if (Test-Path $chromaDir) {
            Get-ChildItem -LiteralPath $chromaDir -File | ForEach-Object {
                $keepSet["video_chroma\" + $_.Name] = $true
            }
        }
        continue
    }
    $keepSet[$k] = $true
}

if (Test-Path $pluginsRoot) {
    Get-ChildItem -LiteralPath $pluginsRoot -Directory | ForEach-Object {
        $cat = $_.Name
        Get-ChildItem -LiteralPath $_.FullName -File | ForEach-Object {
            $rel = "$cat\" + $_.Name
            if (-not $keepSet.ContainsKey($rel)) {
                Write-Host "Prune-LibVlc: removed plugin $rel"
                $_.Delete()
            }
        }
    }
    # drop category folders left empty
    Get-ChildItem -LiteralPath $pluginsRoot -Directory | ForEach-Object {
        if ((Get-ChildItem -LiteralPath $_.FullName -Force | Measure-Object).Count -eq 0) {
            $_.Delete()
        }
    }
    # plugin cache refers to the OLD file set; force a clean rescan
    $cache = Join-Path $pluginsRoot "plugins.dat"
    if (Test-Path $cache) {
        Remove-Item -LiteralPath $cache -Force
        Write-Host "Prune-LibVlc: removed stale plugin cache plugins.dat"
    }
}

$after = Get-TreeStats $LibVlcDir
Write-Host ("Prune-LibVlc: after   = {0} MB, {1} files, {2} dirs" -f $after.MB, $after.Files, $after.Dirs)
Write-Host ("Prune-LibVlc: saved   = {0} MB" -f [math]::Round($before.MB - $after.MB, 1))
exit 0
