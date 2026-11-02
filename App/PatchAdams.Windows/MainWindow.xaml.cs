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
    private readonly ObservableCollection<FileTreeEntry> files = [];
    private readonly ObservableCollection<string> findingLabels = [];
    private readonly ObservableCollection<SessionPin> pins = [];
    private readonly ObservableCollection<ComparisonEntry> comparisons = [];
    private readonly List<SessionFinding> findings = [];
    private readonly List<string> categories = [.. ReviewSession.DefaultCategories];
    private readonly List<ManualAnchor> manualAnchors = [];
    private readonly List<Selection> recentSelections = [];
    private readonly List<LoadedColumn> columns = [];
    private readonly Dictionary<string, TextBox> codeEditors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComboBox> rankPickers = new(StringComparer.Ordinal);
    private bool synchronizingScroll;
    private double fontSize = 13;
    private string? basePath;
    private string? baseRevision;
    private string? baseCommit;
    private string baseKind = "synthetic";
    private string selectedPath = "";
    private Selection? selection;
    private string? golden;
    private ReviewSession? loadedSession;
    private ulong blindSeed = unchecked((ulong)Random.Shared.NextInt64());

    public MainWindow()
    {
        InitializeComponent();
        FileTree.ItemsSource = files;
        FindingsList.ItemsSource = findingLabels;
        PinsList.ItemsSource = pins;
        ComparisonList.ItemsSource = comparisons;
        RationaleBox.Text = "";
        GoldenPicker.Items.Add("None");
        GoldenPicker.SelectedIndex = 0;
    }

    private void ChooseBase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the patch base directory" };
        if (dialog.ShowDialog(this) != true) return;
        basePath = dialog.FolderName;
        baseKind = "directory";
        baseRevision = null;
        baseCommit = null;
        StatusText.Text = $"Base: {basePath}";
        ReloadPatches();
    }

    private void ChooseArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a base archive",
            Filter = "Base archives (*.tar;*.tar.gz;*.tgz;*.zip)|*.tar;*.tar.gz;*.tgz;*.zip|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        basePath = dialog.FileName;
        baseKind = "archive";
        baseRevision = null;
        baseCommit = null;
        StatusText.Text = $"Archive base: {basePath}";
        ReloadPatches();
    }

    private void ChooseGitRevision_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = "Choose a local Git repository" };
        if (folder.ShowDialog(this) != true) return;
        var revision = TextPromptDialog.Show(this, "Git revision", "Revision or commit:");
        if (string.IsNullOrWhiteSpace(revision)) return;
        basePath = folder.FolderName;
        baseRevision = revision.Trim();
        baseCommit = null;
        baseKind = "gitRevision";
        StatusText.Text = $"Git base: {basePath}@{baseRevision}";
        ReloadPatches();
    }

    private void RebuildSyntheticBase_Click(object sender, RoutedEventArgs e)
    {
        if (columns.Count == 0) return;
        basePath = null;
        baseRevision = null;
        baseCommit = null;
        baseKind = "synthetic";
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
            categories.Clear();
            categories.AddRange(ReviewSession.DefaultCategories);
            manualAnchors.Clear();
            pins.Clear();
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
        if (columns.Count == 0) return;
        try
        {
            IFileProvider provider;
            if (basePath is null)
            {
                provider = new InMemoryFileProvider(SyntheticBase.Merge(columns.Select(column => column.Patch)),
                    "synthetic base from the patches");
                baseKind = "synthetic";
            }
            else
            {
                try
                {
                    provider = BaseLoader.Open(basePath, baseRevision);
                    baseCommit = (provider as GitRevisionFileProvider)?.Commit;
                }
                catch (Exception) when (!Directory.Exists(basePath) && !File.Exists(basePath))
                {
                    provider = new InMemoryFileProvider(SyntheticBase.Merge(columns.Select(column => column.Patch)),
                        "synthetic base from the patches");
                    basePath = null;
                    baseRevision = null;
                    baseCommit = null;
                    baseKind = "synthetic";
                    StatusText.Text = "The saved base is unavailable; rebuilt a synthetic base from the patches.";
                }
            }
            foreach (var column in columns)
                column.Applied = ApplyEngine.Apply(column.Patch, provider);
            files.Clear();
            foreach (var path in columns.SelectMany(column => column.Applied!.Files).Select(file => file.Path)
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var statuses = columns.Select(column =>
                {
                    var file = column.Applied!.FileAt(path);
                    return $"{BlindDisplayLabel(column.Label)}:{StatusFor(file)}";
                });
                files.Add(new FileTreeEntry(path, string.Join("  ", statuses)));
            }

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
                selectedPath = files[0].Path;
                RenderColumns();
            }
            RenderRanking();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not load base", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        selectedPath = (e.NewValue as FileTreeEntry)?.Path ?? "";
        recentSelections.Clear();
        RenderColumns();
    }

    private void RenderColumns()
    {
        ColumnsGrid.Children.Clear();
        ColumnsGrid.ColumnDefinitions.Clear();
        codeEditors.Clear();
        selection = null;
        comparisons.Clear();
        ReferenceText.Text = "Select lines in a column to generate a reference.";
        if (columns.Count == 0) return;
        var visibleColumns = columns.Where(column => !column.IsHidden).ToArray();
        for (var index = 0; index < visibleColumns.Length; index++)
        {
            ColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var column = visibleColumns[index];
            var panel = new DockPanel { Margin = new Thickness(4) };
            var header = new TextBlock
            {
                Text = BlindModeToggle.IsChecked == true
                    ? $"Patch {BlindDisplayLabel(column.Label)}"
                    : $"{column.Label} — {column.SourceName}",
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(3, 3, 3, 7)
            };
            header.ContextMenu = CreateColumnMenu(column);
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);

            var appliedFile = column.Applied?.FileAt(selectedPath);
            var isGoldenDiff = golden is not null && column.Label != golden;
            IReadOnlyList<DisplayLine> displayLines = appliedFile?.PostText is not null && !isGoldenDiff
                ? DisplayLines.Create(appliedFile)
                : [];
            var editor = new TextBox
            {
                Text = appliedFile?.PostText is null
                    ? RenderColumnText(column, null)
                    : isGoldenDiff
                        ? RenderColumnText(column, appliedFile.PostText)
                        : RenderDisplayLines(displayLines),
                IsReadOnly = true,
                AcceptsReturn = true,
                AcceptsTab = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas"),
                FontSize = fontSize,
                Background = new SolidColorBrush(Color.FromRgb(15, 24, 39)),
                Foreground = new SolidColorBrush(Color.FromRgb(221, 231, 242)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(49, 68, 95)),
                Tag = new EditorContext(column, appliedFile, displayLines, isGoldenDiff),
                Padding = new Thickness(6)
            };
            editor.SelectionChanged += CodeSelectionChanged;
            editor.Loaded += (_, _) =>
            {
                if (editor.Template.FindName("PART_ContentHost", editor) is ScrollViewer scrollViewer)
                {
                    scrollViewer.Tag = new EditorContext(column, appliedFile, displayLines, isGoldenDiff);
                    scrollViewer.ScrollChanged += CodeScrollChanged;
                }
            };
            codeEditors[column.Label] = editor;
            panel.Children.Add(editor);
            Grid.SetColumn(panel, index);
            ColumnsGrid.Children.Add(panel);
        }
    }

    private ContextMenu CreateColumnMenu(LoadedColumn column)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Move left", () => MoveColumn(column, -1)));
        menu.Items.Add(CreateMenuItem("Move right", () => MoveColumn(column, 1)));
        menu.Items.Add(CreateMenuItem(column.IsHidden ? "Show column" : "Hide column", () => ToggleHidden(column)));
        menu.Items.Add(CreateMenuItem(column.IsRead ? "Mark unread" : "Mark read", () =>
        {
            column.IsRead = !column.IsRead;
            RenderColumns();
        }));
        return menu;
    }

    private static MenuItem CreateMenuItem(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        return item;
    }

    private void MoveColumn(LoadedColumn column, int delta)
    {
        var current = columns.IndexOf(column);
        var target = current + delta;
        if (target < 0 || target >= columns.Count) return;
        (columns[current], columns[target]) = (columns[target], columns[current]);
        RenderColumns();
        RenderRanking();
    }

    private void ToggleHidden(LoadedColumn column)
    {
        if (!column.IsHidden && columns.Count(candidate => !candidate.IsHidden) <= 1) return;
        column.IsHidden = !column.IsHidden;
        RenderColumns();
    }

    private void ManageColumns_Click(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = "Manage patch columns",
            Owner = this,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(24, 38, 59)),
            Foreground = new SolidColorBrush(Color.FromRgb(231, 237, 245))
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        var checks = new Dictionary<LoadedColumn, CheckBox>();
        foreach (var column in columns)
        {
            var check = new CheckBox
            {
                Content = BlindModeToggle.IsChecked == true
                    ? $"Patch {BlindDisplayLabel(column.Label)}"
                    : $"{column.Label} — {column.SourceName}",
                IsChecked = !column.IsHidden,
                Margin = new Thickness(0, 4, 0, 4)
            };
            checks[column] = check;
            panel.Children.Add(check);
        }
        var apply = new Button { Content = "Apply", HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_, _) =>
        {
            if (checks.Values.All(check => check.IsChecked != true))
            {
                MessageBox.Show(window, "At least one column must remain visible.", "Manage columns",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var (column, check) in checks)
                column.IsHidden = check.IsChecked != true;
            window.DialogResult = true;
        };
        panel.Children.Add(apply);
        window.Content = panel;
        if (window.ShowDialog() == true)
            RenderColumns();
    }

    private static string RenderDisplayLines(IReadOnlyList<DisplayLine> lines)
    {
        var baseWidth = Math.Max(4, lines.Where(line => line.BaseLine.HasValue)
            .Select(line => line.BaseLine!.Value).DefaultIfEmpty(0).Max().ToString().Length);
        var postWidth = Math.Max(4, lines.Where(line => line.PostLine.HasValue)
            .Select(line => line.PostLine!.Value).DefaultIfEmpty(0).Max().ToString().Length);
        return string.Join(Environment.NewLine, lines.Select(line =>
            $"{(line.BaseLine?.ToString() ?? "").PadLeft(baseWidth)} | " +
            $"{(line.PostLine?.ToString() ?? "").PadLeft(postWidth)} | {line.Text}"));
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
        if (sender is not TextBox editor || editor.Tag is not EditorContext context || editor.SelectionLength == 0)
            return;
        if (context.IsGoldenDiff || context.File?.PostText is null) return;
        var first = editor.GetLineIndexFromCharacterIndex(editor.SelectionStart) + 1;
        var last = editor.GetLineIndexFromCharacterIndex(editor.SelectionStart + editor.SelectionLength) + 1;
        if (selectedPath.Length == 0) return;
        try
        {
            var selected = context.DisplayLines.Skip(first - 1)
                .Take(Math.Min(last, context.DisplayLines.Count) - first + 1).ToArray();
            var reference = ReferenceGenerator.ForDisplaySelection(selected, context.File.Change.FileName);
            var postLines = selected.Where(line => line.PostLine.HasValue).Select(line => line.PostLine!.Value).ToArray();
            var baseLines = selected.Where(line => line.BaseLine.HasValue).Select(line => line.BaseLine!.Value).ToArray();
            var start = postLines.Length > 0 ? postLines[0] : baseLines.FirstOrDefault();
            var end = postLines.Length > 0 ? postLines[^1] : baseLines.LastOrDefault();
            selection = new Selection(context.Column, context.File, start, end, reference,
                string.Join(Environment.NewLine, selected.Select(line => line.Text)));
            recentSelections.RemoveAll(existing => existing.Column.Label == context.Column.Label);
            recentSelections.Add(selection);
            if (recentSelections.Count > 2) recentSelections.RemoveAt(0);
            ReferenceText.Text = $"{context.Column.Label} — {reference}";
            if (postLines.Length > 0)
                UpdateComparisons(context.Column, context.File, postLines[0], postLines[^1]);
        }
        catch (ArgumentOutOfRangeException)
        {
            selection = null;
        }
    }

    private void UpdateComparisons(LoadedColumn sourceColumn, AppliedFile sourceFile, int startLine, int endLine)
    {
        comparisons.Clear();
        var region = new Region(selectedPath, startLine, endLine);
        foreach (var column in columns.Where(column => column.Label != sourceColumn.Label))
        {
            var target = column.Applied?.FileAt(selectedPath);
            var result = AlignmentEngine.Locate(region, sourceFile, target);
            var nearest = result.Status == AlignmentStatus.Absent && result.NearestHunkStart.HasValue
                ? $" · nearest hunk at base line {result.NearestHunkStart}"
                : "";
            comparisons.Add(new ComparisonEntry(column, target, result,
                $"{BlindDisplayLabel(column.Label)}: {result.Status.ToString().ToLowerInvariant()} " +
                $"({result.Confidence:P0}){nearest}"));
        }
    }

    private void ComparisonList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComparisonList.SelectedItem is not ComparisonEntry entry || entry.File is null ||
            !codeEditors.TryGetValue(entry.Column.Label, out var editor) ||
            editor.Tag is not EditorContext context)
            return;

        var rows = context.DisplayLines.ToList();
        var row = entry.Result.StartLine is int postLine
            ? rows.FindIndex(line => line.PostLine == postLine)
            : entry.Result.NearestHunkStart is int baseLine
                ? rows.FindIndex(line => line.BaseLine.HasValue && line.BaseLine.Value >= baseLine)
                : -1;
        if (row < 0) return;
        editor.ScrollToLine(row);
        if (entry.Result.StartLine is int start && entry.Result.EndLine is int end)
        {
            var startRow = rows.FindIndex(line => line.PostLine == start);
            var endRow = rows.FindIndex(line => line.PostLine == end);
            if (startRow >= 0 && endRow >= startRow)
            {
                var startOffset = editor.GetCharacterIndexFromLineIndex(startRow);
                var endOffset = editor.GetCharacterIndexFromLineIndex(endRow) + editor.GetLineLength(endRow);
                editor.Select(startOffset, Math.Max(0, endOffset - startOffset));
            }
        }
    }

    private void CodeScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (synchronizingScroll || ScrollLockToggle.IsChecked != true ||
            sender is not ScrollViewer source || source.Tag is not EditorContext sourceContext ||
            sourceContext.DisplayLines.Count == 0)
            return;

        var firstVisible = source.TemplatedParent is TextBox sourceEditor
            ? sourceEditor.GetFirstVisibleLineIndex()
            : (int)e.VerticalOffset;
        var sourceRow = Math.Clamp(firstVisible, 0, sourceContext.DisplayLines.Count - 1);
        var baseLine = sourceContext.DisplayLines[sourceRow].BaseLine ??
                       sourceContext.DisplayLines.Take(sourceRow + 1)
                           .LastOrDefault(line => line.BaseLine.HasValue)?.BaseLine;
        if (baseLine is null) return;

        synchronizingScroll = true;
        try
        {
            foreach (var editor in codeEditors.Values)
            {
                if (editor.Template.FindName("PART_ContentHost", editor) is not ScrollViewer target ||
                    ReferenceEquals(target, source))
                    continue;
                if (target.Tag is not EditorContext targetContext || targetContext.DisplayLines.Count == 0)
                {
                    editor.ScrollToLine(sourceRow);
                    continue;
                }
                var targetRow = targetContext.DisplayLines.ToList()
                    .FindIndex(line => line.BaseLine == baseLine);
                if (targetRow >= 0)
                    editor.ScrollToLine(targetRow);
            }
        }
        finally
        {
            synchronizingScroll = false;
        }
    }

    private void GoldenPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GoldenPicker.SelectedIndex < 0) return;
        golden = GoldenPicker.SelectedIndex == 0 ? null : columns.ElementAtOrDefault(GoldenPicker.SelectedIndex - 1)?.Label;
        RenderColumns();
    }

    private void BlindMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        RenderColumns();
        RenderRanking();
        for (var index = 0; index < findingLabels.Count && index < findings.Count; index++)
        {
            var finding = findings[index];
            findingLabels[index] =
                $"{BlindDisplayLabel(finding.Label)} · {finding.Severity} · {finding.Title} — {finding.Reference}";
        }
    }

    private string BlindDisplayLabel(string label)
    {
        if (BlindModeToggle.IsChecked != true) return label;
        var mapping = ReviewSession.GetBlindMapping(columns.Select(column => column.Label).ToArray(), blindSeed);
        return mapping.TryGetValue(label, out var number) ? number.ToString() : label;
    }

    private void RenderRanking()
    {
        RankingPanel.Children.Clear();
        rankPickers.Clear();
        foreach (var column in columns)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock
            {
                Text = BlindModeToggle.IsChecked == true
                    ? $"Patch {BlindDisplayLabel(column.Label)}"
                    : $"{column.Label} — {column.SourceName}",
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var picker = new ComboBox { Width = 58, Margin = new Thickness(6, 0, 0, 0) };
            picker.Items.Add("—");
            foreach (var number in Enumerable.Range(1, Math.Max(4, columns.Count)))
                picker.Items.Add(number.ToString());
            var rank = loadedSession?.Ranking.RankOf(column.Label);
            picker.SelectedIndex = rank is null ? 0 : rank.Value;
            DockPanel.SetDock(picker, Dock.Right);
            row.Children.Add(picker);
            RankingPanel.Children.Add(row);
            rankPickers[column.Label] = picker;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, e);
    }

    private void CopyReference_Click(object sender, RoutedEventArgs e)
    {
        if (selection is null) return;
        Clipboard.SetText(selection.Reference);
        StatusText.Text = $"Copied {selection.Reference}";
    }

    private void SmallerFont_Click(object sender, RoutedEventArgs e)
    {
        fontSize = Math.Max(9, fontSize - 1);
        RenderColumns();
    }

    private void LargerFont_Click(object sender, RoutedEventArgs e)
    {
        fontSize = Math.Min(24, fontSize + 1);
        RenderColumns();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Shift) &&
            TryColumnNumber(e.Key, out var goldenIndex))
        {
            GoldenPicker.SelectedIndex = golden == columns.ElementAtOrDefault(goldenIndex)?.Label
                ? 0
                : goldenIndex + 1;
            e.Handled = true;
            return;
        }
        if (!modifiers.HasFlag(ModifierKeys.Control)) return;
        switch (e.Key)
        {
            case Key.S: SaveSession_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.F: SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; break;
            case Key.N: AddFinding_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.P: PinSelection_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.L: ScrollLockToggle.IsChecked = !ScrollLockToggle.IsChecked; e.Handled = true; break;
            case Key.E when modifiers.HasFlag(ModifierKeys.Shift): Export(markdown: false); e.Handled = true; break;
            case Key.E: Export(markdown: true); e.Handled = true; break;
            case Key.C when modifiers.HasFlag(ModifierKeys.Shift): CopyReference_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            default:
                if (TryColumnNumber(e.Key, out var columnIndex) && columnIndex < columns.Count)
                {
                    codeEditors[columns[columnIndex].Label].Focus();
                    e.Handled = true;
                }
                break;
        }
    }

    private static bool TryColumnNumber(Key key, out int index)
    {
        index = key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            _ => -1
        };
        return index >= 0;
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var query = SearchBox.Text;
        if (string.IsNullOrEmpty(query)) return;
        var matches = new List<string>();
        foreach (var column in columns)
        foreach (var file in column.Applied?.Files ?? [])
        {
            if (file.PostText is null) continue;
            var lines = DiffEngine.SplitLines(file.PostText);
            for (var index = 0; index < lines.Count; index++)
                if (lines[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                    matches.Add($"{BlindDisplayLabel(column.Label)} · {Path.GetFileName(file.Path)} L{index + 1}:L{index + 1} — {lines[index].Trim()}");
        }
        MessageBox.Show(this, matches.Count == 0 ? "No matches." : string.Join(Environment.NewLine, matches),
            $"Search: {query}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string StatusFor(AppliedFile? file)
    {
        if (file is null) return "unchanged";
        if (file.Status.Kind == ApplyStatusKind.Failed) return "failed";
        if (file.Status.Kind == ApplyStatusKind.Binary) return "binary";
        return file.Change.Kind switch
        {
            FileChangeKind.Added => "added",
            FileChangeKind.Deleted => "deleted",
            _ when file.Status.Kind == ApplyStatusKind.Fuzz => file.Status.Label,
            _ => "changed"
        };
    }

    private void ShowRawPatch_Click(object sender, RoutedEventArgs e)
    {
        if (BlindModeToggle.IsChecked == true)
        {
            MessageBox.Show(this, "Raw patch files reveal the patch identity. Turn off blind review to inspect them.",
                "Blind review", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var column = columns.FirstOrDefault(candidate => candidate.Patch.Files.Any(file => file.Path == selectedPath))
            ?? columns.FirstOrDefault();
        if (column is null) return;
        var viewer = new RawPatchWindow($"{column.Label} — {column.SourceName}", column.PatchText) { Owner = this };
        viewer.ShowDialog();
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
        var dialog = new FindingDialog(selection.Reference, categories) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!categories.Contains(dialog.Category, StringComparer.Ordinal))
            categories.Add(dialog.Category);
        findings.Add(new SessionFinding(Guid.NewGuid(), selection.Column.Label, selection.File.Path,
            new LineSpan(selection.Start, selection.End), selection.Reference, dialog.TitleText,
            dialog.Description, dialog.Category, Enum.Parse<FindingSeverity>(dialog.Severity, ignoreCase: true),
            Enum.Parse<RequestRelevance>(dialog.RequestAsks, ignoreCase: true), dialog.NotFlagged,
            dialog.Note, [], DateTimeOffset.UtcNow));
        findingLabels.Add($"{BlindDisplayLabel(selection.Column.Label)} · {dialog.Severity} · {dialog.TitleText} — {selection.Reference}");
        StatusText.Text = "Finding recorded for this window.";
    }

    private void PinSelection_Click(object sender, RoutedEventArgs e)
    {
        if (selection is null) return;
        pins.Add(new SessionPin(Guid.NewGuid(), selection.Column.Label, selection.File.Path,
            new LineSpan(selection.Start, selection.End), selection.Reference, selection.Snippet));
        StatusText.Text = "Selection pinned.";
    }

    private void FindingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = FindingsList.SelectedIndex;
        if (index < 0 || index >= findings.Count) return;
        var finding = findings[index];
        selectedPath = finding.Path;
        RenderColumns();
        if (!codeEditors.TryGetValue(finding.Label, out var editor) || editor.Tag is not EditorContext context)
            return;
        var line = context.DisplayLines.ToList().FindIndex(displayLine =>
            displayLine.PostLine == finding.Lines.Lower || displayLine.BaseLine == finding.Lines.Lower);
        if (line >= 0) editor.ScrollToLine(line);
    }

    private void CorrectAnchor_Click(object sender, RoutedEventArgs e)
    {
        if (recentSelections.Count < 2)
        {
            MessageBox.Show(this, "Select a source region and its matching region in another column first.",
                "Manual anchor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var source = recentSelections[0];
        var target = recentSelections[1];
        if (source.File.Path != target.File.Path || source.Column.Label == target.Column.Label)
        {
            MessageBox.Show(this, "Choose regions from the same file in two different columns.",
                "Manual anchor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        manualAnchors.RemoveAll(anchor => anchor.SourceLabel == source.Column.Label &&
            anchor.TargetLabel == target.Column.Label && anchor.Path == source.File.Path &&
            anchor.SourceLines.Lower == source.Start && anchor.SourceLines.Upper == source.End);
        manualAnchors.Add(new ManualAnchor(Guid.NewGuid(), source.Column.Label, source.File.Path,
            new LineSpan(source.Start, source.End), target.Column.Label, new LineSpan(target.Start, target.End)));
        StatusText.Text = $"Manual anchor saved: {source.Column.Label} → {target.Column.Label}.";
    }

    private void PinsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PinsList.SelectedItem is not SessionPin pin) return;
        selectedPath = pin.Path;
        RenderColumns();
        if (!codeEditors.TryGetValue(pin.Label, out var editor) || editor.Tag is not EditorContext context)
            return;
        var row = context.DisplayLines.ToList().FindIndex(line => line.PostLine == pin.Lines.Lower ||
            line.BaseLine == pin.Lines.Lower);
        if (row >= 0) editor.ScrollToLine(row);
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
            pins.Clear();
            manualAnchors.Clear();
            loadedSession = contents.Session;
            blindSeed = contents.Session.Blind.Seed;
            BlindModeToggle.IsChecked = contents.Session.Blind.IsOn;
            RequestBox.Text = contents.Session.Request;
            RationaleBox.Text = contents.Session.Ranking.Rationale;
            categories.Clear();
            categories.AddRange(contents.Session.Categories);
            foreach (var column in contents.Session.Columns)
            {
                var text = contents.PatchTexts[column.Label];
                columns.Add(new LoadedColumn(column.Label, column.SourceName, text, UnifiedDiffParser.Parse(text))
                {
                    IsHidden = column.IsHidden,
                    IsRead = column.IsRead
                });
            }
            findings.AddRange(contents.Session.Findings);
            foreach (var pin in contents.Session.Pins) pins.Add(pin);
            manualAnchors.AddRange(contents.Session.ManualAnchors);
            foreach (var finding in findings)
                findingLabels.Add($"{BlindDisplayLabel(finding.Label)} · {finding.Severity} · {finding.Title} — {finding.Reference}");
            basePath = contents.Session.Base.Path;
            baseRevision = contents.Session.Base.Commit ?? contents.Session.Base.Revision;
            baseCommit = contents.Session.Base.Commit;
            baseKind = contents.Session.Base.Kind;
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
        var baseInfo = new SessionBase(baseKind, Path: basePath,
            Revision: baseRevision, Commit: baseCommit, Label: old?.Base.Label);
        return new ReviewSession(ReviewSession.CurrentVersion, old?.Name ?? "Patch review", RequestBox.Text,
            baseInfo,
            columns.Select(column => new SessionColumn(column.Label, column.SourceName, "",
                column.IsHidden, column.IsRead)).ToArray(),
            golden, categories.ToArray(), findings.ToArray(),
            new SessionRanking(rankPickers.ToDictionary(pair => pair.Key,
                pair => int.TryParse(pair.Value.SelectedItem as string, out var rank) ? rank : (int?)null)
                .Where(pair => pair.Value.HasValue)
                .ToDictionary(pair => pair.Key, pair => pair.Value!.Value, StringComparer.Ordinal),
                RationaleBox.Text),
            pins.ToArray(), manualAnchors.ToArray(),
            new BlindMode(BlindModeToggle.IsChecked == true, blindSeed), old?.Created ?? DateTimeOffset.UtcNow);
    }

    private sealed class LoadedColumn(string label, string sourceName, string patchText, Patch patch)
    {
        public string Label { get; } = label;
        public string SourceName { get; } = sourceName;
        public string PatchText { get; } = patchText;
        public Patch Patch { get; } = patch;
        public AppliedPatch? Applied { get; set; }
        public bool IsHidden { get; set; }
        public bool IsRead { get; set; }
    }

    private sealed record FileTreeEntry(string Path, string Statuses)
    {
        public string DisplayText => $"{Path}  [{Statuses}]";
    }

    private sealed record ComparisonEntry(
        LoadedColumn Column, AppliedFile? File, AlignmentResult Result, string DisplayText);

    private sealed record Selection(
        LoadedColumn Column, AppliedFile File, int Start, int End, string Reference, string Snippet);
    private sealed record EditorContext(LoadedColumn Column, AppliedFile? File,
        IReadOnlyList<DisplayLine> DisplayLines, bool IsGoldenDiff);
}

internal sealed class TextPromptDialog : Window
{
    private readonly TextBox input = new() { MinWidth = 320, Margin = new Thickness(0, 8, 0, 12) };

    private TextPromptDialog(string title, string prompt)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(24, 38, 59));
        Foreground = new SolidColorBrush(Color.FromRgb(231, 237, 245));
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt });
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var accept = new Button { Content = "Use revision", IsDefault = true };
        accept.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => input.Focus();
    }

    public static string? Show(Window owner, string title, string prompt)
    {
        var dialog = new TextPromptDialog(title, prompt) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.input.Text : null;
    }
}

internal sealed class RawPatchWindow : Window
{
    public RawPatchWindow(string title, string patchText)
    {
        Title = $"Raw patch — {title}";
        Width = 1000;
        Height = 750;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(16, 26, 43));
        Foreground = new SolidColorBrush(Color.FromRgb(231, 237, 245));
        Content = new TextBox
        {
            Text = patchText,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Background = new SolidColorBrush(Color.FromRgb(15, 24, 39)),
            Foreground = new SolidColorBrush(Color.FromRgb(221, 231, 242)),
            Padding = new Thickness(8)
        };
    }
}
