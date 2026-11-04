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
            Console.WriteLine("PASS WPF navigation, compact scrollbars, copy-code buttons, and shared file caption.");
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
