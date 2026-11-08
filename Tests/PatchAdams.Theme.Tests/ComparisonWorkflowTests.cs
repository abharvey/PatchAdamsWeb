using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PatchAdams.Core;
using PatchAdamsSharp;

internal static class ComparisonWorkflowTests
{
    public static void Run(MainWindow window)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Fixture root not found.");
        var paths = new[] { "kettle-1.patch", "kettle-2.patch" }
            .Select(name => Path.Combine(root.FullName, "Fixtures", "patches", name)).ToArray();
        Field("promptedForBlindMode").SetValue(window, true);
        Field("reviewPanelCollapsed").SetValue(window, false);
        Call("ApplySidePanelLayout");
        var loading = (Task)Call("LoadPatchFilesAsync", (object)paths)!;
        PumpUntil(() => loading.IsCompleted);
        loading.GetAwaiter().GetResult();
        var a = FixtureEvidence("A");
        var b = FixtureEvidence("B", a.Path);

        Select(a);
        Click("StartComparisonButton");
        Require(Control<Border>("ComparisonDraftPanel").Visibility == Visibility.Visible, "draft tray opens");
        Require(Draft().Count == 1 && !Control<Button>("ReviewComparisonButton").IsEnabled,
            "one patch cannot save a comparison");
        Control<TextBox>("DraftEvidenceNote").Text = "A checks before writing.";
        Select(b);
        Click("AddComparisonEvidenceButton");
        Require(Draft().Count == 2 && Draft()[0].Note == "A checks before writing.", "draft survives file navigation");
        Control<TextBox>("DraftEvidenceNote").Text = "B checks after writing.";
        Click("AddComparisonEvidenceButton");
        Require(Draft().Count == 3, "multiple evidence locations per patch are supported");
        Click("RemoveEvidenceButton");
        Require(Draft().Count == 2, "remove changes only the selected draft evidence");
        Control<ListBox>("DraftComparisonEvidence").SelectedIndex = 1;
        Select(a);
        Click("ReplaceEvidenceButton");
        Require(!Control<Button>("ReviewComparisonButton").IsEnabled, "two locations in one patch cannot save");
        Select(b);
        Click("ReplaceEvidenceButton");
        Require(Control<Button>("ReviewComparisonButton").IsEnabled, "two distinct patches enable assessment");
        Require(Draft()[1].Note == "B checks after writing.", "replacement retains that row's observation");
        Capture("comparison-draft.png", Control<Border>("ComparisonDraftPanel"));

        Review(dialog =>
        {
            DialogField<TextBox>(dialog, "TitleBox").Text = "Cancellation handling differs";
            DialogField<TextBox>(dialog, "DescriptionBox").Text = "A avoids a partial write.\r\nB checks too late.";
            DialogField<ComboBox>(dialog, "CategoryBox").Text = "correctness";
            dialog.DialogResult = false;
        });
        Require(Snapshot().ComparisonFindings.Count == 0 && Draft().Count == 2,
            "back to draft does not create or discard a finding");
        Review(dialog =>
        {
            Require(dialog.TitleText == "Cancellation handling differs", "assessment text survives back to draft");
            Require(dialog.Description.Contains("partial write", StringComparison.Ordinal), "shared assessment survives");
            Capture("comparison-form.png", dialog, dialog);
            SaveDialog(dialog);
        });
        var saved = Snapshot().ComparisonFindings.Single();
        Require(saved.Evidence.Count == 2 && Snapshot().Findings.Count == 0, "one shared finding, no ordinary copies");
        Require(Control<Border>("ComparisonDraftPanel").Visibility == Visibility.Collapsed, "saving closes draft");
        Require(Control<ListBox>("ComparisonFindingsList").Items.Count == 1, "saved list contains the comparison");
        Require(((SolidColorBrush)Control<TextBlock>("ComparisonAssessment").Foreground).Color ==
            ((SolidColorBrush)Application.Current.Resources["OneDarkText"]).Color, "saved assessment uses readable theme text");
        Require(saved.Evidence[0].SelectedRanges.SequenceEqual(a.SelectedRanges) &&
            saved.Evidence[1].SelectedRanges.SequenceEqual(b.SelectedRanges), "actual selected fixture ranges retained");
        Require(saved.Evidence.Any(item => item.Ranges.Any(range => range.IsBase)), "removed evidence retained");
        Capture("comparison-saved.png", Control<ListBox>("ComparisonFindingsList"));
        Control<ListBox>("SavedComparisonEvidence").SelectedIndex = 0;
        PumpUntil(() => Matches(a));
        Require(Control<TextBlock>("SavedComparisonNote").Text == "A checks before writing.", "evidence click shows its note");

        Control<MenuItem>("BlindModeToggle").IsChecked = true;
        var snapshot = Snapshot();
        var label = snapshot.DisplayLabel("A");
        Require(Control<ListBox>("SavedComparisonEvidence").Items.Cast<string>().Any(item =>
            item.StartsWith("Patch " + label + " ·", StringComparison.Ordinal)), "evidence uses blind labels");
        Require(Control<ListBox>("ComparisonFindingsList").Items.Cast<string>().All(item =>
            !item.Contains("Patches A", StringComparison.Ordinal)), "saved summary does not reveal column labels");
        Click("EditComparisonButton");
        Control<ListBox>("DraftComparisonEvidence").SelectedIndex = 0;
        Control<TextBox>("DraftEvidenceNote").Text = "Edited draft only";
        Click("RemoveEvidenceButton");
        Require(!Control<Button>("ReviewComparisonButton").IsEnabled, "editing still requires two patches");
        Require(Snapshot().ComparisonFindings.Single().Evidence[0].Note == saved.Evidence[0].Note,
            "editing draft does not mutate saved evidence");
        Call("ClearComparisonDraft");
        Require(Snapshot().ComparisonFindings.Single().Evidence.Count == 2, "discarded edit leaves saved comparison intact");

        Click("EditComparisonButton");
        Control<ListBox>("DraftComparisonEvidence").SelectedIndex = 0;
        Control<TextBox>("DraftEvidenceNote").Text = "Updated A observation";
        Review(dialog =>
        {
            DialogField<TextBox>(dialog, "DescriptionBox").Text = "Revised assessment";
            SaveDialog(dialog);
        });
        var revised = Snapshot().ComparisonFindings.Single();
        Require(revised.Id == saved.Id && revised.Created == saved.Created, "edit retains identity and timestamp");
        Require(revised.Evidence[0].Note == "Updated A observation" && revised.Description == "Revised assessment",
            "shared assessment and independent note updated");
        var directory = Path.Combine(AppContext.BaseDirectory, "comparison-workflow-" + Guid.NewGuid().ToString("N"));
        try
        {
            SessionPackage.Write(new SessionContents(Snapshot(), new Dictionary<string, string>
            {
                ["A"] = File.ReadAllText(paths[0]), ["B"] = File.ReadAllText(paths[1])
            }), directory);
            loading = (Task)Call("OpenSessionAsync", directory)!;
            PumpUntil(() => loading.IsCompleted);
            loading.GetAwaiter().GetResult();
            Require(Control<ListBox>("ComparisonFindingsList").Items.Count == 1, "saved UI restores after reopening");
            Require(Snapshot().ComparisonFindings.Single().Description == "Revised assessment", "assessment persists");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Control<ListBox>("ComparisonFindingsList").SelectedIndex = 0;
        Click("EditComparisonButton");
        Call("RemoveComparisonFinding", saved.Id);
        Require(Snapshot().ComparisonFindings.Count == 0 && Draft().Count == 0,
            "deleting a comparison removes all its evidence and its edit draft");
        Select(a);
        Click("StartComparisonButton");
        Call("TryStartNewSession", (Func<MessageBoxResult>)(() => MessageBoxResult.No), (Func<bool>)(() => false));
        Require(Draft().Count == 0 && Control<Border>("ComparisonDraftPanel").Visibility == Visibility.Collapsed,
            "new session clears unfinished drafts");
        Console.WriteLine("PASS comparison capture, add/replace/remove, two-patch gate, back-to-draft, save/edit/cancel, navigation, blind labels, reopen, delete, and reset");

        FieldInfo Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? Call(string name, params object[] args) =>
            typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        T Control<T>(string name) => (T)Field(name).GetValue(window)!;
        IReadOnlyList<ComparisonEvidence> Draft() => (List<ComparisonEvidence>)Field("draftEvidence").GetValue(window)!;
        ReviewSession Snapshot() => (ReviewSession)Call("BuildSession")!;
        bool Matches(ComparisonEvidence expected) => Call("CurrentComparisonEvidence") is ComparisonEvidence actual &&
            actual.Label == expected.Label && actual.Path == expected.Path &&
            actual.SelectedRanges.SequenceEqual(expected.SelectedRanges);
        void Select(ComparisonEvidence evidence)
        {
            Call("NavigateToSelection", evidence.Label, evidence.Path, evidence.SelectedRanges);
            PumpUntil(() => Matches(evidence));
        }
        void Click(string name)
        {
            var button = Control<Button>(name);
            Require(button.IsEnabled, name + " must be enabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }
        ComparisonEvidence FixtureEvidence(string label, string? excludePath = null)
        {
            var column = ((IEnumerable)Field("columns").GetValue(window)!).Cast<object>()
                .Single(item => (string)item.GetType().GetProperty("Label")!.GetValue(item)! == label);
            var applied = (AppliedPatch)column.GetType().GetProperty("Applied")!.GetValue(column)!;
            var file = applied.Files.First(file => file.Path != excludePath && file.PostText is not null &&
                DisplayLines.Create(file).Any(row => row.Kind == DisplayLineKind.Removed) &&
                DisplayLines.Create(file).Any(row => row.Kind == DisplayLineKind.Added));
            var rows = DisplayLines.Create(file).Where(row => row.Kind is DisplayLineKind.Added or DisplayLineKind.Removed).ToArray();
            return new ComparisonEvidence(label, file.Path, SelectionRanges.FromDisplayLines([rows[0], rows[^1]]));
        }
        void Review(Action<ComparisonFindingDialog> action)
        {
            Exception? error = null;
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<ComparisonFindingDialog>().Single();
                try { action(dialog); }
                catch (Exception exception) { error = exception; dialog.Close(); }
            }));
            Click("ReviewComparisonButton");
            if (error is not null) throw new InvalidOperationException("Comparison form check failed.", error);
        }
        void Capture(string name, FrameworkElement target, Window? owner = null)
        {
            var scratch = Environment.GetEnvironmentVariable("DELTA_SCRATCH_DIR");
            if (string.IsNullOrEmpty(scratch)) return;
            var visual = owner ?? window;
            target.BringIntoView();
            visual.UpdateLayout();
            visual.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(scratch, name));
            encoder.Save(stream);
        }
    }

    private static T DialogField<T>(ComparisonFindingDialog dialog, string name) =>
        (T)typeof(ComparisonFindingDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;

    private static void SaveDialog(ComparisonFindingDialog dialog) =>
        typeof(ComparisonFindingDialog).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, [dialog, new RoutedEventArgs()]);

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Comparison UI did not settle.");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
