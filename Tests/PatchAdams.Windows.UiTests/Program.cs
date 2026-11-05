using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PatchAdams.Core;
using PatchAdamsSharp;

// A separate Windows/STA runner exercises real WPF layout and modal event handlers.
// The core fixture runner remains UI-independent. No user patches or settings are written.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow { Width = 1400, Height = 800 };
        app.MainWindow = window;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        window.Show();
        try
        {
            RunNavigationTests(window);
            RunScrollbarTests(window);
            RunCopyCodeTests(window);
            RunSelectionTests(window);
            RunDisconnectedSelectionTests(window);
            Console.WriteLine("PASS WPF navigation, scrollbars, copy code, deferred processing, and disconnected selections.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    private static void RunNavigationTests(MainWindow window)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Fixtures", "patches")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Could not find the repository fixtures.");
        var output = Path.Combine(root.FullName, "artifacts", "ui-navigation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var baseDirectory = Path.Combine(output, "base");
        Directory.CreateDirectory(Path.Combine(baseDirectory, "src"));
        File.WriteAllText(Path.Combine(baseDirectory, "src", "target.ts"), string.Join('\n', BaseLines("src/target.ts", 800)) + "\n");
        File.WriteAllText(Path.Combine(baseDirectory, "src", "other.ts"), string.Join('\n', BaseLines("src/other.ts", 240)) + "\n");
        var paths = new[] { Path.Combine(output, "a.patch"), Path.Combine(output, "b.patch") };
        for (var index = 0; index < paths.Length; index++)
            File.WriteAllText(paths[index],
                ModifiedFile("src/target.ts", 800, index) + ModifiedFile("src/other.ts", 240, index));

        // These hooks only set up test data; search uses the actual modal and its controls.
        SetField(window, "askBlindModeOnNewSession", false);
        SetField(window, "basePath", baseDirectory);
        SetField(window, "baseKind", "directory");
        var load = (Task)Invoke(window, "LoadPatchFilesAsync", new object[] { paths })!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!load.IsCompleted && DateTime.UtcNow < deadline) Pump();
        Check(load.IsCompleted, "test patches finish loading");
        load.GetAwaiter().GetResult();
        Pump();

        var columns = (IList)GetField(window, "columns")!;
        columns[1]!.GetType().GetProperty("IsHidden")!.SetValue(columns[1], true);
        SetField(window, "selectedPath", "src/other.ts");
        Invoke(window, "RenderColumns", null!, null!);
        Pump();
        ((CheckBox)window.FindName("TestsOnlyToggle")).IsChecked = true;
        ((MenuItem)window.FindName("ScrollLockToggle")).IsChecked = true;

        Search(window, "needle-453", "B", doubleClick: false);
        VerifyDestination(window, "B", "src/target.ts", 453);
        Check(((CheckBox)window.FindName("TestsOnlyToggle")).IsChecked != true,
            "search exposes a non-test file hidden by the tree filter");
        // Containers may not exist until a previously collapsed Files panel is shown.
        SetField(window, "filePanelCollapsed", false);
        Invoke(window, "ApplySidePanelLayout");
        Pump();
        Check(((TreeView)window.FindName("FileTree")).SelectedItem?.ToString()?.Contains("src/target.ts") == true,
            "file tree follows the search destination");

        Search(window, "needle-175", "A", doubleClick: true);
        VerifyDestination(window, "A", "src/target.ts", 175);
        Invoke(window, "SelectGolden", "A");
        Pump();
        Search(window, "needle-453", "B", doubleClick: true);
        VerifyDestination(window, "B", "src/target.ts", 453);
        Check((string?)GetField(window, "golden") == "A", "navigation preserves golden comparison");
        Search(window, "other-130", "B", doubleClick: false);
        VerifyDestination(window, "B", "src/other.ts", 130);

        Invoke(window, "NavigateToPostLine", "A", "src/target.ts", 1);
        Pump();
        VerifyDestination(window, "A", "src/target.ts", 1, requireCentered: false);
        Invoke(window, "NavigateToPostLine", "B", "src/target.ts", 800);
        Pump();
        VerifyDestination(window, "B", "src/target.ts", 800, requireCentered: false);

        // A later navigation must win over a queued reveal on an editor it replaces.
        Invoke(window, "NavigateToPostLine", "A", "src/target.ts", 175);
        Invoke(window, "NavigateToPostLine", "B", "src/other.ts", 130);
        Pump();
        VerifyDestination(window, "B", "src/other.ts", 130);
    }

    private static void RunScrollbarTests(MainWindow window)
    {
        ((MenuItem)window.FindName("ScrollLockToggle")).IsChecked = false;
        var editor = (RichTextBox)((IDictionary)GetField(window, "codeEditors")!)["B"]!;
        var viewer = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor);
        var bars = Descendants<ScrollBar>(editor).Where(bar => bar.IsVisible).ToArray();
        var vertical = bars.Single(bar => bar.Orientation == Orientation.Vertical);
        var horizontal = bars.Single(bar => bar.Orientation == Orientation.Horizontal);
        Check(Math.Abs(vertical.ActualWidth - 10) < 0.1, "vertical scrollbar uses 10px, not native width");
        Check(Math.Abs(horizontal.ActualHeight - 10) < 0.1, "horizontal scrollbar uses 10px, not native height");

        foreach (var bar in bars)
        {
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            Check(((SolidColorBrush)bar.Background).Color == Color.FromRgb(40, 44, 51) &&
                  ((SolidColorBrush)track.Thumb.Background).Color == Color.FromRgb(129, 138, 152),
                $"{bar.Orientation} track and thumb use the contrast-tested dark palette");
            Check(track.Thumb.ActualWidth >= (bar.Orientation == Orientation.Vertical ? 6 : 24) &&
                  track.Thumb.ActualHeight >= (bar.Orientation == Orientation.Vertical ? 24 : 6),
                $"{bar.Orientation} thumb retains a usable minimum length");
            Check(Descendants<RepeatButton>(bar).Count() == 2, "only invisible page targets remain, no arrow buttons");
        }

        viewer.ScrollToVerticalOffset(0);
        viewer.ScrollToHorizontalOffset(0);
        Pump();
        var pageDown = (RepeatButton)vertical.Template.FindName("PageAfter", vertical);
        typeof(RepeatButton).GetMethod("OnClick", Private)!.Invoke(pageDown, null);
        Pump();
        Check(viewer.VerticalOffset > 0, "clicking the vertical track pages down");
        var before = viewer.VerticalOffset;
        var thumb = ((Track)vertical.Template.FindName("PART_Track", vertical)).Thumb;
        DragThumb(thumb, 0, 30);
        Check(viewer.VerticalOffset > before, "dragging the vertical thumb scrolls down");
        var pageRight = (RepeatButton)horizontal.Template.FindName("PageAfter", horizontal);
        typeof(RepeatButton).GetMethod("OnClick", Private)!.Invoke(pageRight, null);
        Pump();
        Check(viewer.HorizontalOffset > 0, "clicking the horizontal track pages right");
        viewer.ScrollToHorizontalOffset(0);
        Pump();
        var horizontalThumb = ((Track)horizontal.Template.FindName("PART_Track", horizontal)).Thumb;
        DragThumb(horizontalThumb, 30, 0);
        Check(viewer.HorizontalOffset > 0, "dragging the horizontal thumb scrolls right");

        before = viewer.VerticalOffset;
        editor.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
        Pump();
        Check(viewer.VerticalOffset > before, "mouse-wheel scrolling is preserved");
    }

    private static void RunCopyCodeTests(MainWindow window)
    {
        Invoke(window, "NavigateToPostLine", "B", "src/target.ts", 453);
        Pump();
        var caption = (TextBlock)window.FindName("SelectedFileText");
        Check(caption.Text == "src/target.ts" && caption.Visibility == Visibility.Visible &&
              (string?)caption.ToolTip == "src/target.ts", "one shared caption shows the full selected path");
        var previousClipboard = Clipboard.GetDataObject();
        try
        {
            var columns = ((IList)GetField(window, "columns")!).Cast<object>().ToArray();
            foreach (var label in new[] { "A", "B" })
            {
                var column = columns.Single(item => (string?)item.GetType().GetProperty("Label")!.GetValue(item) == label);
                var applied = (AppliedPatch)column.GetType().GetProperty("Applied")!.GetValue(column)!;
                var expected = applied.FileAt("src/target.ts")!.PostText!;
                var editor = (RichTextBox)((IDictionary)GetField(window, "codeEditors")!)[label]!;
                var copy = Descendants<Button>((DependencyObject)editor.Parent).Single(button => (string?)button.ToolTip == "copy code");
                Check(copy.IsEnabled && copy.Content is System.Windows.Shapes.Path &&
                      System.Windows.Automation.AutomationProperties.GetName(copy) == "copy code",
                    $"{label} has an accessible copy icon beside the patch name");
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Clipboard.GetText() == expected, $"{label} copies its entire resulting file, not gutters or golden diff text");
            }

            ((MenuItem)window.FindName("ShowRemovedToggle")).IsChecked = false;
            ((MenuItem)window.FindName("BlindModeToggle")).IsChecked = true;
            Pump();
            var target = columns.Single(item => (string?)item.GetType().GetProperty("Label")!.GetValue(item) == "B");
            var targetFile = ((AppliedPatch)target.GetType().GetProperty("Applied")!.GetValue(target)!).FileAt("src/target.ts")!;
            var targetEditor = (RichTextBox)((IDictionary)GetField(window, "codeEditors")!)["B"]!;
            Descendants<Button>((DependencyObject)targetEditor.Parent).Single(button => (string?)button.ToolTip == "copy code")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Clipboard.GetText() == targetFile.PostText, "blind mode and removed-line visibility do not change copied code");

            Invoke(window, "NavigateToPostLine", "B", "src/other.ts", 130);
            Pump();
            Check(caption.Text == "src/other.ts", "shared filename follows file navigation");
            SetField(window, "selectedPath", "not-in-base.ts");
            Invoke(window, "RenderColumns", null!, null!);
            Pump();
            Check(Descendants<Button>((Grid)window.FindName("ColumnsGrid"))
                .Where(button => (string?)button.ToolTip == "copy code").All(button => !button.IsEnabled),
                "copy buttons are disabled for unavailable files");
        }
        finally
        {
            if (previousClipboard is null)
                Clipboard.Clear();
            else
                Clipboard.SetDataObject(previousClipboard, true);
        }
    }

    private static void DragThumb(Thumb thumb, double horizontal, double vertical)
    {
        thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        thumb.RaiseEvent(new DragDeltaEventArgs(horizontal, vertical) { RoutedEvent = Thumb.DragDeltaEvent });
        thumb.RaiseEvent(new DragCompletedEventArgs(horizontal, vertical, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Pump();
    }

    private static void RunSelectionTests(MainWindow window)
    {
        Invoke(window, "SelectGolden", new object[] { null! });
        ((MenuItem)window.FindName("BlindModeToggle")).IsChecked = false;
        Invoke(window, "NavigateToPostLine", "A", "src/target.ts", 40);
        Pump();
        var editor = (RichTextBox)((IDictionary)GetField(window, "codeEditors")!)["A"]!;
        Check(editor.IsReadOnly && editor.IsReadOnlyCaretVisible &&
              ((SolidColorBrush)editor.CaretBrush).Color == Color.FromRgb(229, 192, 123),
            "read-only code exposes a high-contrast keyboard caret");
        Check(((DispatcherTimer)GetField(editor, "selectionTimer")!).Interval == TimeSpan.FromMilliseconds(200),
            "selection quiet period is configured to 200 ms");
        var paragraphs = editor.Document.Blocks.Cast<Paragraph>().ToArray();
        var settled = 0;
        EventHandler onSettled = (_, _) => settled++;
        editor.GetType().GetEvent("SelectionSettled")!.AddEventHandler(editor, onSettled);

        MouseInput(editor, Mouse.PreviewMouseDownEvent);
        editor.Selection.Select(paragraphs[20].ContentStart, paragraphs[30].ContentEnd);
        PumpFor(300);
        Check(settled == 0 && GetField(window, "selection") is null &&
              GetField(window, "comparisonCancellation") is null,
            "a paused mouse drag does not start selection processing");
        MouseInput(editor, Mouse.PreviewMouseUpEvent);
        PumpFor(130);
        Check(settled == 0, "mouse release still waits the full 200 ms");
        WaitUntil(() => settled == 1 && Comparisons(window).Count > 0, "mouse selection finishes after release");
        Check(GetField(window, "selection") is not null, "settled mouse selection generates a finding reference");

        // Changing character endpoints within the same rows reuses completed line comparisons.
        var cached = GetField(window, "selectionCache");
        editor.Selection.Select(paragraphs[20].ContentStart.GetNextInsertionPosition(LogicalDirection.Forward)!,
            paragraphs[30].ContentEnd);
        WaitUntil(() => settled == 2 && Comparisons(window).Count > 0, "same-row selection settles");
        Check(ReferenceEquals(cached, GetField(window, "selectionCache")), "same-row changes reuse cached comparisons");

        KeyInput(editor, Key.LeftShift, Keyboard.PreviewKeyDownEvent);
        KeyInput(editor, Key.Down, Keyboard.PreviewKeyDownEvent);
        // Exercise WPF's real keyboard selection command, not a replacement selection model.
        EditingCommands.SelectDownByLine.Execute(null, editor);
        KeyInput(editor, Key.Down, Keyboard.PreviewKeyDownEvent);
        PumpFor(300);
        Check(settled == 2 && GetField(window, "comparisonCancellation") is null,
            "held and repeated selection keys do not start comparisons");
        KeyInput(editor, Key.Down, Keyboard.PreviewKeyUpEvent);
        PumpFor(250);
        Check(settled == 2, "releasing the arrow still waits for Shift release");
        KeyInput(editor, Key.LeftShift, Keyboard.PreviewKeyUpEvent);
        PumpFor(130);
        Check(settled == 2, "keyboard release still waits the full 200 ms");
        WaitUntil(() => settled == 3 && Comparisons(window).Count > 0, "keyboard selection finishes after all keys release");

        editor.Selection.Select(paragraphs[2].ContentStart, paragraphs[3].ContentEnd);
        PumpFor(130);
        editor.Selection.Select(paragraphs[4].ContentStart, paragraphs[5].ContentEnd);
        PumpFor(130);
        Check(settled == 3, "another selection change restarts the 200 ms quiet period");
        WaitUntil(() => settled == 4 && Comparisons(window).Count > 0, "only the final selection is processed");

        KeyInput(editor, Key.Down, Keyboard.PreviewKeyDownEvent);
        // The user can release a held key after moving focus to another control.
        ((TextBox)window.FindName("SearchBox")).Focus();
        PumpFor(650);
        Check(settled == 5, "lost keyboard focus cannot strand a pending selection");
        editor.Focus();

        editor.Selection.Select(paragraphs[1].ContentStart, paragraphs[1].ContentStart);
        Check(GetField(window, "selection") is null && Comparisons(window).Count == 0,
            "clearing selection immediately invalidates its reference and comparisons");
        PumpFor(250);
        Check(GetField(window, "selection") is null, "an empty selection cannot restore an old finding reference");

        editor.Selection.Select(paragraphs[10].ContentStart, paragraphs[12].ContentEnd);
        Invoke(window, "NavigateToPostLine", "B", "src/other.ts", 130);
        PumpFor(400);
        var reference = (TextBlock)window.FindName("ReferenceText");
        Check(reference.Text.Contains("B — other.ts L130:L130", StringComparison.Ordinal),
            "navigation discards a replaced editor's pending selection");
        editor.Selection.Select(paragraphs[15].ContentStart, paragraphs[17].ContentEnd);
        PumpFor(250);
        Check(reference.Text.Contains("B — other.ts L130:L130", StringComparison.Ordinal),
            "late events from a detached editor cannot invalidate the current selection");

        RunBackgroundSelectionTest(window);
        editor.GetType().GetEvent("SelectionSettled")!.RemoveEventHandler(editor, onSettled);
    }

    private static void RunBackgroundSelectionTest(MainWindow window)
    {
        var columns = ((IList)GetField(window, "columns")!).Cast<object>().ToArray();
        var originals = columns.Select(column => column.GetType().GetProperty("Applied")!.GetValue(column)).ToArray();
        try
        {
            // Similar, unmapped regions force expensive fallback work without running reviewed code.
            for (var index = 0; index < columns.Length; index++)
            {
                var text = string.Join('\n', Enumerable.Range(1, 800).Select(line =>
                    $"value{line} = input{line + index} + shared + shared + shared + shared;"));
                var file = AppliedFile.Unchanged("src/target.ts", text) with
                {
                    BaseText = "",
                    LineMap = new LineMap(new int?[800], [])
                };
                columns[index].GetType().GetProperty("Applied")!.SetValue(columns[index], new AppliedPatch([file]));
            }
            SetField(window, "selectedPath", "src/target.ts");
            Invoke(window, "RenderColumns", null!, null!);
            Pump();
            var editor = (RichTextBox)((IDictionary)GetField(window, "codeEditors")!)["A"]!;
            var paragraphs = editor.Document.Blocks.Cast<Paragraph>().ToArray();
            editor.Selection.Select(paragraphs[100].ContentStart, paragraphs[499].ContentEnd);
            editor.GetType().GetMethod("CancelPendingSelection")!.Invoke(editor, null);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // Bypass only the already-tested delay to observe the worker before its first continuation.
            Invoke(window, "CodeSelectionSettled", editor, EventArgs.Empty);
            Check(clock.ElapsedMilliseconds < 150, "starting a large comparison does not block the dispatcher");
            var cancellation = (CancellationTokenSource)GetField(window, "comparisonCancellation")!;
            var token = cancellation.Token;
            var heartbeats = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => heartbeats++;
            timer.Start();
            PumpFor(80);
            timer.Stop();
            Check(heartbeats >= 2, "dispatcher keeps processing events during background alignment");
            editor.Selection.Select(paragraphs[10].ContentStart, paragraphs[10].ContentEnd);
            Check(token.IsCancellationRequested, "new selection cancels an in-flight comparison");
            WaitUntil(() => Comparisons(window).Count > 0 && GetField(window, "comparisonCancellation") is null,
                "latest selection publishes comparisons");
            Check(((TextBlock)window.FindName("ReferenceText")).Text == "A — target.ts L11:L11",
                "cancelled work cannot overwrite the latest reference");
            var cache = GetField(window, "selectionCache")!;
            Check(((IEnumerable<LineSpan>)cache.GetType().GetProperty("Rows")!.GetValue(cache)!)
                    .SequenceEqual([new LineSpan(11, 11)]),
                "only the latest row range enters the comparison cache");
        }
        finally
        {
            for (var index = 0; index < columns.Length; index++)
                columns[index].GetType().GetProperty("Applied")!.SetValue(columns[index], originals[index]);
            Invoke(window, "RenderColumns", null!, null!);
            Pump();
        }
    }

    private static IList Comparisons(MainWindow window) => (IList)GetField(window, "comparisons")!;

    private static void RunDisconnectedSelectionTests(MainWindow window)
    {
        ((MenuItem)window.FindName("ShowRemovedToggle")).IsChecked = true;
        Invoke(window, "NavigateToPostLine", "A", "src/target.ts", 100);
        Pump();
        var editor = (RichTextBox)((IDictionary)GetField(window, "codeEditors")!)["A"]!;
        SelectPostLines(editor, 100, 105);
        WaitUntil(() => SelectionReady(window, "target.ts L100:L105"), "initial line range settles");
        AddCtrlRange(editor, () => SelectPostLines(editor, 20, 21), releaseControl: false);
        PumpFor(250);
        Check(GetField(window, "selection") is null, "Ctrl remains an active interaction between disconnected drags");
        KeyInput(editor, Key.LeftCtrl, Keyboard.PreviewKeyUpEvent);
        var reference = (TextBlock)window.FindName("ReferenceText");
        WaitUntil(() => reference.Text == "A — target.ts L20:L21, L100:L105" &&
                        SelectionReady(window, "target.ts L20:L21, L100:L105"), "Ctrl+drag combines separated ranges");
        Check(SelectedRowSpans(editor).Count == 2, "selected rows do not include the gap");
        var display = EditorLines(editor);
        var paragraphs = editor.Document.Blocks.Cast<Paragraph>().ToArray();
        var selectedRow = Enumerable.Range(0, display.Count).First(row => display[row].PostLine == 100);
        var gapRow = Enumerable.Range(0, display.Count).First(row => display[row].PostLine == 50);
        Check((paragraphs[selectedRow].Background as SolidColorBrush)?.Color == Color.FromRgb(62, 82, 111) &&
              (paragraphs[gapRow].Background as SolidColorBrush)?.Color != Color.FromRgb(62, 82, 111),
            "retained ranges stay highlighted without painting intervening lines");
        Check(Comparisons(window).Count == 2, "each post-patch range gets a separate comparison");
        var vertical = Descendants<ScrollBar>(editor).Single(bar => bar.Orientation == Orientation.Vertical);
        vertical.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseDownEvent });
        vertical.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.PreviewMouseUpEvent });
        Check(SelectedRowSpans(editor).Count == 2, "scrollbar interaction preserves disconnected highlights");

        AddCtrlRange(editor, () => SelectPostLines(editor, 104, 108));
        WaitUntil(() => reference.Text == "A — target.ts L20:L21, L100:L108" &&
                        SelectionReady(window, "target.ts L20:L21, L100:L108"), "overlapping ranges merge without duplicates");
        var removedRow = Enumerable.Range(0, display.Count).First(row =>
            display[row].PostLine is null && display[row].BaseLine == 100);
        AddCtrlRange(editor, () => editor.Selection.Select(paragraphs[removedRow].ContentStart,
            paragraphs[removedRow].ContentEnd));
        const string expectedReference = "target.ts L20:L21, L100:L108, -L100:L100";
        WaitUntil(() => reference.Text == "A — " + expectedReference &&
                        SelectionReady(window, expectedReference), "removed and post-patch ranges keep separate anchors");

        var previousClipboard = Clipboard.GetDataObject();
        try
        {
            ApplicationCommands.Copy.Execute(null, editor);
            var text = Clipboard.GetText();
            Check(text.Contains("needle-20", StringComparison.Ordinal) && text.Contains("needle-108", StringComparison.Ordinal) &&
                  !text.Contains("needle-50", StringComparison.Ordinal) && !text.Contains(" | ", StringComparison.Ordinal),
                "copy includes selected source lines only, with neither gaps nor gutters");
        }
        finally
        {
            if (previousClipboard is null) Clipboard.Clear();
            else Clipboard.SetDataObject(previousClipboard, true);
        }

        Invoke(window, "PinSelection_Click", window, new RoutedEventArgs());
        AddTestFinding(window, expectedReference);
        var findings = ((IList)GetField(window, "findings")!).Cast<SessionFinding>().ToArray();
        var finding = findings[^1];
        var pin = ((IList)GetField(window, "pins")!).Cast<SessionPin>().Last();
        Check(finding.SelectedRanges.Count == 3 && pin.SelectedRanges.SequenceEqual(finding.SelectedRanges),
            "one finding and one pin retain all disconnected ranges and sides");
        var session = (ReviewSession)Invoke(window, "BuildSession")!;
        var columns = ((IList)GetField(window, "columns")!).Cast<object>().ToArray();
        var patches = columns.ToDictionary(column => (string)column.GetType().GetProperty("Label")!.GetValue(column)!,
            column => (string)column.GetType().GetProperty("PatchText")!.GetValue(column)!);
        var package = Path.Combine(Path.GetDirectoryName((string)GetField(window, "basePath")!)!, "disconnected.patchadam");
        SessionPackage.Write(new SessionContents(session, patches), package);
        var reopened = SessionPackage.Read(package).Session;
        Check(reopened.Findings.Last().SelectedRanges.SequenceEqual(finding.SelectedRanges) &&
              MarkdownExport.Render(reopened).Contains(expectedReference, StringComparison.Ordinal),
            "saved session and export preserve every range");

        KeyInput(editor, Key.Escape, Keyboard.PreviewKeyDownEvent);
        KeyInput(editor, Key.Escape, Keyboard.PreviewKeyUpEvent);
        Check(SelectedRowSpans(editor).Count == 0, "Esc clears native and retained selections");
        SelectPostLines(editor, 20, 21);
        AddCtrlRange(editor, () => SelectPostLines(editor, 100, 105));
        MouseInput(editor, Mouse.PreviewMouseDownEvent);
        SelectPostLines(editor, 40, 41);
        MouseInput(editor, Mouse.PreviewMouseUpEvent);
        WaitUntil(() => reference.Text == "A — target.ts L40:L41", "ordinary drag replaces all disconnected ranges");
        Check(SelectedRowSpans(editor).Count == 1, "ordinary selection leaves no hidden retained range");

        ((MenuItem)window.FindName("ShowRemovedToggle")).IsChecked = false;
        ((ListBox)window.FindName("FindingsList")).SelectedIndex = findings.Length - 1;
        Pump();
        WaitUntil(() => reference.Text == "A — " + expectedReference &&
                        SelectionReady(window, expectedReference), "opening a finding restores every range");
        editor = (RichTextBox)((IDictionary)GetField(window, "codeEditors")!)["A"]!;
        Check(EditorLines(editor).Any(line => line.PostLine is null && line.BaseLine == 100),
            "finding navigation reveals saved removed lines even when removed lines are hidden");
        KeyInput(editor, Key.Escape, Keyboard.PreviewKeyDownEvent);
        KeyInput(editor, Key.Escape, Keyboard.PreviewKeyUpEvent);
        display = EditorLines(editor);
        paragraphs = editor.Document.Blocks.Cast<Paragraph>().ToArray();
        selectedRow = Enumerable.Range(0, display.Count).First(row => display[row].PostLine == 105);
        gapRow = Enumerable.Range(0, display.Count).First(row => display[row].PostLine == 50);
        Check((paragraphs[selectedRow].Background as SolidColorBrush)?.Color == Color.FromRgb(65, 67, 47) &&
              paragraphs[gapRow].Background is null,
            "finding severity paints its ranges, never their bounding envelope");
        ((ListBox)window.FindName("PinsList")).SelectedItem = pin;
        Pump();
        WaitUntil(() => reference.Text == "A — " + expectedReference, "opening a pin restores every range");
    }

    private static bool SelectionReady(MainWindow window, string reference)
    {
        var cache = GetField(window, "selectionCache");
        if (cache is null || GetField(window, "comparisonCancellation") is not null) return false;
        var selection = cache.GetType().GetProperty("Selection")!.GetValue(cache)!;
        return (string?)selection.GetType().GetProperty("Reference")!.GetValue(selection) == reference;
    }

    private static void AddTestFinding(MainWindow window, string expectedReference)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<FindingDialog>().FirstOrDefault();
            if (dialog is null) return;
            timer.Stop();
            try
            {
                Check(((TextBlock)dialog.FindName("ReferenceLabel")).Text == expectedReference,
                    "finding dialog shows the complete disconnected reference");
                ((TextBox)dialog.FindName("TitleBox")).Text = "Related disconnected lines";
                ((TextBox)dialog.FindName("DescriptionBox")).Text = "These regions describe the same issue.";
                Descendants<Button>(dialog).Single(button => (string?)button.Content == "Save")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error)
            {
                failure = error;
                dialog.Close();
            }
        };
        timer.Start();
        try { Invoke(window, "AddFinding_Click", window, new RoutedEventArgs()); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
    }

    private static void AddCtrlRange(RichTextBox editor, Action select, bool releaseControl = true)
    {
        KeyInput(editor, Key.LeftCtrl, Keyboard.PreviewKeyDownEvent);
        MouseInput(editor, Mouse.PreviewMouseDownEvent);
        select();
        MouseInput(editor, Mouse.PreviewMouseUpEvent);
        if (releaseControl) KeyInput(editor, Key.LeftCtrl, Keyboard.PreviewKeyUpEvent);
    }

    private static IReadOnlyList<DisplayLine> EditorLines(RichTextBox editor) =>
        (IReadOnlyList<DisplayLine>)editor.GetType().GetProperty("DisplayLines")!.GetValue(editor)!;

    private static IReadOnlyList<LineSpan> SelectedRowSpans(RichTextBox editor) =>
        (IReadOnlyList<LineSpan>)editor.GetType().GetMethod("SelectedRowSpans")!.Invoke(editor, null)!;

    private static void SelectPostLines(RichTextBox editor, int first, int last)
    {
        var display = EditorLines(editor);
        var start = Enumerable.Range(0, display.Count).First(row => display[row].PostLine == first);
        var end = Enumerable.Range(0, display.Count).First(row => display[row].PostLine == last);
        var paragraphs = editor.Document.Blocks.Cast<Paragraph>().ToArray();
        editor.Selection.Select(paragraphs[start].ContentStart, paragraphs[end].ContentEnd);
    }

    private static void MouseInput(RichTextBox editor, RoutedEvent routedEvent) =>
        editor.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = routedEvent
        });

    private static void KeyInput(RichTextBox editor, Key key, RoutedEvent routedEvent) =>
        editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor),
            Environment.TickCount, key) { RoutedEvent = routedEvent });

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) PumpFor(10);
        Check(condition(), message);
    }

    private static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<string> BaseLines(string path, int count)
    {
        var name = path.Contains("other", StringComparison.Ordinal) ? "other" : "needle";
        return Enumerable.Range(1, count).Select(line => $"const {name}-{line} = {line};");
    }

    private static string ModifiedFile(string path, int count, int variant)
    {
        var changedLine = variant == 0 ? 100 : 200;
        var lines = BaseLines(path, count).Select((text, index) => index + 1 == changedLine
            ? $"-{text}\n+{text} // candidate {variant}"
            : $" {text}");
        return $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n" +
               $"@@ -1,{count} +1,{count} @@\n" + string.Join('\n', lines) + "\n";
    }

    private static void Search(MainWindow window, string query, string label, bool doubleClick)
    {
        ((TextBox)window.FindName("SearchBox")).Text = query;
        Exception? failure = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>()
                .FirstOrDefault(candidate => candidate.GetType().Name == "SearchResultsWindow");
            if (dialog is null) return;
            timer.Stop();
            try
            {
                var list = Descendants<ListBox>(dialog).Single();
                list.SelectedIndex = list.Items.Cast<string>().Select((text, index) => (text, index))
                    .Single(hit => hit.text.Contains($" · {label} · ", StringComparison.Ordinal)).index;
                list.ScrollIntoView(list.SelectedItem);
                list.UpdateLayout();
                if (doubleClick)
                {
                    // WPF re-raises this direct event on the ListBox, retaining the original item.
                    list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = list
                    });
                    Check(dialog.IsVisible, "double-clicking blank result space does not navigate");
                    var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex);
                    list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = item
                    });
                }
                else
                {
                    Descendants<Button>(dialog).Single(button => (string?)button.Content == "Open location")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            catch (Exception error)
            {
                failure = error;
                dialog.Close();
            }
        };
        timer.Start();
        try
        {
            Invoke(window, "Search_Click", window, new RoutedEventArgs());
        }
        finally
        {
            timer.Stop();
        }
        if (failure is not null) throw failure;
        Pump();
    }

    private static void VerifyDestination(MainWindow window, string label, string path, int line,
        bool requireCentered = true)
    {
        Check((string?)GetField(window, "selectedPath") == path, $"correct file: {path}");
        var editors = (IDictionary)GetField(window, "codeEditors")!;
        var editor = (RichTextBox)editors[label]!;
        var rows = (IEnumerable)editor.GetType().GetProperty("DisplayLines")!.GetValue(editor)!;
        var index = editor.Document.Blocks.Cast<Block>().ToList().IndexOf(editor.Selection.Start.Paragraph!);
        var selected = rows.Cast<DisplayLine>().ElementAt(index);
        Check(selected.PostLine == line, $"correct patch {label} and line {line}");
        Check(editor.IsKeyboardFocusWithin, "destination editor receives keyboard focus");
        var viewer = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor);
        var bounds = editor.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
        Check(!bounds.IsEmpty && bounds.Bottom > 0 && bounds.Top < viewer.ViewportHeight, "target line is visible");
        var fraction = (bounds.Top + bounds.Height / 2) / viewer.ViewportHeight;
        if (requireCentered)
            Check(fraction > 0.40 && fraction < 0.65, $"target is near midpoint (actual {fraction:P1})");
    }

    private static object? GetField(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
