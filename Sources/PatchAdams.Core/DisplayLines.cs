namespace PatchAdams.Core;

public enum DisplayLineKind
{
    Context,
    Added,
    Removed,
    SyntheticFiller
}

public sealed record DisplayLine(DisplayLineKind Kind, int? BaseLine, int? PostLine, string Text, int CollapsedCount = 1);

public static class DisplayLines
{
    public static IReadOnlyList<DisplayLine> Create(AppliedFile file, bool collapseSyntheticFiller = true)
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
}
