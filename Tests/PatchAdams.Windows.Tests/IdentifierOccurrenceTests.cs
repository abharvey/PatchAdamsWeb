using PatchAdams.Core;

static class IdentifierOccurrenceTests
{
    public static void FixtureFindsExactWholeWords()
    {
        var lines = File.ReadAllLines(FixturePath());
        var matches = IdentifierOccurrences.Find(lines, "first");
        Assert.Equal(7, matches.Count, "all exact text occurrences, including strings and comments");
        Assert.SequenceEqual(new[] { 0, 4, 5, 6 }, matches.Select(match => match.Row).Distinct(),
            "one matching-line indicator can represent multiple occurrences");
        foreach (var match in matches)
        {
            Assert.Equal("first", lines[match.Row].Substring(match.Start, match.Length), "exact source span");
            Assert.True(IdentifierOccurrences.IsWholeIdentifier(lines[match.Row], match.Start, match.Length),
                "every occurrence is a whole identifier");
        }
        Assert.Equal(1, IdentifierOccurrences.Find(lines, "First").Count, "case-sensitive matching");
        Assert.Equal(1, IdentifierOccurrences.Find(lines, "firstValue").Count, "longer identifier remains independent");
    }

    public static void BoundariesAndSelectionRulesAreLiteral()
    {
        const string source = "count counter _count count$ $count @count Count obj.count() count";
        var matches = IdentifierOccurrences.Find([source], "count");
        Assert.Equal(3, matches.Count, "prefixes, suffixes, and other case do not match");
        Assert.True(!IdentifierOccurrences.IsWholeIdentifier("firstValue", 0, 5), "partial identifier not activated");
        Assert.True(!IdentifierOccurrences.IsWholeIdentifier("12", 0, 2), "numbers do not activate matching");
        foreach (var invalid in new[] { "", " ", "two words", "a\nb", "a.b", "a-b", "a@b", "123", "@" })
            Assert.Equal(0, IdentifierOccurrences.Find([source], invalid).Count, $"invalid selection: {invalid}");
        Assert.Equal(1, IdentifierOccurrences.Find(["$value $values value"], "$value").Count, "dollar identifier");
        Assert.Equal(1, IdentifierOccurrences.Find(["@value value"], "@value").Count, "verbatim or annotation prefix is literal");
        Assert.Equal(2, IdentifierOccurrences.Find(["élan élan e\u0301lan"], "élan").Count,
            "ordinal matching does not normalize different Unicode spellings");
        Assert.Equal(2, IdentifierOccurrences.Find(["\U00010400name x\U00010400name \U00010400name"], "\U00010400name").Count,
            "supplementary Unicode letters respect identifier boundaries");
        Assert.True(IdentifierOccurrences.IsWholeIdentifier("e\u0301lan", 0, 5), "combining mark within an identifier");
    }

    public static void LargeScansAndCancellationAreSupported()
    {
        var lines = Enumerable.Range(0, 50_000).Select(_ => "first = first ?? last;").ToArray();
        Assert.Equal(100_000, IdentifierOccurrences.Find(lines, "first").Count, "scan retains every occurrence");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => IdentifierOccurrences.Find(lines, "first", cancellation.Token),
            "canceled scans do not publish results");
    }

    private static string FixturePath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var path = Path.Combine(root.FullName, "Fixtures", "syntax", "typescript", "occurrences.ts");
            if (File.Exists(path)) return path;
            root = root.Parent;
        }
        throw new DirectoryNotFoundException("Identifier occurrence fixture not found.");
    }
}
