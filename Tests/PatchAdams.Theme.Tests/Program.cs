using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PatchAdams.Core;
using PatchAdamsSharp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new App();
        app.InitializeComponent();
        var palette = ThemePalette.Current;
        palette.Apply("Ayu Dark");
        var brush = palette.Brush("#C678DD");
        var surface = (SolidColorBrush)app.Resources["OneDarkWindow"];
        Pump();
        Equal("#FFFF8F3F", brush.Color.ToString(), "Ayu keyword");
        Equal("#FF0D1016", surface.Color.ToString(), "Ayu XAML surface");
        palette.Apply("One Dark");
        Pump();
        Equal("#FFC678DD", brush.Color.ToString(), "existing syntax brush updates");
        Equal("#FF282C33", surface.Color.ToString(), "existing XAML brush updates");
        palette.Apply("unknown");
        Pump();
        Equal("Ayu Dark", palette.Name, "unknown theme fallback");
        Equal("#FF0D1016", surface.Color.ToString(), "switch back to Ayu");
        foreach (var theme in new[] { "One Dark", "Ayu Dark" })
        {
            palette.Apply(theme);
            Pump();
            Contrast("OneDarkText", "OneDarkWindow", 4.5);
            Contrast("OneDarkText", "OneDarkSurface", 4.5);
            Contrast("OneDarkText", "OneDarkActive", 4.5);
            Contrast("OneDarkMutedText", "OneDarkSurface", 4.5);
            Contrast("OneDarkControlBorder", "OneDarkSurface", 3);
            Contrast("OneDarkAccent", "OneDarkWindow", 3);
        }

        void Contrast(string foreground, string background, double minimum)
        {
            var first = Luminance(((SolidColorBrush)app.Resources[foreground]).Color);
            var second = Luminance(((SolidColorBrush)app.Resources[background]).Color);
            var ratio = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
            if (ratio < minimum)
                throw new InvalidOperationException($"{palette.Name}: {foreground} on {background}: {ratio:F2}:1");
        }

        // Construct the real XAML tree, without writing user preferences.
        var window = new MainWindow();
        window.Show();
        Pump();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        palette.ApplyTitleBar(window);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var caption = 0x16100D;
            // These attributes are writable; DwmGetWindowAttribute does not
            // support querying them on every supported Windows version.
            var result = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            Equal(0, result, "caption color supported");
            var dark = 1;
            result = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            Equal(0, result, "dark title bar supported");
        }
        var sourceFinding = new SessionFinding(Guid.NewGuid(), "A", "source.cs", new LineSpan(4, 6),
            "source.cs L4:L6", "Finding title", "Keep description", "security", FindingSeverity.Blocking,
            RequestRelevance.Yes, true, "Keep correctness note",
            [new FindingAnchor("B", "modified", 0.9, "target.cs L4:L6")], DateTimeOffset.UtcNow);
        var copyDialog = new FindingDialog("target.cs L20:L22", ["security", "other"], sourceFinding);
        Equal(sourceFinding.Title, copyDialog.TitleText, "duplicate form title prefilled");
        Equal(sourceFinding.Description, copyDialog.Description, "duplicate form description prefilled");
        Equal(sourceFinding.Category, copyDialog.Category, "duplicate form category prefilled");
        Equal("blocking", copyDialog.Severity, "duplicate form severity prefilled");
        Equal("yes", copyDialog.RequestAsks, "duplicate form request relevance prefilled");
        Equal(sourceFinding.Note, copyDialog.Note, "duplicate form note prefilled");
        Equal(true, copyDialog.NotFlagged, "duplicate form disposition prefilled");
        copyDialog.Close();
        NewSessionTests.Run(window);
        window.Close();
        app.Shutdown();
        Console.WriteLine("PASS live theme brushes, XAML construction, fallback, and native title bar");
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(
        () => { }, DispatcherPriority.ApplicationIdle);

    private static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
