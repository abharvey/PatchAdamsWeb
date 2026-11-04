using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Formats.Tar;
using System.Xml.Linq;
using PatchAdams.Core;
using PatchAdamsSharp;

var tests = new (string Name, Action Run)[]
{
    ("fixture inventory and file counts remain stable", FixtureTests.InventoryAndFileCountsRemainStable),
    ("every fixture hunk agrees with its declared line counts", FixtureTests.HunkCountsAgree),
    ("rename fixture retains old path, new path, and similarity", FixtureTests.RenameMetadataRemainsPresent),
    ("strict parser parses every fixture", FixtureTests.ParserParsesEveryFixture),
    ("format-patch mail metadata and body are preserved", FixtureTests.MailHeaderPreservesBody),
    ("added, deleted, binary, rename, and mode-change headers parse", FixtureTests.FileMetadataParses),
    ("test-file heuristic filters common test paths", FixtureTests.TestPathsAreRecognized),
    ("quoted paths containing spaces parse", FixtureTests.QuotedPathsParse),
    ("strict parser reports malformed hunk line", FixtureTests.MalformedHunkReportsItsLine),
    ("directory bases read files without escaping their root", BaseLoaderTests.DirectoryReadsAndContainsPaths),
    ("zip bases strip one common top-level folder", BaseLoaderTests.ZipStripsCommonPrefix),
    ("tar and gzip tar bases expose text files", BaseLoaderTests.TarAndGzipTarExposeFiles),
    ("archive paths cannot escape the archive root", BaseLoaderTests.ArchiveRejectsEscapingPath),
    ("git revisions are read by approved read-only commands", BaseLoaderTests.GitRevisionIsReadOnly),
    ("patches apply in memory with line maps and status reports", ApplyEngineTests.ApplyIsInMemoryAndMapsLines),
    ("unmodified files retain identity line maps", ApplyEngineTests.UnchangedFilesRetainIdentityMap),
    ("context fuzz is reported without writing the base", ApplyEngineTests.FuzzIsReported),
    ("failed hunks keep raw patch data and no post text", ApplyEngineTests.FailedHunkKeepsPatch),
    ("every fixture applies cleanly to its synthetic base", ApplyEngineTests.EveryFixtureAppliesToSyntheticBase),
    ("merged kettle patches share one synthetic base", ApplyEngineTests.MergedKettlePatchesApply),
    ("Myers diff is minimal and replays to the target", DiffEngineTests.MyersDiffIsMinimalAndReplays),
    ("Myers diff replays deterministic randomized inputs", DiffEngineTests.MyersReplaysRandomizedInputs),
    ("patience diff anchors unique lines around reordered blocks", DiffEngineTests.PatienceDiffRetainsMovedAnchors),
    ("token similarity ignores whitespace", DiffEngineTests.TokenSimilarityIgnoresWhitespace),
    ("syntax highlighter identifies code tokens by extension", DiffEngineTests.SyntaxTokensAreClassified),
    ("alignment classifies exact, modified, moved, and absent regions", AlignmentTests.ClassifiesRegions),
    ("manual anchor correction takes precedence over automatic location", AlignmentTests.ManualAnchorWins),
    ("session packages round-trip patches and review state", SessionTests.PackageRoundTripsState),
    ("older sessions reopen with default patch layout", SessionTests.OlderSessionsKeepDefaultLayout),
    ("session package rejects patch paths outside its folder", SessionTests.PackageRejectsEscapingPatchPaths),
    ("blind export reveals its mapping only at the end", SessionTests.BlindExportRevealsMappingLast),
    ("references are generated from post-patch selections", SessionTests.ReferenceIsGeneratedFromSelection),
    ("display lines retain dual gutters and removed-side references", SessionTests.DisplayLinesAndRemovedReference),
    ("deleted files still display their removed base lines", SessionTests.DeletedFileDisplaysBaseLines),
    ("large files collapse to patch hunks with context", SessionTests.HunksOnlyDisplayCollapsesContext),
    ("One Dark input and text colors meet AA contrast", ThemeContrastTests.OneDarkContrastMeetsAA),
    ("finding fields have visible labels and dark combo templates", ThemeContrastTests.FindingInputsAreLabeled),
    ("balance widths allocates current panel space by code demand", ColumnWidthTests.DistributesCurrentSpace),
    ("balance widths ignores an exceptionally long line", ColumnWidthTests.OutliersDoNotDominate),
    ("Files and Review expose drag handles", LayoutPreferencesTests.SidebarsHaveSplitters),
    ("saved sidebar widths preserve old preferences", LayoutPreferencesTests.SidebarWidthsRoundTrip),
    ("compact menus keep commands and request toggle accessible", LayoutPreferencesTests.CompactMenusPreserveCommands),
    ("safety contract excludes code execution and network access", FeatureContractTests.SafetyContractRemainsExplicit),
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failures.Add($"{name}: {error.Message}");
        Console.WriteLine($"FAIL {name}: {error.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static class FixtureTests
{
    private static readonly IReadOnlyDictionary<string, int> ExpectedFiles =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["kettle-1.patch"] = 12,
            ["kettle-2.patch"] = 11,
            ["kettle-3.patch"] = 12,
            ["kettle-4.patch"] = 13,
            ["lantern-1.patch"] = 9,
            ["lantern-2.patch"] = 7,
            ["lantern-3.patch"] = 9,
            ["scaffold.patch"] = 7,
            ["rename.patch"] = 4,
            ["mail.patch"] = 6,
        };

    private static readonly Regex HunkHeader = new(
        @"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void InventoryAndFileCountsRemainStable()
    {
        var fixtureDirectory = FindRepositoryRoot();
        var actual = Directory.GetFiles(fixtureDirectory, "*.patch")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = ExpectedFiles.Keys.Order(StringComparer.Ordinal).ToArray();
        Assert.SequenceEqual(expected, actual, "Patch fixture names changed; update this baseline intentionally.");

        foreach (var (name, expectedCount) in ExpectedFiles)
        {
            var content = File.ReadAllText(Path.Combine(fixtureDirectory, name));
            var actualCount = Regex.Matches(content, @"^diff --git ", RegexOptions.Multiline).Count;
            Assert.Equal(expectedCount, actualCount, $"{name} file count");
        }
    }

    public static void HunkCountsAgree()
    {
        var fixtureDirectory = FindRepositoryRoot();
        foreach (var name in ExpectedFiles.Keys)
        {
            var lines = File.ReadAllLines(Path.Combine(fixtureDirectory, name));
            for (var index = 0; index < lines.Length; index++)
            {
                var match = HunkHeader.Match(lines[index]);
                if (!match.Success)
                    continue;

                var expectedOld = Count(match, "oldCount");
                var expectedNew = Count(match, "newCount");
                var actualOld = 0;
                var actualNew = 0;
                for (var body = index + 1; body < lines.Length; body++)
                {
                    var line = lines[body];
                    if (line.StartsWith("@@", StringComparison.Ordinal) ||
                        line.StartsWith("diff --git ", StringComparison.Ordinal))
                        break;
                    if (line.Length == 0)
                        continue;
                    switch (line[0])
                    {
                        case ' ':
                            actualOld++;
                            actualNew++;
                            break;
                        case '-':
                            actualOld++;
                            break;
                        case '+':
                            actualNew++;
                            break;
                        case '\\':
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"{name}:{body + 1}: unexpected hunk line {line}");
                    }
                }

                Assert.Equal(expectedOld, actualOld, $"{name}:{index + 1} old-line count");
                Assert.Equal(expectedNew, actualNew, $"{name}:{index + 1} new-line count");
            }
        }
    }

    public static void RenameMetadataRemainsPresent()
    {
        var fixture = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "rename.patch"));
        Assert.Contains("rename from tests/billing/test_ledger.py", fixture, "rename source path");
        Assert.Contains("rename to tests/test_ledger.py", fixture, "rename destination path");
        Assert.Contains("similarity index 95%", fixture, "rename similarity");
    }

    public static void ParserParsesEveryFixture()
    {
        var fixtureDirectory = FindRepositoryRoot();
        foreach (var (name, expectedCount) in ExpectedFiles)
        {
            var patch = UnifiedDiffParser.ParseFile(Path.Combine(fixtureDirectory, name));
            Assert.Equal(expectedCount, patch.Files.Count, $"{name} parsed file count");
            foreach (var file in patch.Files)
            foreach (var hunk in file.Hunks)
            {
                Assert.Equal(hunk.OldCount,
                    hunk.Lines.Count(line => line.Kind != PatchLineKind.Added),
                    $"{name}:{file.Path} old hunk count");
                Assert.Equal(hunk.NewCount,
                    hunk.Lines.Count(line => line.Kind != PatchLineKind.Removed),
                    $"{name}:{file.Path} new hunk count");
            }
        }

        var rename = UnifiedDiffParser.ParseFile(Path.Combine(fixtureDirectory, "rename.patch"))
            .Files.Single(file => file.Kind == FileChangeKind.Renamed);
        Assert.Equal("tests/billing/test_ledger.py", rename.OldPath, "rename old path");
        Assert.Equal("tests/test_ledger.py", rename.NewPath, "rename new path");
        Assert.Equal(95, rename.Similarity, "rename similarity");
    }

    public static void MailHeaderPreservesBody()
    {
        var patch = UnifiedDiffParser.ParseFile(Path.Combine(FindRepositoryRoot(), "mail.patch"));
        Assert.Equal("Fixture Author <fixtures@example.invalid>", patch.MailHeader?.From, "mail author");
        Assert.Equal("[PATCH] Cap tile zoom at 18", patch.MailHeader?.Subject, "mail subject");
        Assert.Equal("Deeper tiles are never rendered; refuse them early.", patch.MailHeader?.Body, "mail body");
    }

    public static void FileMetadataParses()
    {
        var fixtureDirectory = FindRepositoryRoot();
        var scaffold = UnifiedDiffParser.ParseFile(Path.Combine(fixtureDirectory, "scaffold.patch"));
        var added = scaffold.Files.Single(file => file.Path == "AGENTS.md");
        Assert.Equal(FileChangeKind.Added, added.Kind, "new file kind");
        Assert.Equal<string?>(null, added.OldPath, "new file old path");
        Assert.Equal("100644", added.NewMode, "new file mode");
        var binary = scaffold.Files.Single(file => file.Path == "assets/logo.png");
        Assert.True(binary.IsBinary, "binary file stub");

        var rename = UnifiedDiffParser.ParseFile(Path.Combine(fixtureDirectory, "rename.patch"));
        var deleted = rename.Files.Single(file => file.Kind == FileChangeKind.Deleted);
        Assert.Equal("ledger/legacy.py", deleted.OldPath, "deleted path");
        Assert.Equal<string?>(null, deleted.NewPath, "deleted destination");
        var modeChange = scaffold.Files.Single(file => file.Path == "tools/run.sh");
        Assert.Equal("100644", modeChange.OldMode, "old mode");
        Assert.Equal("100755", modeChange.NewMode, "new mode");
    }

    public static void QuotedPathsParse()
    {
        const string diff = """
            diff --git "a/source file.txt" "b/source file.txt"
            --- "a/source file.txt"
            +++ "b/source file.txt"
            @@ -1 +1 @@
            -old
            +new
            """;
        var change = UnifiedDiffParser.Parse(diff).Files.Single();
        Assert.Equal("source file.txt", change.Path, "quoted path");
        Assert.Equal("source file.txt", change.FileName, "quoted file name");
    }

    public static void TestPathsAreRecognized()
    {
        Assert.True(FileChange.IsTestPath("tests/billing/test_ledger.py"), "tests directory path");
        Assert.True(FileChange.IsTestPath("internal/pathutil/pathutil_test.go"), "Go test suffix");
        Assert.True(FileChange.IsTestPath("src/review.test.ts"), "TypeScript test suffix");
        Assert.True(!FileChange.IsTestPath("src/testutils/runner.cs"), "testutils is not a test folder");
        Assert.True(!FileChange.IsTestPath("src/main.go"), "ordinary source file");
    }

    public static void MalformedHunkReportsItsLine()
    {
        const string input = """
            diff --git a/example.txt b/example.txt
            --- a/example.txt
            +++ b/example.txt
            @@ -1,2 +1,2 @@
             old
            +new
            """;
        try
        {
            UnifiedDiffParser.Parse(input);
        }
        catch (PatchParseException error)
        {
            Assert.Equal(7, error.Line, "malformed hunk source line");
            return;
        }
        throw new InvalidOperationException("Expected malformed hunk to be rejected.");
    }

    private static int Count(Match match, string group) =>
        match.Groups[group].Success ? int.Parse(match.Groups[group].Value) : 1;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Fixtures", "patches");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Fixtures/patches from the test output directory.");
    }
}

static class SessionTests
{
    public static void PackageRejectsEscapingPatchPaths()
    {
        using var temporary = new TemporaryDirectory();
        var session = CreateSession(blind: false) with
        {
            Columns =
            [
                new SessionColumn("A", "patch-a.diff", "../outside.diff"),
                new SessionColumn("B", "patch-b.diff", "B-patch-b.diff")
            ]
        };
        File.WriteAllBytes(Path.Combine(temporary.Path, "session.json"), SessionPackage.Encode(session));
        Assert.Throws<InvalidDataException>(() => SessionPackage.Read(temporary.Path),
            "session patch path traversal rejected");
    }

    public static void PackageRoundTripsState()
    {
        using var temporary = new TemporaryDirectory();
        var session = CreateSession(blind: false) with
        {
            Columns =
            [
                new SessionColumn("A", "patch-a.diff", "", IsHidden: true,
                    CollapsedTo: CollapseSide.Left, WidthWeight: 2.5),
                new SessionColumn("B", "patch-b.diff", "", WidthWeight: 1.25)
            ]
        };
        SessionPackage.Write(new SessionContents(session,
            new Dictionary<string, string> { ["A"] = "diff A", ["B"] = "diff B" }), temporary.Path);
        var loaded = SessionPackage.Read(temporary.Path);

        Assert.Equal(2, loaded.Session.Columns.Count, "restored columns");
        Assert.Equal("A-patch-a.diff", loaded.Session.Columns[0].PatchFile, "safe copied patch name");
        Assert.Equal("diff A", loaded.PatchTexts["A"], "restored patch text");
        Assert.Equal("main.cs L4:L6", loaded.Session.Findings.Single().Reference, "restored finding reference");
        Assert.Equal(2, loaded.Session.Pins.Count, "restored pins");
        Assert.Equal(2, loaded.Session.ManualAnchors.Count, "restored anchors");
        Assert.Equal("quality first", loaded.Session.Ranking.Rationale, "restored ranking");
        Assert.True(loaded.Session.Columns[0].IsHidden, "restored collapsed column");
        Assert.Equal(CollapseSide.Left, loaded.Session.Columns[0].CollapsedTo, "restored collapse side");
        Assert.Equal(2.5, loaded.Session.Columns[0].WidthWeight, "restored column width");
    }

    public static void OlderSessionsKeepDefaultLayout()
    {
        using var temporary = new TemporaryDirectory();
        var session = CreateSession(blind: false);
        SessionPackage.Write(new SessionContents(session,
            new Dictionary<string, string> { ["A"] = "diff A", ["B"] = "diff B" }), temporary.Path);
        var manifestPath = Path.Combine(temporary.Path, "session.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        foreach (var column in manifest["columns"]!.AsArray())
        {
            column!.AsObject().Remove("collapsedTo");
            column.AsObject().Remove("widthWeight");
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var reopened = SessionPackage.Read(temporary.Path);
        Assert.Equal(CollapseSide.Right, reopened.Session.Columns[0].CollapsedTo, "old session collapse side");
        Assert.Equal(1d, reopened.Session.Columns[0].WidthWeight, "old session width weight");
    }

    public static void BlindExportRevealsMappingLast()
    {
        var markdown = MarkdownExport.Render(CreateSession(blind: true),
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        var mapping = markdown.IndexOf("## Mapping", StringComparison.Ordinal);
        Assert.True(mapping > markdown.IndexOf("## Rationale", StringComparison.Ordinal), "mapping appears after rationale");
        Assert.True(!markdown.Contains("patch-a.diff", StringComparison.Ordinal) ||
                    markdown.LastIndexOf("patch-a.diff", StringComparison.Ordinal) > mapping,
            "patch source names stay hidden until mapping");
        Assert.Contains("| Rank | Patch |", markdown, "blind ranking header");

        var json = System.Text.Encoding.UTF8.GetString(MarkdownExport.RenderJson(CreateSession(blind: true)));
        Assert.Contains("\"blindMapping\"", json, "blind mapping in JSON export");
        Assert.Contains("\"confidence\"", json, "finding anchor confidence in JSON export");
    }

    public static void ReferenceIsGeneratedFromSelection()
    {
        var change = new FileChange("src/main.cs", "src/main.cs", FileChangeKind.Modified,
            null, null, null, false, []);
        var file = new AppliedFile(change, "one\ntwo\n", "one\ntwo\n",
            new ApplyStatus(ApplyStatusKind.Clean), new LineMap([1, 2], [1, 2]), []);
        Assert.Equal("main.cs L1:L2", ReferenceGenerator.ForSelection(file, 1, 2), "generated file-name-only reference");
        Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceGenerator.ForSelection(file, 0, 2),
            "invalid selection rejected");
    }

    public static void DisplayLinesAndRemovedReference()
    {
        using var baseDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(baseDirectory.Path, "main.cs"), "before\nold\ncontext\n");
        var patch = UnifiedDiffParser.Parse("""
            diff --git a/main.cs b/main.cs
            --- a/main.cs
            +++ b/main.cs
            @@ -1,3 +1,3 @@
             before
            -old
            +new
             context
            """);
        var applied = ApplyEngine.Apply(patch, new DirectoryFileProvider(baseDirectory.Path)).Files.Single();
        var display = DisplayLines.Create(applied);
        Assert.SequenceEqual(
            [DisplayLineKind.Context, DisplayLineKind.Removed, DisplayLineKind.Added, DisplayLineKind.Context],
            display.Select(line => line.Kind), "line display kinds");
        Assert.Equal("main.cs -L2:L2",
            ReferenceGenerator.ForDisplaySelection([display[1]], "main.cs"), "removed reference");
        Assert.Equal("main.cs L2:L2",
            ReferenceGenerator.ForDisplaySelection([display[2]], "main.cs"), "post reference");
        Assert.True(DisplayLines.Create(applied, includeRemoved: false)
            .All(line => line.Kind != DisplayLineKind.Removed), "removed lines can be hidden");

        var fillerPatch = UnifiedDiffParser.Parse("""
            diff --git a/main.cs b/main.cs
            --- a/main.cs
            +++ b/main.cs
            @@ -3 +3 @@
            -context
            +changed
            """);
        var synthetic = new InMemoryFileProvider(SyntheticBase.For(fillerPatch));
        var syntheticFile = ApplyEngine.Apply(fillerPatch, synthetic).Files.Single();
        var collapsed = DisplayLines.Create(syntheticFile);
        Assert.Equal(DisplayLineKind.SyntheticFiller, collapsed[0].Kind, "unknown filler collapsed");
        Assert.Equal(2, collapsed[0].CollapsedCount, "collapsed filler count");
    }

    public static void DeletedFileDisplaysBaseLines()
    {
        using var baseDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(baseDirectory.Path, "removed.txt"), "first\nsecond\n");
        var patch = UnifiedDiffParser.Parse("""
            diff --git a/removed.txt b/removed.txt
            deleted file mode 100644
            --- a/removed.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -first
            -second
            """);
        var file = ApplyEngine.Apply(patch, new DirectoryFileProvider(baseDirectory.Path)).Files.Single();
        Assert.Equal<string?>(null, file.PostText, "deleted post text");
        Assert.SequenceEqual([1, 2], DisplayLines.Create(file).Select(line => line.BaseLine!.Value),
            "deleted display base lines");
        Assert.True(DisplayLines.Create(file).All(line => line.Kind == DisplayLineKind.Removed),
            "deleted display rows");
    }

    public static void HunksOnlyDisplayCollapsesContext()
    {
        using var baseDirectory = new TemporaryDirectory();
        var lines = Enumerable.Range(1, 100).Select(line => $"line-{line}").ToArray();
        File.WriteAllText(Path.Combine(baseDirectory.Path, "large.txt"), string.Join('\n', lines) + "\n");
        var patch = UnifiedDiffParser.Parse("""
            diff --git a/large.txt b/large.txt
            --- a/large.txt
            +++ b/large.txt
            @@ -50 +50 @@
            -line-50
            +changed-line
            """);
        var file = ApplyEngine.Apply(patch, new DirectoryFileProvider(baseDirectory.Path)).Files.Single();
        var full = DisplayLines.Create(file);
        var reduced = DisplayLines.CreateHunksOnly(file, contextRows: 1);
        Assert.True(reduced.Count < full.Count / 4, "large unchanged context is reduced");
        Assert.True(reduced.Any(line => line.Text == "changed-line"), "changed line is retained");
        Assert.True(reduced.Any(line => line.Kind == DisplayLineKind.CollapsedContext),
            "hidden unchanged ranges are visible as markers");
    }

    private static ReviewSession CreateSession(bool blind)
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var finding = new SessionFinding(Guid.NewGuid(), "A", "src/main.cs",
            new LineSpan(4, 6), "main.cs L4:L6", "Unnecessary helper", "Avoid the helper.",
            "unnecessary helpers", FindingSeverity.Material, RequestRelevance.No, false, "correctness note",
            [new FindingAnchor("B", "modified", 0.83, "main.cs L5:L7")], now);
        return new ReviewSession(ReviewSession.CurrentVersion, "Review", "Implement a feature",
            new SessionBase("directory", Path: @"C:\source"),
            [
                new SessionColumn("A", "patch-a.diff", "", IsRead: true),
                new SessionColumn("B", "patch-b.diff", "")
            ],
            "A", ReviewSession.DefaultCategories, [finding],
            new SessionRanking(new Dictionary<string, int> { ["A"] = 1, ["B"] = 1 }, "quality first"),
            [
                new SessionPin(Guid.NewGuid(), "A", "src/main.cs", new LineSpan(4, 6), "main.cs L4:L6", "selected"),
                new SessionPin(Guid.NewGuid(), "B", "src/main.cs", new LineSpan(5, 7), "main.cs L5:L7", "other")
            ],
            [
                new ManualAnchor(Guid.NewGuid(), "A", "src/main.cs", new LineSpan(4, 6), "B", new LineSpan(5, 7)),
                new ManualAnchor(Guid.NewGuid(), "B", "src/main.cs", new LineSpan(5, 7), "A", new LineSpan(4, 6))
            ],
            new BlindMode(blind, 773), now);
    }
}

static class AlignmentTests
{
    public static void ManualAnchorWins()
    {
        var source = Applied("first\nvalue = 1\nlast\n", [1, null, 3], [1, 3]);
        var target = Applied("first\nvalue = 2\nvalue = 3\nlast\n", [1, null, null, 3], [1, 4]);
        var manual = new ManualAnchor(Guid.NewGuid(), "A", "file.cs", new LineSpan(2, 2),
            "B", new LineSpan(3, 3));
        var result = AlignmentEngine.Locate(new Region("file.cs", 2, 2), source, target, manual);
        Assert.Equal(3, result.StartLine, "manual target line");
        Assert.Equal(1d, result.Confidence, "manual anchor confidence");
    }

    public static void ClassifiesRegions()
    {
        var exactSource = Applied("first\nvalue = 1\nlast\n", [1, 2, 3], [1, 2, 3]);
        var exactTarget = Applied("first\nvalue = 1\nlast\n", [1, 2, 3], [1, 2, 3]);
        Assert.Equal(AlignmentStatus.Exact,
            AlignmentEngine.Locate(new Region("file.cs", 2, 2), exactSource, exactTarget).Status,
            "exact alignment");

        var modifiedTarget = Applied("first\nvalue = 2\nlast\n", [1, null, 3], [1, 3]);
        var modified = AlignmentEngine.Locate(new Region("file.cs", 2, 2),
            Applied("first\nvalue = 1\nlast\n", [1, null, 3], [1, 3]), modifiedTarget);
        Assert.Equal(AlignmentStatus.Modified, modified.Status, "modified alignment");

        var movedTarget = Applied("first\nlast\nvalue = 1\n", [1, 2, 3], [1, 2, 3]);
        var moved = AlignmentEngine.Locate(new Region("file.cs", 2, 2),
            Applied("first\nvalue = 1\nlast\n", [1, null, 3], [1, 3]), movedTarget);
        Assert.Equal(AlignmentStatus.Moved, moved.Status, "moved alignment");
        Assert.Equal(3, moved.StartLine, "moved destination");

        var absent = AlignmentEngine.Locate(new Region("file.cs", 2, 2),
            Applied("first\nvalue = 1\nlast\n", [1, null, 3], [1, 3]),
            Applied("first\nother\nlast\n", [1, 2, 3], [1, 2, 3],
                [new HunkPlacement(0, 2, 2, 2, 0, 1)]));
        Assert.Equal(AlignmentStatus.Absent, absent.Status, "absent alignment");
        Assert.Equal("file.cs", absent.NearestHunkPath, "nearest hunk path");
    }

    private static AppliedFile Applied(string postText, int?[] postToBase, int?[] baseToPost,
        IReadOnlyList<HunkPlacement>? hunks = null)
    {
        var change = new FileChange("file.cs", "file.cs", FileChangeKind.Modified,
            null, null, null, false, []);
        return new AppliedFile(change, "first\nvalue = 1\nlast\n", postText,
            new ApplyStatus(ApplyStatusKind.Clean),
            new LineMap(postToBase, baseToPost), hunks ?? []);
    }
}

static class DiffEngineTests
{
    public static void MyersDiffIsMinimalAndReplays()
    {
        var before = new[] { "alpha", "beta", "gamma" };
        var after = new[] { "beta", "gamma", "delta" };
        var diff = DiffEngine.Lines(before, after, DiffAlgorithm.Myers);
        Assert.Equal(1, diff.DeletedCount, "minimal deleted line count");
        Assert.Equal(1, diff.InsertedCount, "minimal inserted line count");
        Assert.SequenceEqual(after, diff.Apply(before, after), "diff replay");

        var same = DiffEngine.Lines(before, before, DiffAlgorithm.Myers);
        Assert.True(same.IsIdentical, "identical inputs");
    }

    public static void PatienceDiffRetainsMovedAnchors()
    {
        var before = new[] { "first", "unique A", "middle", "unique B", "last" };
        var after = new[] { "first", "unique B", "middle", "unique A", "last" };
        var diff = DiffEngine.Lines(before, after, DiffAlgorithm.Patience);
        Assert.SequenceEqual(after, diff.Apply(before, after), "patience replay");
        Assert.True(diff.Edits.Any(edit => edit.Kind == EditKind.Equal &&
            before.Skip(edit.AStart).Take(edit.ALength).Any(line => line is "unique A" or "unique B")),
            "unique moved-block anchor");
    }

    public static void MyersReplaysRandomizedInputs()
    {
        var random = new Random(83);
        for (var sample = 0; sample < 200; sample++)
        {
            var before = Enumerable.Range(0, random.Next(0, 24))
                .Select(_ => $"v{random.Next(0, 8)}").ToArray();
            var after = Enumerable.Range(0, random.Next(0, 24))
                .Select(_ => $"v{random.Next(0, 8)}").ToArray();
            try
            {
                var diff = DiffEngine.Lines(before, after, DiffAlgorithm.Myers);
                Assert.SequenceEqual(after, diff.Apply(before, after), $"random diff replay #{sample}");
                var lcs = new int[before.Length + 1, after.Length + 1];
                for (var i = 1; i <= before.Length; i++)
                for (var j = 1; j <= after.Length; j++)
                    lcs[i, j] = before[i - 1] == after[j - 1]
                        ? lcs[i - 1, j - 1] + 1
                        : Math.Max(lcs[i - 1, j], lcs[i, j - 1]);
                Assert.Equal(before.Length + after.Length - 2 * lcs[before.Length, after.Length],
                    diff.DeletedCount + diff.InsertedCount, $"random minimal edit count #{sample}");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"sample {sample}: [{string.Join(",", before)}] -> [{string.Join(",", after)}]", error);
            }
        }

        var disjointBefore = Enumerable.Range(0, 512).Select(index => $"left-{index}").ToArray();
        var disjointAfter = Enumerable.Range(0, 512).Select(index => $"right-{index}").ToArray();
        var disjoint = DiffEngine.Lines(disjointBefore, disjointAfter, DiffAlgorithm.Myers);
        Assert.SequenceEqual(disjointAfter, disjoint.Apply(disjointBefore, disjointAfter),
            "large disjoint diff replay");
        Assert.Equal(512, disjoint.DeletedCount, "large disjoint delete count");
        Assert.Equal(512, disjoint.InsertedCount, "large disjoint insert count");

        var largeBefore = Enumerable.Range(0, 10_000).Select(index => $"line-{index}").ToArray();
        var largeAfter = (string[])largeBefore.Clone();
        largeAfter[5_000] = "changed-line";
        var largeDiff = DiffEngine.Lines(largeBefore, largeAfter, DiffAlgorithm.Myers);
        Assert.SequenceEqual(largeAfter, largeDiff.Apply(largeBefore, largeAfter), "10k-line diff replay");
        Assert.Equal(1, largeDiff.DeletedCount, "10k-line changed count");
        Assert.Equal(1, largeDiff.InsertedCount, "10k-line inserted count");
    }

    public static void TokenSimilarityIgnoresWhitespace()
    {
        var diff = DiffEngine.Tokens("call(value)", "call( value )");
        Assert.Equal(1d, diff.Similarity, "whitespace normalized similarity");
        Assert.Equal("call( value )", string.Concat(diff.B), "token round trip");
    }

    public static void SyntaxTokensAreClassified()
    {
        Assert.Equal(".go", SyntaxHighlighter.LanguageForPath("src/main.go"), "Go language");
        var line = "func build() string { return \"ok\" // note";
        var tokens = SyntaxHighlighter.TokenizeLine(line, ".go");
        string Text(SyntaxToken token) => line.Substring(token.Start, token.Length);
        Assert.True(tokens.Any(token => token.Kind == SyntaxTokenKind.Keyword && Text(token) == "func"),
            "keyword token");
        Assert.True(tokens.Any(token => token.Kind == SyntaxTokenKind.Function && Text(token) == "build"),
            "function token");
        Assert.True(tokens.Any(token => token.Kind == SyntaxTokenKind.String && Text(token) == "\"ok\""),
            "string token");
        Assert.True(tokens.Any(token => token.Kind == SyntaxTokenKind.Comment && Text(token) == "// note"),
            "comment token");
        Assert.Equal(0, SyntaxHighlighter.TokenizeLine("plain text", ".txt").Count,
            "plain text remains uncolored");
        var largeSource = string.Join('\n', Enumerable.Range(0, 10_000).Select(index =>
            $"func process{index}(value int) {{ return \"ok\" }}"));
        var largeTokenCount = largeSource.Split('\n').SelectMany(sourceLine =>
            SyntaxHighlighter.TokenizeLine(sourceLine, ".go")).Count();
        Assert.True(largeTokenCount >= 40_000, "large file token scan returns all runs");
    }
}

static class ApplyEngineTests
{
    public static void UnchangedFilesRetainIdentityMap()
    {
        var file = AppliedFile.Unchanged("source/main.cs", "one\ntwo\n");
        Assert.Equal("one\ntwo\n", file.PostText, "unchanged post text");
        Assert.Equal(1, file.LineMap.BaseForPost(1), "unchanged first line");
        Assert.Equal(2, file.LineMap.PostForBase(2), "unchanged second line");
        Assert.Equal(2, file.LineMap.PostLineCount, "unchanged line count");
    }

    public static void EveryFixtureAppliesToSyntheticBase()
    {
        var fixtureDirectory = Path.Combine(FixtureTestsRoot(), "patches");
        foreach (var path in Directory.GetFiles(fixtureDirectory, "*.patch"))
        {
            var patch = UnifiedDiffParser.ParseFile(path);
            var provider = new InMemoryFileProvider(SyntheticBase.For(patch), Path.GetFileName(path));
            var applied = ApplyEngine.Apply(patch, provider);
            Assert.Equal(0, applied.Report.Failed, $"{Path.GetFileName(path)} failed files");
            Assert.Equal(applied.Files.Count(file => file.Status.Kind != ApplyStatusKind.Binary),
                applied.Report.Clean + applied.Report.Fuzz, $"{Path.GetFileName(path)} applied files");
        }
    }

    public static void MergedKettlePatchesApply()
    {
        var fixtureDirectory = Path.Combine(FixtureTestsRoot(), "patches");
        var patches = Enumerable.Range(1, 4)
            .Select(index => UnifiedDiffParser.ParseFile(Path.Combine(fixtureDirectory, $"kettle-{index}.patch")))
            .ToArray();
        var provider = new InMemoryFileProvider(SyntheticBase.Merge(patches), "merged kettle fixtures");
        foreach (var patch in patches)
        {
            var applied = ApplyEngine.Apply(patch, provider);
            Assert.Equal(0, applied.Report.Failed, "merged kettle patch failed files");
            Assert.True(applied.Report.IsClean, "merged kettle patch applies without fuzz");
        }
    }

    private static string FixtureTestsRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Fixtures");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Fixtures.");
    }

    public static void ApplyIsInMemoryAndMapsLines()
    {
        using var baseDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(baseDirectory.Path, "file.txt"), "first\nold\nlast\n");
        var patch = UnifiedDiffParser.Parse("""
            diff --git a/file.txt b/file.txt
            --- a/file.txt
            +++ b/file.txt
            @@ -1,3 +1,3 @@
             first
            -old
            +new
             last
            """);

        var result = ApplyEngine.Apply(patch, new DirectoryFileProvider(baseDirectory.Path));
        var file = result.Files.Single();
        Assert.Equal("first\nnew\nlast\n", file.PostText, "post-patch text");
        Assert.Equal(ApplyStatusKind.Clean, file.Status.Kind, "apply status");
        Assert.Equal(1, file.LineMap.BaseForPost(1), "first line base mapping");
        Assert.Equal<int?>(null, file.LineMap.BaseForPost(2), "added line has no base line");
        Assert.Equal(3, file.LineMap.BaseForPost(3), "last line base mapping");
        Assert.Equal(1, result.Report.Clean, "clean report count");
        Assert.Equal("first\nold\nlast\n", File.ReadAllText(Path.Combine(baseDirectory.Path, "file.txt")),
            "base remains unchanged");
    }

    public static void FuzzIsReported()
    {
        using var baseDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(baseDirectory.Path, "file.txt"), "changed context\nmiddle\nlast\n");
        var patch = UnifiedDiffParser.Parse("""
            diff --git a/file.txt b/file.txt
            --- a/file.txt
            +++ b/file.txt
            @@ -1,3 +1,3 @@
             original context
             middle
            -last
            +end
            """);
        var result = ApplyEngine.Apply(patch, new DirectoryFileProvider(baseDirectory.Path));
        Assert.Equal(ApplyStatusKind.Fuzz, result.Files.Single().Status.Kind, "fuzz status");
        Assert.Equal(1, result.Files.Single().Status.Fuzz, "fuzz amount");
        Assert.Equal(1, result.Report.Fuzz, "fuzz report count");
    }

    public static void FailedHunkKeepsPatch()
    {
        using var baseDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(baseDirectory.Path, "file.txt"), "present\n");
        var patch = UnifiedDiffParser.Parse("""
            diff --git a/file.txt b/file.txt
            --- a/file.txt
            +++ b/file.txt
            @@ -1 +1 @@
            -missing
            +replacement
            """);
        var file = ApplyEngine.Apply(patch, new DirectoryFileProvider(baseDirectory.Path), maxFuzz: 0).Files.Single();
        Assert.Equal(ApplyStatusKind.Failed, file.Status.Kind, "failed status");
        Assert.Equal<string?>(null, file.PostText, "failed post text");
        Assert.Equal(1, file.Change.Hunks.Count, "raw hunk retained");
        Assert.Contains("hunk 1", file.Status.Message!, "failure identifies hunk");
    }
}

static class BaseLoaderTests
{
    public static void DirectoryReadsAndContainsPaths()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, "src"));
        File.WriteAllText(Path.Combine(temporary.Path, "src", "main.cs"), "local source");
        Directory.CreateDirectory(Path.Combine(temporary.Path, ".git"));
        File.WriteAllText(Path.Combine(temporary.Path, ".git", "config"), "ignored");

        var provider = new DirectoryFileProvider(temporary.Path);
        Assert.Equal("local source", BaseText.Decode(provider.ReadFile("src/main.cs")!), "read directory file");
        Assert.SequenceEqual(["src/main.cs"], provider.GetPaths(), "directory paths exclude .git");
        Assert.Equal<byte[]?>(null, provider.ReadFile("missing.cs"), "missing file");
        Assert.Throws<BaseLoaderException>(() => provider.ReadFile("../outside.txt"), "path traversal");
    }

    public static void ZipStripsCommonPrefix()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "base.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var file = archive.CreateEntry("project/src/main.cs", CompressionLevel.Optimal);
            using var writer = new StreamWriter(file.Open());
            writer.Write("inside zip");
        }

        var provider = BaseLoader.Open(archivePath);
        Assert.SequenceEqual(["src/main.cs"], provider.GetPaths(), "stripped zip paths");
        Assert.Equal("inside zip", BaseText.Decode(provider.ReadFile("src/main.cs")!), "zip content");
    }

    public static void ArchiveRejectsEscapingPath()
    {
        using var temporary = new TemporaryDirectory();
        var archivePath = Path.Combine(temporary.Path, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "../outside.txt", "C:/outside.txt" })
            {
                var outside = archive.CreateEntry(name);
                using var writer = new StreamWriter(outside.Open());
                writer.Write("not exposed");
            }
        }

        var provider = BaseLoader.Open(archivePath);
        Assert.Equal(0, provider.GetPaths().Count, "unsafe archive entry omitted");
    }

    public static void GitRevisionIsReadOnly()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null)
            throw new DirectoryNotFoundException("Could not locate repository root.");
        var repository = root.FullName;
        var provider = BaseLoader.Open(repository, "HEAD");
        Assert.True(provider.GetPaths().Contains("README.md"), "git revision lists repository file");
        var readme = provider.ReadFile("README.md");
        Assert.True(readme is { Length: > 0 }, "git revision reads tracked file");
        Assert.Equal<byte[]?>(null, provider.ReadFile("not-a-real-file.txt"), "missing git path");
        Assert.True(GitRevisionFileProvider.GitVersion()?.StartsWith("git version", StringComparison.Ordinal) == true,
            "git version detection");
    }

    public static void TarAndGzipTarExposeFiles()
    {
        using var temporary = new TemporaryDirectory();
        var tarBytes = CreateTar("repository/src/main.cs", "from tar");
        var tarPath = Path.Combine(temporary.Path, "base.tar");
        File.WriteAllBytes(tarPath, tarBytes);
        var tarProvider = BaseLoader.Open(tarPath);
        Assert.Equal("from tar", BaseText.Decode(tarProvider.ReadFile("src/main.cs")!), "tar content");

        var gzipPath = Path.Combine(temporary.Path, "base.tar.gz");
        using (var file = File.Create(gzipPath))
        using (var gzip = new GZipStream(file, CompressionMode.Compress))
            gzip.Write(tarBytes);
        var gzipProvider = BaseLoader.Open(gzipPath);
        Assert.Equal("from tar", BaseText.Decode(gzipProvider.ReadFile("src/main.cs")!), "gzip tar content");
    }

    private static byte[] CreateTar(string path, string text)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text))
            };
            writer.WriteEntry(entry);
        }
        return output.ToArray();
    }
}

sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "patchadams-tests-" + Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

static class FeatureContractTests
{
    public static void SafetyContractRemainsExplicit()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "windows-feature-set.md")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate docs/windows-feature-set.md.");

        var contract = File.ReadAllText(Path.Combine(directory.FullName, "docs", "windows-feature-set.md"));
        Assert.Contains("No network calls or telemetry.", contract, "offline safety requirement");
        Assert.Contains("never runs, builds, installs, or tests", contract, "no code execution requirement");
    }
}

static class ThemeContrastTests
{
    public static void OneDarkContrastMeetsAA()
    {
        var app = XDocument.Load(Path.Combine(FindRoot(), "App", "PatchAdams.Windows", "App.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var colors = app.Descendants()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToDictionary(element => element.Attribute(xaml + "Key")!.Value,
                element => element.Attribute("Color")!.Value);

        Check("OneDarkText", "OneDarkWindow", 4.5);
        Check("OneDarkText", "OneDarkSurface", 4.5);
        Check("OneDarkText", "OneDarkElement", 4.5);
        Check("OneDarkText", "OneDarkActive", 4.5);
        Check("OneDarkMutedText", "OneDarkWindow", 4.5);
        Check("OneDarkMutedText", "OneDarkSurface", 4.5);
        Check("OneDarkControlBorder", "OneDarkWindow", 3);
        Check("OneDarkControlBorder", "OneDarkSurface", 3);
        Check("OneDarkAccent", "OneDarkWindow", 3);

        void Check(string foreground, string background, double minimum)
        {
            var contrast = Contrast(colors[foreground], colors[background]);
            Assert.True(contrast >= minimum,
                $"{foreground} on {background}: {contrast:F2}:1 is below {minimum}:1.");
        }
    }

    public static void FindingInputsAreLabeled()
    {
        var root = FindRoot();
        var app = XDocument.Load(Path.Combine(root, "App", "PatchAdams.Windows", "App.xaml"));
        var combo = app.Descendants().Single(element =>
            element.Name.LocalName == "ControlTemplate" &&
            (string?)element.Attribute("TargetType") == "ComboBox");
        var namedParts = combo.Descendants()
            .Select(element => (string?)element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "Name")?.Value)
            .ToHashSet();
        Assert.True(namedParts.Contains("PART_EditableTextBox"), "editable combo text part");
        Assert.True(namedParts.Contains("PART_Popup"), "combo popup part");

        var finding = XDocument.Load(Path.Combine(root, "App", "PatchAdams.Windows", "FindingDialog.xaml"));
        var labelTargets = finding.Descendants().Where(element => element.Name.LocalName == "Label")
            .Select(element => (string?)element.Attribute("Target")).ToArray();
        foreach (var name in new[] { "TitleBox", "CategoryBox", "SeverityBox",
                     "DescriptionBox", "RequestAsksBox", "NoteBox" })
            Assert.True(labelTargets.Any(target => target?.Contains($"ElementName={name}", StringComparison.Ordinal) == true),
                $"{name} has a visible label");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "App", "PatchAdams.Windows", "App.xaml")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Windows app theme.");
    }

    private static double Contrast(string first, string second)
    {
        var bright = Math.Max(Luminance(first), Luminance(second));
        var dark = Math.Min(Luminance(first), Luminance(second));
        return (bright + 0.05) / (dark + 0.05);
    }

    private static double Luminance(string hex)
    {
        var rgb = hex.TrimStart('#');
        if (rgb.Length == 8) rgb = rgb[2..];
        static double Channel(string component)
        {
            var value = Convert.ToInt32(component, 16) / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(rgb[..2]) + 0.7152 * Channel(rgb[2..4]) +
               0.0722 * Channel(rgb[4..6]);
    }
}

static class ColumnWidthTests
{
    public static void DistributesCurrentSpace()
    {
        IReadOnlyList<IReadOnlyList<string>> panels =
        [
            Enumerable.Repeat(new string('x', 40), 30).ToArray(),
            Enumerable.Repeat(new string('x', 80), 30).ToArray(),
            Enumerable.Repeat(new string('x', 120), 30).ToArray()
        ];
        var widths = ColumnWidthBalancer.Allocate(900, 13, panels);
        Assert.Equal(3, widths.Length, "three expanded panels");
        Assert.True(Math.Abs(widths.Sum() - 900) < 0.001, "all available width is assigned");
        Assert.True(widths[2] > widths[1] && widths[1] > widths[0],
            "longer typical code receives more space");
        Assert.True(widths.All(width => width >= 90), "expanded panels keep their minimum width");

        var narrow = ColumnWidthBalancer.Allocate(240, 13, panels);
        Assert.True(Math.Abs(narrow.Sum() - 240) < 0.001, "narrow width is fully assigned");
        Assert.True(narrow.All(width => width >= 80), "narrow panels share the available width");
    }

    public static void OutliersDoNotDominate()
    {
        IReadOnlyList<IReadOnlyList<string>> panels =
        [
            Enumerable.Repeat(new string('x', 50), 19).Append(new string('x', 2000)).ToArray(),
            Enumerable.Repeat(new string('x', 50), 20).ToArray()
        ];
        var widths = ColumnWidthBalancer.Allocate(700, 13, panels);
        Assert.True(Math.Abs(widths[0] - widths[1]) < 1, "one long line cannot dominate allocation");
        Assert.Equal(0, ColumnWidthBalancer.Allocate(700, 13, []).Length, "no expanded panels");
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ColumnWidthBalancer.Allocate(double.PositiveInfinity, 13, panels),
            "invalid available width");
    }
}

static class LayoutPreferencesTests
{
    public static void CompactMenusPreserveCommands()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "App", "PatchAdams.Windows", "MainWindow.xaml")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate the WPF window layout.");
        var xaml = XDocument.Load(Path.Combine(directory.FullName, "App", "PatchAdams.Windows", "MainWindow.xaml"));
        var menu = xaml.Descendants().Single(element => element.Name.LocalName == "Menu");
        Assert.SequenceEqual(new[] { "_File", "_View", "_Review", "_Export" },
            menu.Elements().Select(element => (string)element.Attribute("Header")!), "four command groups");
        var handlers = menu.Descendants().Select(element => (string?)element.Attribute("Click"))
            .Where(handler => handler is not null).Cast<string>().ToArray();
        foreach (var handler in new[]
        {
            "ChooseBase_Click", "ChooseArchive_Click", "ChooseGitRevision_Click", "RebuildSyntheticBase_Click",
            "AddPatches_Click", "PastePatch_Click", "OpenSession_Click", "SaveSession_Click",
            "ManageColumns_Click", "BalanceWidths_Click", "SmallerFont_Click", "LargerFont_Click",
            "Settings_Click", "CopyReference_Click", "ExportMarkdown_Click", "ExportJson_Click",
            "CopyMarkdown_Click", "RequestPanelToggle_Click"
        })
            Assert.True(handlers.Contains(handler), $"menu retains {handler}");
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml";
        var search = xaml.Descendants().Single(element => (string?)element.Attribute(ns + "Name") == "SearchBox");
        Assert.True(!search.Ancestors().Contains(menu), "search stays outside dropdowns");
        var request = menu.Descendants().Single(element =>
            (string?)element.Attribute(ns + "Name") == "RequestPanelToggle");
        Assert.Equal("True", (string?)request.Attribute("IsCheckable"), "request visibility is checkable");
    }

    public static void SidebarsHaveSplitters()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "App", "PatchAdams.Windows", "MainWindow.xaml")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate the WPF window layout.");
        var xaml = XDocument.Load(Path.Combine(directory.FullName, "App", "PatchAdams.Windows", "MainWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml";
        var splitters = xaml.Descendants().Where(element => element.Name.LocalName == "GridSplitter")
            .ToDictionary(element => (string)element.Attribute(ns + "Name")!, element => element);
        Assert.Equal("1", (string?)splitters["FilePanelSplitter"].Attribute("Grid.Column"),
            "Files divider position");
        Assert.Equal("3", (string?)splitters["ReviewPanelSplitter"].Attribute("Grid.Column"),
            "Review divider position");
        Assert.True(splitters.Values.All(element =>
            (string?)element.Attribute("DragCompleted") == "SidePanelSplitter_DragCompleted"),
            "sidebar drags persist their widths");
        var codeGrid = xaml.Descendants().Single(element =>
            (string?)element.Attribute(ns + "Name") == "ColumnsGrid");
        Assert.Equal("2", (string?)codeGrid.Attribute("Grid.Column"), "code panels stay between sidebars");
    }

    public static void SidebarWidthsRoundTrip()
    {
        const string oldSettings = """
            {"FontFamily":"Consolas","FontSize":13,"AskBlindModeOnNewSession":true,
             "FilePanelCollapsed":true,"ReviewPanelCollapsed":false}
            """;
        var original = System.Text.Json.JsonSerializer.Deserialize<AppPreferences>(oldSettings)!;
        Assert.Equal(230d, original.FilePanelWidth, "older settings use default Files width");
        Assert.Equal(270d, original.ReviewPanelWidth, "older settings use default Review width");
        Assert.True(!original.RequestPanelCollapsed, "older settings keep request visible");

        var changed = original with { FilePanelWidth = 320, ReviewPanelWidth = 390, RequestPanelCollapsed = true };
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppPreferences>(
            System.Text.Json.JsonSerializer.Serialize(changed))!;
        Assert.Equal(320d, restored.FilePanelWidth, "Files width persists");
        Assert.Equal(390d, restored.ReviewPanelWidth, "Review width persists");
        Assert.True(restored.FilePanelCollapsed, "collapse state persists alongside width");
        Assert.True(restored.RequestPanelCollapsed, "request visibility persists independently");
    }
}

static class Assert
{
    public static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}.");
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string context)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(context);
    }

    public static void Contains(string expected, string actual, string context)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"{context}: missing '{expected}'.");
    }

    public static void Throws<TException>(Action action, string context) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"{context}: expected {typeof(TException).Name}.");
    }

    public static void True(bool condition, string context)
    {
        if (!condition)
            throw new InvalidOperationException(context);
    }
}
