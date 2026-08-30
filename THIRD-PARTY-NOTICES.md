# Third-Party Notices

Сторонние компоненты, входящие в portable-комплект YouTube Downloader.
Этот файл обязателен к включению в распространяемый релиз вместе с LICENSE.

---

## yt-dlp (yt.exe)

- **Версия:** 2026.08.19
- **Назначение:** загрузка видео, получение информации о роликах, самостоятельное обновление
- **Проект / официальный источник:** https://github.com/yt-dlp/yt-dlp
- **Исходники:** https://github.com/yt-dlp/yt-dlp (тег `2026.08.19`)
- **Лицензия:** The Unlicense (общественное достояние)
- **Copyright / attribution:**

  > Copyright © yt-dlp contributors
  > https://github.com/yt-dlp/yt-dlp — licensed under The Unlicense

- **Примечание:** официальный бинарник `yt-dlp.exe` собран с помощью PyInstaller и
  содержит CPython (Python Software Foundation License, версия 2) и загрузчик PyInstaller
  (GPL-2.0-or-later со специальным исключением PyInstaller, явно разрешающим
  распространение собранных с его помощью бандлов без применения GPL к бандлу).
- **Ссылки на лицензии:**
  - The Unlicense: https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE
  - PyInstaller bootloader exception: https://www.pyinstaller.org/license.html
  - PSF License: https://docs.python.org/3/license.html

---

## Deno (deno.exe)

- **Версия:** 2.6.3 (stable, x86_64-pc-windows-msvc)
- **Назначение:** JavaScript/TypeScript-рантайм (используется yt-dlp как JS-движок для обхода challenge)
- **Проект / официальный источник:** https://github.com/denoland/deno
- **Исходники:** https://github.com/denoland/deno (тег `v2.6.3`)
- **Лицензия:** MIT License
- **Copyright / attribution:**

  > Copyright © Deno contributors & Deno Land Inc.
  > MIT licensed — как указано в ресурсах самого бинарника deno.exe

- **Ссылка на лицензию:** https://github.com/denoland/deno/blob/main/LICENSE.md
- **Примечание:** в состав Deno входят сторонние компоненты (V8, TypeScript, BoringSSL и др.).
  Полный список уведомлений третьих сторон приведён в файле LICENSE.md официального
  дистрибутива Deno: https://github.com/denoland/deno/blob/main/LICENSE.md

---

## FFmpeg (ffmpeg.exe) и FFprobe (ffprobe.exe)

Оба файла — из одного дистрибутива и имеют одинаковую конфигурацию сборки.

- **Версия:** FFmpeg 8.0.1, сборка `8.0.1-essentials_build-www.gyan.dev` (release-essentials), x64, статическая
- **Назначение:** обработка/склейка аудио и видеопотоков, анализ медиафайлов (ffprobe)
- **Проект:** FFmpeg — https://ffmpeg.org
- **Источник бинарников:** сборка Gyan Doshi (gyan.dev) — https://www.gyan.dev/ffmpeg/builds/
  (дистрибутив `ffmpeg-release-essentials`; именно gyan.dev указан в строке версии
  самих бинарников: `8.0.1-essentials_build-www.gyan.dev`)
- **Исходники:** https://ffmpeg.org/download.html (тег `n8.0.1`); конфигурация сборки
  видна в выводе `ffmpeg -version` / `ffprobe -version` и на странице сборок gyan.dev
- **Лицензия:** **GNU GPL v3 или новее (GPL-3.0-or-later)**

  Конкретно для нашей сборки это подтверждается её конфигурацией, видимой в выводе
  `ffmpeg -version` / `ffprobe -version`:

  ```
  --enable-gpl --enable-version3
  ```

  Наличие `--enable-gpl` означает, что в бинарник включены компоненты под GPL
  (в частности libx264, libx265, libxvid), и весь бинарник распространяется
  в режиме GPL v3+.
- **Copyright / attribution:**

  > FFmpeg — Copyright © 2000–2025 the FFmpeg developers
  > ffprobe — Copyright © 2007–2025 the FFmpeg developers
  > Windows-сборка: gyan.dev (Gyan Doshi), essentials build, gcc 15.2.0 (MSYS2)

- **Обязательные условия распространения (GPL-3.0):**
  1. Включить полный текст GNU GPL v3 в дистрибутив.
  2. Сохранить указанные выше copyright/attribution notices.
  3. Предоставить получателям доступ к исходникам: исходный код FFmpeg 8.0.1
     (см. ссылку выше) и сведения о конфигурации сборки (см. вывод `-version`
     и сайт gyan.dev).
- **Ссылка на текст лицензии:** https://www.gnu.org/licenses/gpl-3.0.txt

---

## Сводка

| Компонент | Версия | Лицензия | Источник |
|-----------|--------|----------|----------|
| yt-dlp (yt.exe) | 2026.08.19 | Unlicense (+PSF/PyInstaller в бандле) | https://github.com/yt-dlp/yt-dlp |
| Deno (deno.exe) | 2.6.3 | MIT | https://github.com/denoland/deno |
| FFmpeg (ffmpeg.exe) | 8.0.1 essentials (gyan.dev) | GPL-3.0-or-later | https://ffmpeg.org · https://www.gyan.dev/ffmpeg/builds/ |
| FFprobe (ffprobe.exe) | 8.0.1 essentials (gyan.dev) | GPL-3.0-or-later | https://ffmpeg.org · https://www.gyan.dev/ffmpeg/builds/ |

YouTube Downloader сам по себе распространяется под MIT License (см. LICENSE).
