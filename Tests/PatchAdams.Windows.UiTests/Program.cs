using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
            Console.WriteLine("PASS WPF search navigation: Open location, double-click, patch/file/line, focus, and centering.");
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
