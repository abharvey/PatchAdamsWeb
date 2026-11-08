using System.Windows;
using System.Windows.Controls;
using PatchAdams.Core;

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

    public FindingDialog(string reference, IEnumerable<string> categories, SessionFinding? copyFrom = null)
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
        if (copyFrom is not null)
        {
            TitleBox.Text = copyFrom.Title;
            CategoryBox.Text = copyFrom.Category;
            SeverityBox.SelectedIndex = copyFrom.Severity switch
            {
                FindingSeverity.Trivial => 0,
                FindingSeverity.Minor => 1,
                FindingSeverity.Material => 2,
                FindingSeverity.Blocking => 3,
                _ => 1
            };
            DescriptionBox.Text = copyFrom.Description;
            RequestAsksBox.SelectedIndex = copyFrom.RequestAsks switch
            {
                RequestRelevance.Yes => 1,
                RequestRelevance.No => 2,
                _ => 0
            };
            NoteBox.Text = copyFrom.Note;
            NotFlaggedBox.IsChecked = copyFrom.NotFlagged;
        }
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
