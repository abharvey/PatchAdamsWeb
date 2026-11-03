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
        bool includeRemoved = true)
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
                postIndex < postLines.Count && SyntheticBase.IsFiller(postLines[postIndex]))
            {
                var count = 0;
                while (baseIndex < baseLines.Count && postIndex < postLines.Count &&
                       file.LineMap.PostForBase(baseIndex + 1) == postIndex + 1 &&
                       SyntheticBase.IsFiller(baseLines[baseIndex]) && SyntheticBase.IsFiller(postLines[postIndex]))
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
        bool includeRemoved = true)
    {
        if (contextRows < 0)
            throw new ArgumentOutOfRangeException(nameof(contextRows));
        var full = Create(file, collapseSyntheticFiller, includeRemoved);
        if (full.Count == 0)
            return [];
        if (file.Hunks.Count == 0)
            return [Collapsed(full.Count)];

        var visible = new bool[full.Count];
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

    private static DisplayLine Collapsed(int count) =>
        new(DisplayLineKind.CollapsedContext, null, null,
            $"… {count} unchanged lines outside patch hunks hidden …", count);
}
