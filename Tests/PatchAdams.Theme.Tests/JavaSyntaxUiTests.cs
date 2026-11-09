using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PatchAdamsSharp;

internal static class JavaSyntaxUiTests
{
    public static void Run(MainWindow window)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fixtures", "syntax", "java", "ReviewExample.java")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Java fixture not found.");
        var lines = File.ReadAllLines(Path.Combine(root.FullName, "Fixtures", "syntax", "java", "ReviewExample.java"));
        var patch = $"--- /dev/null\n+++ b/src/ReviewExample.java\n@@ -0,0 +1,{lines.Length} @@\n" +
            string.Join('\n', lines.Select(line => "+" + line)) + "\n";
        var directory = Path.Combine(AppContext.BaseDirectory, "java-syntax-" + Guid.NewGuid().ToString("N"));
        var oldTheme = ThemePalette.Current.Name;
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new[] { Path.Combine(directory, "A.patch"), Path.Combine(directory, "B.patch") };
            foreach (var path in paths) File.WriteAllText(path, patch);
            typeof(MainWindow).GetField("promptedForBlindMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var loading = (Task)typeof(MainWindow).GetMethod("LoadPatchFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [(object)paths])!;
            var timer = Stopwatch.StartNew();
            while (!loading.IsCompleted)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Java fixture did not load.");
                Pump();
            }
            loading.GetAwaiter().GetResult();
            var editors = (IDictionary)typeof(MainWindow).GetField("codeEditors", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            var editor = (RichTextBox)editors["A"]!;
            var runs = editor.Document.Blocks.OfType<Paragraph>().SelectMany(row => row.Inlines.OfType<Run>()).ToArray();
            foreach (var theme in new[] { "One Dark", "Ayu Dark" })
            {
                ThemePalette.Current.Apply(theme);
                Pump();
                Check("public", "#C678DD");
                Check("implements", "#C678DD");
                Check("@Deprecated", "#56B6C2");
                Check("1_000", "#D19A66");
                Check("\"https://example.invalid/review\"", "#98C379");
                Check("compare", "#61AFEF");
            }
            var scratch = Environment.GetEnvironmentVariable("DELTA_SCRATCH_DIR");
            if (!string.IsNullOrEmpty(scratch))
            {
                window.UpdateLayout();
                Pump();
                var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(Path.Combine(scratch, "java-syntax.png"));
                encoder.Save(stream);
            }
            Console.WriteLine("PASS Java fixture patches render keyword, annotation, literal, and method colors in both themes");

            void Check(string text, string color)
            {
                if (!runs.Any(run => run.Text == text && run.Foreground is SolidColorBrush brush &&
                    brush.Color == ThemePalette.Current[color]))
                    throw new InvalidOperationException($"{ThemePalette.Current.Name}: missing Java color for {text}");
            }
        }
        finally
        {
            ThemePalette.Current.Apply(oldTheme);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
