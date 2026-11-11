using PatchAdams.Core;

static class PatchLabelAssignmentTests
{
    public static void DownloadNamesIgnoreSelectionOrder()
    {
        foreach (var space in new[] { "", " " })
        foreach (var stem in new[] { "feather", "CandidateA" })
        {
            var names = new[] { $"{stem}.patch", $"{stem}{space}(1).patch", $"{stem}{space}(2).patch", $"{stem}{space}(3).patch" };
            for (var count = 2; count <= 4; count++)
            foreach (var order in Permutations(Enumerable.Range(0, count).ToArray()))
            {
                var actual = PatchLabelAssignment.Infer(order.Select(index => names[index]).ToArray());
                Assert.True(actual is not null, "download family is recognized, even with conflicting capital letters");
                Assert.SequenceEqual(order.Select(index => PatchLabelAssignment.Labels[index]), actual!,
                    "download identities do not depend on picker or drop order");
            }
        }
        Assert.SequenceEqual(new[] { "D", "A", "B" },
            PatchLabelAssignment.Infer(["FEATHER (3).PATCH", "feather.patch", "feather(1).diff"])!,
            "download stems are case insensitive, allow missing candidates, and ignore extension");
    }

    public static void LetterNamesRetainIdentity()
    {
        foreach (var names in new[]
        {
            new[] { "featherC.patch", "A-feather.patch", "feather_B_review.diff", "reviewDfinal.patch" },
            new[] { "feather_c.patch", "a-feather.PATCH", "feather (b).diff", "review-d-final.patch" }
        })
        {
            Assert.SequenceEqual(new[] { "C", "A", "B", "D" }, PatchLabelAssignment.Infer(names)!,
                "prefix, suffix, and embedded uppercase letters or standalone lowercase markers");
        }
        Assert.SequenceEqual(new[] { "D", "B" }, PatchLabelAssignment.Infer(["D.patch", "B.patch"])!,
            "labels identify candidates rather than forcing a contiguous selection");
        Assert.SequenceEqual(new[] { "A", "B" }, PatchLabelAssignment.Infer(["A-A.patch", "B-B.patch"])!,
            "repeated mentions of the same letter are not conflicting identities");
    }

    public static void UncertainNamesRequireManualAssignment()
    {
        var uncertain = new[]
        {
            new[] { "feather.patch", "other.patch" },
            new[] { "feather.patch", "feather(4).patch" },
            new[] { "feather(1).patch", "feather(2).patch" },
            new[] { "feather.patch", "other(1).patch" },
            new[] { "feather.patch", "feather(1).patch", "other.patch" },
            new[] { "A.patch", "anotherA.patch" },
            new[] { "A-B.patch", "C.patch" },
            new[] { "CandidateA.patch", "B.patch" },
            new[] { "A.patch", "unknown.patch" },
            new[] { "feather.patch", "badger.patch" },
            new[] { "same.A", "unknown.B" }
        };
        foreach (var names in uncertain)
            Assert.True(PatchLabelAssignment.Infer(names) is null, $"ask instead of guessing: {string.Join(", ", names)}");
        Assert.True(PatchLabelAssignment.Infer([
            Path.Combine("A", "feather.patch"), Path.Combine("B", "other.patch")]) is null,
            "directory letters do not identify a patch");
        Assert.True(PatchLabelAssignment.Infer([
            Path.Combine("first", "A.patch"), Path.Combine("second", "A.patch")]) is null,
            "duplicate download names in different directories do not imply different candidates");
        Assert.True(PatchLabelAssignment.Infer(["A.patch", "A(1).patch", "A (1).patch"]) is null,
            "duplicate download identities are not reinterpreted as letters");
        Assert.Throws<ArgumentException>(() => PatchLabelAssignment.Infer(["A.patch"]), "reject fewer than two");
        Assert.Throws<ArgumentException>(() => PatchLabelAssignment.Infer(["A", "B", "C", "D", "E"]),
            "reject more than four");
    }

    public static void ManualAssignmentsMustBeCompleteAndUnique()
    {
        foreach (var labels in new string?[][]
        {
            ["A", null], ["A", "A"], ["A", ""], ["A", "E"], ["a", "b"], ["A"], ["A", "B", "C", "D", "A"]
        })
            Assert.True(!PatchLabelAssignment.IsValid(labels), "incomplete, duplicate, or invalid assignments cannot load");
        Assert.True(PatchLabelAssignment.IsValid(["C", "A", "B"]), "manual assignments retain file correspondence");
        Assert.True(PatchLabelAssignment.IsValid(["D", "B"]), "any unique candidate subset is valid");
    }

    public static void FixtureIdentitySurvivesSessionSave()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Could not find fixtures.");
        var originalTexts = Enumerable.Range(1, 3).Select(index =>
            File.ReadAllText(Path.Combine(root.FullName, "Fixtures", "patches", $"kettle-{index}.patch"))).ToArray();
        foreach (var names in new[]
        {
            new[] { "feather(1).patch", "feather(2).patch", "feather.patch" },
            new[] { "reviewB.patch", "reviewC.patch", "reviewA.patch" },
            new[] { "unknown-one.patch", "unknown-two.patch", "unknown-three.patch" }
        })
        {
            var labels = PatchLabelAssignment.Infer(names) ?? new[] { "B", "C", "A" };
            Assert.True(PatchLabelAssignment.IsValid(labels), "inferred and manual assignments share validation");
            var inputs = new[] { originalTexts[1], originalTexts[2], originalTexts[0] };
            var columns = names.Select((name, index) => new SessionColumn(labels[index], name, ""))
                .OrderBy(column => column.Label, StringComparer.Ordinal).ToArray();
            var texts = inputs.Select((text, index) => (Label: labels[index], Text: text))
                .ToDictionary(item => item.Label, item => item.Text);
            var patches = texts.Values.Select(text => UnifiedDiffParser.Parse(text)).ToArray();
            var provider = new InMemoryFileProvider(SyntheticBase.Merge(patches), "assigned fixtures");
            foreach (var patch in patches)
                Assert.Equal(0, ApplyEngine.Apply(patch, provider).Report.Failed, "assigned fixture still applies in memory");
            var session = new ReviewSession(ReviewSession.CurrentVersion, "Patch identities", "",
                new SessionBase("synthetic"), columns, null, ReviewSession.DefaultCategories, [],
                new SessionRanking(new Dictionary<string, int>(), ""), [], [],
                new BlindMode(true, 42), DateTimeOffset.UtcNow);
            using var temporary = new TemporaryDirectory();
            SessionPackage.Write(new SessionContents(session, texts), temporary.Path);
            var reopened = SessionPackage.Read(temporary.Path);
            Assert.SequenceEqual(new[] { "A", "B", "C" }, reopened.Session.Columns.Select(column => column.Label),
                "columns open in candidate order");
            for (var index = 0; index < originalTexts.Length; index++)
                Assert.Equal(originalTexts[index], reopened.PatchTexts[PatchLabelAssignment.Labels[index]],
                    "saving and blind display cannot detach a label from its original fixture content");
            Assert.Equal(names[0], reopened.Session.Columns.Single(column => column.Label == "B").SourceName,
                "original filename stays attached to candidate B");
        }
    }

    private static IEnumerable<int[]> Permutations(int[] values)
    {
        if (values.Length == 0) yield return [];
        for (var index = 0; index < values.Length; index++)
        foreach (var rest in Permutations(values.Where((_, position) => position != index).ToArray()))
            yield return [values[index], .. rest];
    }
}
