namespace PatchAdams.Core;

public enum ApplyStatusKind
{
    Clean,
    Fuzz,
    Failed,
    Binary
}

public sealed record ApplyStatus(ApplyStatusKind Kind, int Fuzz = 0, string? Message = null)
{
    public string Label => Kind switch
    {
        ApplyStatusKind.Clean => "clean",
        ApplyStatusKind.Fuzz => $"fuzz {Fuzz}",
        ApplyStatusKind.Failed => "failed",
        ApplyStatusKind.Binary => "binary",
        _ => throw new ArgumentOutOfRangeException()
    };
}

public sealed record HunkPlacement(int Index, int DeclaredOldStart, int FoundOldStart, int PostStart, int Fuzz, int NewLineCount)
{
    public int Offset => FoundOldStart - DeclaredOldStart;
}

public sealed record LineMap(IReadOnlyList<int?> PostToBase, IReadOnlyList<int?> BaseToPost)
{
    public static LineMap Empty { get; } = new([], []);
    public int PostLineCount => PostToBase.Count;
    public int BaseLineCount => BaseToPost.Count;

    public int? BaseForPost(int line) =>
        line >= 1 && line <= PostToBase.Count ? PostToBase[line - 1] : null;

    public int? PostForBase(int line) =>
        line >= 1 && line <= BaseToPost.Count ? BaseToPost[line - 1] : null;

    public int? NearestBaseForPost(int line)
    {
        for (var candidate = Math.Clamp(line, 1, PostToBase.Count); candidate >= 1; candidate--)
            if (PostToBase[candidate - 1] is int mapped)
                return mapped;
        return PostToBase.FirstOrDefault(mapped => mapped.HasValue);
    }
}

public sealed record AppliedFile(
    FileChange Change,
    string? BaseText,
    string? PostText,
    ApplyStatus Status,
    LineMap LineMap,
    IReadOnlyList<HunkPlacement> Hunks)
{
    public string Path => Change.Path;

    public static AppliedFile Unchanged(string path, string text)
    {
        var lineCount = DiffEngine.SplitLines(text).Count;
        var identity = Enumerable.Range(1, lineCount).Select(line => (int?)line).ToArray();
        var change = new FileChange(path, path, FileChangeKind.Modified, null, null, null, false, []);
        return new AppliedFile(change, text, text, new ApplyStatus(ApplyStatusKind.Clean),
            new LineMap(identity, identity), []);
    }

    public static AppliedFile Deleted(string path, string baseText)
    {
        var baseLineCount = DiffEngine.SplitLines(baseText).Count;
        var change = new FileChange(path, null, FileChangeKind.Deleted, null, null, null, false, []);
        return new AppliedFile(change, baseText, null, new ApplyStatus(ApplyStatusKind.Clean),
            new LineMap([], new int?[baseLineCount]), []);
    }
}

public sealed record ApplyReport(int Clean, int Fuzz, int Failed, int Binary)
{
    public int Total => Clean + Fuzz + Failed + Binary;
    public bool IsClean => Fuzz == 0 && Failed == 0;
}

public sealed record AppliedPatch(IReadOnlyList<AppliedFile> Files)
{
    public ApplyReport Report => new(
        Files.Count(file => file.Status.Kind == ApplyStatusKind.Clean),
        Files.Count(file => file.Status.Kind == ApplyStatusKind.Fuzz),
        Files.Count(file => file.Status.Kind == ApplyStatusKind.Failed),
        Files.Count(file => file.Status.Kind == ApplyStatusKind.Binary));

    public AppliedFile? FileAt(string path) => Files.FirstOrDefault(file => file.Change.Path == path);
}

/// <summary>Applies patch hunks to in-memory text; this engine never writes or executes patched code.</summary>
public static class ApplyEngine
{
    public const int DefaultMaxFuzz = 2;

    public static AppliedPatch Apply(Patch patch, IFileProvider baseFiles, int maxFuzz = DefaultMaxFuzz) =>
        new(patch.Files.Select(change => Apply(change, baseFiles, maxFuzz)).ToArray());

    public static AppliedFile Apply(FileChange change, IFileProvider baseFiles, int maxFuzz = DefaultMaxFuzz)
    {
        if (maxFuzz < 0)
            throw new ArgumentOutOfRangeException(nameof(maxFuzz));

        string? baseText = null;
        if (change.OldPath is not null && change.Kind != FileChangeKind.Added)
        {
            try
            {
                var bytes = baseFiles.ReadFile(change.OldPath);
                if (bytes is null)
                    return Failed(change, null, $"{change.OldPath} is not in the base");
                baseText = BaseText.Decode(bytes);
            }
            catch (Exception error)
            {
                return Failed(change, null, $"{change.OldPath}: {error.Message}");
            }
        }

        if (change.IsBinary)
            return new AppliedFile(change, null, null,
                new ApplyStatus(ApplyStatusKind.Binary), LineMap.Empty, []);

        if (change.Kind == FileChangeKind.Added && change.NewPath is not null)
        {
            try
            {
                if (baseFiles.ReadFile(change.NewPath) is not null)
                    return Failed(change, null, $"{change.NewPath} already exists in the base");
            }
            catch (Exception error)
            {
                return Failed(change, null, $"{change.NewPath}: {error.Message}");
            }
        }

        var baseLines = SplitLines(baseText ?? "");
        var worker = new Worker(baseLines, maxFuzz);
        for (var index = 0; index < change.Hunks.Count; index++)
        {
            var failure = worker.Place(change.Hunks[index], index);
            if (failure is not null)
                return new AppliedFile(change, baseText, null,
                    new ApplyStatus(ApplyStatusKind.Failed, Message: failure), LineMap.Empty, worker.Placements);
        }

        worker.Flush();
        var endsWithNewline = change.Kind == FileChangeKind.Added || baseText?.EndsWith('\n') == true;
        foreach (var hunk in change.Hunks)
        {
            var oldLines = hunk.Lines.Where(line => line.Kind != PatchLineKind.Added).ToArray();
            var newLines = hunk.Lines.Where(line => line.Kind != PatchLineKind.Removed).ToArray();
            if (newLines.LastOrDefault()?.NoNewlineAtEnd == true)
                endsWithNewline = false;
            else if (oldLines.LastOrDefault()?.NoNewlineAtEnd == true)
                endsWithNewline = true;
        }

        var postText = change.Kind == FileChangeKind.Deleted
            ? null
            : worker.Post.Count == 0
                ? ""
                : string.Join('\n', worker.Post) + (endsWithNewline ? "\n" : "");
        var maxFuzzUsed = worker.Placements.Select(placement => placement.Fuzz).DefaultIfEmpty(0).Max();
        var status = maxFuzzUsed > 0
            ? new ApplyStatus(ApplyStatusKind.Fuzz, maxFuzzUsed)
            : new ApplyStatus(ApplyStatusKind.Clean);
        return new AppliedFile(change, baseText, postText, status,
            new LineMap(worker.PostToBase.Select(value => value is null ? null : value + 1).ToArray(),
                worker.BaseToPost.Select(value => value is null ? null : value + 1).ToArray()),
            worker.Placements);
    }

    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Split('\n').ToList();
        if (text.EndsWith('\n')) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static AppliedFile Failed(FileChange change, string? baseText, string message) =>
        new(change, baseText, null, new ApplyStatus(ApplyStatusKind.Failed, Message: message), LineMap.Empty, []);

    private sealed class Worker(IReadOnlyList<string> baseLines, int maxFuzz)
    {
        public List<string> Post { get; } = [];
        public List<int?> PostToBase { get; } = [];
        public int?[] BaseToPost { get; } = new int?[baseLines.Count];
        public List<HunkPlacement> Placements { get; } = [];
        private int cursor;
        private int offset;

        public string? Place(Hunk hunk, int index)
        {
            var old = hunk.Lines.Where(line => line.Kind != PatchLineKind.Added).ToArray();
            var declared = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
            var leading = hunk.Lines.TakeWhile(line => line.Kind == PatchLineKind.Context).Count();
            var trailing = old.Length == 0
                ? 0
                : hunk.Lines.Reverse().TakeWhile(line => line.Kind == PatchLineKind.Context).Count();

            for (var fuzz = 0; fuzz <= maxFuzz; fuzz++)
            {
                var dropLead = Math.Min(fuzz, leading);
                var dropTrail = Math.Min(fuzz, trailing);
                if (fuzz > 0 && dropLead == 0 && dropTrail == 0)
                    break;
                var needle = old.Skip(dropLead).Take(old.Length - dropLead - dropTrail).Select(line => line.Text).ToArray();
                var expected = declared + offset + dropLead;
                if (Search(needle, expected) is int found)
                {
                    Splice(hunk, found, dropLead, dropTrail, fuzz, index);
                    return null;
                }
            }

            var position = hunk.OldCount == 0 ? $"after base line {hunk.OldStart}" : $"at base line {hunk.OldStart}";
            return $"hunk {index + 1} ({position}) does not match the base";
        }

        private int? Search(IReadOnlyList<string> needle, int expected)
        {
            var limit = baseLines.Count - needle.Count;
            if (limit < cursor) return null;
            if (needle.Count == 0) return Math.Clamp(expected, cursor, baseLines.Count);
            for (var distance = 0; ; distance++)
            {
                var forward = expected + distance;
                var backward = expected - distance;
                var any = false;
                if (forward >= cursor && forward <= limit)
                {
                    any = true;
                    if (Matches(needle, forward)) return forward;
                }
                if (distance > 0 && backward >= cursor && backward <= limit)
                {
                    any = true;
                    if (Matches(needle, backward)) return backward;
                }
                if (!any && forward > limit && backward < cursor) return null;
            }
        }

        private bool Matches(IReadOnlyList<string> needle, int position)
        {
            for (var i = 0; i < needle.Count; i++)
                if (baseLines[position + i] != needle[i]) return false;
            return true;
        }

        private void Splice(Hunk hunk, int found, int dropLead, int dropTrail, int fuzz, int index)
        {
            var start = found - dropLead;
            CopyBase(start);
            var postStart = Post.Count;
            var newLineCount = hunk.Lines.Count(line => line.Kind != PatchLineKind.Removed);
            for (var lineIndex = 0; lineIndex < hunk.Lines.Count; lineIndex++)
            {
                var line = hunk.Lines[lineIndex];
                var isDroppedContext = line.Kind == PatchLineKind.Context &&
                    (lineIndex < dropLead || hunk.Lines.Count - 1 - lineIndex < dropTrail);
                switch (line.Kind)
                {
                    case PatchLineKind.Context:
                        if (!isDroppedContext || cursor < baseLines.Count)
                            CopyBase(cursor + 1);
                        break;
                    case PatchLineKind.Removed:
                        if (cursor < baseLines.Count)
                            BaseToPost[cursor] = null;
                        cursor++;
                        break;
                    case PatchLineKind.Added:
                        Post.Add(line.Text);
                        PostToBase.Add(null);
                        break;
                }
            }
            Placements.Add(new HunkPlacement(index, hunk.OldStart,
                hunk.OldCount == 0 ? start : start + 1,
                hunk.NewCount == 0 ? postStart : postStart + 1,
                fuzz, newLineCount));
            offset = start - (hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1);
        }

        private void CopyBase(int end)
        {
            while (cursor < end && cursor < baseLines.Count)
            {
                BaseToPost[cursor] = Post.Count;
                PostToBase.Add(cursor);
                Post.Add(baseLines[cursor]);
                cursor++;
            }
        }

        public void Flush() => CopyBase(baseLines.Count);
    }
}
