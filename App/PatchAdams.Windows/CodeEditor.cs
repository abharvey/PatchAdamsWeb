using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PatchAdams.Core;

namespace PatchAdamsSharp;

internal sealed partial class CodeEditor : RichTextBox
{
    private static readonly Brush Plain = Brush("#ABB2BF");
    private static readonly Brush Keyword = Brush("#C678DD");
    private static readonly Brush Type = Brush("#56B6C2");
    private static readonly Brush Function = Brush("#61AFEF");
    private static readonly Brush String = Brush("#98C379");
    private static readonly Brush Number = Brush("#D19A66");
    private static readonly Brush Comment = Brush("#5C6370");

    private readonly List<Paragraph> rows = [];
    private readonly Dictionary<Paragraph, int> rowIndexes = [];
    private readonly Dictionary<int, int> baseRowIndexes = [];
    private readonly List<DisplayLine> displayLines = [];
    private readonly List<string> plainLines = [];
    private readonly List<Brush?> rowBackgrounds = [];
    private IReadOnlyList<LineSpan> retainedRowSpans = [];
    private readonly HashSet<Key> pressedKeys = [];
    private readonly HashSet<MouseButton> pressedButtons = [];
    private readonly DispatcherTimer selectionTimer;
    private bool selectionPending;
    private bool waitingForRelease;
    private bool lostKeyboardFocus;
    private double rowHeight;

    public event EventHandler? SelectionActivity;
    public event EventHandler? SelectionSettled;

    public IReadOnlyList<DisplayLine> DisplayLines => displayLines;
    public IReadOnlyList<string> PlainLines => plainLines;
    private bool ControlPressed => Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ||
        pressedKeys.Contains(Key.LeftCtrl) || pressedKeys.Contains(Key.RightCtrl);
    private bool ShiftPressed => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
        pressedKeys.Contains(Key.LeftShift) || pressedKeys.Contains(Key.RightShift);

    public CodeEditor()
    {
        selectionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        selectionTimer.Tick += SelectionTimer_Tick;
        InitializeWordMatching();
        Unloaded += (_, _) => CancelPendingSelection();
        AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler(CopyDisconnectedSelection));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = true;
        CaretBrush = Brush("#E5C07B");
        IsDocumentEnabled = true;
        AcceptsTab = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Background = Brush("#282C33");
        Foreground = Plain;
        BorderBrush = Brush("#818A98");
        Padding = new Thickness(12, 3, 3, 3);
        FontFamily = new FontFamily("Consolas");
        FontSize = 13;
        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity
        };
        rowHeight = FontSize * 1.15;
    }

    protected override void OnSelectionChanged(RoutedEventArgs e)
    {
        base.OnSelectionChanged(e);
        // RichTextBox can raise this while its base constructor is still running.
        if (selectionTimer is null) return;
        InvalidateSelection();
        ArmSelectionTimer();
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !IsScrollBarInteraction(e.OriginalSource))
        {
            if (ControlPressed)
                RetainActiveSelection();
            else
                SetRetainedRows([]);
        }
        pressedButtons.Add(e.ChangedButton);
        InvalidateSelection();
        base.OnPreviewMouseDown(e);
    }

    private bool IsScrollBarInteraction(object source)
    {
        for (var element = source as DependencyObject; element is not null && element != this;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is ScrollBar) return true;
        return false;
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        pressedButtons.Remove(e.ChangedButton);
        waitingForRelease = false;
        base.OnPreviewMouseUp(e);
        ArmSelectionTimer();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        pressedButtons.Clear();
        base.OnLostMouseCapture(e);
        ArmSelectionTimer();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        lostKeyboardFocus = false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        pressedKeys.Add(key);
        if (key == Key.Escape)
        {
            SetRetainedRows([]);
            Selection.Select(CaretPosition, CaretPosition);
            e.Handled = true;
        }
        else if (key == Key.A && ControlPressed)
            SetRetainedRows([]);
        else if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown &&
                 !ShiftPressed)
            SetRetainedRows([]);
        InvalidateSelection();
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        pressedKeys.Remove(e.Key == Key.System ? e.SystemKey : e.Key);
        waitingForRelease = false;
        base.OnPreviewKeyUp(e);
        ArmSelectionTimer();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (selectionTimer is null) return;
        lostKeyboardFocus = false;
        InvalidateSelection();
        ArmSelectionTimer();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ClearWordMatches();
        // Key-up can go to another control after focus changes.
        lostKeyboardFocus = true;
        ArmSelectionTimer();
    }

    private void InvalidateSelection()
    {
        ClearWordMatches();
        selectionTimer.Stop();
        selectionPending = true;
        SelectionActivity?.Invoke(this, EventArgs.Empty);
    }

    private void ArmSelectionTimer()
    {
        selectionTimer.Stop();
        if (selectionPending)
            selectionTimer.Start();
    }

    private void SelectionTimer_Tick(object? sender, EventArgs e)
    {
        // Do not treat a pause during a drag or held Shift/arrow key as completion.
        // Polling also covers releasing capture or focus before the final input-up.
        if (lostKeyboardFocus)
            pressedKeys.RemoveWhere(key => !Keyboard.IsKeyDown(key));
        if (pressedKeys.Count > 0 || pressedButtons.Count > 0 || Keyboard.Modifiers != ModifierKeys.None ||
            Mouse.LeftButton == MouseButtonState.Pressed || Mouse.RightButton == MouseButtonState.Pressed ||
            Mouse.MiddleButton == MouseButtonState.Pressed)
        {
            waitingForRelease = true;
            ArmSelectionTimer();
            return;
        }
        if (waitingForRelease)
        {
            waitingForRelease = false;
            ArmSelectionTimer();
            return;
        }
        selectionTimer.Stop();
        if (!selectionPending) return;
        selectionPending = false;
        _ = UpdateWordMatchesAsync();
        SelectionSettled?.Invoke(this, EventArgs.Empty);
    }

    public void CancelPendingSelection()
    {
        ClearWordMatches();
        selectionTimer.Stop();
        selectionPending = false;
        pressedKeys.Clear();
        pressedButtons.Clear();
        waitingForRelease = false;
        lostKeyboardFocus = false;
    }

    public void SetCode(
        IReadOnlyList<string> lines,
        IReadOnlyList<DisplayLine> mappedLines,
        string extension,
        bool hasLineGutters,
        IReadOnlyList<SessionFinding>? findings = null)
    {
        CancelPendingSelection();
        SetRetainedRows([]);
        Document.Blocks.Clear();
        rows.Clear();
        rowBackgrounds.Clear();
        rowIndexes.Clear();
        baseRowIndexes.Clear();
        displayLines.Clear();
        displayLines.AddRange(mappedLines);
        var displayRows = lines.Select(line => line.TrimEnd('\r')).ToArray();
        SetWordMatchSources(displayRows, mappedLines, hasLineGutters);
        plainLines.Clear();
        plainLines.AddRange(displayRows);
        rowHeight = FontSize * 1.15;
        var longestLine = displayRows.Select(line => line.Length).DefaultIfEmpty(0).Max();
        Document.PageWidth = Math.Max(640, longestLine * FontSize * 0.8 + 32);
        var findingRanges = findings?.Where(finding => !finding.NotFlagged)
            .Select(finding => (Finding: finding, Ranges: finding.SelectedRanges)).ToArray() ?? [];
        for (var index = 0; index < displayRows.Length; index++)
        {
            var line = displayRows[index];
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = rowHeight };
            if (index < mappedLines.Count)
            {
                paragraph.Background = mappedLines[index].Kind switch
                {
                    DisplayLineKind.Added => Brush("#263B2C"),
                    DisplayLineKind.Removed => Brush("#472D33"),
                    _ => null
                };
            }
            else if (!hasLineGutters && line.StartsWith("+ ", StringComparison.Ordinal))
            {
                paragraph.Background = Brush("#263B2C");
            }
            else if (!hasLineGutters && line.StartsWith("- ", StringComparison.Ordinal))
            {
                paragraph.Background = Brush("#472D33");
            }
            if (index < mappedLines.Count)
            {
                var displayLine = mappedLines[index];
                var finding = findingRanges.FirstOrDefault(item =>
                    item.Ranges.Any(range => range.Contains(displayLine))).Finding;
                if (finding is not null)
                    paragraph.Background = FindingBrush(finding.Severity);
            }
            AddHighlightedLine(paragraph, line, extension, hasLineGutters);
            Document.Blocks.Add(paragraph);
            rowIndexes.Add(paragraph, index);
            if (index < mappedLines.Count && mappedLines[index].BaseLine is int baseLine)
                baseRowIndexes.TryAdd(baseLine, index);
            rows.Add(paragraph);
            rowBackgrounds.Add(paragraph.Background);
        }
        CancelPendingSelection();
    }

    private static SolidColorBrush FindingBrush(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Blocking => Brush("#5A3038"),
        FindingSeverity.Material => Brush("#51402C"),
        FindingSeverity.Minor => Brush("#41432F"),
        FindingSeverity.Trivial => Brush("#35414B"),
        _ => Brush("#464B57")
    };

    public (int Start, int End)? SelectedRows()
    {
        if (Selection.IsEmpty) return null;
        if (Selection.Start.Paragraph is not { } startParagraph ||
            Selection.End.Paragraph is not { } endParagraph ||
            !rowIndexes.TryGetValue(startParagraph, out var start) ||
            !rowIndexes.TryGetValue(endParagraph, out var end))
            return null;
        return (start, Math.Max(start, end));
    }

    /// <summary>Returns one-based display-row spans, including retained Ctrl selections.</summary>
    public IReadOnlyList<LineSpan> SelectedRowSpans()
    {
        var active = SelectedRows();
        return LineSpan.Normalize(active is { } selected
            ? retainedRowSpans.Append(new LineSpan(selected.Start + 1, selected.End + 1))
            : retainedRowSpans);
    }

    private void RetainActiveSelection()
    {
        SetRetainedRows(SelectedRowSpans());
        // Start a fresh native drag, including when it begins inside the previous range.
        // Otherwise WPF can treat Ctrl+drag as dragging the old selected text.
        Selection.Select(CaretPosition, CaretPosition);
    }

    private void SetRetainedRows(IReadOnlyList<LineSpan> spans)
    {
        var previous = retainedRowSpans;
        if (previous.SequenceEqual(spans)) return;
        foreach (var span in previous)
            for (var row = span.Lower - 1; row < span.Upper; row++)
                if (!spans.Any(next => next.Contains(row + 1)))
                    rows[row].Background = rowBackgrounds[row];
        retainedRowSpans = spans;
        foreach (var span in retainedRowSpans)
            for (var row = span.Lower - 1; row < span.Upper; row++)
                if (!previous.Any(old => old.Contains(row + 1)))
                    rows[row].Background = RetainedSelection;
    }

    private static readonly Brush RetainedSelection = Brush("#3E526F");

    private void CopyDisconnectedSelection(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Copy || retainedRowSpans.Count == 0) return;
        var text = string.Join(Environment.NewLine + Environment.NewLine, SelectedRowSpans().Select(span =>
            string.Join(Environment.NewLine, Enumerable.Range(span.Lower - 1, span.Upper - span.Lower + 1)
                .Select(row => row < displayLines.Count ? displayLines[row].Text : plainLines[row]))));
        try
        {
            if (text.Length == 0) Clipboard.Clear();
            else Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // A busy clipboard must not interrupt selection or discard retained ranges.
        }
        e.Handled = true;
    }

    public int RowForBaseLine(int baseLine) => baseRowIndexes.GetValueOrDefault(baseLine, -1);

    public int FirstVisibleRow()
    {
        var viewer = FindViewer();
        return viewer is null ? 0 : Math.Max(0, (int)(viewer.VerticalOffset / Math.Max(1, rowHeight)));
    }

    public void ScrollToRow(int row)
    {
        if (row >= 0 && row < rows.Count)
            rows[row].BringIntoView();
    }

    public void SelectRows(int start, int end)
    {
        if (start < 0 || end < start || end >= rows.Count) return;
        SetRetainedRows([]);
        Selection.Select(rows[start].ContentStart, rows[end].ContentEnd);
        InvalidateSelection();
        ArmSelectionTimer();
    }

    public void SelectRowSpans(IReadOnlyList<LineSpan> spans)
    {
        var normalized = LineSpan.Normalize(spans);
        if (normalized.Count == 0 || normalized.Any(span => span.Upper > rows.Count)) return;
        SetRetainedRows(normalized.Skip(1).ToArray());
        var first = normalized[0];
        Selection.Select(rows[first.Lower - 1].ContentStart, rows[first.Upper - 1].ContentEnd);
        InvalidateSelection();
        ArmSelectionTimer();
    }

    public void NavigateToRow(int row)
    {
        if (row < 0 || row >= rows.Count) return;
        // Newly rendered documents have no text geometry until WPF has laid them out.
        // Run after Loaded/render work, and ignore editors replaced by later navigation.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!IsLoaded) return;
            Focus();
            SelectRows(row, row);
            UpdateLayout();
            var viewer = FindViewer();
            if (viewer is null) return;
            var bounds = rows[row].ContentStart.GetCharacterRect(LogicalDirection.Forward);
            viewer.ScrollToVerticalOffset(Math.Clamp(
                viewer.VerticalOffset + bounds.Top - (viewer.ViewportHeight - bounds.Height) / 2,
                0, viewer.ScrollableHeight));
            viewer.ScrollToHorizontalOffset(0);
        }));
    }

    private ScrollViewer? FindViewer()
    {
        ApplyTemplate();
        return Template.FindName("PART_ContentHost", this) as ScrollViewer;
    }

    private static void AddHighlightedLine(Paragraph paragraph, string line, string extension, bool hasGutters)
    {
        var codeStart = CodeStart(line, hasGutters);

        if (codeStart > 0)
            paragraph.Inlines.Add(new Run(line[..codeStart]) { Foreground = Comment });
        var source = line[codeStart..];
        var index = 0;
        foreach (var token in SyntaxHighlighter.TokenizeLine(source, extension))
        {
            if (token.Start > index)
                paragraph.Inlines.Add(new Run(source[index..token.Start]) { Foreground = Plain });
            var brush = token.Kind switch
            {
                SyntaxTokenKind.Comment => Comment,
                SyntaxTokenKind.String => String,
                SyntaxTokenKind.Number => Number,
                SyntaxTokenKind.Keyword => Keyword,
                SyntaxTokenKind.Type => Type,
                SyntaxTokenKind.Function => Function,
                _ => Plain
            };
            paragraph.Inlines.Add(new Run(source.Substring(token.Start, token.Length)) { Foreground = brush });
            index = token.Start + token.Length;
        }
        if (index < source.Length)
            paragraph.Inlines.Add(new Run(source[index..]) { Foreground = Plain });
    }

    private static SolidColorBrush Brush(string color)
    {
        return ThemePalette.Current.Brush(color);
    }
}
