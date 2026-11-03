using System.Windows;
using System.Windows.Controls;

namespace PatchAdamsSharp;

public partial class FindingDialog : Window
{
    public string TitleText => TitleBox.Text.Trim();
    public string Severity => (SeverityBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "minor";
    public string Category => CategoryBox.Text.Trim();
    public string Description => DescriptionBox.Text;
    public string Note => NoteBox.Text;
    public string RequestAsks => (RequestAsksBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "unclear";
    public bool NotFlagged => NotFlaggedBox.IsChecked == true;

    public FindingDialog(string reference, IEnumerable<string> categories)
    {
        InitializeComponent();
        ReferenceLabel.Text = reference;
        CategoryBox.IsEditable = true;
        CategoryBox.Items.Clear();
        foreach (var category in categories)
            CategoryBox.Items.Add(new ComboBoxItem { Content = category });
        CategoryBox.SelectedIndex = CategoryBox.Items.Count > 0 ? 0 : -1;
        SeverityBox.SelectedIndex = 1;
        RequestAsksBox.SelectedIndex = 0;
        TitleBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show(this, "A finding title is required.", "New finding",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
