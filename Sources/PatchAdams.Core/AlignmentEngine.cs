using System.Runtime.CompilerServices;
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
    // Applied files are immutable snapshots; weak keys release cached lines with their session.
    private static readonly ConditionalWeakTable<AppliedFile, CachedText> TextCache = new();

    public static AlignmentResult Locate(Region sourceRegion, AppliedFile source, AppliedFile? target,
        ManualAnchor? manualOverride = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceLines = TextCache.GetValue(source, static file => new CachedText(file.PostText)).Lines;
        var selected = Slice(sourceLines, sourceRegion.StartLine, sourceRegion.EndLine);
        if (selected.Count == 0 || target?.PostText is null)
            return Absent(sourceRegion, target, selected, target?.PostText, cancellationToken);

        var targetLines = TextCache.GetValue(target, static file => new CachedText(file.PostText)).Lines;
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
                var diff = DiffEngine.Tokens(sourceText, targetText, cancellationToken);
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
                    return ResultForCandidate(selected, candidate, first, moved: false, cancellationToken);
                }
            }
        }

        var declarationName = selected.Select(line => Declaration.Match(line))
            .FirstOrDefault(match => match.Success)?.Groups[1].Value;
        if (declarationName is not null)
        {
            var declarationLine = -1;
            for (var index = 0; index < targetLines.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Declaration.Match(targetLines[index]) is { Success: true } match &&
                    match.Groups[1].Value == declarationName)
                {
                    declarationLine = index;
                    break;
                }
            }
            if (declarationLine >= 0)
            {
                var start = declarationLine + 1;
                var candidate = Slice(targetLines, start, start + selected.Count - 1);
                return ResultForCandidate(selected, candidate, start,
                    moved: start != sourceRegion.StartLine, cancellationToken);
            }
        }

        var best = FindBestWindow(selected, targetLines, cancellationToken);
        if (best is not null && best.Value.Confidence >= MinimumConfidence)
            return ResultForCandidate(selected, best.Value.Lines, best.Value.Start,
                moved: best.Value.Start != sourceRegion.StartLine, cancellationToken, best.Value.Difference);

        return Absent(sourceRegion, target, selected, target.PostText, cancellationToken);
    }

    private static AlignmentResult ResultForCandidate(
        IReadOnlyList<string> source,
        IReadOnlyList<string> candidate,
        int targetStart,
        bool moved,
        CancellationToken cancellationToken,
        TokenDiff? knownDifference = null)
    {
        var sourceText = string.Join('\n', source);
        var candidateText = string.Join('\n', candidate);
        var difference = knownDifference ?? DiffEngine.Tokens(sourceText, candidateText, cancellationToken);
        if (Normalize(sourceText) == Normalize(candidateText))
            return new AlignmentResult(moved ? AlignmentStatus.Moved : AlignmentStatus.Exact,
                targetStart, targetStart + Math.Max(0, candidate.Count - 1), 1, null, null, null, difference);
        return new AlignmentResult(moved ? AlignmentStatus.Moved : AlignmentStatus.Modified,
            targetStart, targetStart + Math.Max(0, candidate.Count - 1),
            Math.Max(MinimumConfidence, difference.Similarity), null, null, null, difference);
    }

    private static (int Start, IReadOnlyList<string> Lines, double Confidence, TokenDiff? Difference)? FindBestWindow(
        IReadOnlyList<string> source,
        IReadOnlyList<string> target,
        CancellationToken cancellationToken)
    {
        if (target.Count == 0) return null;
        var sourceText = string.Join('\n', source);
        var normalizedSource = Normalize(sourceText);
        var sourceTokens = DiffEngine.Tokenize(sourceText, cancellationToken);
        var sourceCounts = CountTokens(sourceTokens);
        var sourceCount = sourceCounts.Values.Sum();
        var bestScore = -1d;
        var bestStart = 0;
        IReadOnlyList<string> bestLines = [];
        TokenDiff? bestDifference = null;
        for (var start = 1; start <= target.Count; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Slice(target, start, start + source.Count - 1);
            if (candidate.Count == 0) continue;
            var candidateText = string.Join('\n', candidate);
            if (normalizedSource == Normalize(candidateText))
                return (start, candidate, 1, null);
            var candidateTokens = DiffEngine.Tokenize(candidateText, cancellationToken);
            var candidateCounts = CountTokens(candidateTokens);
            var total = sourceCount + candidateCounts.Values.Sum();
            var shared = candidateCounts.Sum(pair =>
                Math.Min(pair.Value, sourceCounts.GetValueOrDefault(pair.Key)));
            var upperBound = total == 0 ? 1 : 2d * shared / total;
            // Any ordered token match is bounded by the multiset intersection.
            // This prunes impossible winners without changing scores or tie order.
            if (upperBound < MinimumConfidence || upperBound <= bestScore) continue;
            var diff = DiffEngine.Tokens(sourceTokens, candidateTokens, cancellationToken);
            if (diff.Similarity > bestScore)
            {
                bestScore = diff.Similarity;
                bestStart = start;
                bestLines = candidate;
                bestDifference = diff;
            }
        }
        return bestScore < 0 ? null : (bestStart, bestLines, bestScore, bestDifference);
    }

    private static Dictionary<string, int> CountTokens(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
            if (!string.IsNullOrWhiteSpace(token))
                counts[token] = counts.GetValueOrDefault(token) + 1;
        return counts;
    }

    private static AlignmentResult Absent(
        Region region,
        AppliedFile? target,
        IReadOnlyList<string> source,
        string? targetText,
        CancellationToken cancellationToken)
    {
        var nearest = target?.Hunks
            .OrderBy(hunk => Math.Abs(hunk.FoundOldStart - region.StartLine))
            .FirstOrDefault();
        var difference = targetText is null
            ? null
            : DiffEngine.Tokens(string.Join('\n', source), "", cancellationToken);
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

    private sealed class CachedText(string? text)
    {
        public IReadOnlyList<string> Lines { get; } = DiffEngine.SplitLines(text ?? "");
    }
}
