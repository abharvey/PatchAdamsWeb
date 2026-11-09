using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PatchAdams.Core;

namespace PatchAdamsSharp;

internal sealed partial class CodeEditor
{
    // A view-only overlay keeps matching out of document runs, copied text, and diff/finding backgrounds.
    private static readonly Brush WordMatchFill = Brush("#4074ADE8");
    private static readonly Brush WordMatchAccent = Brush("#74ADE8");
    private string[] wordMatchSources = [];
    private int[] wordMatchCodeStarts = [];
    private IReadOnlyList<IdentifierOccurrence> wordMatches = [];
    private CancellationTokenSource? wordMatchCancellation;
    private long wordMatchGeneration;
    private WordMatchesAdorner? wordMatchesAdorner;
    private AdornerLayer? wordMatchesLayer;
    private int selectedIdentifierRow;
    private int selectedIdentifierStart;

    public string? MatchedIdentifier { get; private set; }
    public IReadOnlyList<IdentifierOccurrence> WordMatches => wordMatches;

    private void InitializeWordMatching()
    {
        SizeChanged += (_, _) => wordMatchesAdorner?.InvalidateVisual();
        AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, _) => wordMatchesAdorner?.InvalidateVisual()));
    }

    private void SetWordMatchSources(IReadOnlyList<string> rendered, IReadOnlyList<DisplayLine> mapped, bool hasGutters)
    {
        wordMatchCodeStarts = rendered.Select(line => CodeStart(line, hasGutters)).ToArray();
        wordMatchSources = rendered.Select((line, index) =>
            index < mapped.Count && mapped[index].Kind is not DisplayLineKind.CollapsedContext and not DisplayLineKind.SyntheticFiller &&
            (mapped[index].BaseLine.HasValue || mapped[index].PostLine.HasValue)
                ? line[wordMatchCodeStarts[index]..] : "").ToArray();
    }

    private bool TrySelectedIdentifier(out string identifier, out int row, out int start)
    {
        identifier = "";
        row = start = -1;
        if (Selection.IsEmpty || retainedRowSpans.Count > 0 ||
            Selection.Start.Paragraph is not { } paragraph || Selection.End.Paragraph != paragraph ||
            !rowIndexes.TryGetValue(paragraph, out row) || row >= wordMatchSources.Length)
            return false;
        start = new TextRange(paragraph.ContentStart, Selection.Start).Text.Length - wordMatchCodeStarts[row];
        identifier = Selection.Text;
        return IdentifierOccurrences.IsWholeIdentifier(wordMatchSources[row], start, identifier.Length);
    }

    private async Task UpdateWordMatchesAsync()
    {
        ClearWordMatches();
        if (!IsLoaded || !IsKeyboardFocusWithin || !TrySelectedIdentifier(out var identifier, out var row, out var start))
            return;
        var sources = wordMatchSources;
        var generation = wordMatchGeneration;
        using var cancellation = new CancellationTokenSource();
        wordMatchCancellation = cancellation;
        try
        {
            var matches = await Task.Run(() => IdentifierOccurrences.Find(sources, identifier, cancellation.Token),
                cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != wordMatchGeneration ||
                !IsLoaded || !IsKeyboardFocusWithin) return;
            wordMatches = matches;
            MatchedIdentifier = identifier;
            selectedIdentifierRow = row;
            selectedIdentifierStart = start;
            AutomationProperties.SetHelpText(this,
                $"{matches.Count} exact matches for {identifier} on {matches.Select(match => match.Row).Distinct().Count()} displayed lines.");
            wordMatchesLayer = AdornerLayer.GetAdornerLayer(this);
            if (wordMatchesLayer is not null)
            {
                wordMatchesAdorner = new WordMatchesAdorner(this) { IsHitTestVisible = false };
                wordMatchesLayer.Add(wordMatchesAdorner);
            }
        }
        catch (OperationCanceledException)
        {
            // A new selection, another pane, or a replacement document owns the display now.
        }
        finally
        {
            if (ReferenceEquals(wordMatchCancellation, cancellation)) wordMatchCancellation = null;
        }
    }

    private void ClearWordMatches()
    {
        ++wordMatchGeneration;
        wordMatchCancellation?.Cancel();
        wordMatchCancellation = null;
        wordMatches = [];
        MatchedIdentifier = null;
        if (wordMatchesAdorner is not null) wordMatchesLayer?.Remove(wordMatchesAdorner);
        wordMatchesAdorner = null;
        wordMatchesLayer = null;
        AutomationProperties.SetHelpText(this, "");
    }

    private TextPointer? SourcePosition(int row, int offset)
    {
        offset += wordMatchCodeStarts[row];
        foreach (var run in rows[row].Inlines.OfType<Run>())
        {
            if (offset <= run.Text.Length) return run.ContentStart.GetPositionAtOffset(offset);
            offset -= run.Text.Length;
        }
        return null;
    }

    private Rect MatchViewport()
    {
        var viewer = FindViewer();
        if (viewer?.Template?.FindName("PART_ScrollContentPresenter", viewer) is ScrollContentPresenter content)
        {
            var origin = content.TranslatePoint(new Point(), this);
            return new Rect(Math.Max(Padding.Left, origin.X), origin.Y,
                Math.Max(0, content.ActualWidth - Math.Max(0, Padding.Left - origin.X)), content.ActualHeight);
        }
        return new Rect(Padding.Left, Padding.Top,
            Math.Max(0, ActualWidth - Padding.Left - Padding.Right - 12),
            Math.Max(0, ActualHeight - Padding.Top - Padding.Bottom - 12));
    }

    private static int CodeStart(string line, bool hasGutters)
    {
        if (hasGutters)
        {
            var secondSeparator = line.IndexOf('|', line.IndexOf('|') + 1);
            if (secondSeparator < 0) return line.Length;
            var start = secondSeparator + 1;
            return start < line.Length && line[start] == ' ' ? start + 1 : start;
        }
        return line.Length >= 2 && (line.StartsWith("+ ", StringComparison.Ordinal) ||
            line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("  ", StringComparison.Ordinal)) ? 2 : 0;
    }

    private sealed class WordMatchesAdorner(CodeEditor editor) : Adorner(editor)
    {
        private readonly Pen outline = new(WordMatchAccent, 1);

        protected override void OnRender(DrawingContext context)
        {
            var viewport = editor.MatchViewport();
            if (viewport.IsEmpty || viewport.Width <= 0 || viewport.Height <= 0) return;
            var top = editor.GetPositionFromPoint(new Point(viewport.Left + 1, viewport.Top + 1), true)?.Paragraph;
            var bottom = editor.GetPositionFromPoint(new Point(viewport.Left + 1, viewport.Bottom - 1), true)?.Paragraph;
            var first = top is not null && editor.rowIndexes.TryGetValue(top, out var a) ? a : 0;
            var last = bottom is not null && editor.rowIndexes.TryGetValue(bottom, out var b) ? b : editor.rows.Count - 1;
            var markedRows = new HashSet<int>();
            foreach (var match in editor.wordMatches)
            {
                if (match.Row < first) continue;
                if (match.Row > last) break;
                var start = editor.SourcePosition(match.Row, match.Start);
                var end = editor.SourcePosition(match.Row, match.Start + match.Length);
                if (start is null || end is null) continue;
                var startRect = start.GetCharacterRect(LogicalDirection.Forward);
                var endRect = end.GetCharacterRect(LogicalDirection.Backward);
                if (startRect.IsEmpty || endRect.IsEmpty) continue;
                context.PushClip(new RectangleGeometry(viewport));
                var selected = match.Row == editor.selectedIdentifierRow && match.Start == editor.selectedIdentifierStart;
                if (Math.Abs(startRect.Top - endRect.Top) < 1)
                    Draw(context, new Rect(startRect.TopLeft, endRect.BottomRight), selected);
                else
                {
                    // Rare wrapped identifiers need separate boxes instead of one rectangle across intervening text.
                    var position = start;
                    while (position is not null && position.CompareTo(end) < 0)
                    {
                        var rect = position.GetCharacterRect(LogicalDirection.Forward);
                        if (!rect.IsEmpty) Draw(context, rect, selected);
                        position = position.GetNextInsertionPosition(LogicalDirection.Forward);
                    }
                }
                context.Pop();
                if (markedRows.Add(match.Row))
                {
                    context.PushClip(new RectangleGeometry(new Rect(0, viewport.Top, editor.Padding.Left, viewport.Height)));
                    context.DrawEllipse(WordMatchAccent, null,
                        new Point(6, startRect.Top + startRect.Height / 2), 2.5, 2.5);
                    context.Pop();
                }
            }
        }

        private void Draw(DrawingContext context, Rect bounds, bool selected) =>
            context.DrawRoundedRectangle(selected ? null : WordMatchFill, outline, bounds, 1, 1);
    }
}
