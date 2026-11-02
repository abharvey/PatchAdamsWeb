using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Formats.Tar;
using PatchAdams.Core;

var tests = new (string Name, Action Run)[]
{
    ("fixture inventory and file counts remain stable", FixtureTests.InventoryAndFileCountsRemainStable),
    ("every fixture hunk agrees with its declared line counts", FixtureTests.HunkCountsAgree),
    ("rename fixture retains old path, new path, and similarity", FixtureTests.RenameMetadataRemainsPresent),
    ("strict parser parses every fixture", FixtureTests.ParserParsesEveryFixture),
    ("strict parser reports malformed hunk line", FixtureTests.MalformedHunkReportsItsLine),
    ("directory bases read files without escaping their root", BaseLoaderTests.DirectoryReadsAndContainsPaths),
    ("zip bases strip one common top-level folder", BaseLoaderTests.ZipStripsCommonPrefix),
    ("tar and gzip tar bases expose text files", BaseLoaderTests.TarAndGzipTarExposeFiles),
    ("archive paths cannot escape the archive root", BaseLoaderTests.ArchiveRejectsEscapingPath),
    ("patches apply in memory with line maps and status reports", ApplyEngineTests.ApplyIsInMemoryAndMapsLines),
    ("context fuzz is reported without writing the base", ApplyEngineTests.FuzzIsReported),
    ("failed hunks keep raw patch data and no post text", ApplyEngineTests.FailedHunkKeepsPatch),
    ("Myers diff is minimal and replays to the target", DiffEngineTests.MyersDiffIsMinimalAndReplays),
    ("Myers diff replays deterministic randomized inputs", DiffEngineTests.MyersReplaysRandomizedInputs),
    ("patience diff anchors unique lines around reordered blocks", DiffEngineTests.PatienceDiffRetainsMovedAnchors),
    ("token similarity ignores whitespace", DiffEngineTests.TokenSimilarityIgnoresWhitespace),
    ("alignment classifies exact, modified, moved, and absent regions", AlignmentTests.ClassifiesRegions),
    ("session packages round-trip patches and review state", SessionTests.PackageRoundTripsState),
    ("blind export reveals its mapping only at the end", SessionTests.BlindExportRevealsMappingLast),
    ("references are generated from post-patch selections", SessionTests.ReferenceIsGeneratedFromSelection),
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
    public static void PackageRoundTripsState()
    {
        using var temporary = new TemporaryDirectory();
        var session = CreateSession(blind: false);
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
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    $"sample {sample}: [{string.Join(",", before)}] -> [{string.Join(",", after)}]", error);
            }
        }
    }

    public static void TokenSimilarityIgnoresWhitespace()
    {
        var diff = DiffEngine.Tokens("call(value)", "call( value )");
        Assert.Equal(1d, diff.Similarity, "whitespace normalized similarity");
        Assert.Equal("call( value )", string.Concat(diff.B), "token round trip");
    }
}

static class ApplyEngineTests
{
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
            var outside = archive.CreateEntry("../outside.txt");
            using var writer = new StreamWriter(outside.Open());
            writer.Write("not exposed");
        }

        var provider = BaseLoader.Open(archivePath);
        Assert.Equal(0, provider.GetPaths().Count, "unsafe archive entry omitted");
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
