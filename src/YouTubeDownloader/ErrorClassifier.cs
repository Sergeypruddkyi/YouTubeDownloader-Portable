using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace YouTubeDownloader
{
    public enum ErrorCategory
    {
        UpdateSuspect,
        Network,
        InvalidUrl,
        ContentUnavailable,
        PathError,
        Unknown
    }

    public class ErrorClassifier
    {
        private class Rule
        {
            public readonly Regex Rx;
            public readonly ErrorCategory Cat;
            public readonly string Hint;

            public Rule(string pattern, ErrorCategory cat, string hint)
            {
                Rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                Cat = cat;
                Hint = hint;
            }
        }

        private static readonly List<Rule> Rules = new List<Rule>
        {
            new Rule(@"No space left on device|errno\s*28", ErrorCategory.PathError,
                "На диске закончилось место. Освободите место или выберите другую папку."),
            new Rule(@"Permission denied|Access is denied", ErrorCategory.PathError,
                "Нет прав на запись в выбранную папку. Выберите другую папку или проверьте права."),
            new Rule(@"Unable to (?:create|open) (?:directory|file)|The system cannot find the path", ErrorCategory.PathError,
                "Не удалось создать или открыть папку назначения. Проверьте путь."),
            new Rule(@"File ?name too long", ErrorCategory.PathError,
                "Имя файла слишком длинное. Выберите папку с более коротким путём."),

            new Rule(@"Unsupported URL|is not a valid URL", ErrorCategory.InvalidUrl,
                "Ссылка не распознана как поддерживаемая. Убедитесь, что скопирована ссылка YouTube."),

            new Rule(@"Unable to extract|Failed to extract", ErrorCategory.UpdateSuspect,
                "YouTube изменил страницу/структуру данных. Возможная проблема версии yt-dlp — точно установить причину нельзя."),
            new Rule(@"nsig extraction failed|Signature extraction failed", ErrorCategory.UpdateSuspect,
                "Не удалось расшифровать подпись (nsig). Возможная проблема версии yt-dlp — точно установить причину нельзя."),
            new Rule(@"Requested format is not available", ErrorCategory.UpdateSuspect,
                "Требуемый формат недоступен. Возможная проблема версии yt-dlp — точно установить причину нельзя."),
            new Rule(@"Sign in to confirm you.re not a bot", ErrorCategory.UpdateSuspect,
                "YouTube требует подтверждения «не робот». Возможная проблема версии yt-dlp — обновление может помочь, но не гарантировано."),
            new Rule(@"Precondition check failed|HTTP Error 403", ErrorCategory.UpdateSuspect,
                "Запрос отклонён (403). Возможная проблема версии yt-dlp — точно установить причину нельзя."),

            new Rule(@"unable to download (?:video|audio) data|getaddrinfo failed|Temporary failure in name resolution|Connection (?:reset|refused)|timed out|Network is unreachable|SSL: CERTIFICATE", ErrorCategory.Network,
                "Проблема с сетью или доступом к YouTube. Проверьте интернет-соединение и попробуйте позже. Обновление yt-dlp не требуется."),
            new Rule(@"HTTP Error 5\d\d", ErrorCategory.Network,
                "Сервер YouTube временно недоступен (ошибка 5xx). Попробуйте позже. Обновление yt-dlp не требуется."),

            new Rule(@"Private video", ErrorCategory.ContentUnavailable,
                "Видео приватное — скачать его нельзя."),
            new Rule(@"Video unavailable|This video is unavailable|removed by the uploader", ErrorCategory.ContentUnavailable,
                "Видео недоступно или удалено."),
            new Rule(@"members[- ]only|Join this channel", ErrorCategory.ContentUnavailable,
                "Видео доступно только участникам канала."),
            new Rule(@"Sign in to confirm your age|confirm your age|age.?restricted", ErrorCategory.ContentUnavailable,
                "Возрастное ограничение: требуется вход в аккаунт."),
            new Rule(@"not available in your country|blocked it in your country", ErrorCategory.ContentUnavailable,
                "Видео заблокировано в вашем регионе."),
        };

        public class Result
        {
            public ErrorCategory Category;
            public string Hint;
            public string MatchedLine;
        }

        public static Result Classify(string output, int exitCode)
        {
            if (exitCode == 0) return null;
            if (string.IsNullOrEmpty(output))
                return new Result { Category = ErrorCategory.Unknown, Hint = null, MatchedLine = null };

            foreach (Rule r in Rules)
            {
                Match m = r.Rx.Match(output);
                if (m.Success)
                {
                    return new Result { Category = r.Cat, Hint = r.Hint, MatchedLine = ExtractLine(output, m.Index) };
                }
            }
            return new Result { Category = ErrorCategory.Unknown, Hint = null, MatchedLine = LastErrorLine(output) };
        }

        private static string ExtractLine(string text, int index)
        {
            if (index < 0 || index >= text.Length) return null;
            int start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1));
            start = start < 0 ? 0 : start + 1;
            int end = text.IndexOf('\n', index);
            if (end < 0) end = text.Length;
            string line = text.Substring(start, end - start).TrimEnd('\r');
            return line.Length > 300 ? line.Substring(0, 300) + "…" : line;
        }

        private static string LastErrorLine(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || l.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                    return l.Length > 300 ? l.Substring(0, 300) + "…" : l;
            }
            return null;
        }
    }
}
