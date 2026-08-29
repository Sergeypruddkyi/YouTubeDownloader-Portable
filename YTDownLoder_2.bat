@echo off
chcp 65001 >nul
setlocal

set "DOWNLOAD_FOLDER=%~dp0Видео"
if not exist "%DOWNLOAD_FOLDER%" mkdir "%DOWNLOAD_FOLDER%"

:: Получаем URL
for /f "usebackq delims=" %%a in (`powershell -NoProfile -Command "Get-Clipboard"`) do set "URL=%%a"
if "%URL%"=="" set /p URL="Вставьте ссылку вручную: "
if "%URL%"=="" exit /b

echo Работаю с URL: "%URL%"

:: Проверка Deno (как альтернатива QuickJS)
set "JS_PARAM="
if exist "%~dp0deno.exe" (
    echo [OK] Использую движок Deno для ускорения.
    set "JS_PARAM=--js-runtime deno"
) else (
    echo [!] Deno не найден, использую стандартный метод.
)

:: Проверка ffmpeg
set "FFMPEG_CMD=ffmpeg"
if exist "%~dp0ffmpeg.exe" set "FFMPEG_CMD=%~dp0ffmpeg.exe"

:: ЗАПУСК
yt.exe %JS_PARAM% -f "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best" --merge-output-format mp4 --ffmpeg-location "%FFMPEG_CMD%" -o "%DOWNLOAD_FOLDER%\%%(title)s.%%(ext)s" "%URL%"

echo.
echo Скачивание завершено!
pause