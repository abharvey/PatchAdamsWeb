using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace PatchAdamsSharp;

/// <summary>
/// Maps the original One Dark colors to the selected palette. Bound brush colors
/// keep existing controls and syntax runs live without rebuilding review documents.
/// </summary>
public sealed class ThemePalette : INotifyPropertyChanged
{
    public static ThemePalette Current { get; } = new();
    public string Name { get; private set; } = "Ayu Dark";
    public event PropertyChangedEventHandler? PropertyChanged;

    // Ayu Dark values from Zed's assets/themes/ayu/ayu.json. Control outlines
    // and muted text are brighter than upstream to preserve our contrast contract.
    private static readonly Dictionary<string, string> Ayu = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#282C33"] = "#0D1016", ["#2F343E"] = "#1F2127",
        ["#2E343E"] = "#1F2127", ["#363C46"] = "#2D2F34",
        ["#454A56"] = "#3E4043", ["#464B57"] = "#3F4043",
        ["#818A98"] = "#8A8986", ["#74ADE8"] = "#5AC1FE",
        ["#DCE0E5"] = "#BFBDB6", ["#ABB2BF"] = "#BFBDB6",
        ["#A9AFBC"] = "#A6A5A0", ["#C678DD"] = "#FF8F3F",
        ["#56B6C2"] = "#59C2FF", ["#61AFEF"] = "#FFB353",
        ["#98C379"] = "#A9D94B", ["#D19A66"] = "#D2A6FF",
        ["#5C6370"] = "#8C8B88", ["#E5C07B"] = "#FEB454",
        ["#263B2C"] = "#294113", ["#472D33"] = "#48161B",
        ["#5A3038"] = "#66272D", ["#51402C"] = "#572815",
        ["#41432F"] = "#405C1C", ["#35414B"] = "#0D2F4E",
        ["#3E526F"] = "#1B4A6E", ["#6674ADE8"] = "#665AC1FE",
        ["#4074ADE8"] = "#405AC1FE"
    };

    public Color this[string original] => (Color)ColorConverter.ConvertFromString(
        Name == "Ayu Dark" && Ayu.TryGetValue(original, out var color) ? color : original)!;

    public SolidColorBrush Brush(string original)
    {
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
            new Binding($"[{original}]") { Source = this, Mode = BindingMode.OneWay });
        return brush;
    }

    public void Apply(string? name)
    {
        Name = name == "One Dark" ? name : "Ayu Dark";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);
    }

    public void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = 1;
        // Windows 10 1809 used attribute 19; newer Windows uses 20.
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        var background = this["#282C33"];
        var caption = background.R | background.G << 8 | background.B << 16;
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        var text = this["#DCE0E5"];
        var foreground = text.R | text.G << 8 | text.B << 16;
        _ = DwmSetWindowAttribute(handle, 36, ref foreground, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
