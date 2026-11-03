using System.Text.RegularExpressions;

namespace PatchAdams.Core;

public enum AlignmentStatus
{
    Exact,
    Modified,
    Moved,
    Absent
}

public sealed record Region(string FilePath, int StartLine, int EndLine)
{
    public int LineCount => Math.Max(0, EndLine - StartLine + 1);
}

public sealed record AlignmentResult(
    AlignmentStatus Status,
    int? StartLine,
    int? EndLine,
    double Confidence,
    string? NearestHunkPath,
    int? NearestHunkStart,
    int? NearestHunkEnd,
    TokenDiff? Difference);

/// <summary>Locates selected lines in another applied patch using line maps and token similarity.</summary>
public static class AlignmentEngine
{
    public const double MinimumConfidence = 0.6;

    private static readonly Regex Declaration = new(
        @"\b(?:func|function|def|class|struct|type|interface|enum|const|let|var)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static AlignmentResult Locate(Region sourceRegion, AppliedFile source, AppliedFile? target,
        ManualAnchor? manualOverride = null)
    {
        var sourceLines = DiffEngine.SplitLines(source.PostText ?? "");
        var selected = Slice(sourceLines, sourceRegion.StartLine, sourceRegion.EndLine);
        if (selected.Count == 0 || target?.PostText is null)
            return Absent(sourceRegion, target, selected, target?.PostText);

        var targetLines = DiffEngine.SplitLines(target.PostText).ToList();
        if (manualOverride is not null &&
            manualOverride.Path == sourceRegion.FilePath &&
            manualOverride.SourceLines.Lower == sourceRegion.StartLine &&
            manualOverride.SourceLines.Upper == sourceRegion.EndLine)
        {
            var manuallyLocated = Slice(targetLines, manualOverride.TargetLines.Lower, manualOverride.TargetLines.Upper);
            if (manuallyLocated.Count == manualOverride.TargetLines.Upper - manualOverride.TargetLines.Lower + 1)
            {
                var sourceText = string.Join('\n', selected);
                var targetText = string.Join('\n', manuallyLocated);
                var diff = DiffEngine.Tokens(sourceText, targetText);
                var same = Normalize(sourceText) == Normalize(targetText);
                var moved = manualOverride.TargetLines.Lower != sourceRegion.StartLine;
                var status = same
                    ? moved ? AlignmentStatus.Moved : AlignmentStatus.Exact
                    : moved ? AlignmentStatus.Moved : AlignmentStatus.Modified;
                return new AlignmentResult(status, manualOverride.TargetLines.Lower,
                    manualOverride.TargetLines.Upper, 1, null, null, null, diff);
            }
        }

        var mappedBaseLines = Enumerable.Range(sourceRegion.StartLine, sourceRegion.LineCount)
            .Select(source.LineMap.BaseForPost)
            .Where(line => line.HasValue)
            .Select(line => line!.Value)
            .ToArray();

        if (mappedBaseLines.Length > 0)
        {
            var targetLinesMapped = mappedBaseLines
                .Select(target.LineMap.PostForBase)
                .ToArray();
            if (targetLinesMapped.All(line => line.HasValue))
            {
                var first = targetLinesMapped[0]!.Value;
                var contiguous = targetLinesMapped.Select((line, index) => line!.Value == first + index).All(value => value);
                if (contiguous)
                {
                    var candidate = Slice(targetLines, first, first + targetLinesMapped.Length - 1);
                    return ResultForCandidate(selected, candidate, first, moved: false);
                }
            }
        }

        var declarationName = selected.Select(line => Declaration.Match(line))
            .FirstOrDefault(match => match.Success)?.Groups[1].Value;
        if (declarationName is not null)
        {
            var declarationLine = targetLines.FindIndex(line =>
                Declaration.Match(line) is { Success: true } match && match.Groups[1].Value == declarationName);
            if (declarationLine >= 0)
            {
                var start = declarationLine + 1;
                var candidate = Slice(targetLines, start, start + selected.Count - 1);
                return ResultForCandidate(selected, candidate, start,
                    moved: start != sourceRegion.StartLine);
            }
        }

        var best = FindBestWindow(selected, targetLines);
        if (best is not null && best.Value.Confidence >= MinimumConfidence)
            return ResultForCandidate(selected, best.Value.Lines, best.Value.Start,
                moved: best.Value.Start != sourceRegion.StartLine);

        return Absent(sourceRegion, target, selected, target.PostText);
    }

    private static AlignmentResult ResultForCandidate(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate,
        int targetStart,
        bool moved)
    {
        var sourceText = string.Join('\n', source);
        var candidateText = string.Join('\n', candidate);
        var difference = DiffEngine.Tokens(sourceText, candidateText);
        if (Normalize(sourceText) == Normalize(candidateText))
            return new AlignmentResult(moved ? AlignmentStatus.Moved : AlignmentStatus.Exact,
                targetStart, targetStart + Math.Max(0, candidate.Count - 1), 1, null, null, null, difference);
        return new AlignmentResult(moved ? AlignmentStatus.Moved : AlignmentStatus.Modified,
            targetStart, targetStart + Math.Max(0, candidate.Count - 1),
            Math.Max(MinimumConfidence, difference.Similarity), null, null, null, difference);
    }

    private static (int Start, IReadOnlyList<string> Lines, double Confidence, bool Exact)? FindBestWindow(
        IReadOnlyList<string> source,
        IReadOnlyList<string> target)
    {
        if (target.Count == 0) return null;
        var bestScore = -1d;
        var bestStart = 0;
        IReadOnlyList<string> bestLines = [];
        var exact = false;
        for (var start = 1; start <= target.Count; start++)
        {
            var candidate = Slice(target, start, start + source.Count - 1);
            if (candidate.Count == 0) continue;
            var diff = DiffEngine.Tokens(string.Join('\n', source), string.Join('\n', candidate));
            if (Normalize(string.Join('\n', source)) == Normalize(string.Join('\n', candidate)))
            {
                bestScore = 1;
                bestStart = start;
                bestLines = candidate;
                exact = true;
                break;
            }
            if (diff.Similarity > bestScore)
            {
                bestScore = diff.Similarity;
                bestStart = start;
                bestLines = candidate;
                exact = false;
            }
        }
        return bestScore < 0 ? null : (bestStart, bestLines, bestScore, exact);
    }

    private static AlignmentResult Absent(
        Region region,
        AppliedFile? target,
        IReadOnlyList<string> source,
        string? targetText)
    {
        var nearest = target?.Hunks
            .OrderBy(hunk => Math.Abs(hunk.FoundOldStart - region.StartLine))
            .FirstOrDefault();
        var difference = targetText is null
            ? null
            : DiffEngine.Tokens(string.Join('\n', source), "");
        return new AlignmentResult(AlignmentStatus.Absent, null, null, 0,
            nearest is null ? null : target?.Path,
            nearest?.FoundOldStart,
            nearest is null ? null : nearest.FoundOldStart + Math.Max(0, nearest.NewLineCount - 1),
            difference);
    }

    private static IReadOnlyList<string> Slice(IReadOnlyList<string> lines, int start, int end)
    {
        if (start < 1 || start > lines.Count || end < start) return [];
        return lines.Skip(start - 1).Take(Math.Min(end, lines.Count) - start + 1).ToArray();
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
