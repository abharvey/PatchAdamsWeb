using System.Text.RegularExpressions;
using PatchAdams.Core;

static class AlignmentRegressionTests
{
    private static readonly Regex Declaration = new(
        @"\b(?:func|function|def|class|struct|type|interface|enum|const|let|var)\s+([A-Za-z_][A-Za-z0-9_]*)");

    public static void FixtureFallbackMatchesExhaustiveSearch()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Could not find patch fixtures.");
        var checkedRegions = 0;
        foreach (var path in Directory.GetFiles(Path.Combine(root.FullName, "Fixtures", "patches"), "*.patch"))
        {
            var patch = UnifiedDiffParser.ParseFile(path);
            var applied = ApplyEngine.Apply(patch, new InMemoryFileProvider(SyntheticBase.For(patch)));
            foreach (var file in applied.Files.Where(file => file.PostText is not null))
            {
                var lines = DiffEngine.SplitLines(file.PostText!);
                var row = lines.Select((text, index) => (text, index)).FirstOrDefault(item =>
                    !SyntheticBase.IsFiller(item.text) && !Declaration.IsMatch(item.text) &&
                    !string.IsNullOrWhiteSpace(item.text));
                if (row.text is null) continue;
                CheckFallback(lines[row.index], file.BaseText ?? "", file.Path);
                checkedRegions++;
            }
        }
        Assert.True(checkedRegions > 40, "fixture-backed fallback coverage");
    }

    public static void RandomizedFallbackMatchesExhaustiveSearch()
    {
        var random = new Random(84319);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            string Line() => $"value{random.Next(4)} = input{random.Next(4)} + {random.Next(4)};";
            var selected = string.Join('\n', Enumerable.Range(0, random.Next(1, 5)).Select(_ => Line()));
            var target = string.Join('\n', Enumerable.Range(0, random.Next(0, 12)).Select(_ => Line()));
            CheckFallback(selected, target, "random.txt");
        }
        CheckFallback("value = 1;", "value=1;\nvalue = 1;", "whitespace.txt");
        CheckFallback("value = 1;", "value = 2;\nvalue = 2;", "ties.txt");
        CheckFallback(" \n\t", "\t\n ", "blank.txt");
    }

    private static void CheckFallback(string selected, string targetText, string path)
    {
        var source = AppliedFile.Unchanged(path, selected) with { LineMap = LineMap.Empty };
        var target = AppliedFile.Unchanged(path, targetText);
        var selectedCount = DiffEngine.SplitLines(selected).Count;
        var result = AlignmentEngine.Locate(new Region(path, 1, selectedCount), source, target);
        var targetLines = DiffEngine.SplitLines(targetText);
        var bestScore = -1d;
        var bestStart = 0;
        string bestText = "";
        var normalized = Normalize(selected);
        for (var start = 0; start < targetLines.Count; start++)
        {
            var text = string.Join('\n', targetLines.Skip(start).Take(selectedCount));
            var difference = DiffEngine.Tokens(selected, text);
            if (normalized == Normalize(text))
            {
                bestScore = 1;
                bestStart = start + 1;
                bestText = text;
                break;
            }
            if (difference.Similarity > bestScore)
            {
                bestScore = difference.Similarity;
                bestStart = start + 1;
                bestText = text;
            }
        }
        if (bestScore < AlignmentEngine.MinimumConfidence)
        {
            Assert.Equal(AlignmentStatus.Absent, result.Status, $"{path}: low confidence remains absent");
            return;
        }
        var expectedStatus = bestStart != 1 ? AlignmentStatus.Moved
            : normalized == Normalize(bestText) ? AlignmentStatus.Exact : AlignmentStatus.Modified;
        Assert.Equal(expectedStatus, result.Status, $"{path}: fallback status");
        Assert.Equal((int?)bestStart, result.StartLine, $"{path}: earliest winning window");
        Assert.Equal(bestScore, result.Confidence, $"{path}: fallback confidence");
        var expectedDiff = DiffEngine.Tokens(selected, bestText);
        Assert.SequenceEqual(expectedDiff.A, result.Difference!.A, $"{path}: source tokens");
        Assert.SequenceEqual(expectedDiff.B, result.Difference.B, $"{path}: target tokens");
        Assert.SequenceEqual(expectedDiff.Edits, result.Difference.Edits, $"{path}: detailed token diff");
    }

    public static void CancellationStopsWork()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var file = AppliedFile.Unchanged("test.txt", "value = 1;");
        Assert.Throws<OperationCanceledException>(() =>
            AlignmentEngine.Locate(new Region(file.Path, 1, 1), file, file,
                cancellationToken: cancelled.Token), "cancelled alignment");
        Assert.Throws<OperationCanceledException>(() =>
            DiffEngine.Tokens("left", "right", cancelled.Token), "cancelled token diff");

        using var running = new CancellationTokenSource();
        var left = string.Join(' ', Enumerable.Range(0, 20_000).Select(index => $"left{index}"));
        var right = string.Join(' ', Enumerable.Range(0, 20_000).Select(index => $"right{index}"));
        var task = Task.Run(() => DiffEngine.Tokens(left, right, running.Token));
        running.CancelAfter(30);
        Assert.Throws<OperationCanceledException>(() =>
            task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(), "running token diff cancellation");
    }

    private static string Normalize(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
