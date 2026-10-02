using System;
using System.Collections.Generic;
using System.Globalization;

namespace YouTubeDownloader
{
    public enum Lang
    {
        En,
        Ru
    }

    public enum Msg
    {
        LabelUrl,
        LabelFolder,
        LabelQuality,
        QualityBest,
        Quality1080,
        Quality720,
        Quality480,
        Quality360,
        QualityAudio,
        BtnPaste,
        BtnBrowse,
        BtnCheckUpdate,
        BtnDownload,
        BtnCancel,
        BtnOpenFolder,
        BtnShowLog,
        BtnHideLog,
        FileWord,

        TitleWaiting,
        TitleFetching,
        TitleIs,
        TitleFailed,
        NotYouTube,

        YtPending,
        YtReady,
        YtFailed,
        BundleIncomplete,
        StatusMissingCannotDownload,
        NoFfprobe,

        StatusReady,
        StatusNoFolder,
        StatusNoUrlInClipboard,
        StatusCreateFolderError,
        StatusProcessStartError,
        StatusCanceled,
        StatusDone,
        StatusStopping,

        InfoPreparing,
        InfoDownloadingFile,
        InfoMergingFile,
        InfoExtractingFile,
        InfoEstimatedSize,

        ProgressComplete,
        ProgressAlready,

        PhaseDownloading,
        PhaseMerging,
        PhaseFetchingInfo,

        MsgNeedUrl,
        MsgBadUrl,
        MsgNoFolder,
        MsgBundleBroken,
        MsgMissingPlain,
        FolderDlgDesc,

        ErrorUnknownYt,
        ErrorNoDetail,
        ErrorWithDetail,

        PromptVersionProblemTitle,
        PromptVersionProblemMsg,
        BtnYesCheck,
        BtnNo,

        StatusCheckingUpdate,
        MsgLocalVersionFailed,
        CaptionUpdateCheck,
        StatusVersionUndetected,
        MsgCheckNetworkFail,
        BtnUseCurrent,
        BtnTryLater,
        StatusUsingCurrent,
        StatusCheckPostponed,
        MsgUpToDate,
        StatusUpToDate,
        CaptionUpdateAvailable,
        MsgUpdateAvailable,
        BtnUpdate,
        StatusUpdatePostponed,
        StatusUpdating,
        StatusYtUpdated,
        StatusRetrying,
        MsgUpdateFailed,
        CaptionYtUpdate,
        StatusUpdateNotPerformed,

        MsgExitDuringDownload,
        MsgExitDuringUpdate,

        HintNoSpace,
        HintNoWriteAccess,
        HintDestCreateFail,
        HintFileNameTooLong,
        HintUnsupportedUrl,
        HintExtractFail,
        HintNsigFail,
        HintFormatUnavailable,
        HintBotCheck,
        HintHttp403,
        HintNetwork,
        HintServer5xx,
        HintPrivateVideo,
        HintVideoUnavailable,
        HintMembersOnly,
        HintAgeRestricted,
        HintGeoBlocked,
        HintQualityUnavailable,

        BtnTrimFrame,
        EdTitle,
        EdOpenFile,
        EdOpenImage,
        EdClose,
        EdSaveFrame,
        EdCropTitle,
        EdCropSave,
        EdSaveVideo,
        EdOpenPlayer,
        EdStepBack,
        EdStepFwd,
        EdUndo,
        EdRedo,
        EdCut,
        EdCancel,
        EdRangeApplied,
        EdRangeStartSet,
        EdCutBlockedInMark,
        EdCutBlockedOverlap,
        EdRangeCanceled,
        EdRangeUndone,
        EdZoomFit,
        EdDeleteCut,
        EdClearCuts,
        EdMontageHeader,
        EdFramesHeader,
        EdRangesHeader,
        EdEditHint,
        EdInfoLine,
        EdAudioOnly,
        EdStatusReady,
        EdStatusOpening,
        EdStatusStarting,
        EdStatusOverview,
        EdStatusDecoding,
        EdStatusTrimming,
        EdStatusTrimDone,
        EdStatusTrimCanceled,
        EdStatusFrameSaved,
        EdStatusImageOpened,
        EdPosition,
        EdNothingToSave,
        EdErrorDecode,
        EdErrorTrim,
        EdErrorMove,
        EdErrorSave,
        EdErrorProbe,
        EdMsgExitBusy,
        EdNoFfprobe,
        EdErrorSameFile,
        EdNothingLeft,
        EdStatusTrimSaved
    }

    public sealed class Sub
    {
        public readonly Msg Key;
        public readonly object[] Args;

        public Sub(Msg key, params object[] args)
        {
            Key = key;
            Args = args;
        }
    }

    public static class L10n
    {
        private static readonly Dictionary<Msg, string> En = new Dictionary<Msg, string>
        {
            { Msg.LabelUrl, "URL:" },
            { Msg.LabelFolder, "Folder:" },
            { Msg.LabelQuality, "Quality:" },
            { Msg.QualityBest, "Best available" },
            { Msg.Quality1080, "1080p" },
            { Msg.Quality720, "720p" },
            { Msg.Quality480, "480p" },
            { Msg.Quality360, "360p" },
            { Msg.QualityAudio, "Audio only" },
            { Msg.BtnPaste, "Paste" },
            { Msg.BtnBrowse, "Browse…" },
            { Msg.BtnCheckUpdate, "Check yt-dlp update" },
            { Msg.BtnDownload, "DOWNLOAD" },
            { Msg.BtnCancel, "Cancel" },
            { Msg.BtnOpenFolder, "Open folder" },
            { Msg.BtnShowLog, "Show log" },
            { Msg.BtnHideLog, "Hide log" },
            { Msg.FileWord, "file" },

            { Msg.TitleWaiting, "Waiting for a link — copy a YouTube URL or enter it manually" },
            { Msg.TitleFetching, "Fetching title…" },
            { Msg.TitleIs, "Title: {0}" },
            { Msg.TitleFailed, "Could not get the title (non-critical for download)" },
            { Msg.NotYouTube, "Doesn't look like a YouTube link" },

            { Msg.YtPending, "yt-dlp: detecting version…" },
            { Msg.YtReady, "yt-dlp: {0} — ready" },
            { Msg.YtFailed, "yt-dlp: failed to detect version" },
            { Msg.BundleIncomplete, "Bundle incomplete: {0} not found" },
            { Msg.StatusMissingCannotDownload, "{0} is missing in the application folder. Download is not possible." },
            { Msg.NoFfprobe, "ffprobe.exe not found (non-critical)" },

            { Msg.StatusReady, "Ready." },
            { Msg.StatusNoFolder, "No destination folder selected — press «Browse…» before the first download." },
            { Msg.StatusNoUrlInClipboard, "No YouTube link found in the clipboard." },
            { Msg.StatusCreateFolderError, "Error: failed to create folder — {0}" },
            { Msg.StatusProcessStartError, "Error: failed to start yt.exe — {0}" },
            { Msg.StatusCanceled, "Download canceled by the user." },
            { Msg.StatusDone, "Done: {0}" },
            { Msg.StatusStopping, "Stopping…" },

            { Msg.InfoPreparing, "Preparing…" },
            { Msg.InfoDownloadingFile, "Downloading: {0}" },
            { Msg.InfoMergingFile, "Merging: {0}" },
            { Msg.InfoExtractingFile, "Extracting audio: {0}" },
            { Msg.InfoEstimatedSize, "Estimated size: ~{0}" },

            { Msg.ProgressComplete, "✓ DOWNLOAD COMPLETE" },
            { Msg.ProgressAlready, "✓ FILE WAS ALREADY DOWNLOADED" },

            { Msg.PhaseDownloading, "Downloading…" },
            { Msg.PhaseMerging, "Merging/processing (FFmpeg)…" },
            { Msg.PhaseFetchingInfo, "Fetching video data…" },

            { Msg.MsgNeedUrl, "Enter or paste a YouTube link." },
            { Msg.MsgBadUrl, "The field does not contain a recognized YouTube link. Check the link." },
            { Msg.MsgNoFolder, "No destination folder selected." },
            { Msg.MsgBundleBroken, "{0} is missing in the application folder. The bundle is damaged." },
            { Msg.MsgMissingPlain, "{0} is missing in the application folder." },
            { Msg.FolderDlgDesc, "Select the folder for downloading videos" },

            { Msg.ErrorUnknownYt, "Unknown yt-dlp error (code {0})." },
            { Msg.ErrorNoDetail, "Error: {0}" },
            { Msg.ErrorWithDetail, "Error: {0} [{1}]" },

            { Msg.PromptVersionProblemTitle, "Possible version problem" },
            { Msg.PromptVersionProblemMsg, "The download finished with an error.\n\n{0}\n\nThe cause is flagged as a \"possible yt-dlp version problem\" — it cannot be determined exactly.\n\nCheck for a yt-dlp update?" },
            { Msg.BtnYesCheck, "Yes, check" },
            { Msg.BtnNo, "No" },

            { Msg.StatusCheckingUpdate, "Checking yt-dlp update…" },
            { Msg.MsgLocalVersionFailed, "Failed to detect the version of the installed yt.exe." },
            { Msg.CaptionUpdateCheck, "Update check" },
            { Msg.StatusVersionUndetected, "Error: yt-dlp version not detected." },
            { Msg.MsgCheckNetworkFail, "Failed to check for a yt-dlp update.\nReason: {0}\n\nThis does not mean yt-dlp is outdated." },
            { Msg.BtnUseCurrent, "Use current version" },
            { Msg.BtnTryLater, "Try later" },
            { Msg.StatusUsingCurrent, "Using current yt-dlp version {0}." },
            { Msg.StatusCheckPostponed, "Update check postponed." },
            { Msg.MsgUpToDate, "The installed yt-dlp is up to date ({0}).\nThe error is most likely not version-related." },
            { Msg.StatusUpToDate, "yt-dlp {0} — up to date." },
            { Msg.CaptionUpdateAvailable, "yt-dlp update available" },
            { Msg.MsgUpdateAvailable, "Installed version: {0}\nAvailable version: {1}\n\nUpdate now?" },
            { Msg.BtnUpdate, "Update" },
            { Msg.StatusUpdatePostponed, "Update postponed." },
            { Msg.StatusUpdating, "Updating yt-dlp {0}…" },
            { Msg.StatusYtUpdated, "yt-dlp updated: {0} → {1}." },
            { Msg.StatusRetrying, "Retrying the download after the update…" },
            { Msg.MsgUpdateFailed, "The update failed. See the log for details." },
            { Msg.CaptionYtUpdate, "yt-dlp update" },
            { Msg.StatusUpdateNotPerformed, "Error: yt-dlp update was not performed (version {0} remains)." },

            { Msg.MsgExitDuringDownload, "A download is in progress. Interrupt and exit?" },
            { Msg.MsgExitDuringUpdate, "A yt-dlp update is in progress. Interrupt and exit?" },

            { Msg.HintNoSpace, "No space left on the disk. Free up space or choose another folder." },
            { Msg.HintNoWriteAccess, "No write permission for the selected folder. Choose another folder or check permissions." },
            { Msg.HintDestCreateFail, "Failed to create or open the destination folder. Check the path." },
            { Msg.HintFileNameTooLong, "The file name is too long. Choose a folder with a shorter path." },
            { Msg.HintUnsupportedUrl, "The link was not recognized as supported. Make sure a YouTube link is copied." },
            { Msg.HintExtractFail, "YouTube changed the page/data structure. Possible yt-dlp version problem — the exact cause cannot be determined." },
            { Msg.HintNsigFail, "Failed to decipher the signature (nsig). Possible yt-dlp version problem — the exact cause cannot be determined." },
            { Msg.HintFormatUnavailable, "The requested format is not available. Possible yt-dlp version problem — the exact cause cannot be determined." },
            { Msg.HintBotCheck, "YouTube requires a \"not a robot\" confirmation. Possible yt-dlp version problem — an update may help but is not guaranteed." },
            { Msg.HintHttp403, "The request was rejected (403). Possible yt-dlp version problem — the exact cause cannot be determined." },
            { Msg.HintNetwork, "Network or YouTube access problem. Check the internet connection and try later. A yt-dlp update is not required." },
            { Msg.HintServer5xx, "The YouTube server is temporarily unavailable (5xx error). Try later. A yt-dlp update is not required." },
            { Msg.HintPrivateVideo, "The video is private and cannot be downloaded." },
            { Msg.HintVideoUnavailable, "The video is unavailable or removed." },
            { Msg.HintMembersOnly, "The video is available to channel members only." },
            { Msg.HintAgeRestricted, "Age restriction: signing in to an account is required." },
            { Msg.HintGeoBlocked, "The video is blocked in your region." },
            { Msg.HintQualityUnavailable, "No video at or below the selected quality is available for this video. Choose «Best available» or set a higher quality limit." },

            { Msg.BtnTrimFrame, "Trim / Frame" },
            { Msg.EdTitle, "Video Editor — {0}" },
            { Msg.EdOpenFile, "Open Video…" },
            { Msg.EdOpenImage, "Open Image…" },
            { Msg.EdClose, "Close" },
            { Msg.EdSaveFrame, "Save frame (PNG)" },
            { Msg.EdCropTitle, "Crop frame" },
            { Msg.EdCropSave, "Save" },
            { Msg.EdSaveVideo, "Save video (trim)" },
            { Msg.EdOpenPlayer, "Open in player" },
            { Msg.EdStepBack, "Prev frame" },
            { Msg.EdStepFwd, "Next frame" },
            { Msg.EdUndo, "Undo" },
            { Msg.EdRedo, "Redo" },
            { Msg.EdCut, "Cut" },
            { Msg.EdCancel, "Cancel" },
            { Msg.EdRangeApplied, "Marked: {0} · total: {1}" },
            { Msg.EdRangeStartSet, "Start set · move playhead, then press again for End" },
            { Msg.EdCutBlockedInMark, "Cut not allowed: playhead is inside a marked range — move it to free space" },
            { Msg.EdCutBlockedOverlap, "Cut not allowed: the range would cross a marked range — move the playhead and press Cut again" },
            { Msg.EdRangeCanceled, "Selection canceled" },
            { Msg.EdRangeUndone, "Undone: last range removed · marked: {0} · total: {1}" },
            { Msg.EdZoomFit, "Fit" },
            { Msg.EdDeleteCut, "Delete selected range" },
            { Msg.EdClearCuts, "Remove all ranges" },
            { Msg.EdMontageHeader, "Editing: drag on the strip to select the part to remove — red will be deleted, green will remain" },
            { Msg.EdFramesHeader, "Frames: find the exact frame here, then press «Save frame (PNG)»" },
            { Msg.EdRangesHeader, "Ranges to remove:" },
            { Msg.EdEditHint, "Red = will be removed, green = will remain. Remaining parts are automatically joined into one video. Drag a range edge to fine-tune it. Del = delete selected range, Ctrl+Z / Ctrl+Y = undo / redo." },
            { Msg.EdInfoLine, "{0} · {1} · {2} · ranges: {3} · to remove: {4} · result: {5} — joined automatically" },
            { Msg.EdAudioOnly, "This file contains audio only, so trimming and frame extraction are not available." },
            { Msg.EdStatusReady, "Ready" },
            { Msg.EdStatusOpening, "Opening…" },
            { Msg.EdStatusStarting, "Starting video engine…" },
            { Msg.EdStatusOverview, "Preparing overview…" },
            { Msg.EdStatusDecoding, "Decoding frame…" },
            { Msg.EdStatusTrimming, "Trimming…" },
            { Msg.EdStatusTrimDone, "✓ Saved" },
            { Msg.EdStatusTrimCanceled, "Trim canceled, original file is untouched." },
            { Msg.EdStatusFrameSaved, "✓ Frame saved" },
            { Msg.EdStatusImageOpened, "✓ Image opened: {0}" },
            { Msg.EdPosition, "Position:" },
            { Msg.EdNothingToSave, "Nothing to save: no ranges are marked. Drag on the top strip to select the part you do not need." },
            { Msg.EdErrorDecode, "Failed to decode the file:" },
            { Msg.EdErrorTrim, "Trim failed." },
            { Msg.EdErrorMove, "Failed to save the output file." },
            { Msg.EdErrorSave, "Failed to save the frame." },
            { Msg.EdErrorProbe, "Could not open this file:" },
            { Msg.EdMsgExitBusy, "A trim is in progress. Closing this window cancels it." },
            { Msg.EdNoFfprobe, "ffprobe.exe not found in the application folder — editing is not available." },
            { Msg.EdErrorSameFile, "Cannot overwrite the original input file. Choose a different file name or folder." },
            { Msg.EdNothingLeft, "Nothing to save: the marked ranges cover the whole video, so no part would remain." },
            { Msg.EdStatusTrimSaved, "Video saved successfully: {0}" }
        };

        private static readonly Dictionary<Msg, string> Ru = new Dictionary<Msg, string>
        {
            { Msg.LabelUrl, "Ссылка:" },
            { Msg.LabelFolder, "Папка:" },
            { Msg.LabelQuality, "Качество:" },
            { Msg.QualityBest, "Лучшее доступное" },
            { Msg.Quality1080, "1080p" },
            { Msg.Quality720, "720p" },
            { Msg.Quality480, "480p" },
            { Msg.Quality360, "360p" },
            { Msg.QualityAudio, "Только аудио" },
            { Msg.BtnPaste, "Вставить" },
            { Msg.BtnBrowse, "Обзор…" },
            { Msg.BtnCheckUpdate, "Проверить обновление yt-dlp" },
            { Msg.BtnDownload, "СКАЧАТЬ" },
            { Msg.BtnCancel, "Отмена" },
            { Msg.BtnOpenFolder, "Открыть папку" },
            { Msg.BtnShowLog, "Показать лог" },
            { Msg.BtnHideLog, "Скрыть лог" },
            { Msg.FileWord, "файл" },

            { Msg.TitleWaiting, "Ожидание ссылки — скопируйте URL YouTube или введите вручную" },
            { Msg.TitleFetching, "Получение названия…" },
            { Msg.TitleIs, "Название: {0}" },
            { Msg.TitleFailed, "Название получить не удалось (не критично для скачивания)" },
            { Msg.NotYouTube, "Не похоже на YouTube-ссылку" },

            { Msg.YtPending, "yt-dlp: определение версии…" },
            { Msg.YtReady, "yt-dlp: {0} — готов" },
            { Msg.YtFailed, "yt-dlp: не удалось определить версию" },
            { Msg.BundleIncomplete, "Комплект неполный: не найден {0}" },
            { Msg.StatusMissingCannotDownload, "В папке приложения отсутствует {0}. Скачать невозможно." },
            { Msg.NoFfprobe, "ffprobe.exe не найден (не критично)" },

            { Msg.StatusReady, "Готово." },
            { Msg.StatusNoFolder, "Папка назначения не выбрана — нажмите «Обзор…» перед первым скачиванием." },
            { Msg.StatusNoUrlInClipboard, "В буфере обмена не найдена YouTube-ссылка." },
            { Msg.StatusCreateFolderError, "Ошибка: не удалось создать папку — {0}" },
            { Msg.StatusProcessStartError, "Ошибка: не удалось запустить yt.exe — {0}" },
            { Msg.StatusCanceled, "Скачивание отменено пользователем." },
            { Msg.StatusDone, "Готово: {0}" },
            { Msg.StatusStopping, "Остановка…" },

            { Msg.InfoPreparing, "Подготовка…" },
            { Msg.InfoDownloadingFile, "Скачивание: {0}" },
            { Msg.InfoMergingFile, "Склейка: {0}" },
            { Msg.InfoExtractingFile, "Извлечение аудио: {0}" },
            { Msg.InfoEstimatedSize, "Оценочный размер: ~{0}" },

            { Msg.ProgressComplete, "✓ СКАЧИВАНИЕ ЗАВЕРШЕНО" },
            { Msg.ProgressAlready, "✓ ФАЙЛ УЖЕ БЫЛ СКАЧАН" },

            { Msg.PhaseDownloading, "Скачивание…" },
            { Msg.PhaseMerging, "Склейка/обработка (FFmpeg)…" },
            { Msg.PhaseFetchingInfo, "Получение данных о видео…" },

            { Msg.MsgNeedUrl, "Введите или скопируйте ссылку YouTube." },
            { Msg.MsgBadUrl, "Поле не содержит распознанной YouTube-ссылки. Проверьте ссылку." },
            { Msg.MsgNoFolder, "Не выбрана папка назначения." },
            { Msg.MsgBundleBroken, "В папке приложения отсутствует {0}. Комплект повреждён." },
            { Msg.MsgMissingPlain, "В папке приложения отсутствует {0}." },
            { Msg.FolderDlgDesc, "Выберите папку для скачивания видео" },

            { Msg.ErrorUnknownYt, "Неизвестная ошибка yt-dlp (код {0})." },
            { Msg.ErrorNoDetail, "Ошибка: {0}" },
            { Msg.ErrorWithDetail, "Ошибка: {0} [{1}]" },

            { Msg.PromptVersionProblemTitle, "Возможная проблема версии" },
            { Msg.PromptVersionProblemMsg, "Скачивание завершилось ошибкой.\n\n{0}\n\nПричина обозначена как «возможная проблема версии yt-dlp» — точно установить её нельзя.\n\nПроверить наличие обновления yt-dlp?" },
            { Msg.BtnYesCheck, "Да, проверить" },
            { Msg.BtnNo, "Нет" },

            { Msg.StatusCheckingUpdate, "Проверка обновления yt-dlp…" },
            { Msg.MsgLocalVersionFailed, "Не удалось определить версию установленного yt.exe." },
            { Msg.CaptionUpdateCheck, "Проверка обновления" },
            { Msg.StatusVersionUndetected, "Ошибка: версия yt-dlp не определена." },
            { Msg.MsgCheckNetworkFail, "Не удалось проверить обновление yt-dlp.\nПричина: {0}\n\nЭто не означает, что yt-dlp устарел." },
            { Msg.BtnUseCurrent, "Использовать текущую версию" },
            { Msg.BtnTryLater, "Попробовать позже" },
            { Msg.StatusUsingCurrent, "Используется текущая версия yt-dlp {0}." },
            { Msg.StatusCheckPostponed, "Проверка обновления отложена." },
            { Msg.MsgUpToDate, "Установлена актуальная версия yt-dlp ({0}).\nОшибка, скорее всего, не связана с версией." },
            { Msg.StatusUpToDate, "yt-dlp {0} — актуален." },
            { Msg.CaptionUpdateAvailable, "Доступно обновление yt-dlp" },
            { Msg.MsgUpdateAvailable, "Установлена версия: {0}\nДоступна версия: {1}\n\nОбновить сейчас?" },
            { Msg.BtnUpdate, "Обновить" },
            { Msg.StatusUpdatePostponed, "Обновление отложено." },
            { Msg.StatusUpdating, "Обновление yt-dlp {0}…" },
            { Msg.StatusYtUpdated, "yt-dlp обновлён: {0} → {1}." },
            { Msg.StatusRetrying, "Повторная попытка скачивания после обновления…" },
            { Msg.MsgUpdateFailed, "Обновление не удалось. Подробности — в логе." },
            { Msg.CaptionYtUpdate, "Обновление yt-dlp" },
            { Msg.StatusUpdateNotPerformed, "Ошибка: обновление yt-dlp не выполнено (осталась версия {0})." },

            { Msg.MsgExitDuringDownload, "Идёт скачивание. Прервать и выйти?" },
            { Msg.MsgExitDuringUpdate, "Идёт обновление yt-dlp. Прервать и выйти?" },

            { Msg.HintNoSpace, "На диске закончилось место. Освободите место или выберите другую папку." },
            { Msg.HintNoWriteAccess, "Нет прав на запись в выбранную папку. Выберите другую папку или проверьте права." },
            { Msg.HintDestCreateFail, "Не удалось создать или открыть папку назначения. Проверьте путь." },
            { Msg.HintFileNameTooLong, "Имя файла слишком длинное. Выберите папку с более коротким путём." },
            { Msg.HintUnsupportedUrl, "Ссылка не распознана как поддерживаемая. Убедитесь, что скопирована ссылка YouTube." },
            { Msg.HintExtractFail, "YouTube изменил страницу/структуру данных. Возможная проблема версии yt-dlp — точно установить причину нельзя." },
            { Msg.HintNsigFail, "Не удалось расшифровать подпись (nsig). Возможная проблема версии yt-dlp — точно установить причину нельзя." },
            { Msg.HintFormatUnavailable, "Требуемый формат недоступен. Возможная проблема версии yt-dlp — точно установить причину нельзя." },
            { Msg.HintBotCheck, "YouTube требует подтверждения «не робот». Возможная проблема версии yt-dlp — обновление может помочь, но не гарантировано." },
            { Msg.HintHttp403, "Запрос отклонён (403). Возможная проблема версии yt-dlp — точно установить причину нельзя." },
            { Msg.HintNetwork, "Проблема с сетью или доступом к YouTube. Проверьте интернет-соединение и попробуйте позже. Обновление yt-dlp не требуется." },
            { Msg.HintServer5xx, "Сервер YouTube временно недоступен (ошибка 5xx). Попробуйте позже. Обновление yt-dlp не требуется." },
            { Msg.HintPrivateVideo, "Видео приватное — скачать его нельзя." },
            { Msg.HintVideoUnavailable, "Видео недоступно или удалено." },
            { Msg.HintMembersOnly, "Видео доступно только участникам канала." },
            { Msg.HintAgeRestricted, "Возрастное ограничение: требуется вход в аккаунт." },
            { Msg.HintGeoBlocked, "Видео заблокировано в вашем регионе." },
            { Msg.HintQualityUnavailable, "Для этого ролика нет видео не выше выбранного качества. Выберите «Лучшее доступное» или увеличьте лимит качества." },

            { Msg.BtnTrimFrame, "Обрезка / Кадр" },
            { Msg.EdTitle, "Редактор видео — {0}" },
            { Msg.EdOpenFile, "Открыть видео…" },
            { Msg.EdOpenImage, "Открыть изображение…" },
            { Msg.EdClose, "Закрыть" },
            { Msg.EdSaveFrame, "Сохранить кадр (PNG)" },
            { Msg.EdCropTitle, "Обрезка кадра" },
            { Msg.EdCropSave, "Сохранить" },
            { Msg.EdSaveVideo, "Сохранить видео (обрезка)" },
            { Msg.EdOpenPlayer, "Открыть в плеере" },
            { Msg.EdStepBack, "Пред. кадр" },
            { Msg.EdStepFwd, "След. кадр" },
            { Msg.EdUndo, "Отменить" },
            { Msg.EdRedo, "Вернуть" },
            { Msg.EdCut, "Вырезать" },
            { Msg.EdCancel, "Отмена" },
            { Msg.EdRangeApplied, "Помечено: {0} · всего: {1}" },
            { Msg.EdRangeStartSet, "Начало задано · переместите playhead и нажмите снова для конца" },
            { Msg.EdCutBlockedInMark, "Вырезать нельзя: playhead внутри помеченного диапазона — переместите его в свободную область" },
            { Msg.EdCutBlockedOverlap, "Вырезать нельзя: диапазон пересёк бы помеченный — переместите playhead и нажмите «Вырезать» снова" },
            { Msg.EdRangeCanceled, "Выделение снято" },
            { Msg.EdRangeUndone, "Отменено: последний диапазон удалён · помечено: {0} · всего: {1}" },
            { Msg.EdZoomFit, "Весь клип" },
            { Msg.EdDeleteCut, "Удалить выбранный диапазон" },
            { Msg.EdClearCuts, "Удалить все диапазоны" },
            { Msg.EdMontageHeader, "Монтаж: потяните мышью по шкале, чтобы выделить фрагмент для удаления — красное удалится, зелёное останется" },
            { Msg.EdFramesHeader, "Кадры: найдите здесь точный кадр и нажмите «Сохранить кадр (PNG)»" },
            { Msg.EdRangesHeader, "Диапазоны удаления:" },
            { Msg.EdEditHint, "Красное — будет удалено, зелёное — останется. Оставшиеся части автоматически склеятся в один файл. Потяните за край диапазона, чтобы уточнить границы. Del — удалить выбранный диапазон, Ctrl+Z / Ctrl+Y — отменить / вернуть." },
            { Msg.EdInfoLine, "{0} · {1} · {2} · диапазонов: {3} · будет удалено: {4} · итог: {5} — склеится автоматически" },
            { Msg.EdAudioOnly, "Файл содержит только аудио — обрезка и извлечение кадров недоступны." },
            { Msg.EdStatusReady, "Готово" },
            { Msg.EdStatusOpening, "Открытие…" },
            { Msg.EdStatusStarting, "Запуск видео-движка…" },
            { Msg.EdStatusOverview, "Подготовка обзора…" },
            { Msg.EdStatusDecoding, "Декодирование кадра…" },
            { Msg.EdStatusTrimming, "Обрезка…" },
            { Msg.EdStatusTrimDone, "✓ Сохранено" },
            { Msg.EdStatusTrimCanceled, "Обрезка отменена, исходный файл не тронут." },
            { Msg.EdStatusFrameSaved, "✓ Кадр сохранён" },
            { Msg.EdStatusImageOpened, "✓ Изображение открыто: {0}" },
            { Msg.EdPosition, "Позиция:" },
            { Msg.EdNothingToSave, "Сохранять нечего: нет ни одного диапазона удаления. Потяните мышью по верхней шкале, чтобы выделить ненужный фрагмент." },
            { Msg.EdErrorDecode, "Не удалось декодировать файл:" },
            { Msg.EdErrorTrim, "Обрезка не выполнена." },
            { Msg.EdErrorMove, "Не удалось сохранить выходной файл." },
            { Msg.EdErrorSave, "Не удалось сохранить кадр." },
            { Msg.EdErrorProbe, "Не удалось открыть файл:" },
            { Msg.EdMsgExitBusy, "Идёт обрезка. Закрытие окна отменит её." },
            { Msg.EdNoFfprobe, "ffprobe.exe не найден в папке приложения — редактирование недоступно." },
            { Msg.EdErrorSameFile, "Нельзя сохранять результат поверх исходного файла. Выберите другое имя или папку." },
            { Msg.EdNothingLeft, "Сохранять нечего: помеченные диапазоны покрывают весь ролик, не осталось ни одной части." },
            { Msg.EdStatusTrimSaved, "Видео сохранено: {0}" }
        };

        public static Lang Current { get; private set; }

        public static void Set(Lang lang)
        {
            Current = lang;
        }

        public static void SetFromSetting(string value)
        {
            Current = string.Equals(value != null ? value.Trim() : null, "ru", StringComparison.OrdinalIgnoreCase) ? Lang.Ru : Lang.En;
        }

        public static string ToSetting()
        {
            return Current == Lang.Ru ? "ru" : "en";
        }

        public static string T(Msg key, params object[] args)
        {
            string s = null;
            if (Current == Lang.Ru && !Ru.TryGetValue(key, out s)) En.TryGetValue(key, out s);
            if (s == null && !En.TryGetValue(key, out s)) s = key.ToString();
            if (args != null && args.Length > 0)
            {
                object[] resolved = new object[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    object a = args[i];
                    if (a is Msg) resolved[i] = T((Msg)a);
                    else if (a is Sub) { Sub sub = (Sub)a; resolved[i] = T(sub.Key, sub.Args); }
                    else resolved[i] = a;
                }
                s = string.Format(s, resolved);
            }
            return s;
        }

        public static string FormatSize(long bytes)
        {
            bool ru = Current == Lang.Ru;
            CultureInfo ci = ru ? new CultureInfo("ru-RU") : CultureInfo.InvariantCulture;
            if (bytes < 1024) return bytes.ToString("0", ci) + " " + (ru ? "Б" : "B");
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0", ci) + " " + (ru ? "КБ" : "KB");
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0", ci) + " " + (ru ? "МБ" : "MB");
            return (mb / 1024.0).ToString("0.0", ci) + " " + (ru ? "ГБ" : "GB");
        }

        public static string FormatDurationShort(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Floor(seconds + 0.05);
            int tenths = (int)Math.Round((seconds - total) * 10.0);
            if (tenths >= 10) { tenths = 0; total++; }
            int h = total / 3600;
            int m = (total % 3600) / 60;
            int s = total % 60;
            CultureInfo ci = CultureInfo.InvariantCulture;
            if (h > 0) return string.Format(ci, "{0}:{1:00}:{2:00}.{3}", h, m, s, tenths);
            return string.Format(ci, "{0}:{1:00}.{2}", m, s, tenths);
        }
    }
}
