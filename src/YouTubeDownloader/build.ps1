$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $src)
$out = Join-Path $root 'dist\YouTubeDownloader'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'csc.exe не найден' }
$manifest = Join-Path $src 'app.manifest'
$outExe = Join-Path $out 'YouTubeDownloader.exe'
$files = Get-ChildItem -LiteralPath $src -Filter '*.cs' | ForEach-Object { $_.FullName }
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /win32manifest:$manifest /out:$outExe /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll @files
if ($LASTEXITCODE -ne 0) { throw "Компиляция завершилась с кодом $LASTEXITCODE" }
Write-Host "OK: $outExe"
