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
}
