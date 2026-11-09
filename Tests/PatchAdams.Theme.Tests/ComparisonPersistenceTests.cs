using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PatchAdams.Core;
using PatchAdamsSharp;

internal static class ComparisonPersistenceTests
{
    public static void Run(MainWindow window)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Fixture root not found.");
        var paths = new[] { "kettle-1.patch", "kettle-2.patch" }
            .Select(name => Path.Combine(root.FullName, "Fixtures", "patches", name)).ToArray();
        var texts = paths.Select(File.ReadAllText).ToArray();
        var patches = texts.Select(text => UnifiedDiffParser.Parse(text)).ToArray();
        var provider = new InMemoryFileProvider(SyntheticBase.Merge(patches));
        var evidence = patches.Select((patch, index) =>
        {
            var file = ApplyEngine.Apply(patch, provider).Files.First(file => file.PostText is not null &&
                DisplayLines.Create(file).Any(row => row.Kind == DisplayLineKind.Added));
            var ranges = SelectionRanges.FromDisplayLines(DisplayLines.Create(file)
                .Where(row => row.Kind == DisplayLineKind.Added).Take(2));
            return new ComparisonEvidence(index == 0 ? "A" : "B", file.Path, ranges, $"Patch {index + 1} note");
        }).ToArray();
        var comparison = new ComparisonFinding(Guid.NewGuid(), "Compare approaches", "Shared assessment",
            "other", FindingSeverity.Minor, RequestRelevance.Unclear, evidence, DateTimeOffset.UtcNow);
        var session = new ReviewSession(ReviewSession.CurrentVersion, "Comparison save workflow", "",
            new SessionBase("synthetic"),
            [new SessionColumn("A", "kettle-1.patch", ""), new SessionColumn("B", "kettle-2.patch", "")],
            null, ReviewSession.DefaultCategories, [], new SessionRanking(new Dictionary<string, int>(), ""),
            [], [], new BlindMode(false, 42), DateTimeOffset.UtcNow)
        {
            ComparisonFindings = [comparison]
        };
        var patchTexts = new Dictionary<string, string> { ["A"] = texts[0], ["B"] = texts[1] };
        var directory = Path.Combine(AppContext.BaseDirectory, "comparison-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            SessionPackage.Write(new SessionContents(session, patchTexts), directory);
            Open();
            Verify();
            // Paste/add operations may retire old session metadata; comparison state is independent.
            Field("loadedSession").SetValue(window, null);
            Verify();
            SessionPackage.Write(new SessionContents(Snapshot(), patchTexts), directory);
            Require(JsonSerializer.Serialize(SessionPackage.Read(directory).Session.ComparisonFindings) ==
                JsonSerializer.Serialize(session.ComparisonFindings), "UI snapshot saves comparisons exactly");
            Call("TryStartNewSession", (Func<MessageBoxResult>)(() => MessageBoxResult.No), (Func<bool>)(() => false));
            Require(Snapshot().ComparisonFindings.Count == 0, "New session clears comparison findings");
            Open();
            Open();
            Verify();
            Field("promptedForBlindMode").SetValue(window, true);
            Wait((Task)Call("LoadPatchFilesAsync", (object)paths)!);
            Require(Snapshot().ComparisonFindings.Count == 0, "replacing patches clears comparison findings");
            Console.WriteLine("PASS app open/resave preserves comparisons; new session and replacement clear them");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? Call(string name, params object[] args) =>
            typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        ReviewSession Snapshot() => (ReviewSession)Call("BuildSession")!;
        void Open() => Wait((Task)Call("OpenSessionAsync", directory)!);
        void Verify() => Require(JsonSerializer.Serialize(Snapshot().ComparisonFindings) ==
            JsonSerializer.Serialize(session.ComparisonFindings), "app snapshot preserves comparison data without duplication");
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
