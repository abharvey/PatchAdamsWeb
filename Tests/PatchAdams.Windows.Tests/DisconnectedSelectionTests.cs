using System.Text;
using System.Text.Json.Nodes;
using PatchAdams.Core;

static class DisconnectedSelectionTests
{
    private static readonly LineRange[] Example =
    [
        new(new LineSpan(20, 21)),
        new(new LineSpan(100, 105)),
        new(new LineSpan(40, 41), IsBase: true)
    ];

    public static void NormalizeWithoutGaps()
    {
        var normalized = SelectionRanges.Normalize(
        [
            Example[1], new(new LineSpan(104, 108)), Example[0], Example[2],
            new(new LineSpan(22, 22)), Example[0]
        ]);
        Assert.SequenceEqual(new[]
        {
            new LineRange(new LineSpan(20, 22)),
            new LineRange(new LineSpan(100, 108)),
            Example[2]
        }, normalized, "sort, merge overlaps/adjacency, deduplicate, and keep sides separate");
        Assert.True(!normalized.Any(range => !range.IsBase && range.Lines.Contains(50)), "gap is not selected");
        Assert.Equal("file.cs L20:L21, L100:L105, -L40:L41",
            ReferenceGenerator.ForRanges(Example, "file.cs"), "every reference range is explicit");
        Assert.Throws<ArgumentException>(() => LineSpan.Normalize([new LineSpan(0, 4)]), "one-based ranges");
        Assert.Throws<ArgumentException>(() => LineSpan.Normalize([new LineSpan(4, 3)]), "ordered ranges");
    }

    public static void FixtureSelectionsKeepExactLines()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Could not find fixtures.");
        var patch = UnifiedDiffParser.ParseFile(Path.Combine(root.FullName, "Fixtures", "patches", "kettle-1.patch"));
        var applied = ApplyEngine.Apply(patch, new InMemoryFileProvider(SyntheticBase.For(patch)));
        var checkedFiles = 0;
        foreach (var file in applied.Files)
        {
            var display = DisplayLines.Create(file);
            var candidates = display.Where(line => line.Kind is DisplayLineKind.Added or DisplayLineKind.Removed).ToArray();
            if (candidates.Length < 3) continue;
            var selected = new[] { candidates[0], candidates[^1] };
            var ranges = SelectionRanges.FromDisplayLines(selected);
            Assert.True(selected.All(line => ranges.Any(range => range.Contains(line))),
                $"{file.Path}: selected fixture rows remain anchored");
            foreach (var line in display.Where(line => line.Kind != DisplayLineKind.SyntheticFiller))
                Assert.Equal(selected.Contains(line), ranges.Any(range => range.Contains(line)),
                    $"{file.Path}: no unselected row enters the finding");
            checkedFiles++;
        }
        Assert.True(checkedFiles > 2, "multiple fixture files cover disconnected anchors");
        Assert.Equal(0, SelectionRanges.FromDisplayLines(
            [new DisplayLine(DisplayLineKind.SyntheticFiller, 1, 1, "hidden", 20)]).Count,
            "a collapsed summary is not a selected source line");
    }

    public static void RoundTripAndExport()
    {
        using var temporary = new TemporaryDirectory();
        var session = CreateSession();
        SessionPackage.Write(new SessionContents(session, new Dictionary<string, string> { ["A"] = "patch data" }),
            temporary.Path);
        var loaded = SessionPackage.Read(temporary.Path).Session;
        Assert.Equal(2, loaded.Version, "new schema prevents older readers from discarding extra ranges");
        Assert.SequenceEqual(Example, loaded.Findings[0].SelectedRanges, "finding ranges survive save/load");
        Assert.SequenceEqual(Example, loaded.Pins[0].SelectedRanges, "pin ranges survive save/load");
        Assert.Contains("file.cs L20:L21, L100:L105, -L40:L41", MarkdownExport.Render(loaded),
            "Markdown preserves disconnected references");
        var json = JsonNode.Parse(Encoding.UTF8.GetString(MarkdownExport.RenderJson(loaded)))!;
        Assert.Equal(3, json["session"]!["findings"]![0]!["ranges"]!.AsArray().Count, "JSON exports every range");
        Assert.Equal(20, loaded.Findings[0].Lines.Lower, "legacy field stores the first range, never a bounding envelope");
        var manifestPath = Path.Combine(temporary.Path, "session.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["findings"]![0]!["ranges"] = new JsonArray();
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SessionPackage.Read(temporary.Path), "reject empty explicit ranges");
    }

    public static void LegacySessionsRemainReadable()
    {
        using var temporary = new TemporaryDirectory();
        SessionPackage.Write(new SessionContents(CreateSession(),
            new Dictionary<string, string> { ["A"] = "patch data" }), temporary.Path);
        var manifestPath = Path.Combine(temporary.Path, "session.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["version"] = 1;
        var finding = manifest["findings"]![0]!.AsObject();
        finding.Remove("ranges");
        finding["reference"] = "file.cs L20:L21";
        var pin = manifest["pins"]![0]!.AsObject();
        pin.Remove("ranges");
        pin["reference"] = "file.cs -L20:L21";
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var loaded = SessionPackage.Read(temporary.Path).Session;
        Assert.SequenceEqual(new[] { Example[0] }, loaded.Findings[0].SelectedRanges, "legacy post range");
        Assert.SequenceEqual(new[] { new LineRange(Example[0].Lines, IsBase: true) },
            loaded.Pins[0].SelectedRanges, "legacy removed-side range");
    }

    public static void CompactViewsRevealAllRanges()
    {
        var text = string.Join('\n', Enumerable.Range(1, 50_001).Select(line => $"line-{line}"));
        var file = AppliedFile.Unchanged("large.txt", text);
        LineRange[] ranges = [new(new LineSpan(20, 21)), new(new LineSpan(40_000, 40_002))];
        var rows = DisplayLines.CreateHunksOnly(file, contextRows: 1, revealRanges: ranges);
        foreach (var line in new[] { 20, 21, 40_000, 40_001, 40_002 })
            Assert.True(rows.Any(row => row.PostLine == line), "every disconnected range is revealed");
        Assert.True(rows.Count < 20, "restoring ranges does not expand the entire large file");

        var removed = AppliedFile.Deleted("removed.txt", text);
        var removedRanges = ranges.Select(range => range with { IsBase = true }).ToArray();
        var removedRows = DisplayLines.CreateHunksOnly(removed, contextRows: 1,
            includeRemoved: false, revealRanges: removedRanges);
        Assert.Equal(5, removedRows.Count(row => row.Kind == DisplayLineKind.Removed),
            "saved removed ranges are revealed even when removed lines are hidden");

        var synthetic = AppliedFile.Unchanged("synthetic.txt",
            string.Join('\n', Enumerable.Range(1, 500).Select(SyntheticBase.Filler)));
        var syntheticRows = DisplayLines.Create(synthetic, revealRanges:
            [new(new LineSpan(20, 21)), new(new LineSpan(400, 401))]);
        Assert.True(new[] { 20, 21, 400, 401 }.All(line => syntheticRows.Any(row =>
            row.Kind == DisplayLineKind.Context && row.PostLine == line)), "synthetic gaps reveal all saved lines");
    }

    private static ReviewSession CreateSession()
    {
        var reference = ReferenceGenerator.ForRanges(Example, "file.cs");
        var finding = new SessionFinding(Guid.NewGuid(), "A", "src/file.cs", Example[0].Lines, reference,
            "Related lines", "One finding spans separate regions.", "other", FindingSeverity.Minor,
            RequestRelevance.Unclear, false, "", [], DateTimeOffset.UtcNow, Example);
        return new ReviewSession(ReviewSession.CurrentVersion, "Disconnected selection", "",
            new SessionBase("synthetic"), [new SessionColumn("A", "test.patch", "")], null,
            ReviewSession.DefaultCategories, [finding],
            new SessionRanking(new Dictionary<string, int>(), ""),
            [new SessionPin(Guid.NewGuid(), "A", "src/file.cs", Example[0].Lines, reference, "selected lines", Example)],
            [], new BlindMode(false, 1), DateTimeOffset.UtcNow);
    }
}
