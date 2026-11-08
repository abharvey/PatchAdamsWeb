using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PatchAdams.Core;

namespace PatchAdamsSharp;

public partial class MainWindow
{
    private readonly ObservableCollection<string> comparisonFindingLabels = [];
    private readonly ObservableCollection<string> savedEvidenceLabels = [];
    private readonly ObservableCollection<string> draftEvidenceLabels = [];
    private readonly List<ComparisonEvidence> draftEvidence = [];
    private ComparisonFinding? editedComparison;
    private bool refreshingComparisonUi;

    private void InitializeComparisonFindings()
    {
        ComparisonFindingsList.ItemsSource = comparisonFindingLabels;
        SavedComparisonEvidence.ItemsSource = savedEvidenceLabels;
        DraftComparisonEvidence.ItemsSource = draftEvidenceLabels;
        RefreshComparisonFindings();
    }

    private string EvidenceLabel(ComparisonEvidence evidence) =>
        $"Patch {BlindDisplayLabel(evidence.Label)} · {ReferenceGenerator.ForRanges(evidence.SelectedRanges, evidence.Path)}";

    private ComparisonFinding? SelectedComparisonFinding =>
        comparisonFindings.ElementAtOrDefault(ComparisonFindingsList.SelectedIndex);

    private ComparisonEvidence? CurrentComparisonEvidence()
    {
        var current = selection;
        return current is null ? null : new ComparisonEvidence(current.Column.Label, current.File.Path,
            current.Ranges.ToArray());
    }

    private bool ConfirmDiscardComparisonDraft() =>
        ComparisonDraftPanel.Visibility != Visibility.Visible || MessageBox.Show(this,
            "Discard the current comparison draft? Saved comparisons will not change.",
            "Comparison draft", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private void StartComparison_Click(object sender, RoutedEventArgs e)
    {
        var evidence = CurrentComparisonEvidence();
        if (evidence is null || !ConfirmDiscardComparisonDraft()) return;
        BeginComparisonDraft([evidence]);
    }

    private void BeginComparisonDraft(IEnumerable<ComparisonEvidence> evidence, ComparisonFinding? original = null)
    {
        draftEvidence.Clear();
        draftEvidence.AddRange(evidence);
        editedComparison = original;
        ComparisonDraftPanel.Visibility = Visibility.Visible;
        ComparisonDraftHeading.Text = original is null ? "New comparison draft" : "Editing comparison";
        RefreshComparisonDraft(draftEvidence.Count - 1);
        ComparisonDraftPanel.BringIntoView();
    }

    private void AddComparisonEvidence_Click(object sender, RoutedEventArgs e)
    {
        var evidence = CurrentComparisonEvidence();
        if (evidence is null) return;
        draftEvidence.Add(evidence);
        RefreshComparisonDraft(draftEvidence.Count - 1);
    }

    private void ReplaceComparisonEvidence_Click(object sender, RoutedEventArgs e)
    {
        var evidence = CurrentComparisonEvidence();
        var index = DraftComparisonEvidence.SelectedIndex;
        if (evidence is null || index < 0 || index >= draftEvidence.Count) return;
        // Re-anchoring keeps the observation attached to the selected evidence row.
        draftEvidence[index] = evidence with { Note = draftEvidence[index].Note };
        RefreshComparisonDraft(index);
    }

    private void RemoveComparisonEvidence_Click(object sender, RoutedEventArgs e)
    {
        var index = DraftComparisonEvidence.SelectedIndex;
        if (index < 0 || index >= draftEvidence.Count) return;
        draftEvidence.RemoveAt(index);
        RefreshComparisonDraft(Math.Min(index, draftEvidence.Count - 1));
    }

    private void DraftComparisonEvidence_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingComparisonUi) return;
        RefreshComparisonDraft(DraftComparisonEvidence.SelectedIndex);
    }

    private void DraftEvidenceNote_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (refreshingComparisonUi) return;
        var index = DraftComparisonEvidence.SelectedIndex;
        if (index >= 0 && index < draftEvidence.Count)
            draftEvidence[index] = draftEvidence[index] with { Note = DraftEvidenceNote.Text };
    }

    private void NavigateDraftEvidence_Click(object sender, RoutedEventArgs e)
    {
        var evidence = draftEvidence.ElementAtOrDefault(DraftComparisonEvidence.SelectedIndex);
        if (evidence is not null)
            NavigateToSelection(evidence.Label, evidence.Path, evidence.SelectedRanges);
    }

    private void ReviewComparisonDraft_Click(object sender, RoutedEventArgs e)
    {
        if (draftEvidence.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count() < 2) return;
        var dialog = new ComparisonFindingDialog(editedComparison, categories,
            string.Join(Environment.NewLine, draftEvidence.Select(EvidenceLabel)))
        {
            Owner = this,
            Title = comparisonFindings.Any(item => item.Id == editedComparison?.Id)
                ? "Edit comparison finding" : "New comparison finding"
        };
        var accepted = dialog.ShowDialog() == true;
        var finding = new ComparisonFinding(editedComparison?.Id ?? Guid.NewGuid(), dialog.TitleText,
            dialog.Description, dialog.Category, dialog.Severity, dialog.RequestAsks,
            draftEvidence.ToArray(), editedComparison?.Created ?? DateTimeOffset.UtcNow);
        // Back to draft preserves typed assessment text while the reviewer adjusts evidence.
        editedComparison = finding;
        if (!accepted) return;
        CommitComparisonDraft(finding);
    }

    private void CommitComparisonDraft(ComparisonFinding finding)
    {
        finding.Validate(columns.Select(column => column.Label));
        var index = comparisonFindings.FindIndex(item => item.Id == finding.Id);
        if (index < 0) comparisonFindings.Add(finding);
        else comparisonFindings[index] = finding;
        if (!categories.Contains(finding.Category, StringComparer.Ordinal))
            categories.Add(finding.Category);
        ClearComparisonDraft();
        RefreshComparisonFindings(finding.Id);
        SavedComparisonsExpander.IsExpanded = true;
        ComparisonFindingsList.BringIntoView();
        StatusText.Text = "Comparison recorded. Save the session to keep it.";
    }

    private void CancelComparisonDraft_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDiscardComparisonDraft()) ClearComparisonDraft();
    }

    private void ClearComparisonDraft()
    {
        draftEvidence.Clear();
        editedComparison = null;
        ComparisonDraftPanel.Visibility = Visibility.Collapsed;
        RefreshComparisonDraft(-1);
    }

    private void EditComparison_Click(object sender, RoutedEventArgs e)
    {
        var finding = SelectedComparisonFinding;
        if (finding is null || !ConfirmDiscardComparisonDraft()) return;
        BeginComparisonDraft(finding.Evidence, finding);
    }

    private void RemoveComparison_Click(object sender, RoutedEventArgs e)
    {
        var finding = SelectedComparisonFinding;
        if (finding is null) return;
        if (MessageBox.Show(this, $"Delete the entire comparison \"{finding.Title}\" and all its evidence?",
                "Delete comparison", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        RemoveComparisonFinding(finding.Id);
    }

    private void RemoveComparisonFinding(Guid id)
    {
        comparisonFindings.RemoveAll(item => item.Id == id);
        if (editedComparison?.Id == id) ClearComparisonDraft();
        RefreshComparisonFindings();
        StatusText.Text = "Comparison deleted. Save the session to keep this change.";
    }

    private void ComparisonFindingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingComparisonUi) RefreshSavedComparisonEvidence();
    }

    private void SavedComparisonEvidence_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingComparisonUi) return;
        var evidence = SelectedComparisonFinding?.Evidence.ElementAtOrDefault(SavedComparisonEvidence.SelectedIndex);
        SavedComparisonNote.Text = evidence?.Note ?? "";
        if (evidence is not null)
            NavigateToSelection(evidence.Label, evidence.Path, evidence.SelectedRanges);
    }

    private void RefreshComparisonFindings(Guid? selectedId = null)
    {
        selectedId ??= SelectedComparisonFinding?.Id;
        refreshingComparisonUi = true;
        SavedComparisonsExpander.Header = $"Saved comparisons ({comparisonFindings.Count})";
        if (comparisonFindings.Count == 0) SavedComparisonsExpander.IsExpanded = false;
        comparisonFindingLabels.Clear();
        foreach (var finding in comparisonFindings)
        {
            var patches = string.Join(", ", finding.Evidence.Select(item => item.Label)
                .Distinct(StringComparer.Ordinal).Select(BlindDisplayLabel));
            comparisonFindingLabels.Add($"{finding.Title} · {finding.Severity} · Patches {patches}");
        }
        ComparisonFindingsList.SelectedIndex = comparisonFindings.FindIndex(item => item.Id == selectedId);
        refreshingComparisonUi = false;
        RefreshSavedComparisonEvidence();
        RefreshComparisonDraft(DraftComparisonEvidence.SelectedIndex);
        UpdateComparisonCaptureButtons();
    }

    private void RefreshSavedComparisonEvidence()
    {
        refreshingComparisonUi = true;
        savedEvidenceLabels.Clear();
        var finding = SelectedComparisonFinding;
        ComparisonAssessment.Text = finding?.Description ?? "";
        SavedComparisonNote.Text = "";
        if (finding is not null)
            foreach (var evidence in finding.Evidence) savedEvidenceLabels.Add(EvidenceLabel(evidence));
        SavedComparisonEvidence.SelectedIndex = -1;
        EditComparisonButton.IsEnabled = finding is not null;
        RemoveComparisonButton.IsEnabled = finding is not null;
        refreshingComparisonUi = false;
    }

    private void RefreshComparisonDraft(int index)
    {
        refreshingComparisonUi = true;
        draftEvidenceLabels.Clear();
        foreach (var evidence in draftEvidence) draftEvidenceLabels.Add(EvidenceLabel(evidence));
        DraftComparisonEvidence.SelectedIndex = index;
        var selected = draftEvidence.ElementAtOrDefault(index);
        DraftEvidenceNote.Text = selected?.Note ?? "";
        DraftEvidenceNote.IsEnabled = selected is not null;
        RemoveEvidenceButton.IsEnabled = selected is not null;
        NavigateDraftEvidenceButton.IsEnabled = selected is not null;
        var patchCount = draftEvidence.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count();
        ComparisonDraftStatus.Text = $"{draftEvidence.Count} locations · {patchCount} patches. " +
            (patchCount >= 2 ? "Ready to review." : "Add evidence from another patch.");
        ReviewComparisonButton.IsEnabled = patchCount >= 2;
        refreshingComparisonUi = false;
        UpdateComparisonCaptureButtons();
    }

    private void UpdateComparisonCaptureButtons()
    {
        // Selection events can arrive during InitializeComponent.
        if (StartComparisonButton is null) return;
        StartComparisonButton.IsEnabled = selection is not null;
        AddComparisonEvidenceButton.IsEnabled = selection is not null;
        ReplaceEvidenceButton.IsEnabled = selection is not null && DraftComparisonEvidence.SelectedIndex >= 0;
    }

    private void ResetComparisonReview()
    {
        ClearComparisonDraft();
        RefreshComparisonFindings();
    }
}
