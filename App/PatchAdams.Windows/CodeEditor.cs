using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PatchAdams.Core;

namespace PatchAdamsSharp;

internal sealed class CodeEditor : RichTextBox
{
    private static readonly Brush Plain = Brush("#ABB2BF");
    private static readonly Brush Keyword = Brush("#C678DD");
    private static readonly Brush Type = Brush("#56B6C2");
    private static readonly Brush Function = Brush("#61AFEF");
    private static readonly Brush String = Brush("#98C379");
    private static readonly Brush Number = Brush("#D19A66");
    private static readonly Brush Comment = Brush("#5C6370");

    private readonly List<Paragraph> rows = [];
    private readonly List<DisplayLine> displayLines = [];
    private readonly List<string> plainLines = [];
    private double rowHeight;

    public IReadOnlyList<DisplayLine> DisplayLines => displayLines;
    public IReadOnlyList<string> PlainLines => plainLines;

    public CodeEditor()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        AcceptsTab = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Background = Brush("#282C33");
        Foreground = Plain;
        BorderBrush = Brush("#464B57");
        Padding = new Thickness(6);
        FontFamily = new FontFamily("Consolas");
        FontSize = 13;
        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity
        };
        rowHeight = FontSize * 1.15;
    }

    public void SetCode(
        IReadOnlyList<string> lines,
        IReadOnlyList<DisplayLine> mappedLines,
        string extension,
        bool hasLineGutters,
        IReadOnlyList<SessionFinding>? findings = null)
    {
        Document.Blocks.Clear();
        rows.Clear();
        displayLines.Clear();
        displayLines.AddRange(mappedLines);
        var displayRows = lines.Select(line => line.TrimEnd('\r')).ToArray();
        plainLines.Clear();
        plainLines.AddRange(displayRows);
        rowHeight = FontSize * 1.15;
        var longestLine = displayRows.Select(line => line.Length).DefaultIfEmpty(0).Max();
        Document.PageWidth = Math.Max(640, longestLine * FontSize * 0.8 + 32);
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
            if (index < mappedLines.Count && findings is not null)
            {
                var displayLine = mappedLines[index];
                var finding = findings.FirstOrDefault(item => !item.NotFlagged &&
                    ((displayLine.PostLine is int post && post >= item.Lines.Lower && post <= item.Lines.Upper) ||
                     (displayLine.PostLine is null && displayLine.BaseLine is int baseLine &&
                      baseLine >= item.Lines.Lower && baseLine <= item.Lines.Upper)));
                if (finding is not null)
                    paragraph.Background = FindingBrush(finding.Severity);
            }
            AddHighlightedLine(paragraph, line, extension, hasLineGutters);
            Document.Blocks.Add(paragraph);
            rows.Add(paragraph);
        }
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
        var blocks = Document.Blocks.Cast<Block>().ToList();
        var start = blocks.IndexOf(Selection.Start.Paragraph);
        var end = blocks.IndexOf(Selection.End.Paragraph);
        if (start < 0 || end < 0) return null;
        return (start, Math.Max(start, end));
    }

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
        Selection.Select(rows[start].ContentStart, rows[end].ContentEnd);
    }

    private ScrollViewer? FindViewer()
    {
        ApplyTemplate();
        return Template.FindName("PART_ContentHost", this) as ScrollViewer;
    }

    private static void AddHighlightedLine(Paragraph paragraph, string line, string extension, bool hasGutters)
    {
        var codeStart = 0;
        if (hasGutters)
        {
            var secondSeparator = line.IndexOf('|', line.IndexOf('|') + 1);
            codeStart = secondSeparator < 0 ? line.Length : secondSeparator + 1;
        }
        else if (line.Length >= 2 && (line.StartsWith("+ ", StringComparison.Ordinal) ||
                                      line.StartsWith("- ", StringComparison.Ordinal) ||
                                      line.StartsWith("  ", StringComparison.Ordinal)))
        {
            codeStart = 2;
        }

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
        var converted = (Color)ColorConverter.ConvertFromString(color)!;
        var brush = new SolidColorBrush(converted);
        brush.Freeze();
        return brush;
    }
}
