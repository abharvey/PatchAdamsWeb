using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PatchAdams.Core;
using PatchAdamsSharp;

internal static class WordMatchUiTests
{
    public static void Run(MainWindow owner)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fixtures", "syntax", "typescript", "occurrences.ts")))
            root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Occurrence fixture not found.");
        var source = File.ReadAllLines(Path.Combine(root.FullName, "Fixtures", "syntax", "typescript", "occurrences.ts"));
        var type = typeof(MainWindow).Assembly.GetType("PatchAdamsSharp.CodeEditor")!;
        var editor = (RichTextBox)Activator.CreateInstance(type)!;
        var other = new TextBox { Text = "Independent search text", Height = 30 };
        var panel = new DockPanel();
        DockPanel.SetDock(other, Dock.Bottom);
        panel.Children.Add(other);
        panel.Children.Add(editor);
        var host = new Window { Title = "Word matching regression", Owner = owner, Width = 1050, Height = 430, Content = panel };
        var oldTheme = ThemePalette.Current.Name;
        host.Show();
        try
        {
            var mapped = source.Select((text, row) => new DisplayLine(
                row == 4 ? DisplayLineKind.Added : row == 5 ? DisplayLineKind.Removed : DisplayLineKind.Context,
                row == 4 ? null : row + 1, row == 5 ? null : row + 1, text))
                .Concat([new(DisplayLineKind.SyntheticFiller, 99, 99, "first unknown source"),
                    new(DisplayLineKind.CollapsedContext, null, null, "first in hidden context", 100)]).ToArray();
            var finding = new SessionFinding(Guid.NewGuid(), "A", "occurrences.ts", new LineSpan(1, 1),
                "occurrences.ts L1:L1", "Keep finding shading", "", "other", FindingSeverity.Material,
                RequestRelevance.Unclear, false, "", [], DateTimeOffset.UtcNow);
            Load(mapped, [finding]);
            var documentText = Text();
            var backgrounds = editor.Document.Blocks.OfType<Paragraph>().Select(row => row.Background).ToArray();
            Select(0, source[0].IndexOf("first", StringComparison.Ordinal), 5);
            PumpUntil(() => Identifier() == "first" && Matches().Count == 7);
            Require(editor.Selection.Text == "first", "native word selection remains intact");
            Require(Matches().All(match => match.Row < source.Length), "collapsed and synthetic rows excluded");
            Require(Matches().Any(match => match.Row == 4) && Matches().Any(match => match.Row == 5),
                "visible added and removed source participates");
            Require(Text() == documentText, "matching never changes copied document text");
            Require(editor.Document.Blocks.OfType<Paragraph>().Select(row => row.Background).SequenceEqual(backgrounds),
                "finding and diff backgrounds stay unchanged");
            Capture("word-matches.png");
            var adorner = AdornerLayer.GetAdornerLayer(editor)!.GetAdorners(editor)!.Single();
            Require(!adorner.IsHitTestVisible, "overlay cannot intercept editor selection or scrolling");
            var dots = Drawings(VisualTreeHelper.GetDrawing(adorner)).Where(item => item.Geometry is EllipseGeometry).ToArray();
            Require(dots.Length == 4, "one gutter dot per matching row, not per occurrence");
            Require(dots.All(dot => Math.Abs(dot.Geometry.Bounds.Left + dot.Geometry.Bounds.Width / 2 - 6) < 1),
                "markers occupy their own gutter lane");
            foreach (var theme in new[] { "One Dark", "Ayu Dark" })
            {
                ThemePalette.Current.Apply(theme);
                Pump();
                Require(dots.All(dot => dot.Brush is SolidColorBrush brush && brush.Color == ThemePalette.Current["#74ADE8"]),
                    "marker colors follow theme changes");
            }
            Select(2, source[2].IndexOf("first", StringComparison.Ordinal), 5);
            Settled();
            Require(Identifier() is null && Matches().Count == 0, "partial identifiers do not activate matching");
            Select(3, source[3].IndexOf("First", StringComparison.Ordinal), 5);
            PumpUntil(() => Identifier() == "First" && Matches().Count == 1);
            other.Focus();
            Require(Matches().Count == 0, "leaving the code pane clears its matches");
            editor.Focus();
            PumpUntil(() => Identifier() == "First");
            Require(other.Text == "Independent search text", "selection matching never repurposes another input");

            Call("SelectRowSpans", (object)new[] { new LineSpan(1, 1), new LineSpan(5, 5) });
            Settled();
            Require(Matches().Count == 0, "disconnected line selections remain finding selections, not word searches");
            Require(!ReferenceEquals(editor.Document.Blocks.OfType<Paragraph>().ElementAt(4).Background, backgrounds[4]),
                "retained-range highlighting remains visible");
            Select(0, source[0].IndexOf("first", StringComparison.Ordinal), 5);
            PumpUntil(() => Identifier() == "first");
            Require(ReferenceEquals(editor.Document.Blocks.OfType<Paragraph>().ElementAt(4).Background, backgrounds[4]),
                "clearing retained selections restores diff shading");
            var presentation = PresentationSource.FromVisual(editor)!;
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            Settled();
            Require(editor.Selection.IsEmpty && Matches().Count == 0, "Escape clears matching and native selection");

            Select(0, source[0].IndexOf("first", StringComparison.Ordinal), 5);
            var pending = (Task)Call("UpdateWordMatchesAsync")!;
            Load([new(DisplayLineKind.Context, 1, 1, "const replacement = 1;")]);
            PumpUntil(() => pending.IsCompleted);
            pending.GetAwaiter().GetResult();
            Require(Matches().Count == 0 && Identifier() is null, "replaced documents reject stale scan results");

            var longSource = Enumerable.Range(1, 100).Select(row =>
                new DisplayLine(DisplayLineKind.Context, row, row, "first = first ?? last;")).ToArray();
            Load(longSource);
            Select(0, 0, 5);
            PumpUntil(() => Matches().Count == 200);
            editor.ScrollToEnd();
            Capture(null);
            var layer = AdornerLayer.GetAdornerLayer(editor)!;
            var visible = Drawings(VisualTreeHelper.GetDrawing(layer.GetAdorners(editor)!.Single()))
                .Where(item => item.Geometry is EllipseGeometry).ToArray();
            Require(visible.Length > 0 && visible.Length < 100, "scrolled overlay draws visible rows only");
            var clippedDots = ClippedDrawings(VisualTreeHelper.GetDrawing(layer.GetAdorners(editor)!.Single()))
                .Where(item => item.Drawing.Geometry is EllipseGeometry && !item.Bounds.IsEmpty).ToArray();
            Require(clippedDots.Length > 0 &&
                clippedDots.All(dot => dot.Bounds.Top >= 0 && dot.Bounds.Bottom <= editor.ActualHeight),
                "markers remain clipped to the viewport while scrolling");
            var prefix = new string(' ', 200);
            Load([new(DisplayLineKind.Context, 1, 1, prefix + "first first"),
                new(DisplayLineKind.Context, 2, 2, prefix + "first")]);
            Select(0, prefix.Length, 5);
            PumpUntil(() => Matches().Count == 3);
            editor.ScrollToHorizontalOffset(0);
            host.Width = 900;
            Capture(null);
            var horizontalDots = Drawings(VisualTreeHelper.GetDrawing(
                AdornerLayer.GetAdornerLayer(editor)!.GetAdorners(editor)!.Single()))
                .Where(item => item.Geometry is EllipseGeometry).ToArray();
            Require(horizontalDots.Length == 2 && horizontalDots.All(dot => Math.Abs(dot.Geometry.Bounds.Left - 3.5) < 1),
                "horizontal scrolling and resizing keep gutter markers fixed even for offscreen matches");
            host.Close();
            PumpUntil(() => Matches().Count == 0);
            Require(Matches().Count == 0, "unloading the editor removes match state");
            Console.WriteLine("PASS word matching, whole-word/case rules, gutter dots, theme changes, source preservation, focus, retained selections, Escape, stale scans, scroll, and unload");
        }
        finally
        {
            ThemePalette.Current.Apply(oldTheme);
            host.Close();
        }

        object? Call(string name, params object?[] args) =>
            type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(editor, args);
        string? Identifier() => (string?)type.GetProperty("MatchedIdentifier")!.GetValue(editor);
        IReadOnlyList<IdentifierOccurrence> Matches() =>
            (IReadOnlyList<IdentifierOccurrence>)type.GetProperty("WordMatches")!.GetValue(editor)!;
        string Text() => new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text;
        void Load(IReadOnlyList<DisplayLine> mapped, IReadOnlyList<SessionFinding>? findings = null)
        {
            var rendered = mapped.Select(line =>
                $"{(line.BaseLine?.ToString() ?? "").PadLeft(4)} | {(line.PostLine?.ToString() ?? "").PadLeft(4)} | {line.Text}").ToArray();
            Call("SetCode", rendered, mapped, ".ts", true, findings);
            editor.Focus();
            host.UpdateLayout();
            Pump();
        }
        void Select(int row, int start, int length)
        {
            Call("SetRetainedRows", (object)Array.Empty<LineSpan>());
            editor.Focus();
            var a = (TextPointer)Call("SourcePosition", row, start)!;
            var b = (TextPointer)Call("SourcePosition", row, start + length)!;
            editor.Selection.Select(a, b);
        }
        void Settled() => PumpUntil(() =>
            !(bool)type.GetField("selectionPending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)! &&
            type.GetField("wordMatchCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor) is null);
        void Capture(string? name)
        {
            host.UpdateLayout();
            Pump();
            var image = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(host);
            var scratch = Environment.GetEnvironmentVariable("DELTA_SCRATCH_DIR");
            if (name is null || string.IsNullOrEmpty(scratch)) return;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(scratch, name));
            encoder.Save(stream);
        }
    }

    private static IEnumerable<GeometryDrawing> Drawings(Drawing? drawing)
    {
        if (drawing is GeometryDrawing geometry) yield return geometry;
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var item in Drawings(child)) yield return item;
    }

    private static IEnumerable<(GeometryDrawing Drawing, Rect Bounds)> ClippedDrawings(Drawing? drawing, Rect? clip = null)
    {
        if (drawing is GeometryDrawing geometry)
        {
            var bounds = geometry.Bounds;
            if (clip is { } clipping) bounds.Intersect(clipping);
            yield return (geometry, bounds);
        }
        if (drawing is not DrawingGroup group) yield break;
        if (group.ClipGeometry is { } mask)
        {
            var bounds = mask.Bounds;
            if (clip is { } parent) bounds.Intersect(parent);
            clip = bounds;
        }
        foreach (var child in group.Children)
            foreach (var item in ClippedDrawings(child, clip)) yield return item;
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Word match UI did not settle.");
            Pump();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
