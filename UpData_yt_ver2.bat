@echo off
chcp 65001 >nul
title Обновление yt-dlp
cd /d "%~dp0"

echo ===============================
echo   Проверка обновления yt-dlp
echo ===============================
echo.

yt.exe -U

echo.
echo ===============================
echo      Готово
echo ===============================
pause