using System.Windows;
using System.Windows.Controls;

namespace PatchAdamsSharp;

public partial class FindingDialog : Window
{
    public string TitleText => TitleBox.Text.Trim();
    public string Severity => (SeverityBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "minor";
    public string Category => (CategoryBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "other";
    public string Description => DescriptionBox.Text;

    public FindingDialog(string reference)
    {
        InitializeComponent();
        ReferenceLabel.Text = reference;
        CategoryBox.SelectedIndex = 0;
        SeverityBox.SelectedIndex = 1;
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
