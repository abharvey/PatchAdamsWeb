using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using PatchAdams.Core;

namespace PatchAdamsSharp;

public partial class PatchAssignmentDialog : Window
{
    private readonly List<ComboBox> pickers = [];

    public IReadOnlyList<string> AssignedLabels => pickers.Select(picker => (string)picker.SelectedItem).ToArray();

    public PatchAssignmentDialog(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        foreach (var path in paths)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            var picker = new ComboBox { ItemsSource = PatchLabelAssignment.Labels, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(picker, $"Candidate label for {Path.GetFileName(path)}");
            Grid.SetColumn(picker, 1);
            row.Children.Add(new Label
            {
                Content = new TextBlock { Text = Path.GetFileName(path), TextWrapping = TextWrapping.Wrap },
                ToolTip = path,
                Target = picker,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(picker);
            pickers.Add(picker);
            picker.SelectionChanged += (_, _) => UpdateValidation();
            AssignmentRows.Children.Add(row);
        }
        UpdateValidation();
    }

    private void UpdateValidation()
    {
        var selected = pickers.Select(picker => picker.SelectedItem as string).ToArray();
        LoadButton.IsEnabled = PatchLabelAssignment.IsValid(selected);
        ValidationText.Text = selected.Any(label => label is null)
            ? "Choose a label for every file."
            : LoadButton.IsEnabled ? "" : "Each patch must have a different label.";
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        UpdateValidation();
        if (LoadButton.IsEnabled) DialogResult = true;
    }
}
