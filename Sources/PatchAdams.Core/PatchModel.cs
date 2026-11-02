namespace PatchAdams.Core;

public sealed record Patch(MailHeader? MailHeader, IReadOnlyList<FileChange> Files);

public sealed record MailHeader(string? From, string? Subject, string? Date, string Body);

public enum FileChangeKind
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied
}

public sealed record FileChange(
    string? OldPath,
    string? NewPath,
    FileChangeKind Kind,
    string? OldMode,
    string? NewMode,
    int? Similarity,
    bool IsBinary,
    IReadOnlyList<Hunk> Hunks)
{
    public string Path => NewPath ?? OldPath ?? "";

    public string FileName => Path.Replace('\\', '/').Split('/').LastOrDefault() ?? "";

    public bool IsTest => IsTestPath(Path);

    public static bool IsTestPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(component => component.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                                        component.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                                        component.Equals("spec", StringComparison.OrdinalIgnoreCase) ||
                                        component.Equals("specs", StringComparison.OrdinalIgnoreCase)))
            return true;
        var name = components.LastOrDefault() ?? "";
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        return stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
               stem.EndsWith(".spec", StringComparison.OrdinalIgnoreCase) ||
               stem.StartsWith("test_", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record Hunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string? SectionHeading,
    IReadOnlyList<PatchLine> Lines)
{
    public IReadOnlyList<NumberedLine> NumberedLines
    {
        get
        {
            var oldNumber = OldStart;
            var newNumber = NewStart;
            var result = new List<NumberedLine>(Lines.Count);
            foreach (var line in Lines)
            {
                int? old = null;
                int? @new = null;
                switch (line.Kind)
                {
                    case PatchLineKind.Context:
                        old = oldNumber++;
                        @new = newNumber++;
                        break;
                    case PatchLineKind.Removed:
                        old = oldNumber++;
                        break;
                    case PatchLineKind.Added:
                        @new = newNumber++;
                        break;
                }
                result.Add(new NumberedLine(line, old, @new));
            }
            return result;
        }
    }
}

public enum PatchLineKind
{
    Context,
    Added,
    Removed
}

public sealed record PatchLine(PatchLineKind Kind, string Text, bool NoNewlineAtEnd = false);

public sealed record NumberedLine(PatchLine Line, int? OldNumber, int? NewNumber);

public sealed class PatchParseException(int line, string message) : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
    public string Detail { get; } = message;
}
