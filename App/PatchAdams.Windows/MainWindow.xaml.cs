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
    private readonly ObservableCollection<FileTreeEntry> visibleFiles = [];
    private readonly ObservableCollection<string> findingLabels = [];
    private readonly ObservableCollection<SessionPin> pins = [];
    private readonly ObservableCollection<ComparisonEntry> comparisons = [];
    private readonly List<SessionFinding> findings = [];
    private readonly List<string> categories = [.. ReviewSession.DefaultCategories];
    private readonly List<ManualAnchor> manualAnchors = [];
    private readonly List<Selection> recentSelections = [];
    private readonly List<LoadedColumn> columns = [];
    private readonly Dictionary<string, CodeEditor> codeEditors = new(StringComparer.Ordinal);
    private readonly Dictionary<LoadedColumn, ColumnDefinition> columnDefinitions = [];
    private readonly Dictionary<string, ComboBox> rankPickers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> baseTextForFiles = new(StringComparer.Ordinal);
    private bool synchronizingScroll;
    private double fontSize = 13;
    private string fontFamily = "Consolas";
    private bool askBlindModeOnNewSession = true;
    private bool filePanelCollapsed;
    private bool reviewPanelCollapsed;
    private bool promptedForBlindMode;
    private string? basePath;
    private string? baseRevision;
    private string? baseCommit;
    private string baseKind = "synthetic";
    private string selectedPath = "";
    private Selection? selection;
    private string? golden;
    private ReviewSession? loadedSession;
    private ulong blindSeed = unchecked((ulong)Random.Shared.NextInt64());
    private int reloadGeneration;
    private int patchLoadGeneration;

    public MainWindow()
    {
        InitializeComponent();
        var preferences = AppPreferencesStore.Load();
        fontFamily = preferences.FontFamily;
        fontSize = Math.Clamp(preferences.FontSize, 9, 24);
        askBlindModeOnNewSession = preferences.AskBlindModeOnNewSession;
        filePanelCollapsed = preferences.FilePanelCollapsed;
        reviewPanelCollapsed = preferences.ReviewPanelCollapsed;
        ApplySidePanelLayout();
        FileTree.ItemsSource = visibleFiles;
        FindingsList.ItemsSource = findingLabels;
        PinsList.ItemsSource = pins;
        ComparisonList.ItemsSource = comparisons;
        RationaleBox.Text = "";
        GoldenPicker.Items.Add("None");
        GoldenPicker.SelectedIndex = 0;
    }

    private async void ChooseBase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the patch base directory" };
        if (dialog.ShowDialog(this) != true) return;
        basePath = dialog.FolderName;
        baseKind = "directory";
        baseRevision = null;
        baseCommit = null;
        StatusText.Text = $"Base: {basePath}";
        await ReloadPatchesAsync();
    }

    private async void ChooseArchive_Click(object sender, RoutedEventArgs e)
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
        await ReloadPatchesAsync();
    }

    private async void ChooseGitRevision_Click(object sender, RoutedEventArgs e)
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
        await ReloadPatchesAsync();
    }

    private async void RebuildSyntheticBase_Click(object sender, RoutedEventArgs e)
    {
        if (columns.Count == 0) return;
        basePath = null;
        baseRevision = null;
        baseCommit = null;
        baseKind = "synthetic";
        await ReloadPatchesAsync();
    }

    private async void AddPatches_Click(object sender, RoutedEventArgs e)
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

        await LoadPatchFilesAsync(dialog.FileNames);
    }

    private async Task LoadPatchFilesAsync(IReadOnlyList<string> paths)
    {
        var generation = ++patchLoadGeneration;
        try
        {
            var labels = new[] { "A", "B", "C", "D" };
            var loaded = await Task.Run(() => paths.Select((path, index) =>
            {
                var text = File.ReadAllText(path);
                return new LoadedColumn(labels[index], Path.GetFileName(path), text, UnifiedDiffParser.Parse(text));
            }).ToArray());
            if (generation != patchLoadGeneration) return;
            columns.Clear();
            findings.Clear();
            categories.Clear();
            categories.AddRange(ReviewSession.DefaultCategories);
            manualAnchors.Clear();
            pins.Clear();
            findingLabels.Clear();
            loadedSession = null;
            columns.AddRange(loaded);
            golden = null;
            await ReloadPatchesAsync();
            StatusText.Text = $"{columns.Count} patches loaded.";
            AskBlindModeOnce();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not load patches", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MainWindow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void MainWindow_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var patches = paths.Where(path =>
            Path.GetExtension(path).Equals(".patch", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(path).Equals(".diff", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (patches.Length is < 2 or > 4)
        {
            MessageBox.Show(this, "Drop between two and four .patch or .diff files.",
                "PatchAdams#", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await LoadPatchFilesAsync(patches);
    }

    private async void PastePatch_Click(object sender, RoutedEventArgs e)
    {
        if (columns.Count is < 2 or >= 4)
        {
            MessageBox.Show(this, "Load two or three patch files first, then paste one more patch.",
                "Paste patch", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Clipboard.ContainsText())
        {
            MessageBox.Show(this, "The clipboard does not contain patch text.", "Paste patch",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var text = Clipboard.GetText();
            var patch = await Task.Run(() => UnifiedDiffParser.Parse(text));
            var label = new[] { "A", "B", "C", "D" }.First(candidate =>
                columns.All(column => column.Label != candidate));
            columns.Add(new LoadedColumn(label, "pasted patch", text, patch));
            loadedSession = null;
            await ReloadPatchesAsync();
            StatusText.Text = $"Patch {BlindDisplayLabel(label)} pasted.";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not parse pasted patch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ReloadPatchesAsync()
    {
        if (columns.Count == 0) return;
        var generation = ++reloadGeneration;
        var inputs = columns.ToArray();
        var requestedPath = basePath;
        var requestedRevision = baseRevision;
        var requestedKind = baseKind;
        StatusText.Text = "Loading base and applying patches…";
        try
        {
            var result = await Task.Run(() =>
            {
                string? resolvedPath = requestedPath;
                string? resolvedRevision = requestedRevision;
                string? resolvedCommit = null;
                var resolvedKind = requestedKind;
                var usedFallback = false;
                IFileProvider provider;
                if (requestedPath is null)
                {
                    provider = new InMemoryFileProvider(SyntheticBase.Merge(inputs.Select(column => column.Patch)),
                        "synthetic base from the patches");
                    resolvedKind = "synthetic";
                }
                else
                {
                    try
                    {
                        provider = BaseLoader.Open(requestedPath, requestedRevision);
                        if (provider is GitRevisionFileProvider git)
                        {
                            resolvedCommit = git.Commit;
                            resolvedRevision = git.RequestedRevision;
                        }
                    }
                    catch (Exception) when (!Directory.Exists(requestedPath) && !File.Exists(requestedPath))
                    {
                        provider = new InMemoryFileProvider(SyntheticBase.Merge(inputs.Select(column => column.Patch)),
                            "synthetic base from the patches");
                        resolvedPath = null;
                        resolvedRevision = null;
                        resolvedCommit = null;
                        resolvedKind = "synthetic";
                        usedFallback = true;
                    }
                }
                var applied = inputs.Select(column => ApplyEngine.Apply(column.Patch, provider)).ToArray();
                var baseTexts = inputs.SelectMany(column => column.Patch.Files)
                    .SelectMany(change => new[] { change.Path, change.OldPath })
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Select(path => path!)
                    .Distinct(StringComparer.Ordinal)
                    .ToDictionary(path => path, path =>
                    {
                        try
                        {
                            var bytes = provider.ReadFile(path);
                            return bytes is null ? null : BaseText.Decode(bytes);
                        }
                        catch
                        {
                            return null;
                        }
                    }, StringComparer.Ordinal);
                return (Applied: applied, Path: resolvedPath, Revision: resolvedRevision,
                    Commit: resolvedCommit, Kind: resolvedKind, UsedFallback: usedFallback,
                    BaseTexts: baseTexts);
            });
            if (generation != reloadGeneration || !inputs.SequenceEqual(columns))
                return;
            basePath = result.Path;
            baseRevision = result.Revision;
            baseCommit = result.Commit;
            baseKind = result.Kind;
            for (var index = 0; index < inputs.Length; index++)
            {
                inputs[index].Applied = result.Applied[index];
                inputs[index].UnchangedFiles.Clear();
            }
            baseTextForFiles.Clear();
            foreach (var (path, text) in result.BaseTexts)
                baseTextForFiles[path] = text;
            files.Clear();
            foreach (var path in columns.SelectMany(column => column.Applied!.Files)
                         .SelectMany(file => file.Change.Kind == FileChangeKind.Renamed && file.Change.OldPath is not null
                             ? new[] { file.Path, file.Change.OldPath! }
                             : new[] { file.Path })
                         .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var statuses = columns.Select(column =>
                {
                    return $"{BlindDisplayLabel(column.Label)}:{StatusForColumn(column, path)}";
                });
                files.Add(new FileTreeEntry(path, string.Join("  ", statuses),
                    FileChange.IsTestPath(path)));
            }
            RefreshFileTree();

            if (result.UsedFallback)
                StatusText.Text = "The saved base is unavailable; rebuilt a synthetic base from the patches.";
            PopulateGoldenPicker();
            ReportText.Text = string.Join(Environment.NewLine, columns.Select(column =>
                $"{(BlindModeToggle.IsChecked == true ? $"Patch {BlindDisplayLabel(column.Label)}" : $"{column.Label} — {column.SourceName}")}: {column.Applied!.Report.Clean} clean, " +
                $"{column.Applied.Report.Fuzz} fuzz, {column.Applied.Report.Failed} failed, " +
                $"{column.Applied.Report.Binary} binary"));
            RenderColumns();
            if (visibleFiles.Count > 0)
            {
                selectedPath = visibleFiles[0].Path;
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

    private void TestsOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        RefreshFileTree();
    }

    private void RefreshFileTree()
    {
        visibleFiles.Clear();
        foreach (var file in files.Where(file => TestsOnlyToggle.IsChecked != true || file.IsTest))
            visibleFiles.Add(file);
        if (!visibleFiles.Any(file => file.Path == selectedPath) && visibleFiles.Count > 0)
        {
            selectedPath = visibleFiles[0].Path;
            RenderColumns();
        }
    }

    private AppliedFile? ColumnFile(LoadedColumn column, string path)
    {
        var changed = column.Applied?.FileAt(path);
        if (changed is not null) return changed;
        var renamed = column.Applied?.Files.FirstOrDefault(file =>
            file.Change.Kind == FileChangeKind.Renamed && file.Change.OldPath == path);
        if (renamed?.BaseText is not null)
            return AppliedFile.Deleted(path, renamed.BaseText);
        if (column.UnchangedFiles.TryGetValue(path, out var cached))
            return cached;
        if (!baseTextForFiles.TryGetValue(path, out var text) || text is null)
            return null;
        var unchanged = AppliedFile.Unchanged(path, text);
        column.UnchangedFiles[path] = unchanged;
        return unchanged;
    }

    private void RenderColumns()
    {
        CaptureColumnWidths();
        ColumnsGrid.Children.Clear();
        ColumnsGrid.ColumnDefinitions.Clear();
        columnDefinitions.Clear();
        codeEditors.Clear();
        selection = null;
        comparisons.Clear();
        ReferenceText.Text = "Select lines in a column to generate a reference.";
        if (columns.Count == 0)
        {
            BalanceWidthsButton.IsEnabled = false;
            return;
        }
        var leftTabs = columns.Where(column => column.IsHidden && column.CollapsedTo == CollapseSide.Left).ToArray();
        var rightTabs = columns.Where(column => column.IsHidden && column.CollapsedTo == CollapseSide.Right).ToArray();
        var visibleColumns = columns.Where(column => !column.IsHidden).ToArray();
        BalanceWidthsButton.IsEnabled = visibleColumns.Length > 1;
        var gridColumn = 0;
        if (leftTabs.Length > 0)
            AddCollapsedStrip(leftTabs, CollapseSide.Left, gridColumn++);
        for (var index = 0; index < visibleColumns.Length; index++)
        {
            if (index > 0)
                AddSplitter(gridColumn++);
            var column = visibleColumns[index];
            var definition = new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1, column.WidthWeight), GridUnitType.Star),
                MinWidth = 90
            };
            ColumnsGrid.ColumnDefinitions.Add(definition);
            columnDefinitions[column] = definition;
            var panel = new DockPanel { Margin = new Thickness(4) };
            var header = new DockPanel { Margin = new Thickness(2, 2, 2, 6) };
            var collapseLeft = new Button
            {
                Content = "◀", FontSize = 10, Width = 28, Padding = new Thickness(0),
                Margin = new Thickness(1, 0, 1, 0), ToolTip = "Collapse this patch to the left",
                IsEnabled = visibleColumns.Length > 1
            };
            collapseLeft.Click += (_, _) => CollapseColumn(column, CollapseSide.Left);
            DockPanel.SetDock(collapseLeft, Dock.Left);
            header.Children.Add(collapseLeft);
            var collapseRight = new Button
            {
                Content = "▶", FontSize = 10, Width = 28, Padding = new Thickness(0),
                Margin = new Thickness(1, 0, 1, 0), ToolTip = "Collapse this patch to the right",
                IsEnabled = visibleColumns.Length > 1
            };
            collapseRight.Click += (_, _) => CollapseColumn(column, CollapseSide.Right);
            DockPanel.SetDock(collapseRight, Dock.Right);
            header.Children.Add(collapseRight);
            var title = BlindModeToggle.IsChecked == true
                ? $"Patch {BlindDisplayLabel(column.Label)}{(column.IsRead ? " ✓" : "")}"
                : $"{column.Label} — {column.SourceName}{(column.IsRead ? " ✓" : "")}";
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = title
            });
            header.ContextMenu = CreateColumnMenu(column);
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);

            var appliedFile = ColumnFile(column, selectedPath);
            var isGoldenDiff = golden is not null && column.Label != golden;
            var canRenderFile = appliedFile is not null &&
                appliedFile.Status.Kind is not ApplyStatusKind.Failed and not ApplyStatusKind.Binary &&
                (appliedFile.PostText is not null || appliedFile.Change.Kind == FileChangeKind.Deleted);
            var isLargeFile = appliedFile is not null &&
                Math.Max(appliedFile.LineMap.PostLineCount, appliedFile.LineMap.BaseLineCount) > 50_000;
            IReadOnlyList<DisplayLine> displayLines = canRenderFile && !isGoldenDiff
                ? isLargeFile
                    ? DisplayLines.CreateHunksOnly(appliedFile!,
                        includeRemoved: ShowRemovedToggle.IsChecked == true)
                    : DisplayLines.Create(appliedFile!, includeRemoved: ShowRemovedToggle.IsChecked == true)
                : [];
            var renderedText = !canRenderFile
                ? RenderColumnText(column, null)
                : isGoldenDiff
                    ? RenderColumnText(column, appliedFile?.PostText, isLargeFile)
                    : RenderDisplayLines(displayLines);
            if (isLargeFile)
                StatusText.Text = "Large file reduced to patch hunks with surrounding context.";
            var editor = new CodeEditor
            {
                FontSize = fontSize,
                FontFamily = new FontFamily(fontFamily),
                Tag = new EditorContext(column, appliedFile, displayLines, isGoldenDiff),
            };
            editor.SetCode(DiffEngine.SplitLines(renderedText), displayLines,
                Path.GetExtension(selectedPath).ToLowerInvariant(), !isGoldenDiff,
                findings.Where(finding => finding.Label == column.Label && finding.Path == selectedPath).ToArray());
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
            Grid.SetColumn(panel, gridColumn++);
            ColumnsGrid.Children.Add(panel);
        }
        if (rightTabs.Length > 0)
            AddCollapsedStrip(rightTabs, CollapseSide.Right, gridColumn++);
    }

    private void CaptureColumnWidths()
    {
        if (columnDefinitions.Count < 2) return;
        foreach (var (column, definition) in columnDefinitions)
            if (double.IsFinite(definition.ActualWidth) && definition.ActualWidth > 0)
                column.WidthWeight = definition.ActualWidth;
    }

    private void BalanceWidths_Click(object sender, RoutedEventArgs e)
    {
        var expanded = columns.Where(column => !column.IsHidden).ToArray();
        if (expanded.Length < 2) return;

        ColumnsGrid.UpdateLayout();
        var stripCount = (columns.Any(column => column.IsHidden && column.CollapsedTo == CollapseSide.Left) ? 1 : 0) +
                         (columns.Any(column => column.IsHidden && column.CollapsedTo == CollapseSide.Right) ? 1 : 0);
        var available = ColumnsGrid.ActualWidth - stripCount * 36 - (expanded.Length - 1) * 6;
        if (!double.IsFinite(available) || available <= 0)
        {
            StatusText.Text = "Not enough space to balance the expanded patches.";
            return;
        }

        var displayedRows = expanded.Select(column =>
            (IReadOnlyList<string>)codeEditors[column.Label].PlainLines).ToArray();
        var widths = ColumnWidthBalancer.Allocate(available, fontSize, displayedRows);
        var minimum = Math.Min(90, available / expanded.Length);
        for (var index = 0; index < expanded.Length; index++)
        {
            var column = expanded[index];
            column.WidthWeight = widths[index];
            var definition = columnDefinitions[column];
            definition.MinWidth = minimum;
            definition.Width = new GridLength(widths[index], GridUnitType.Star);
        }
        ColumnsGrid.UpdateLayout();
        StatusText.Text = $"Balanced {expanded.Length} patch widths for the current file.";
    }

    private void AddSplitter(int gridColumn)
    {
        ColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        var splitter = new GridSplitter
        {
            Width = 6,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Columns,
            ShowsPreview = true,
            Cursor = Cursors.SizeWE,
            Background = new SolidColorBrush(Color.FromRgb(129, 138, 152))
        };
        Grid.SetColumn(splitter, gridColumn);
        ColumnsGrid.Children.Add(splitter);
    }

    private void AddCollapsedStrip(IReadOnlyList<LoadedColumn> tabs, CollapseSide side, int gridColumn)
    {
        ColumnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var column in tabs)
        {
            var name = BlindModeToggle.IsChecked == true
                ? $"Patch {BlindDisplayLabel(column.Label)}"
                : $"{column.Label} — {column.SourceName}";
            var label = new TextBlock
            {
                Text = name,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Width = 110,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                LayoutTransform = new RotateTransform(side == CollapseSide.Left ? -90 : 90)
            };
            var tab = new Button
            {
                Content = label,
                Height = 124,
                Margin = new Thickness(1, 2, 1, 2),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = $"Expand {name}"
            };
            tab.Click += (_, _) => RestoreColumn(column);
            tab.ContextMenu = new ContextMenu();
            tab.ContextMenu.Items.Add(CreateMenuItem("Expand column", () => RestoreColumn(column)));
            var otherSide = side == CollapseSide.Left ? CollapseSide.Right : CollapseSide.Left;
            tab.ContextMenu.Items.Add(CreateMenuItem($"Move tab {otherSide.ToString().ToLowerInvariant()}", () =>
            {
                column.CollapsedTo = otherSide;
                RenderColumns();
            }));
            stack.Children.Add(tab);
        }
        var strip = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack
        };
        Grid.SetColumn(strip, gridColumn);
        ColumnsGrid.Children.Add(strip);
    }

    private void CollapseColumn(LoadedColumn column, CollapseSide side)
    {
        if (!column.IsHidden && columns.Count(candidate => !candidate.IsHidden) <= 1)
            return;
        column.IsHidden = true;
        column.CollapsedTo = side;
        RenderColumns();
    }

    private void RestoreColumn(LoadedColumn column)
    {
        column.IsHidden = false;
        RenderColumns();
    }

    private ContextMenu CreateColumnMenu(LoadedColumn column)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Move left", () => MoveColumn(column, -1)));
        menu.Items.Add(CreateMenuItem("Move right", () => MoveColumn(column, 1)));
        menu.Items.Add(CreateMenuItem("Collapse to left", () => CollapseColumn(column, CollapseSide.Left)));
        menu.Items.Add(CreateMenuItem("Collapse to right", () => CollapseColumn(column, CollapseSide.Right)));
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
            Background = new SolidColorBrush(Color.FromRgb(47, 52, 62)),
            Foreground = new SolidColorBrush(Color.FromRgb(220, 224, 229))
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
        return string.Join('\n', lines.Select(line =>
            $"{(line.BaseLine?.ToString() ?? "").PadLeft(baseWidth)} | " +
            $"{(line.PostLine?.ToString() ?? "").PadLeft(postWidth)} | {line.Text}"));
    }

    private string RenderColumnText(LoadedColumn column, string? text, bool compactLargeFiles = false)
    {
        if (text is null)
        {
            var file = column.Applied?.FileAt(selectedPath);
            if (golden is not null && column.Label != golden &&
                file?.Change.Kind == FileChangeKind.Deleted)
                return RenderGoldenDiff(column, "", compactLargeFiles);
            return file?.Status.Kind == ApplyStatusKind.Failed
                ? $"[Failed to apply]\n{file.Status.Message}\n\nRaw patch hunks remain available in the source file."
                : file?.Status.Kind == ApplyStatusKind.Binary ? "[Binary file change]" : "[File absent in this patch]";
        }
        if (golden is null || column.Label == golden)
            return text;
        return RenderGoldenDiff(column, text, compactLargeFiles);
    }

    private string RenderGoldenDiff(LoadedColumn column, string text, bool compactLargeFiles)
    {
        var goldenText = ColumnFile(columns.FirstOrDefault(item => item.Label == golden)!, selectedPath)?.PostText ?? "";
        var diff = DiffEngine.Lines(goldenText, text);
        var goldenLines = DiffEngine.SplitLines(goldenText);
        var result = new List<string>();
        foreach (var edit in diff.Edits)
        {
            if (edit.Kind == EditKind.Equal)
            {
                if (compactLargeFiles && edit.ALength > 6)
                {
                    result.AddRange(goldenLines.Skip(edit.AStart).Take(3).Select(line => "  " + line));
                    result.Add($"… {edit.ALength - 6} unchanged lines outside diff hunks hidden …");
                    result.AddRange(goldenLines.Skip(edit.AStart + edit.ALength - 3).Take(3).Select(line => "  " + line));
                }
                else
                {
                    result.AddRange(goldenLines.Skip(edit.AStart).Take(edit.ALength).Select(line => "  " + line));
                }
            }
            else if (edit.Kind == EditKind.Delete)
                result.AddRange(goldenLines.Skip(edit.AStart).Take(edit.ALength).Select(line => "- " + line));
            else
                result.AddRange(DiffEngine.SplitLines(text).Skip(edit.BStart).Take(edit.BLength).Select(line => "+ " + line));
        }
        return string.Join('\n', result);
    }

    private void CodeSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CodeEditor editor || editor.Tag is not EditorContext context)
            return;
        var selectedRows = editor.SelectedRows();
        if (selectedRows is null) return;
        if (context.IsGoldenDiff || context.File is null ||
            context.File.Status.Kind is ApplyStatusKind.Failed or ApplyStatusKind.Binary)
            return;
        var first = selectedRows.Value.Start + 1;
        var last = selectedRows.Value.End + 1;
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
        catch (ArgumentException)
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
            var target = ColumnFile(column, selectedPath);
            var manual = manualAnchors.LastOrDefault(anchor =>
                anchor.SourceLabel == sourceColumn.Label && anchor.TargetLabel == column.Label &&
                anchor.Path == selectedPath && anchor.SourceLines.Lower == startLine &&
                anchor.SourceLines.Upper == endLine);
            var result = AlignmentEngine.Locate(region, sourceFile, target, manual);
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
        editor.ScrollToRow(row);
        if (entry.Result.StartLine is int start && entry.Result.EndLine is int end)
        {
            var startRow = rows.FindIndex(line => line.PostLine == start);
            var endRow = rows.FindIndex(line => line.PostLine == end);
            if (startRow >= 0 && endRow >= startRow)
            {
                editor.SelectRows(startRow, endRow);
            }
        }
    }

    private void CodeScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (synchronizingScroll || ScrollLockToggle.IsChecked != true ||
            sender is not ScrollViewer source || source.Tag is not EditorContext sourceContext ||
            sourceContext.DisplayLines.Count == 0)
            return;

        var firstVisible = source.TemplatedParent is CodeEditor sourceEditor
            ? sourceEditor.FirstVisibleRow()
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
                    editor.ScrollToRow(sourceRow);
                    continue;
                }
                var targetRow = targetContext.DisplayLines.ToList()
                    .FindIndex(line => line.BaseLine == baseLine);
                if (targetRow >= 0)
                    editor.ScrollToRow(targetRow);
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
        PopulateGoldenPicker();
        RenderColumns();
        RenderRanking();
        for (var index = 0; index < findingLabels.Count && index < findings.Count; index++)
        {
            var finding = findings[index];
            findingLabels[index] =
                $"{BlindDisplayLabel(finding.Label)} · {finding.Severity} · {finding.Title} — {finding.Reference}";
        }
        ReportText.Text = string.Join(Environment.NewLine, columns.Select(column =>
            $"{(BlindModeToggle.IsChecked == true ? $"Patch {BlindDisplayLabel(column.Label)}" : $"{column.Label} — {column.SourceName}")}: {column.Applied?.Report.Clean ?? 0} clean, " +
            $"{column.Applied?.Report.Fuzz ?? 0} fuzz, {column.Applied?.Report.Failed ?? 0} failed, " +
            $"{column.Applied?.Report.Binary ?? 0} binary"));
    }

    private void PopulateGoldenPicker()
    {
        var selectedLabel = golden;
        GoldenPicker.Items.Clear();
        GoldenPicker.Items.Add("None");
        foreach (var column in columns)
            GoldenPicker.Items.Add(BlindModeToggle.IsChecked == true
                ? $"Patch {BlindDisplayLabel(column.Label)}"
                : $"{column.Label} — {column.SourceName}");
        GoldenPicker.SelectedIndex = columns.FindIndex(column => column.Label == selectedLabel) + 1;
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
        SavePreferences();
        RenderColumns();
    }

    private void LargerFont_Click(object sender, RoutedEventArgs e)
    {
        fontSize = Math.Min(24, fontSize + 1);
        SavePreferences();
        RenderColumns();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PreferencesDialog(fontFamily, fontSize, askBlindModeOnNewSession) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        fontFamily = dialog.SelectedFontFamily;
        fontSize = dialog.SelectedFontSize;
        askBlindModeOnNewSession = dialog.AskBlindMode;
        SavePreferences();
        RenderColumns();
    }

    private void CollapseFilePanel_Click(object sender, RoutedEventArgs e)
    {
        filePanelCollapsed = true;
        ApplySidePanelLayout();
        SavePreferences();
    }

    private void ExpandFilePanel_Click(object sender, RoutedEventArgs e)
    {
        filePanelCollapsed = false;
        ApplySidePanelLayout();
        SavePreferences();
    }

    private void CollapseReviewPanel_Click(object sender, RoutedEventArgs e)
    {
        reviewPanelCollapsed = true;
        ApplySidePanelLayout();
        SavePreferences();
    }

    private void ExpandReviewPanel_Click(object sender, RoutedEventArgs e)
    {
        reviewPanelCollapsed = false;
        ApplySidePanelLayout();
        SavePreferences();
    }

    private void ApplySidePanelLayout()
    {
        FilePanelColumn.Width = new GridLength(filePanelCollapsed ? 36 : 230);
        FilePanelContents.Visibility = filePanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
        FilePanelTab.Visibility = filePanelCollapsed ? Visibility.Visible : Visibility.Collapsed;
        ReviewPanelColumn.Width = new GridLength(reviewPanelCollapsed ? 36 : 270);
        ReviewPanelContents.Visibility = reviewPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ReviewPanelTab.Visibility = reviewPanelCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SavePreferences()
    {
        try
        {
            AppPreferencesStore.Save(new AppPreferences(fontFamily, fontSize, askBlindModeOnNewSession,
                filePanelCollapsed, reviewPanelCollapsed));
        }
        catch (Exception error)
        {
            StatusText.Text = $"Could not save settings: {error.Message}";
        }
    }

    private void AskBlindModeOnce()
    {
        if (promptedForBlindMode) return;
        promptedForBlindMode = true;
        if (!askBlindModeOnNewSession) return;
        var answer = MessageBox.Show(this,
            "Review blind to reduce order bias? You can turn blind mode off at any time.",
            "New patch review", MessageBoxButton.YesNo, MessageBoxImage.Question);
        BlindModeToggle.IsChecked = answer == MessageBoxResult.Yes;
    }

    private void ShowRemoved_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) RenderColumns();
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
        if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.V)
        {
            PastePatch_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.E)
        {
            Clipboard.SetText(MarkdownExport.Render(BuildSession()));
            StatusText.Text = "Copied Markdown review.";
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
        var hits = new List<SearchHit>();
        foreach (var column in columns)
        foreach (var entry in files)
        {
            var file = ColumnFile(column, entry.Path);
            if (file is null) continue;
            if (file.PostText is null) continue;
            var lines = DiffEngine.SplitLines(file.PostText);
            for (var index = 0; index < lines.Count; index++)
                if (lines[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                    hits.Add(new SearchHit(column, file, index + 1,
                        $"{file.Path} · {BlindDisplayLabel(column.Label)} · {Path.GetFileName(file.Path)} L{index + 1}:L{index + 1} — {lines[index].Trim()}"));
        }
        if (hits.Count == 0)
        {
            MessageBox.Show(this, "No matches.", $"Search: {query}", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ordered = hits.OrderBy(hit => hit.File.Path, StringComparer.Ordinal)
            .ThenBy(hit => columns.IndexOf(hit.Column)).ThenBy(hit => hit.Line).ToArray();
        var results = new SearchResultsWindow(query, ordered.Select(hit => hit.DisplayText).ToArray()) { Owner = this };
        if (results.ShowDialog() != true || results.SelectedIndex < 0) return;
        var selected = ordered[results.SelectedIndex];
        selectedPath = selected.File.Path;
        RenderColumns();
        if (!codeEditors.TryGetValue(selected.Column.Label, out var editor) ||
            editor.Tag is not EditorContext context)
            return;
        var row = context.DisplayLines.ToList().FindIndex(line => line.PostLine == selected.Line);
        if (row >= 0) editor.ScrollToRow(row);
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

    private string StatusForColumn(LoadedColumn column, string path)
    {
        var file = column.Applied?.FileAt(path);
        if (file is null && column.Applied?.Files.Any(change =>
                change.Change.Kind == FileChangeKind.Renamed && change.Change.OldPath == path) == true)
            return "deleted";
        return StatusFor(file);
    }

    private void ShowRawPatch_Click(object sender, RoutedEventArgs e)
    {
        if (BlindModeToggle.IsChecked == true)
        {
            MessageBox.Show(this, "Raw patch files reveal the patch identity. Turn off blind review to inspect them.",
                "Blind review", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var column = columns.FirstOrDefault(candidate => candidate.Patch.Files.Any(file =>
                file.Path == selectedPath || file.OldPath == selectedPath))
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
        if (line >= 0) editor.ScrollToRow(line);
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
        if (row >= 0) editor.ScrollToRow(row);
    }

    private async void OpenSession_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Open a .patchadam session folder" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var contents = await Task.Run(() => SessionPackage.Read(dialog.FolderName));
            var restoredColumns = await Task.Run(() => contents.Session.Columns.Select(column =>
            {
                var text = contents.PatchTexts[column.Label];
                return new LoadedColumn(column.Label, column.SourceName, text, UnifiedDiffParser.Parse(text))
                {
                    IsHidden = column.IsHidden,
                    IsRead = column.IsRead,
                    CollapsedTo = column.CollapsedTo,
                    WidthWeight = double.IsFinite(column.WidthWeight) && column.WidthWeight > 0
                        ? column.WidthWeight : 1
                };
            }).ToArray());
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
            columns.AddRange(restoredColumns);
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
            await ReloadPatchesAsync();
            StatusText.Text = $"Session loaded: {dialog.FolderName}";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Could not open session", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private ReviewSession BuildSession()
    {
        CaptureColumnWidths();
        var old = loadedSession;
        var baseInfo = new SessionBase(baseKind, Path: basePath,
            Revision: baseRevision, Commit: baseCommit, Label: old?.Base.Label);
        return new ReviewSession(ReviewSession.CurrentVersion, old?.Name ?? "Patch review", RequestBox.Text,
            baseInfo,
            columns.Select(column => new SessionColumn(column.Label, column.SourceName, "",
                column.IsHidden, column.IsRead, column.CollapsedTo, column.WidthWeight)).ToArray(),
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
        public Dictionary<string, AppliedFile> UnchangedFiles { get; } = new(StringComparer.Ordinal);
        public bool IsHidden { get; set; }
        public bool IsRead { get; set; }
        public CollapseSide CollapsedTo { get; set; } = CollapseSide.Right;
        public double WidthWeight { get; set; } = 1;
    }

    private sealed record FileTreeEntry(string Path, string Statuses, bool IsTest)
    {
        public string DisplayText => $"{Path}  [{Statuses}]";
    }

    private sealed record ComparisonEntry(
        LoadedColumn Column, AppliedFile? File, AlignmentResult Result, string DisplayText);
    private sealed record SearchHit(LoadedColumn Column, AppliedFile File, int Line, string DisplayText);

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
        Background = new SolidColorBrush(Color.FromRgb(47, 52, 62));
        Foreground = new SolidColorBrush(Color.FromRgb(220, 224, 229));
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

internal sealed class PreferencesDialog : Window
{
    private readonly TextBox fontFamilyBox = new() { MinWidth = 300, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox fontSizeBox = new() { MinWidth = 80, Margin = new Thickness(0, 4, 0, 10) };
    private readonly CheckBox askBlindBox = new() { Content = "Ask whether to use blind mode for a new review" };

    public string SelectedFontFamily => fontFamilyBox.Text.Trim();
    public double SelectedFontSize => double.Parse(fontSizeBox.Text, System.Globalization.CultureInfo.InvariantCulture);
    public bool AskBlindMode => askBlindBox.IsChecked == true;

    public PreferencesDialog(string fontFamily, double fontSize, bool askBlindMode)
    {
        Title = "PatchAdams# settings";
        Owner = Application.Current.MainWindow;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(47, 52, 62));
        Foreground = new SolidColorBrush(Color.FromRgb(220, 224, 229));
        fontFamilyBox.Text = fontFamily;
        fontSizeBox.Text = fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        askBlindBox.IsChecked = askBlindMode;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Monospace font family" });
        panel.Children.Add(fontFamilyBox);
        panel.Children.Add(new TextBlock { Text = "Font size (9–24 pt)" });
        panel.Children.Add(fontSizeBox);
        panel.Children.Add(askBlindBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var apply = new Button { Content = "Apply", IsDefault = true };
        apply.Click += Apply_Click;
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedFontFamily) ||
            !double.TryParse(fontSizeBox.Text, System.Globalization.CultureInfo.InvariantCulture, out var size) ||
            size is < 9 or > 24)
        {
            MessageBox.Show(this, "Enter a font family and a size from 9 to 24.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _ = new FontFamily(SelectedFontFamily);
        }
        catch (ArgumentException)
        {
            MessageBox.Show(this, "The font family is not valid.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
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
        Background = new SolidColorBrush(Color.FromRgb(40, 44, 51));
        Foreground = new SolidColorBrush(Color.FromRgb(220, 224, 229));
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
            Background = new SolidColorBrush(Color.FromRgb(40, 44, 51)),
            Foreground = new SolidColorBrush(Color.FromRgb(171, 178, 191)),
            Padding = new Thickness(8)
        };
    }
}

internal sealed class SearchResultsWindow : Window
{
    private readonly ListBox results = new()
    {
        SelectionMode = SelectionMode.Single,
        Margin = new Thickness(10)
    };

    public int SelectedIndex => results.SelectedIndex;

    public SearchResultsWindow(string query, IReadOnlyList<string> hits)
    {
        Title = $"Search results — {query}";
        Width = 850;
        Height = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(40, 44, 51));
        Foreground = new SolidColorBrush(Color.FromRgb(220, 224, 229));
        foreach (var hit in hits)
            results.Items.Add(hit);

        var panel = new DockPanel();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8)
        };
        buttons.Children.Add(new Button { Content = "Close", IsCancel = true });
        var open = new Button { Content = "Open location", IsDefault = true };
        open.Click += (_, _) =>
        {
            if (results.SelectedIndex >= 0)
                DialogResult = true;
        };
        buttons.Children.Add(open);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(results);
        Content = panel;
        results.MouseDoubleClick += (_, _) =>
        {
            if (results.SelectedIndex >= 0)
                DialogResult = true;
        };
    }
}
