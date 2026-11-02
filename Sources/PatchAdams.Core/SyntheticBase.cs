namespace PatchAdams.Core;

public sealed record SyntheticBaseFile(string Path, string Text);

public sealed class SyntheticBaseException(string path, string message) : Exception($"{path}: {message}")
{
    public string FilePath { get; } = path;
}

public sealed class InMemoryFileProvider : IFileProvider
{
    private readonly IReadOnlyDictionary<string, byte[]> files;

    public InMemoryFileProvider(IEnumerable<SyntheticBaseFile> files, string label = "synthetic base")
    {
        this.files = files.ToDictionary(file => file.Path,
            file => System.Text.Encoding.UTF8.GetBytes(file.Text), StringComparer.Ordinal);
        Origin = new BaseOrigin("synthetic", label);
    }

    public BaseOrigin Origin { get; }

    public byte[]? ReadFile(string path)
    {
        path = BaseLoader.ValidateRelativePath(path);
        return files.TryGetValue(path, out var value) ? value : null;
    }

    public IReadOnlyList<string> GetPaths() => files.Keys.Order(StringComparer.Ordinal).ToArray();
}

/// <summary>Reconstructs only the base lines present in patch hunks; unknown gaps remain marked filler.</summary>
public static class SyntheticBase
{
    public static string Filler(int line) => $"~ synthetic base line {line} ~";

    public static bool IsFiller(string text) =>
        text.StartsWith("~ synthetic base line ", StringComparison.Ordinal) && text.EndsWith(" ~", StringComparison.Ordinal);

    public static IReadOnlyList<SyntheticBaseFile> For(Patch patch)
    {
        var result = new List<SyntheticBaseFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in patch.Files)
        {
            var file = For(change);
            if (file is null) continue;
            if (!seen.Add(file.Path))
                throw new SyntheticBaseException(file.Path, "two files in the patch share this base path");
            result.Add(file);
        }
        return result;
    }

    public static IReadOnlyList<SyntheticBaseFile> Merge(IEnumerable<Patch> patches)
    {
        var known = new Dictionary<string, SortedDictionary<int, string>>(StringComparer.Ordinal);
        var longest = new Dictionary<string, int>(StringComparer.Ordinal);
        var noNewline = new Dictionary<string, bool>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var patch in patches)
        foreach (var change in patch.Files)
        {
            var file = For(change);
            if (file is null) continue;
            if (!known.TryGetValue(file.Path, out var lines))
            {
                lines = new SortedDictionary<int, string>();
                known[file.Path] = lines;
                order.Add(file.Path);
            }
            var split = ApplyEngine.SplitLines(file.Text);
            var hasNoNewline = file.Text.Length > 0 && !file.Text.EndsWith('\n');
            for (var i = 0; i < split.Count; i++)
            {
                var lineNumber = i + 1;
                var line = split[i];
                if (IsFiller(line)) continue;
                if (lines.TryGetValue(lineNumber, out var existing) && existing != line)
                    throw new SyntheticBaseException(file.Path,
                        $"patches disagree about base line {lineNumber}: \"{existing[..Math.Min(40, existing.Length)]}\" vs \"{line[..Math.Min(40, line.Length)]}\"");
                lines[lineNumber] = line;
            }
            if (split.Count > longest.GetValueOrDefault(file.Path))
            {
                longest[file.Path] = split.Count;
                noNewline[file.Path] = hasNoNewline;
            }
            else if (split.Count == longest.GetValueOrDefault(file.Path) && hasNoNewline)
            {
                noNewline[file.Path] = true;
            }
        }

        return order.Select(path =>
        {
            var lines = known[path];
            var count = lines.Keys.DefaultIfEmpty(0).Max();
            if (count == 0) return new SyntheticBaseFile(path, "");
            var body = string.Join('\n', Enumerable.Range(1, count)
                .Select(line => lines.GetValueOrDefault(line) ?? Filler(line)));
            return new SyntheticBaseFile(path, body + (noNewline.GetValueOrDefault(path) ? "" : "\n"));
        }).ToArray();
    }

    public static SyntheticBaseFile? For(FileChange change)
    {
        if (change.Kind == FileChangeKind.Added || change.IsBinary) return null;
        if (string.IsNullOrWhiteSpace(change.OldPath))
            throw new SyntheticBaseException(change.Path, $"{change.Kind} file has no base path");

        var lines = new List<string>();
        var next = 1;
        var lastLineNoNewline = false;
        for (var index = 0; index < change.Hunks.Count; index++)
        {
            var hunk = change.Hunks[index];
            if (hunk.OldCount == 0) continue;
            if (hunk.OldStart < next)
                throw new SyntheticBaseException(change.OldPath,
                    $"hunk {index + 1} starts at base line {hunk.OldStart} but line {next - 1} is already covered");
            while (next < hunk.OldStart)
            {
                lines.Add(Filler(next));
                next++;
            }
            foreach (var line in hunk.Lines.Where(line => line.Kind != PatchLineKind.Added))
            {
                lines.Add(line.Text);
                lastLineNoNewline = line.NoNewlineAtEnd;
            }
            next = hunk.OldStart + hunk.OldCount;
        }
        if (lines.Count == 0) return new SyntheticBaseFile(change.OldPath, "");
        var text = string.Join('\n', lines) + (lastLineNoNewline ? "" : "\n");
        return new SyntheticBaseFile(change.OldPath, text);
    }
}
