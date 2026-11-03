using System.Text;
using System.Text.RegularExpressions;

namespace PatchAdams.Core;

/// <summary>Strict parser for unified diffs and git format-patch messages.</summary>
public static class UnifiedDiffParser
{
    private static readonly Regex HunkHeader = new(
        @"^@@ -(?<old>\d+)(?:,(?<oldCount>\d+))? \+(?<new>\d+)(?:,(?<newCount>\d+))? @@(?<heading>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static Patch Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray());
        return parser.Run();
    }

    public static Patch ParseFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }
        return Parse(text);
    }

    private sealed class Parser
    {
        private readonly string[] lines;
        private int index;

        public Parser(string[] lines) =>
            this.lines = lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;

        private bool AtEnd => index >= lines.Length;
        private string Current => lines[index];
        private int LineNumber => index + 1;

        public Patch Run()
        {
            var mail = ParseMailHeader();
            var files = new List<FileChange>();
            while (!AtEnd)
            {
                if (Current.StartsWith("diff --git ", StringComparison.Ordinal) ||
                    Current.StartsWith("--- ", StringComparison.Ordinal) &&
                    index + 1 < lines.Length && lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal))
                {
                    files.Add(ParseFile());
                }
                else if (Current == "-- ")
                {
                    break;
                }
                else
                {
                    index++;
                }
            }
            if (files.Count == 0)
                throw new PatchParseException(1, "no `diff --git` or `---`/`+++` header found");
            return new Patch(mail, files);
        }

        private MailHeader? ParseMailHeader()
        {
            if (AtEnd || !Current.StartsWith("From ", StringComparison.Ordinal) ||
                Current.Length <= 45 || !Current.AsSpan(5, 40).ToString().All(Uri.IsHexDigit))
                return null;

            index++;
            string? from = null, subject = null, date = null, lastKey = null;
            while (!AtEnd && Current.Length > 0)
            {
                if (char.IsWhiteSpace(Current[0]) && lastKey is not null)
                {
                    var continuation = Current.Trim();
                    if (lastKey == "subject") subject = $"{subject} {continuation}";
                    if (lastKey == "from") from = $"{from} {continuation}";
                }
                else
                {
                    var colon = Current.IndexOf(':');
                    if (colon >= 0)
                    {
                        lastKey = Current[..colon].ToLowerInvariant();
                        var value = Current[(colon + 1)..].Trim();
                        if (lastKey == "from") from = value;
                        if (lastKey == "subject") subject = value;
                        if (lastKey == "date") date = value;
                    }
                }
                index++;
            }

            var body = new List<string>();
            while (!AtEnd && !Current.StartsWith("diff --git ", StringComparison.Ordinal) && Current != "---")
                body.Add(lines[index++]);
            if (!AtEnd && Current == "---")
                index++;
            return new MailHeader(from, subject, date, string.Join('\n', body).Trim());
        }

        private FileChange ParseFile()
        {
            string? oldPath = null, newPath = null, oldMode = null, newMode = null;
            var kind = FileChangeKind.Modified;
            int? similarity = null;
            var binary = false;
            var sawPaths = false;

            if (Current.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                (oldPath, newPath) = SplitGitPaths(Current["diff --git ".Length..]);
                index++;
            }

            while (!AtEnd)
            {
                var line = Current;
                if (line.StartsWith("old mode ", StringComparison.Ordinal)) oldMode = line["old mode ".Length..];
                else if (line.StartsWith("new mode ", StringComparison.Ordinal)) newMode = line["new mode ".Length..];
                else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
                {
                    kind = FileChangeKind.Deleted;
                    oldMode = line["deleted file mode ".Length..];
                }
                else if (line.StartsWith("new file mode ", StringComparison.Ordinal))
                {
                    kind = FileChangeKind.Added;
                    newMode = line["new file mode ".Length..];
                }
                else if (line.StartsWith("rename from ", StringComparison.Ordinal))
                {
                    kind = FileChangeKind.Renamed;
                    oldPath = line["rename from ".Length..];
                }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal))
                {
                    kind = FileChangeKind.Renamed;
                    newPath = line["rename to ".Length..];
                }
                else if (line.StartsWith("copy from ", StringComparison.Ordinal))
                {
                    kind = FileChangeKind.Copied;
                    oldPath = line["copy from ".Length..];
                }
                else if (line.StartsWith("copy to ", StringComparison.Ordinal))
                {
                    kind = FileChangeKind.Copied;
                    newPath = line["copy to ".Length..];
                }
                else if (line.StartsWith("similarity index ", StringComparison.Ordinal) ||
                         line.StartsWith("dissimilarity index ", StringComparison.Ordinal))
                {
                    var digits = new string(line.Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out var parsed)) similarity = parsed;
                }
                else if (line.StartsWith("index ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        oldMode ??= parts[2];
                        newMode ??= parts[2];
                    }
                }
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch")
                {
                    binary = true;
                    index++;
                    while (!AtEnd && !Current.StartsWith("diff --git ", StringComparison.Ordinal)) index++;
                    break;
                }
                else if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    var oldRaw = PathPart(line);
                    oldPath = oldRaw == "/dev/null" ? null : StripPrefix(oldRaw);
                    if (oldRaw == "/dev/null" && kind == FileChangeKind.Modified) kind = FileChangeKind.Added;
                    index++;
                    if (AtEnd || !Current.StartsWith("+++ ", StringComparison.Ordinal))
                        throw new PatchParseException(LineNumber, "expected `+++` after `---`");
                    var newRaw = PathPart(Current);
                    newPath = newRaw == "/dev/null" ? null : StripPrefix(newRaw);
                    if (newRaw == "/dev/null" && kind == FileChangeKind.Modified) kind = FileChangeKind.Deleted;
                    index++;
                    sawPaths = true;
                    break;
                }
                else if (line.StartsWith("diff --git ", StringComparison.Ordinal) ||
                         line.StartsWith("@@ ", StringComparison.Ordinal) || line == "-- ")
                {
                    break;
                }
                index++;
            }

            var hunks = new List<Hunk>();
            if (sawPaths)
                while (!AtEnd && Current.StartsWith("@@ ", StringComparison.Ordinal))
                    hunks.Add(ParseHunk());
            if (kind == FileChangeKind.Added) oldPath = null;
            if (kind == FileChangeKind.Deleted) newPath = null;
            return new FileChange(oldPath, newPath, kind, oldMode, newMode, similarity, binary, hunks);
        }

        private Hunk ParseHunk()
        {
            var headerLine = LineNumber;
            var match = HunkHeader.Match(Current);
            if (!match.Success)
                throw new PatchParseException(headerLine, $"malformed hunk header: {Current}");
            var oldStart = ParseNumber(match, "old");
            var oldRemaining = ParseNumber(match, "oldCount", 1);
            var newStart = ParseNumber(match, "new");
            var newRemaining = ParseNumber(match, "newCount", 1);
            var heading = match.Groups["heading"].Value.Trim();
            if (heading.Length == 0) heading = null!;
            index++;

            var hunkLines = new List<PatchLine>();
            while (oldRemaining > 0 || newRemaining > 0)
            {
                if (AtEnd)
                    throw new PatchParseException(LineNumber,
                        $"hunk at line {headerLine} ends early: {oldRemaining} old / {newRemaining} new lines missing");
                var raw = Current;
                if (raw.StartsWith('\\'))
                {
                    if (hunkLines.Count > 0)
                        hunkLines[^1] = hunkLines[^1] with { NoNewlineAtEnd = true };
                    index++;
                    continue;
                }

                var marker = raw.Length == 0 ? '\0' : raw[0];
                var content = raw.Length == 0 ? "" : raw[1..];
                switch (marker)
                {
                    case ' ':
                    case '\0':
                        hunkLines.Add(new PatchLine(PatchLineKind.Context, content));
                        oldRemaining--;
                        newRemaining--;
                        break;
                    case '+':
                        hunkLines.Add(new PatchLine(PatchLineKind.Added, content));
                        newRemaining--;
                        break;
                    case '-':
                        hunkLines.Add(new PatchLine(PatchLineKind.Removed, content));
                        oldRemaining--;
                        break;
                    default:
                        throw new PatchParseException(LineNumber, $"unexpected line in hunk: {raw[..Math.Min(40, raw.Length)]}");
                }
                if (oldRemaining < 0 || newRemaining < 0)
                    throw new PatchParseException(LineNumber,
                        $"hunk at line {headerLine} has more lines than its header declares");
                index++;
            }

            if (!AtEnd && Current.StartsWith('\\') && hunkLines.Count > 0)
            {
                hunkLines[^1] = hunkLines[^1] with { NoNewlineAtEnd = true };
                index++;
            }
            return new Hunk(oldStart, ParseNumber(match, "oldCount", 1), newStart,
                ParseNumber(match, "newCount", 1), heading, hunkLines);
        }
    }

    private static int ParseNumber(Match match, string group, int defaultValue = 0) =>
        match.Groups[group].Success ? int.Parse(match.Groups[group].Value) : defaultValue;

    private static string PathPart(string line)
    {
        var part = line[4..];
        if (part.StartsWith('"'))
            return Unquote(part);
        var tab = part.IndexOf('\t');
        return tab < 0 ? part : part[..tab];
    }

    private static string? StripPrefix(string path) =>
        path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;

    private static (string? Old, string? New) SplitGitPaths(string text)
    {
        var first = ReadGitToken(text, 0, out var next);
        var second = ReadGitToken(text, next, out _);
        return (StripPrefix(first), StripPrefix(second));
    }

    private static string ReadGitToken(string text, int start, out int next)
    {
        while (start < text.Length && text[start] == ' ') start++;
        if (start < text.Length && text[start] == '"')
        {
            var end = start + 1;
            var escaped = false;
            while (end < text.Length)
            {
                if (!escaped && text[end] == '"') break;
                escaped = !escaped && text[end] == '\\';
                if (text[end] != '\\') escaped = false;
                end++;
            }
            next = Math.Min(text.Length, end + 1);
            return Unquote(text[start..next]);
        }
        var space = text.IndexOf(' ', start);
        next = space < 0 ? text.Length : space + 1;
        return text[start..(space < 0 ? text.Length : space)];
    }

    private static string Unquote(string quoted)
    {
        var value = quoted.Trim('"');
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                result.Append(next switch { 't' => '\t', 'n' => '\n', _ => next });
            }
            else
            {
                result.Append(value[i]);
            }
        }
        return result.ToString();
    }
}
