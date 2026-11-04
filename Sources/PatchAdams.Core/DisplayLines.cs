namespace PatchAdams.Core;

public enum DisplayLineKind
{
    Context,
    Added,
    Removed,
    SyntheticFiller,
    CollapsedContext
}

public sealed record DisplayLine(DisplayLineKind Kind, int? BaseLine, int? PostLine, string Text, int CollapsedCount = 1);

public static class DisplayLines
{
    public static IReadOnlyList<DisplayLine> Create(
        AppliedFile file,
        bool collapseSyntheticFiller = true,
        bool includeRemoved = true,
        int? revealPostLine = null)
    {
        var baseLines = DiffEngine.SplitLines(file.BaseText ?? "");
        var postLines = DiffEngine.SplitLines(file.PostText ?? "");
        var result = new List<DisplayLine>();
        var baseIndex = 0;
        var postIndex = 0;

        while (baseIndex < baseLines.Count)
        {
            var mappedPost = file.LineMap.PostForBase(baseIndex + 1);
            if (mappedPost is null)
            {
                if (includeRemoved)
                    result.Add(new DisplayLine(DisplayLineKind.Removed, baseIndex + 1, null, baseLines[baseIndex]));
                baseIndex++;
                continue;
            }

            var targetPostIndex = mappedPost.Value - 1;
            while (postIndex < targetPostIndex)
            {
                result.Add(new DisplayLine(DisplayLineKind.Added, null, postIndex + 1, postLines[postIndex]));
                postIndex++;
            }

            if (collapseSyntheticFiller && SyntheticBase.IsFiller(baseLines[baseIndex]) &&
                postIndex < postLines.Count && SyntheticBase.IsFiller(postLines[postIndex]) &&
                postIndex + 1 != revealPostLine)
            {
                var count = 0;
                while (baseIndex < baseLines.Count && postIndex < postLines.Count &&
                       file.LineMap.PostForBase(baseIndex + 1) == postIndex + 1 &&
                       SyntheticBase.IsFiller(baseLines[baseIndex]) && SyntheticBase.IsFiller(postLines[postIndex]) &&
                       postIndex + 1 != revealPostLine)
                {
                    baseIndex++;
                    postIndex++;
                    count++;
                }
                result.Add(new DisplayLine(DisplayLineKind.SyntheticFiller, baseIndex - count + 1,
                    postIndex - count + 1, $"… {count} synthetic base lines …", count));
                continue;
            }

            result.Add(new DisplayLine(DisplayLineKind.Context, baseIndex + 1, postIndex + 1, postLines[postIndex]));
            baseIndex++;
            postIndex++;
        }

        while (postIndex < postLines.Count)
        {
            var baseLine = file.LineMap.BaseForPost(postIndex + 1);
            result.Add(new DisplayLine(baseLine is null ? DisplayLineKind.Added : DisplayLineKind.Context,
                baseLine, postIndex + 1, postLines[postIndex]));
            postIndex++;
        }
        return result;
    }

    public static IReadOnlyList<DisplayLine> CreateHunksOnly(
        AppliedFile file,
        int contextRows = 3,
        bool collapseSyntheticFiller = true,
        bool includeRemoved = true,
        int? revealPostLine = null)
    {
        if (contextRows < 0)
            throw new ArgumentOutOfRangeException(nameof(contextRows));
        var full = Create(file, collapseSyntheticFiller, includeRemoved, revealPostLine);
        if (full.Count == 0)
            return [];

        var visible = new bool[full.Count];
        for (var row = 0; row < full.Count; row++)
            if (full[row].PostLine == revealPostLine && revealPostLine.HasValue)
                for (var nearby = Math.Max(0, row - contextRows);
                     nearby <= Math.Min(full.Count - 1, row + contextRows); nearby++)
                    visible[nearby] = true;
        foreach (var placement in file.Hunks)
        {
            if (placement.Index < 0 || placement.Index >= file.Change.Hunks.Count)
                continue;
            var hunk = file.Change.Hunks[placement.Index];
            var baseStart = placement.FoundOldStart;
            var baseEnd = baseStart + hunk.OldCount - 1;
            var postStart = placement.PostStart;
            var postEnd = postStart + hunk.NewCount - 1;
            var matched = new List<int>();
            for (var row = 0; row < full.Count; row++)
            {
                var line = full[row];
                var inPost = line.PostLine is int post && post >= postStart && post <= postEnd;
                var inBase = hunk.OldCount > 0 && line.BaseLine is int @base && @base >= baseStart && @base <= baseEnd;
                if (inPost || inBase)
                    matched.Add(row);
            }
            if (matched.Count == 0)
                continue;
            foreach (var row in matched)
            {
                var start = Math.Max(0, row - contextRows);
                var end = Math.Min(full.Count - 1, row + contextRows);
                for (var visibleRow = start; visibleRow <= end; visibleRow++)
                    visible[visibleRow] = true;
            }
        }

        var output = new List<DisplayLine>();
        var index = 0;
        while (index < full.Count)
        {
            if (visible[index])
            {
                output.Add(full[index++]);
                continue;
            }
            var start = index;
            while (index < full.Count && !visible[index])
                index++;
            output.Add(Collapsed(index - start));
        }
        return output;
    }

    /// <summary>Golden comparison rows keep the candidate's real line numbers for navigation.</summary>
    public static IReadOnlyList<DisplayLine> CreateGoldenComparison(
        AppliedFile file, string referenceText, bool compactLargeFiles = false, int? revealPostLine = null)
    {
        var reference = DiffEngine.SplitLines(referenceText);
        var post = DiffEngine.SplitLines(file.PostText ?? "");
        var result = new List<DisplayLine>();
        foreach (var edit in DiffEngine.Lines(reference, post).Edits)
        {
            if (edit.Kind == EditKind.Delete)
            {
                for (var offset = 0; offset < edit.ALength; offset++)
                    result.Add(new DisplayLine(DisplayLineKind.Removed, null, null, reference[edit.AStart + offset]));
                continue;
            }

            var hidden = 0;
            for (var offset = 0; offset < edit.BLength; offset++)
            {
                var postLine = edit.BStart + offset + 1;
                if (compactLargeFiles && edit.Kind == EditKind.Equal && offset >= 3 &&
                    offset < edit.BLength - 3 &&
                    !(revealPostLine is int target && Math.Abs(postLine - target) <= 40))
                {
                    hidden++;
                    continue;
                }
                FlushHidden();
                result.Add(new DisplayLine(edit.Kind == EditKind.Insert ? DisplayLineKind.Added : DisplayLineKind.Context,
                    file.LineMap.BaseForPost(postLine), postLine, post[postLine - 1]));
            }
            FlushHidden();

            void FlushHidden()
            {
                if (hidden == 0) return;
                result.Add(new DisplayLine(DisplayLineKind.CollapsedContext, null, null,
                    $"… {hidden} unchanged lines outside diff hunks hidden …", hidden));
                hidden = 0;
            }
        }
        return result;
    }

    private static DisplayLine Collapsed(int count) =>
        new(DisplayLineKind.CollapsedContext, null, null,
            $"… {count} unchanged lines outside patch hunks hidden …", count);
}
