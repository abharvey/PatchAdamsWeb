using System.Text.RegularExpressions;

namespace PatchAdams.Core;

/// <summary>Resolves candidate identity from filenames, never from selection order or patch contents.</summary>
public static class PatchLabelAssignment
{
    public static IReadOnlyList<string> Labels { get; } = Array.AsReadOnly(new[] { "A", "B", "C", "D" });

    private static readonly Regex DownloadSuffix = new(
        @"^(?<stem>.+?)\s*\((?<number>[1-3])\)$", RegexOptions.CultureInvariant);
    private static readonly Regex LetterMarker = new(
        @"[A-D]|(?<![\p{L}\p{N}])[a-d](?![\p{L}\p{N}])", RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns labels in input order, or null when the entire set needs manual assignment.
    /// A shared bare name and its (1), (2), (3) downloads take precedence over letter markers.
    /// </summary>
    public static IReadOnlyList<string>? Infer(IReadOnlyList<string> paths)
    {
        if (paths.Count is < 2 or > 4)
            throw new ArgumentException("Choose between two and four patch files.", nameof(paths));

        var names = paths.Select(Path.GetFileNameWithoutExtension).ToArray();
        var downloads = names.Select(name =>
        {
            var match = DownloadSuffix.Match(name!);
            return match.Success
                ? (Stem: match.Groups["stem"].Value.TrimEnd(), Index: match.Groups["number"].Value[0] - '0')
                : (Stem: name!, Index: 0);
        }).ToArray();
        if (downloads.Any(item => item.Index == 0) &&
            downloads.Any(item => item.Index != 0) &&
            downloads.All(item => string.Equals(item.Stem, downloads[0].Stem, StringComparison.OrdinalIgnoreCase)))
        {
            var numbered = downloads.Select(item => Labels[item.Index]).ToArray();
            // A recognized download family must not fall back to a conflicting letter interpretation.
            return IsValid(numbered) ? numbered : null;
        }

        var letters = names.Select(name => LetterMarker.Matches(name!)
            .Select(match => match.Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray()).ToArray();
        if (letters.Any(matches => matches.Length != 1)) return null;
        var assigned = letters.Select(matches => matches[0]).ToArray();
        return IsValid(assigned) ? assigned : null;
    }

    /// <summary>Manual and inferred assignments use the same unique A–D identities.</summary>
    public static bool IsValid(IReadOnlyList<string?> labels) =>
        labels.Count is >= 2 and <= 4 &&
        labels.All(label => label is not null && Labels.Contains(label, StringComparer.Ordinal)) &&
        labels.Distinct(StringComparer.Ordinal).Count() == labels.Count;
}
