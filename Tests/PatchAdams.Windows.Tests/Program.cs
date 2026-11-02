using System.Text.RegularExpressions;
using PatchAdams.Core;

var tests = new (string Name, Action Run)[]
{
    ("fixture inventory and file counts remain stable", FixtureTests.InventoryAndFileCountsRemainStable),
    ("every fixture hunk agrees with its declared line counts", FixtureTests.HunkCountsAgree),
    ("rename fixture retains old path, new path, and similarity", FixtureTests.RenameMetadataRemainsPresent),
    ("strict parser parses every fixture", FixtureTests.ParserParsesEveryFixture),
    ("strict parser reports malformed hunk line", FixtureTests.MalformedHunkReportsItsLine),
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
}
