using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PatchAdams.Core;

namespace PatchAdamsSharp;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> files = [];
    private readonly ObservableCollection<string> findingLabels = [];
    private readonly List<SessionFinding> findings = [];
    private readonly List<LoadedColumn> columns = [];
    private string? basePath;
    private string selectedPath = "";
    private Selection? selection;
    private string? golden;
    private ReviewSession? loadedSession;

    public MainWindow()
    {
        InitializeComponent();
        FileTree.ItemsSource = files;
        FindingsList.ItemsSource = findingLabels;
        GoldenPicker.Items.Add("None");
        GoldenPicker.SelectedIndex = 0;
    }

    private void ChooseBase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the patch base directory" };
        if (dialog.ShowDialog(this) != true) return;
        basePath = dialog.FolderName;
        StatusText.Text = $"Base: {basePath}";
        ReloadPatches();
    }

    private void AddPatches_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add two to four patch files",
            Filter = "Patch files (*.patch;*.diff)|*.patch;*.diff|All files (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (dialog.FileNames.Length is < 2 or > 4)
        {
            MessageBox.Show(this, "Choose between two and four patch files.", "PatchAdams#",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            columns.Clear();
            findings.Clear();
            findingLabels.Clear();
            loadedSession = null;
            var labels = new[] { "A", "B", "C", "D" };
            for (var index = 0; index < dialog.FileNames.Length; index++)
            {
                var path = dialog.FileNames[index];
                var text = File.ReadAllText(path);
                var patch = UnifiedDiffParser.Parse(text);
                columns.Add(new LoadedColumn(labels[index], Path.GetFileName(path), text, patch));
            }
            golden = null;
            ReloadPatches();
            StatusText.Text = $"{columns.Count} patches loaded.";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not load patches", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReloadPatches()
    {
        if (basePath is null || columns.Count == 0) return;
        try
        {
            var provider = BaseLoader.Open(basePath);
            foreach (var column in columns)
                column.Applied = ApplyEngine.Apply(column.Patch, provider);
            files.Clear();
            foreach (var path in columns.SelectMany(column => column.Applied!.Files).Select(file => file.Path)
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                files.Add(path);

            GoldenPicker.Items.Clear();
            GoldenPicker.Items.Add("None");
            foreach (var column in columns)
                GoldenPicker.Items.Add($"{column.Label} — {column.SourceName}");
            var goldenIndex = columns.FindIndex(column => column.Label == golden);
            GoldenPicker.SelectedIndex = goldenIndex + 1;
            ReportText.Text = string.Join(Environment.NewLine, columns.Select(column =>
                $"{column.Label} — {column.SourceName}: {column.Applied!.Report.Clean} clean, " +
                $"{column.Applied.Report.Fuzz} fuzz, {column.Applied.Report.Failed} failed, " +
                $"{column.Applied.Report.Binary} binary"));
            RenderColumns();
            if (files.Count > 0)
            {
                selectedPath = files[0];
                RenderColumns();
            }
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not load base", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        selectedPath = e.NewValue as string ?? "";
        RenderColumns();
    }

    private void RenderColumns()
    {
        ColumnsGrid.Children.Clear();
        ColumnsGrid.ColumnDefinitions.Clear();
        selection = null;
        ReferenceText.Text = "Select lines in a column to generate a reference.";
        if (columns.Count == 0) return;
        for (var index = 0; index < columns.Count; index++)
        {
            ColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = columns[index];
            var panel = new DockPanel { Margin = new Thickness(4) };
            var header = new TextBlock
            {
                Text = $"{column.Label} — {column.SourceName}",
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(3, 3, 3, 7)
            };
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);

            var text = column.Applied?.FileAt(selectedPath)?.PostText;
            var editor = new TextBox
            {
                Text = RenderColumnText(column, text),
                IsReadOnly = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Background = new SolidColorBrush(Color.FromRgb(15, 24, 39)),
                Foreground = new SolidColorBrush(Color.FromRgb(221, 231, 242)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(49, 68, 95)),
                Tag = column,
                Padding = new Thickness(6)
            };
            editor.SelectionChanged += CodeSelectionChanged;
            panel.Children.Add(editor);
            Grid.SetColumn(panel, index);
            ColumnsGrid.Children.Add(panel);
        }
    }

    private string RenderColumnText(LoadedColumn column, string? text)
    {
        if (text is null)
        {
            var file = column.Applied?.FileAt(selectedPath);
            return file?.Status.Kind == ApplyStatusKind.Failed
                ? $"[Failed to apply]\n{file.Status.Message}\n\nRaw patch hunks remain available in the source file."
                : file?.Status.Kind == ApplyStatusKind.Binary ? "[Binary file change]" : "[File absent in this patch]";
        }
        if (golden is null || column.Label == golden)
            return text;
        var goldenText = columns.FirstOrDefault(item => item.Label == golden)?.Applied?.FileAt(selectedPath)?.PostText ?? "";
        var diff = DiffEngine.Lines(goldenText, text);
        var result = new List<string>();
        foreach (var edit in diff.Edits)
        {
            if (edit.Kind == EditKind.Equal)
                result.AddRange(DiffEngine.SplitLines(goldenText).Skip(edit.AStart).Take(edit.ALength).Select(line => "  " + line));
            else if (edit.Kind == EditKind.Delete)
                result.AddRange(DiffEngine.SplitLines(goldenText).Skip(edit.AStart).Take(edit.ALength).Select(line => "- " + line));
            else
                result.AddRange(DiffEngine.SplitLines(text).Skip(edit.BStart).Take(edit.BLength).Select(line => "+ " + line));
        }
        return string.Join(Environment.NewLine, result);
    }

    private void CodeSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox editor || editor.Tag is not LoadedColumn column || editor.SelectionLength == 0)
            return;
        var first = editor.GetLineIndexFromCharacterIndex(editor.SelectionStart) + 1;
        var last = editor.GetLineIndexFromCharacterIndex(editor.SelectionStart + editor.SelectionLength) + 1;
        if (selectedPath.Length == 0) return;
        if (golden is not null && column.Label != golden) return;
        var applied = column.Applied?.FileAt(selectedPath);
        if (applied?.PostText is null) return;
        try
        {
            var reference = ReferenceGenerator.ForSelection(applied, first, last);
            selection = new Selection(column, applied, first, last, reference);
            ReferenceText.Text = $"{column.Label} — {reference}";
        }
        catch (ArgumentOutOfRangeException)
        {
            selection = null;
        }
    }

    private void GoldenPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GoldenPicker.SelectedIndex < 0) return;
        golden = GoldenPicker.SelectedIndex == 0 ? null : columns.ElementAtOrDefault(GoldenPicker.SelectedIndex - 1)?.Label;
        RenderColumns();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, e);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text;
        if (string.IsNullOrEmpty(query)) return;
        var matches = columns.SelectMany(column => column.Applied?.Files ?? [])
            .Where(file => file.PostText?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .Select(file => $"{file.Path} — {columns.First(column => column.Applied?.Files.Contains(file) == true).Label}")
            .Distinct().ToArray();
        MessageBox.Show(this, matches.Length == 0 ? "No matches." : string.Join(Environment.NewLine, matches),
            $"Search: {query}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveSession_Click(object sender, RoutedEventArgs e)
    {
        if (columns.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = "Choose or create a .patchadam session folder" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SessionPackage.Write(new SessionContents(BuildSession(),
                columns.ToDictionary(column => column.Label, column => column.PatchText)), dialog.FolderName);
            StatusText.Text = $"Session saved to {dialog.FolderName}";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not save session", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportMarkdown_Click(object sender, RoutedEventArgs e) => Export(markdown: true);
    private void ExportJson_Click(object sender, RoutedEventArgs e) => Export(markdown: false);

    private void Export(bool markdown)
    {
        var dialog = new SaveFileDialog
        {
            Filter = markdown ? "Markdown (*.md)|*.md" : "JSON (*.json)|*.json",
            FileName = markdown ? "patch-review.md" : "patch-review.json"
        };
        if (dialog.ShowDialog(this) != true) return;
        var session = BuildSession();
        if (markdown)
            File.WriteAllText(dialog.FileName, MarkdownExport.Render(session));
        else
            File.WriteAllBytes(dialog.FileName, MarkdownExport.RenderJson(session));
    }

    private void AddFinding_Click(object sender, RoutedEventArgs e)
    {
        if (selection is null)
        {
            MessageBox.Show(this, "Select lines in a patch column first.", "PatchAdams#",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new FindingDialog(selection.Reference) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        findings.Add(new SessionFinding(Guid.NewGuid(), selection.Column.Label, selection.File.Path,
            new LineSpan(selection.Start, selection.End), selection.Reference, dialog.TitleText,
            dialog.Description, dialog.Category, Enum.Parse<FindingSeverity>(dialog.Severity, ignoreCase: true),
            RequestRelevance.Unclear, false, "", [], DateTimeOffset.UtcNow));
        findingLabels.Add($"{selection.Column.Label} · {dialog.Severity} · {dialog.TitleText} — {selection.Reference}");
        StatusText.Text = "Finding recorded for this window.";
    }

    private void OpenSession_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Open a .patchadam session folder" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var contents = SessionPackage.Read(dialog.FolderName);
            columns.Clear();
            findings.Clear();
            findingLabels.Clear();
            loadedSession = contents.Session;
            RequestBox.Text = contents.Session.Request;
            foreach (var column in contents.Session.Columns)
            {
                var text = contents.PatchTexts[column.Label];
                columns.Add(new LoadedColumn(column.Label, column.SourceName, text, UnifiedDiffParser.Parse(text)));
            }
            findings.AddRange(contents.Session.Findings);
            foreach (var finding in findings)
                findingLabels.Add($"{finding.Label} · {finding.Severity} · {finding.Title} — {finding.Reference}");
            basePath = contents.Session.Base.Path;
            golden = contents.Session.Golden;
            ReloadPatches();
            StatusText.Text = $"Session loaded: {dialog.FolderName}";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not open session", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ReviewSession BuildSession()
    {
        var old = loadedSession;
        var baseInfo = old?.Base ?? new SessionBase(basePath is null ? "synthetic" : "directory", Path: basePath);
        return new ReviewSession(ReviewSession.CurrentVersion, old?.Name ?? "Patch review", RequestBox.Text,
            baseInfo,
            columns.Select(column => new SessionColumn(column.Label, column.SourceName, "",
                old?.Columns.FirstOrDefault(existing => existing.Label == column.Label)?.IsHidden ?? false,
                old?.Columns.FirstOrDefault(existing => existing.Label == column.Label)?.IsRead ?? false)).ToArray(),
            golden, old?.Categories ?? ReviewSession.DefaultCategories, findings.ToArray(),
            old?.Ranking ?? new SessionRanking(new Dictionary<string, int>(), ""),
            old?.Pins ?? [], old?.ManualAnchors ?? [],
            old?.Blind ?? new BlindMode(false, 0), old?.Created ?? DateTimeOffset.UtcNow);
    }

    private sealed class LoadedColumn(string label, string sourceName, string patchText, Patch patch)
    {
        public string Label { get; } = label;
        public string SourceName { get; } = sourceName;
        public string PatchText { get; } = patchText;
        public Patch Patch { get; } = patch;
        public AppliedPatch? Applied { get; set; }
    }

    private sealed record Selection(LoadedColumn Column, AppliedFile File, int Start, int End, string Reference);
}
