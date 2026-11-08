using System.Windows;
using PatchAdams.Core;

namespace PatchAdamsSharp;

public partial class ComparisonFindingDialog : Window
{
    public string TitleText => TitleBox.Text.Trim();
    public string Description => DescriptionBox.Text;
    public string Category => CategoryBox.Text.Trim();
    public FindingSeverity Severity => (FindingSeverity)SeverityBox.SelectedItem;
    public RequestRelevance RequestAsks => (RequestRelevance)RequestAsksBox.SelectedItem;

    public ComparisonFindingDialog(ComparisonFinding? original, IEnumerable<string> categories, string evidenceSummary)
    {
        InitializeComponent();
        Title = original is null ? "New comparison finding" : "Edit comparison finding";
        EvidenceSummary.Text = evidenceSummary;
        CategoryBox.ItemsSource = categories.ToArray();
        CategoryBox.Text = original?.Category ?? categories.FirstOrDefault() ?? "other";
        SeverityBox.ItemsSource = Enum.GetValues<FindingSeverity>();
        SeverityBox.SelectedItem = original?.Severity ?? FindingSeverity.Minor;
        RequestAsksBox.ItemsSource = Enum.GetValues<RequestRelevance>();
        RequestAsksBox.SelectedItem = original?.RequestAsks ?? RequestRelevance.Unclear;
        TitleBox.Text = original?.Title ?? "";
        DescriptionBox.Text = original?.Description ?? "";
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleText) || string.IsNullOrWhiteSpace(Description) ||
            string.IsNullOrWhiteSpace(Category))
        {
            MessageBox.Show(this, "Enter a title, shared assessment, and category.", "Comparison finding",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
