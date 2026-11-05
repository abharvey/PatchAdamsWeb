using System.Windows;
using PatchAdams.Core;

namespace PatchAdamsSharp;

public partial class RemoveFindingDialog : Window
{
    public RemoveFindingDialog(SessionFinding finding, string displayLabel)
    {
        InitializeComponent();
        FindingTitle.Text = finding.Title;
        FindingReference.Text = $"Patch {displayLabel} · {finding.Reference}";
        FindingDescription.Text = finding.Description;
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
