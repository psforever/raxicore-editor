using System;
using System.Collections.Generic;
using System.IO;

namespace RaxicoreEditor.Editor.Validation
{
    public enum IssueSeverity { Error, Warning }

    public sealed class TextIssue
    {
        public int Line { get; init; }            // 1-based; 0 = whole-file
        public IssueSeverity Severity { get; init; }
        public string Message { get; init; } = "";

        public string Where => Line > 0 ? $"line {Line}" : "file";
        public string Sev => Severity == IssueSeverity.Error ? "ERROR" : "warn";
        public string Display => $"{Sev}  {Where}: {Message}";
    }

    public sealed class ValidationReport
    {
        public required string FormatName { get; init; }
        public List<TextIssue> Issues { get; } = new();

        public int ErrorCount { get { int n = 0; foreach (TextIssue i in Issues) if (i.Severity == IssueSeverity.Error) n++; return n; } }
        public int WarningCount { get { int n = 0; foreach (TextIssue i in Issues) if (i.Severity == IssueSeverity.Warning) n++; return n; } }
        public bool IsValid => ErrorCount == 0;

        public string Summary =>
            ErrorCount == 0 && WarningCount == 0
                ? $"✓ valid {FormatName}"
                : $"{FormatName}: {ErrorCount} error(s), {WarningCount} warning(s)";
    }

    /// <summary>
    /// Lightweight, conservative parsers that validate the editable engine-derived text formats and enforce
    /// file-type rules on edit: INI ([section] / key=value), command scripts (.lst/.cfg — balanced
    /// quotes, ASCII), and generic text (control-char check). Rules flag only clear violations so valid
    /// shipped files never false-positive.
    /// </summary>
    public static class TextValidator
    {
        public static ValidationReport Validate(string fileName, string text)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".ini" => ValidateIni(text),
                ".lst" or ".cfg" => ValidateCommandScript(text, ext),
                _ => ValidateText(text, ext),
            };
        }

        private static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static bool IsComment(string trimmed) =>
            trimmed.StartsWith(';') || trimmed.StartsWith('#') || trimmed.StartsWith("//");

        // Flag control chars (always an error) and, when asciiOnly, non-ASCII chars (warning).
        private static void CheckChars(string line, int lineNo, bool asciiOnly, ValidationReport r)
        {
            foreach (char c in line)
            {
                if (c is '\t') continue;
                if (c < 0x20 || c == 0x7F)
                {
                    r.Issues.Add(new TextIssue { Line = lineNo, Severity = IssueSeverity.Error, Message = $"control character 0x{(int)c:X2}" });
                    return;
                }
                if (asciiOnly && c > 0x7E)
                {
                    r.Issues.Add(new TextIssue { Line = lineNo, Severity = IssueSeverity.Warning, Message = $"non-ASCII character '{c}' (0x{(int)c:X})" });
                    return;
                }
            }
        }

        private static ValidationReport ValidateIni(string text)
        {
            // Engine-derived .ini comes in two flavours: "key=value" (client/machine.ini) and whitespace-
            // delimited "key value # comment" (engine3d_options.ini). Accept both; only flag genuine
            // malformations (unclosed section, empty key, control/non-ASCII chars).
            var r = new ValidationReport { FormatName = "INI" };
            string[] lines = SplitLines(text);
            string section = "";
            var keysInSection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                int lineNo = i + 1;
                string raw = lines[i];
                CheckChars(raw, lineNo, asciiOnly: true, r);
                string t = raw.Trim();
                if (t.Length == 0 || IsComment(t)) continue;

                // Strip an inline comment (# or ;).
                int cut = IndexOfAny(t, '#', ';');
                if (cut >= 0) t = t.Substring(0, cut).Trim();
                if (t.Length == 0) continue;

                if (t[0] == '[')
                {
                    if (!t.EndsWith(']') || t.Length < 3)
                    {
                        r.Issues.Add(new TextIssue { Line = lineNo, Severity = IssueSeverity.Error, Message = "malformed section header (expected \"[name]\")" });
                    }
                    else
                    {
                        section = t.Substring(1, t.Length - 2);
                        keysInSection.Clear();
                    }
                    continue;
                }

                // key = before '=' (if present) or the first whitespace-delimited token.
                int eq = t.IndexOf('=');
                string key;
                if (eq >= 0)
                {
                    key = t.Substring(0, eq).Trim();
                }
                else
                {
                    int ws = 0;
                    while (ws < t.Length && !char.IsWhiteSpace(t[ws])) ws++;
                    key = t.Substring(0, ws);
                }
                if (key.Length == 0)
                {
                    r.Issues.Add(new TextIssue { Line = lineNo, Severity = IssueSeverity.Error, Message = "missing key (line starts with a separator)" });
                }
                else if (!keysInSection.Add(key))
                {
                    r.Issues.Add(new TextIssue { Line = lineNo, Severity = IssueSeverity.Warning, Message = $"duplicate key '{key}' in [{section}]" });
                }
            }
            return r;
        }

        private static int IndexOfAny(string s, char a, char b)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] == a || s[i] == b) return i;
            return -1;
        }

        private static ValidationReport ValidateCommandScript(string text, string ext)
        {
            var r = new ValidationReport { FormatName = ext == ".cfg" ? "config script" : "keymap/command script" };
            string[] lines = SplitLines(text);
            for (int i = 0; i < lines.Length; i++)
            {
                int lineNo = i + 1;
                string raw = lines[i];
                CheckChars(raw, lineNo, asciiOnly: true, r);
                string t = raw.Trim();
                if (t.Length == 0 || IsComment(t)) continue;

                // Balanced double-quotes (the only hard structural rule for these command lines).
                int quotes = 0;
                foreach (char c in t) if (c == '"') quotes++;
                if ((quotes & 1) != 0)
                {
                    r.Issues.Add(new TextIssue { Line = lineNo, Severity = IssueSeverity.Error, Message = "unbalanced double-quote" });
                }
            }
            return r;
        }

        private static ValidationReport ValidateText(string text, string ext)
        {
            var r = new ValidationReport { FormatName = ext.Length > 1 ? ext.Substring(1).ToUpperInvariant() + " text" : "text" };
            string[] lines = SplitLines(text);
            for (int i = 0; i < lines.Length; i++)
            {
                // Plain text may be Unicode (e.g. news_unicode*.txt), so only flag control chars.
                CheckChars(lines[i], i + 1, asciiOnly: false, r);
            }
            return r;
        }
    }
}
