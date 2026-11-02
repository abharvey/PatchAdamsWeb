using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PatchAdams.Core;

namespace PatchAdamsSharp;

internal sealed class CodeEditor : RichTextBox
{
    private static readonly Brush Plain = Brush("#DDE7F2");
    private static readonly Brush Keyword = Brush("#FF4FD8");
    private static readonly Brush Type = Brush("#43E8FF");
    private static readonly Brush Function = Brush("#66FF99");
    private static readonly Brush String = Brush("#FFB38A");
    private static readonly Brush Number = Brush("#FF9F43");
    private static readonly Brush Comment = Brush("#8294AD");

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
        Background = Brush("#0F1827");
        Foreground = Plain;
        BorderBrush = Brush("#31445F");
        Padding = new Thickness(6);
        FontFamily = new FontFamily("Consolas");
        FontSize = 13;
        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            PageWidth = double.PositiveInfinity
        };
        rowHeight = FontSize * 1.25;
    }

    public void SetCode(
        IReadOnlyList<string> lines,
        IReadOnlyList<DisplayLine> mappedLines,
        string extension,
        bool hasLineGutters)
    {
        Document.Blocks.Clear();
        rows.Clear();
        displayLines.Clear();
        displayLines.AddRange(mappedLines);
        plainLines.Clear();
        plainLines.AddRange(lines);
        rowHeight = FontSize * 1.25;
        foreach (var line in lines)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = rowHeight };
            AddHighlightedLine(paragraph, line, extension, hasLineGutters);
            Document.Blocks.Add(paragraph);
            rows.Add(paragraph);
        }
    }

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
