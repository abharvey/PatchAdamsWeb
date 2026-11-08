using System.Text.Json.Serialization;

namespace PatchAdams.Core;

/// <summary>One selected location supporting a comparison, with a patch-specific observation.</summary>
public sealed record ComparisonEvidence(
    string Label,
    string Path,
    IReadOnlyList<LineRange> Ranges,
    string Note = "")
{
    [JsonIgnore]
    public IReadOnlyList<LineRange> SelectedRanges => SelectionRanges.Normalize(Ranges);

    [JsonIgnore]
    public string Reference => ReferenceGenerator.ForRanges(SelectedRanges,
        Path.Replace('\\', '/').Split('/').Last());
}

/// <summary>A shared assessment comparing explicit evidence from at least two candidate patches.</summary>
public sealed record ComparisonFinding(
    Guid Id,
    string Title,
    string Description,
    string Category,
    FindingSeverity Severity,
    RequestRelevance RequestAsks,
    IReadOnlyList<ComparisonEvidence> Evidence,
    DateTimeOffset Created)
{
    internal static void ValidateAll(IReadOnlyList<ComparisonFinding> findings, IEnumerable<string> columnLabels)
    {
        if (findings is null)
            throw new InvalidDataException("Comparison findings must be an array, not null.");
        var labels = columnLabels.ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        foreach (var finding in findings)
        {
            if (finding is null || finding.Id == Guid.Empty || !ids.Add(finding.Id))
                throw new InvalidDataException("Comparison findings must have nonempty, unique IDs.");
            if (string.IsNullOrWhiteSpace(finding.Title) || string.IsNullOrWhiteSpace(finding.Description) ||
                string.IsNullOrWhiteSpace(finding.Category))
                throw new InvalidDataException("A comparison finding requires a title, description, and category.");
            if (!Enum.IsDefined(finding.Severity) || !Enum.IsDefined(finding.RequestAsks))
                throw new InvalidDataException("A comparison finding has an invalid severity or request relevance.");
            if (finding.Evidence is null)
                throw new InvalidDataException("A comparison finding requires evidence from at least two patches.");
            var comparedLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evidence in finding.Evidence)
            {
                if (evidence is null || evidence.Label is null || !labels.Contains(evidence.Label))
                    throw new InvalidDataException("Comparison evidence must refer to a patch in the session.");
                comparedLabels.Add(evidence.Label);
                ValidatePath(evidence.Path);
                if (evidence.Note is null || evidence.Ranges is null || evidence.Ranges.Count == 0 ||
                    evidence.Ranges.Any(range => range is null || range.Lines is null))
                    throw new InvalidDataException("Comparison evidence requires nonempty line ranges and a non-null note.");
                try
                {
                    _ = evidence.SelectedRanges;
                }
                catch (ArgumentException error)
                {
                    throw new InvalidDataException("Comparison evidence contains invalid line ranges.", error);
                }
            }
            if (comparedLabels.Count < 2)
                throw new InvalidDataException("A comparison finding requires evidence from at least two distinct patches.");
        }
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("Comparison evidence requires a relative file path.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0') ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe comparison evidence path: {path}");
    }
}
