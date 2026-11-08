using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchAdams.Core;

public enum FindingSeverity
{
    Trivial,
    Minor,
    Material,
    Blocking
}

public enum RequestRelevance
{
    Yes,
    No,
    Unclear
}

public sealed record LineSpan(int Lower, int Upper)
{
    public bool Contains(int line) => line >= Lower && line <= Upper;

    /// <summary>Normalizes one-based inclusive ranges without filling unselected gaps.</summary>
    public static IReadOnlyList<LineSpan> Normalize(IEnumerable<LineSpan> spans)
    {
        var result = new List<LineSpan>();
        foreach (var span in spans.OrderBy(span => span.Lower).ThenBy(span => span.Upper))
        {
            if (span.Lower < 1 || span.Upper < span.Lower)
                throw new ArgumentException("Line ranges must be positive and ordered.", nameof(spans));
            if (result.Count > 0 && span.Lower <= (long)result[^1].Upper + 1)
                result[^1] = result[^1] with { Upper = Math.Max(result[^1].Upper, span.Upper) };
            else
                result.Add(span);
        }
        return result;
    }
}

public sealed record LineRange(LineSpan Lines, bool IsBase = false)
{
    public bool Contains(DisplayLine line) => IsBase
        ? line.PostLine is null && line.BaseLine is int baseLine && Lines.Contains(baseLine)
        : line.PostLine is int postLine && Lines.Contains(postLine);
}

public static class SelectionRanges
{
    public static IReadOnlyList<LineRange> Normalize(IEnumerable<LineRange> ranges) =>
        ranges.GroupBy(range => range.IsBase).OrderBy(group => group.Key)
            .SelectMany(group => LineSpan.Normalize(group.Select(range => range.Lines))
                .Select(span => new LineRange(span, group.Key))).ToArray();

    public static IReadOnlyList<LineRange> FromDisplayLines(IEnumerable<DisplayLine> lines) =>
        Normalize(lines.Where(line => line.Kind is not DisplayLineKind.CollapsedContext and not DisplayLineKind.SyntheticFiller &&
                (line.PostLine.HasValue || line.BaseLine.HasValue))
            .Select(line => new LineRange(new LineSpan(line.PostLine ?? line.BaseLine!.Value,
                line.PostLine ?? line.BaseLine!.Value), IsBase: line.PostLine is null)));

    public static IReadOnlyList<LineRange> Resolve(
        IReadOnlyList<LineRange>? ranges, LineSpan legacyLines, string legacyReference)
    {
        var result = Normalize(ranges ?? [new LineRange(legacyLines,
            IsBase: legacyReference.EndsWith($" -L{legacyLines.Lower}:L{legacyLines.Upper}", StringComparison.Ordinal))]);
        if (result.Count == 0) throw new ArgumentException("A selection must contain at least one line range.");
        return result;
    }
}

public sealed record FindingAnchor(string Label, string Status, double Confidence, string? Reference);
public enum CollapseSide { Left, Right }
public sealed record SessionColumn(
    string Label,
    string SourceName,
    string PatchFile,
    bool IsHidden = false,
    bool IsRead = false,
    CollapseSide CollapsedTo = CollapseSide.Right,
    double WidthWeight = 1);
public sealed record SessionBase(string Kind, string? Path = null, string? Revision = null, string? Commit = null, string? Label = null);
public sealed record SessionRanking(IReadOnlyDictionary<string, int> Ranks, string Rationale)
{
    public int? RankOf(string label) => Ranks.TryGetValue(label, out var rank) ? rank : null;
}

public sealed record SessionFinding(
    Guid Id,
    string Label,
    string Path,
    LineSpan Lines,
    string Reference,
    string Title,
    string Description,
    string Category,
    FindingSeverity Severity,
    RequestRelevance RequestAsks,
    bool NotFlagged,
    string Note,
    IReadOnlyList<FindingAnchor> Anchors,
    DateTimeOffset Created,
    IReadOnlyList<LineRange>? Ranges = null)
{
    [JsonIgnore]
    public IReadOnlyList<LineRange> SelectedRanges => SelectionRanges.Resolve(Ranges, Lines, Reference);

    public bool IsComplete => !string.IsNullOrWhiteSpace(Title) &&
                              !string.IsNullOrWhiteSpace(Category) &&
                              !string.IsNullOrWhiteSpace(Reference);

    public SessionFinding DuplicateFor(string label, string path, IReadOnlyList<LineRange> ranges, string reference)
    {
        if (label == Label)
            throw new ArgumentException("A finding can only be duplicated to a different patch.", nameof(label));
        var selected = SelectionRanges.Normalize(ranges);
        if (selected.Count == 0)
            throw new ArgumentException("A finding duplicate needs at least one selected line range.", nameof(ranges));
        return this with
        {
            Id = Guid.NewGuid(),
            Label = label,
            Path = path,
            Lines = selected[0].Lines,
            Reference = reference,
            Anchors = [],
            Created = DateTimeOffset.UtcNow,
            Ranges = selected
        };
    }
}

public sealed record SessionPin(Guid Id, string Label, string Path, LineSpan Lines, string Reference, string Snippet,
    IReadOnlyList<LineRange>? Ranges = null)
{
    [JsonIgnore]
    public IReadOnlyList<LineRange> SelectedRanges => SelectionRanges.Resolve(Ranges, Lines, Reference);
}
public sealed record ManualAnchor(Guid Id, string SourceLabel, string Path, LineSpan SourceLines,
    string TargetLabel, LineSpan TargetLines);
public sealed record BlindMode(bool IsOn, ulong Seed);

public sealed record ReviewSession(
    int Version,
    string Name,
    string Request,
    SessionBase Base,
    IReadOnlyList<SessionColumn> Columns,
    string? Golden,
    IReadOnlyList<string> Categories,
    IReadOnlyList<SessionFinding> Findings,
    SessionRanking Ranking,
    IReadOnlyList<SessionPin> Pins,
    IReadOnlyList<ManualAnchor> ManualAnchors,
    BlindMode Blind,
    DateTimeOffset Created,
    DateTimeOffset? Saved = null,
    string ReviewerFeedback = "")
{
    public const int CurrentVersion = 3;

    public IReadOnlyList<ComparisonFinding> ComparisonFindings { get; init; } = [];

    public static IReadOnlyList<string> DefaultCategories { get; } =
    [
        "unnecessary helpers",
        "excessive defensiveness",
        "unnecessary machinery or repeated work",
        "duplicated logic or state",
        "too many branches",
        "unclear code",
        "unhelpful comments or names",
        "code in the wrong place",
        "unneeded backwards compatibility",
        "dead or unrelated code",
        "premature extensibility",
        "low-value tests",
        "other"
    ];

    public IReadOnlyDictionary<string, int> BlindMapping =>
        Blind.IsOn ? GetBlindMapping(Columns.Select(column => column.Label).ToArray(), Blind.Seed)
            : new Dictionary<string, int>();

    public string DisplayLabel(string label) =>
        BlindMapping.TryGetValue(label, out var number) ? number.ToString() : label;

    public IReadOnlyList<SessionColumn> RankedColumns()
    {
        var order = Columns.Select((column, index) => (column, index));
        return order.OrderBy(item => Ranking.RankOf(item.column.Label) ?? int.MaxValue)
            .ThenBy(item => item.index).Select(item => item.column).ToArray();
    }

    public static IReadOnlyDictionary<string, int> GetBlindMapping(IReadOnlyList<string> labels, ulong seed)
    {
        var shuffled = labels.Order(StringComparer.Ordinal).ToArray();
        var state = seed;
        ulong Next()
        {
            state = unchecked(state + 0x9E3779B97F4A7C15);
            var value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EB;
            return value ^ (value >> 31);
        }
        for (var i = shuffled.Length - 1; i > 0; i--)
        {
            var j = (int)(Next() % (ulong)(i + 1));
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        return shuffled.Select((label, index) => (label, number: index + 1))
            .ToDictionary(pair => pair.label, pair => pair.number, StringComparer.Ordinal);
    }
}

public static class ReferenceGenerator
{
    public static string ForSelection(AppliedFile file, int startLine, int endLine)
    {
        if (startLine < 1 || endLine < startLine || file.PostText is null)
            throw new ArgumentOutOfRangeException(nameof(startLine), "Selection must be a valid post-patch line range.");
        var lines = DiffEngine.SplitLines(file.PostText);
        if (endLine > lines.Count)
            throw new ArgumentOutOfRangeException(nameof(endLine), "Selection extends past the post-patch file.");
        var name = file.Change.FileName;
        return $"{name} L{startLine}:L{endLine}";
    }

    public static string ForDisplaySelection(IReadOnlyList<DisplayLine> lines, string fileName)
    {
        return ForRanges(SelectionRanges.FromDisplayLines(lines), fileName);
    }

    public static string ForRanges(IReadOnlyList<LineRange> ranges, string fileName)
    {
        var normalized = SelectionRanges.Normalize(ranges);
        if (normalized.Count == 0)
            throw new ArgumentException("Selection has no base or post-patch line.", nameof(ranges));
        return $"{fileName} " + string.Join(", ", normalized.Select(range =>
            $"{(range.IsBase ? "-" : "")}L{range.Lines.Lower}:L{range.Lines.Upper}"));
    }
}

public sealed record SessionContents(ReviewSession Session, IReadOnlyDictionary<string, string> PatchTexts);

public static class SessionPackage
{
    public const string Extension = ".patchadam";
    private const string SessionFileName = "session.json";
    private const string PatchesDirectory = "patches";

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static void Write(SessionContents contents, string directory)
    {
        ComparisonFinding.ValidateAll(contents.Session.ComparisonFindings,
            contents.Session.Columns.Select(column => column.Label));
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var patchDirectory = Path.Combine(root, PatchesDirectory);
        Directory.CreateDirectory(patchDirectory);
        var columns = new List<SessionColumn>();
        foreach (var column in contents.Session.Columns)
        {
            if (!contents.PatchTexts.TryGetValue(column.Label, out var patchText))
                throw new InvalidDataException($"no patch text for column {column.Label}");
            var patchFile = $"{column.Label}-{SafeFileName(column.SourceName)}";
            WriteAtomically(Path.Combine(patchDirectory, patchFile), Encoding.UTF8.GetBytes(patchText));
            columns.Add(column with { PatchFile = patchFile });
        }
        var session = contents.Session with
        {
            Version = ReviewSession.CurrentVersion,
            Columns = columns,
            Saved = DateTimeOffset.UtcNow
        };
        WriteAtomically(Path.Combine(root, SessionFileName), JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions));
    }

    public static SessionContents Read(string directory)
    {
        var root = Path.GetFullPath(directory);
        var sessionPath = Path.Combine(root, SessionFileName);
        var session = JsonSerializer.Deserialize<ReviewSession>(File.ReadAllBytes(sessionPath), JsonOptions)
            ?? throw new InvalidDataException("session.json contains no session");
        if (session.Version > ReviewSession.CurrentVersion)
            throw new InvalidDataException(
                $"session.json is version {session.Version}; this build reads up to {ReviewSession.CurrentVersion}");
        ComparisonFinding.ValidateAll(session.ComparisonFindings, session.Columns.Select(column => column.Label));
        if (session.Version < 3 && session.ComparisonFindings.Count > 0)
            throw new InvalidDataException("Comparison findings require session version 3 or later.");
        try
        {
            foreach (var finding in session.Findings) _ = finding.SelectedRanges;
            foreach (var pin in session.Pins) _ = pin.SelectedRanges;
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("Session contains invalid selection ranges.", error);
        }
        var patchDirectory = Path.GetFullPath(Path.Combine(root, PatchesDirectory));
        var patchPrefix = patchDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var patches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in session.Columns)
        {
            if (Path.GetFileName(column.PatchFile) != column.PatchFile ||
                column.PatchFile.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException($"unsafe patch file name: {column.PatchFile}");
            var patchPath = Path.GetFullPath(Path.Combine(patchDirectory, column.PatchFile));
            if (!patchPath.StartsWith(patchPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"unsafe patch file name: {column.PatchFile}");
            patches[column.Label] = File.ReadAllText(patchPath, Encoding.UTF8);
        }
        return new SessionContents(session, patches);
    }

    public static byte[] Encode(ReviewSession session)
    {
        ComparisonFinding.ValidateAll(session.ComparisonFindings, session.Columns.Select(column => column.Label));
        if (session.Version < 3 && session.ComparisonFindings.Count > 0)
            throw new InvalidDataException("Comparison findings require session version 3 or later.");
        return JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '\0']).ToHashSet();
        var cleaned = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "patch.diff" : cleaned;
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public static class MarkdownExport
{
    public static string Render(ReviewSession session, DateTimeOffset? date = null)
    {
        var output = new List<string>();
        output.Add("# " + (string.IsNullOrWhiteSpace(session.Name) ? "Patch review" : session.Name));
        output.Add("");
        var comparisonCount = session.ComparisonFindings.Count > 0
            ? $" · {session.ComparisonFindings.Count} comparison{(session.ComparisonFindings.Count == 1 ? "" : "s")}" : "";
        output.Add($"_{session.Columns.Count} patches · {session.Findings.Count(finding => !finding.NotFlagged)} findings{comparisonCount} · {(date ?? DateTimeOffset.UtcNow):yyyy-MM-dd}_");
        output.Add("");
        if (!string.IsNullOrWhiteSpace(session.Request))
        {
            output.Add("<details><summary>Request</summary>");
            output.Add("");
            output.Add(session.Request.Trim());
            output.Add("");
            output.Add("</details>");
            output.Add("");
        }

        output.Add("## Ranking");
        output.Add("");
        output.Add(session.Blind.IsOn ? "| Rank | Patch |" : "| Rank | Patch | File |");
        output.Add(session.Blind.IsOn ? "|---:|---|" : "|---:|---|---|");
        foreach (var column in session.RankedColumns())
        {
            var rank = session.Ranking.RankOf(column.Label)?.ToString() ?? "—";
            output.Add(session.Blind.IsOn
                ? $"| {rank} | {session.DisplayLabel(column.Label)} |"
                : $"| {rank} | {column.Label} | `{column.SourceName}` |");
        }
        output.Add("");

        output.Add("## Findings");
        output.Add("");
        var flagged = session.Findings.Where(finding => !finding.NotFlagged).ToArray();
        if (flagged.Length == 0)
        {
            output.Add("_None._");
            output.Add("");
        }
        foreach (var column in session.RankedColumns())
        {
            var findings = SortFindings(flagged.Where(finding => finding.Label == column.Label));
            if (findings.Count == 0) continue;
            var rank = session.Ranking.RankOf(column.Label);
            var heading = session.Blind.IsOn
                ? $"Patch {session.DisplayLabel(column.Label)}"
                : $"{column.Label} — `{column.SourceName}`";
            if (rank.HasValue) heading += $" (rank {rank})";
            output.Add("### " + heading);
            output.Add("");
            foreach (var finding in findings)
            {
                output.Add($"- **{finding.Severity.ToString().ToLowerInvariant()} · {finding.Category}** — {finding.Title} `{finding.Reference}`");
                if (!string.IsNullOrWhiteSpace(finding.Description))
                    output.Add("  " + OneLine(finding.Description));
                if (finding.RequestAsks != RequestRelevance.Unclear)
                    output.Add($"  Request asks for this: {finding.RequestAsks.ToString().ToLowerInvariant()}.");
                if (!string.IsNullOrWhiteSpace(finding.Note))
                    output.Add("  Note: " + OneLine(finding.Note));
            }
            output.Add("");
        }

        var passes = SortFindings(session.Findings.Where(finding => finding.NotFlagged));
        if (passes.Count > 0)
        {
            output.Add("## Not flagged");
            output.Add("");
            output.Add("Considered and passed:");
            output.Add("");
            foreach (var finding in passes)
            {
                var line = $"- {session.DisplayLabel(finding.Label)} `{finding.Reference}` — {finding.Title}";
                if (!string.IsNullOrWhiteSpace(finding.Description))
                    line += ": " + OneLine(finding.Description);
                output.Add(line);
            }
            output.Add("");
        }

        if (session.ComparisonFindings.Count > 0)
        {
            output.Add("## Comparison findings");
            output.Add("");
            foreach (var finding in session.ComparisonFindings)
            {
                output.Add($"### {OneLine(finding.Title)}");
                output.Add("");
                output.Add($"**{finding.Severity.ToString().ToLowerInvariant()} · {finding.Category}**");
                output.Add($"Comparison ID: `{finding.Id}`");
                output.Add("");
                output.Add(OneLine(finding.Description));
                output.Add("");
                if (finding.RequestAsks != RequestRelevance.Unclear)
                    output.Add($"Request asks for this: {finding.RequestAsks.ToString().ToLowerInvariant()}.");
                foreach (var evidence in finding.Evidence)
                {
                    output.Add($"- **Patch {session.DisplayLabel(evidence.Label)}** · `{evidence.Path}` · `{evidence.Reference}`");
                    if (!string.IsNullOrWhiteSpace(evidence.Note))
                        output.Add("  " + OneLine(evidence.Note));
                }
                output.Add("");
            }
        }

        output.Add("## Rationale");
        output.Add("");
        output.Add(string.IsNullOrWhiteSpace(session.Ranking.Rationale) ? "_Not written._" : session.Ranking.Rationale.Trim());
        output.Add("");
        if (session.Blind.IsOn)
        {
            output.Add("## Mapping");
            output.Add("");
            output.Add("Blind review; the labels above map to:");
            output.Add("");
            output.Add("| Label | Column | File |");
            output.Add("|---|---|---|");
            foreach (var column in session.Columns.OrderBy(column => session.BlindMapping[column.Label]))
                output.Add($"| {session.DisplayLabel(column.Label)} | {column.Label} | `{column.SourceName}` |");
            output.Add("");
        }
        return string.Join('\n', output);
    }

    public static byte[] RenderJson(ReviewSession session, DateTimeOffset? date = null)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            session,
            blindMapping = session.BlindMapping,
            exported = date ?? DateTimeOffset.UtcNow
        }, options);
    }

    private static List<SessionFinding> SortFindings(IEnumerable<SessionFinding> findings)
    {
        var severityOrder = new Dictionary<FindingSeverity, int>
        {
            [FindingSeverity.Blocking] = 0,
            [FindingSeverity.Material] = 1,
            [FindingSeverity.Minor] = 2,
            [FindingSeverity.Trivial] = 3
        };
        return findings.OrderBy(finding => severityOrder[finding.Severity])
            .ThenBy(finding => finding.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Lines.Lower).ToList();
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0));
}
