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
            public readonly Msg HintKey;

            public Rule(string pattern, ErrorCategory cat, Msg hintKey)
            {
                Rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                Cat = cat;
                HintKey = hintKey;
            }
        }

        private static readonly List<Rule> Rules = new List<Rule>
        {
            new Rule(@"No space left on device|errno\s*28", ErrorCategory.PathError,
                Msg.HintNoSpace),
            new Rule(@"Permission denied|Access is denied", ErrorCategory.PathError,
                Msg.HintNoWriteAccess),
            new Rule(@"Unable to (?:create|open) (?:directory|file)|The system cannot find the path", ErrorCategory.PathError,
                Msg.HintDestCreateFail),
            new Rule(@"File ?name too long", ErrorCategory.PathError,
                Msg.HintFileNameTooLong),

            new Rule(@"Unsupported URL|is not a valid URL", ErrorCategory.InvalidUrl,
                Msg.HintUnsupportedUrl),

            new Rule(@"Unable to extract|Failed to extract", ErrorCategory.UpdateSuspect,
                Msg.HintExtractFail),
            new Rule(@"nsig extraction failed|Signature extraction failed", ErrorCategory.UpdateSuspect,
                Msg.HintNsigFail),
            new Rule(@"Requested format is not available", ErrorCategory.UpdateSuspect,
                Msg.HintFormatUnavailable),
            new Rule(@"Sign in to confirm you.re not a bot", ErrorCategory.UpdateSuspect,
                Msg.HintBotCheck),
            new Rule(@"Precondition check failed|HTTP Error 403", ErrorCategory.UpdateSuspect,
                Msg.HintHttp403),

            new Rule(@"unable to download (?:video|audio) data|getaddrinfo failed|Temporary failure in name resolution|Connection (?:reset|refused)|timed out|Network is unreachable|SSL: CERTIFICATE", ErrorCategory.Network,
                Msg.HintNetwork),
            new Rule(@"HTTP Error 5\d\d", ErrorCategory.Network,
                Msg.HintServer5xx),

            new Rule(@"Private video", ErrorCategory.ContentUnavailable,
                Msg.HintPrivateVideo),
            new Rule(@"Video unavailable|This video is unavailable|removed by the uploader", ErrorCategory.ContentUnavailable,
                Msg.HintVideoUnavailable),
            new Rule(@"members[- ]only|Join this channel", ErrorCategory.ContentUnavailable,
                Msg.HintMembersOnly),
            new Rule(@"Sign in to confirm your age|confirm your age|age.?restricted", ErrorCategory.ContentUnavailable,
                Msg.HintAgeRestricted),
            new Rule(@"not available in your country|blocked it in your country", ErrorCategory.ContentUnavailable,
                Msg.HintGeoBlocked),
        };

        public class Result
        {
            public ErrorCategory Category;
            public Msg? HintKey;
            public string MatchedLine;
        }

        public static Result Classify(string output, int exitCode)
        {
            if (exitCode == 0) return null;
            if (string.IsNullOrEmpty(output))
                return new Result { Category = ErrorCategory.Unknown, HintKey = null, MatchedLine = null };

            foreach (Rule r in Rules)
            {
                Match m = r.Rx.Match(output);
                if (m.Success)
                {
                    return new Result { Category = r.Cat, HintKey = r.HintKey, MatchedLine = ExtractLine(output, m.Index) };
                }
            }
            return new Result { Category = ErrorCategory.Unknown, HintKey = null, MatchedLine = LastErrorLine(output) };
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
