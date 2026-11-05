using System.Windows;

namespace PatchAdamsSharp;

public partial class ReviewerFeedbackDialog : Window
{
    public string Feedback => FeedbackBox.Text;

    public ReviewerFeedbackDialog(string feedback)
    {
        InitializeComponent();
        FeedbackBox.Text = feedback;
        FeedbackBox.Focus();
    }

    private void Keep_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
