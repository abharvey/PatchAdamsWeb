using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PatchAdams.Core;
using PatchAdamsSharp;

internal static class NewSessionTests
{
    public static void Run(MainWindow window)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new InvalidOperationException("Fixture root not found.");
        var patch = Path.Combine(root.FullName, "Fixtures", "patches", "kettle-1.patch");

        // Avoid the unrelated blind-mode question when loading the real fixture.
        Set("promptedForBlindMode", true);
        var loading = Load();
        while (!loading.IsCompleted)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        loading.GetAwaiter().GetResult();
        Require(((IList)Get("columns")).Count == 1, "fixture loaded");
        ((TextBox)Get("RequestBox")).Text = "Keep this request";
        ((TextBox)Get("RationaleBox")).Text = "Keep this rationale";
        ((TextBox)Get("SearchBox")).Text = "kettle";
        Set("reviewerFeedback", "Keep this feedback");
        Set("basePath", "previous-base");
        Set("baseRevision", "previous-revision");
        Set("baseCommit", "previous-commit");
        Set("baseKind", "git");
        Set("golden", "A");
        var oldSession = (ReviewSession)Call("BuildSession")!;
        Set("loadedSession", oldSession);
        var oldGeneration = (int)Get("patchLoadGeneration");
        var oldTheme = ThemePalette.Current.Name;
        var oldFont = Get("fontFamily");
        var oldPanelWidth = Get("filePanelWidth");

        var saves = 0;
        Require(!Start(MessageBoxResult.Cancel, () => { saves++; return true; }), "cancel keeps session");
        Require(saves == 0, "cancel never saves");
        Kept();
        Require(!Start(MessageBoxResult.Yes, () => { saves++; return false; }), "canceled or failed save keeps session");
        Require(saves == 1, "yes attempts save once");
        Kept();

        var directory = Path.Combine(AppContext.BaseDirectory, "new-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            Require(Start(MessageBoxResult.Yes, () =>
            {
                // Exercise a real package write before allowing the reset.
                SessionPackage.Write(new SessionContents((ReviewSession)Call("BuildSession")!,
                    new Dictionary<string, string> { ["A"] = File.ReadAllText(patch) }), directory);
                return true;
            }), "successful save starts new session");
            var saved = SessionPackage.Read(directory);
            Require(saved.Session.Request == oldSession.Request, "request saved before clearing");
            Require(saved.Session.ReviewerFeedback == oldSession.ReviewerFeedback, "feedback saved before clearing");
            Require(saved.PatchTexts["A"] == File.ReadAllText(patch), "fixture patch saved before clearing");
            Empty();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        ((TextBox)Get("RequestBox")).Text = "Discard this request without patches";
        Require(Start(MessageBoxResult.No, () => throw new InvalidOperationException("No must not save")),
            "discard starts new session without saving");
        Empty();

        // Start a background fixture load, then reset before its UI continuation.
        Set("promptedForBlindMode", true);
        loading = Load();
        Require(Start(MessageBoxResult.No, () => false), "reset while patch is loading");
        while (!loading.IsCompleted)
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        loading.GetAwaiter().GetResult();
        Empty();
        Require(((TextBlock)Get("StatusText")).Text.StartsWith("New session.", StringComparison.Ordinal),
            "stale load does not replace new-session status");
        Console.WriteLine("PASS new session cancel, save failure, save-before-clear, discard, reset, and stale fixture load");

        Task Load() => (Task)Call("LoadPatchFilesAsync", (object)new[] { patch })!;
        object Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Set(string name, object value) =>
            typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
        object? Call(string name, params object[] args) =>
            typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        bool Start(MessageBoxResult answer, Func<bool> save) =>
            (bool)Call("TryStartNewSession", (Func<MessageBoxResult>)(() => answer), save)!;

        void Kept()
        {
            Require(((IList)Get("columns")).Count == 1, "current patches kept");
            Require(((TextBox)Get("RequestBox")).Text == oldSession.Request, "current request kept");
            Require((string)Get("reviewerFeedback") == oldSession.ReviewerFeedback, "current feedback kept");
            Require((int)Get("patchLoadGeneration") == oldGeneration, "cancel does not retire current work");
        }

        void Empty()
        {
            var session = (ReviewSession)Call("BuildSession")!;
            Require(session.Columns.Count == 0 && session.Findings.Count == 0 && session.Pins.Count == 0 &&
                session.ManualAnchors.Count == 0, "review collections cleared");
            Require(session.Request == "" && session.Ranking.Rationale == "" && session.ReviewerFeedback == "",
                "review text cleared");
            Require(session.Base.Kind == "synthetic" && session.Base.Path is null &&
                session.Base.Revision is null && session.Base.Commit is null, "base cleared");
            Require(session.Golden is null && !session.Blind.IsOn, "comparison modes cleared");
            Require(session.Categories.SequenceEqual(ReviewSession.DefaultCategories), "categories restored");
            Require(Get("loadedSession") is null && !(bool)Get("promptedForBlindMode"), "new review lifecycle restored");
            foreach (var name in new[] { "files", "visibleFiles", "findingLabels", "recentSelections" })
                Require(((IList)Get(name)).Count == 0, name + " cleared");
            Require(((Grid)Get("ColumnsGrid")).Children.Count == 0, "code panels cleared");
            Require(((StackPanel)Get("RankingPanel")).Children.Count == 0, "rankings cleared");
            Require(((TextBox)Get("SearchBox")).Text == "" && ((TextBlock)Get("ReportText")).Text == "",
                "search and report cleared");
            Require(ThemePalette.Current.Name == oldTheme && Equals(Get("fontFamily"), oldFont) &&
                Equals(Get("filePanelWidth"), oldPanelWidth), "local preferences preserved");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
