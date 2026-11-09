using System.Buffers;
using System.Globalization;
using System.Text;

namespace PatchAdams.Core;

/// <summary>A zero-based source-row match. Gutters and display placeholders are not source text.</summary>
public sealed record IdentifierOccurrence(int Row, int Start, int Length);

/// <summary>Ordinal, whole-identifier text matching. No parsing or symbol resolution is performed.</summary>
public static class IdentifierOccurrences
{
    public static IReadOnlyList<IdentifierOccurrence> Find(
        IReadOnlyList<string> lines, string identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsIdentifier(identifier.AsSpan())) return [];
        var result = new List<IdentifierOccurrence>();
        for (var row = 0; row < lines.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[row];
            var offset = 0;
            while (offset <= line.Length - identifier.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = line.IndexOf(identifier, offset, StringComparison.Ordinal);
                if (start < 0) break;
                if (HasBoundaries(line, start, identifier.Length))
                    result.Add(new IdentifierOccurrence(row, start, identifier.Length));
                offset = start + identifier.Length;
            }
        }
        return result;
    }

    public static bool IsWholeIdentifier(string line, int start, int length) =>
        start >= 0 && length > 0 && start <= line.Length - length &&
        IsIdentifier(line.AsSpan(start, length)) && HasBoundaries(line, start, length);

    private static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        if (text[0] == '@') text = text[1..];
        if (text.IsEmpty) return false;
        var first = true;
        foreach (var rune in text.EnumerateRunes())
        {
            var letter = Rune.IsLetter(rune) || rune.Value == '$' ||
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.LetterNumber or UnicodeCategory.ConnectorPunctuation;
            if (rune.Value == '@' || !letter && (first || !IsPart(rune))) return false;
            first = false;
        }
        return true;
    }

    private static bool IsPart(Rune rune) =>
        Rune.IsLetterOrDigit(rune) || rune.Value is '$' or '@' ||
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.LetterNumber or UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static bool HasBoundaries(string line, int start, int length)
    {
        var beforeIsPart = start > 0 &&
            Rune.DecodeLastFromUtf16(line.AsSpan(0, start), out var before, out _) == OperationStatus.Done &&
            IsPart(before);
        var end = start + length;
        var afterIsPart = end < line.Length &&
            Rune.DecodeFromUtf16(line.AsSpan(end), out var after, out _) == OperationStatus.Done &&
            IsPart(after);
        return !beforeIsPart && !afterIsPart;
    }
}
