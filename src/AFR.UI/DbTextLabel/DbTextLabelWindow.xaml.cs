using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AFR.UI;

public enum DbTextLabelDialogAction
{
    None,
    Repair,
    Keep,
    GlyphIssue
}

public sealed class DbTextLabelDialogData
{
    public string Metadata { get; init; } = string.Empty;
    public string CurrentText { get; init; } = string.Empty;
    public string CandidateText { get; init; } = string.Empty;
    public IReadOnlyList<DbTextLabelCandidateData> Candidates { get; init; } = Array.Empty<DbTextLabelCandidateData>();
    public string Evidence { get; init; } = string.Empty;
}

public sealed class DbTextLabelCandidateData
{
    public string Text { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool HasNeuralScore { get; init; }
    public float NeuralScore { get; init; }

    public string DisplayText
    {
        get
        {
            string score = HasNeuralScore ? $"AI {NeuralScore:0.000} · " : string.Empty;
            return $"{score}{Source} · {Text}";
        }
    }
}

public partial class DbTextLabelWindow : Window
{
    private readonly string _candidateText;

    public DbTextLabelDialogAction SelectedAction { get; private set; }

    public string SelectedText => SelectedTextBox.Text.Trim();

    public string Note => NoteTextBox.Text.Trim();

    public DbTextLabelWindow(DbTextLabelDialogData data)
    {
        InitializeComponent();
        WindowPositionHelper.SetupCenterOnParent(this);

        IReadOnlyList<DbTextLabelCandidateData> candidates = data.Candidates.Count > 0
            ? data.Candidates
            : BuildSingleCandidate(data.CandidateText);

        _candidateText = candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c.Text))?.Text ?? string.Empty;
        MetadataText.Text = data.Metadata;
        CurrentTextBox.Text = data.CurrentText;
        CandidateListBox.ItemsSource = candidates;
        CandidateListBox.DisplayMemberPath = nameof(DbTextLabelCandidateData.DisplayText);
        CandidateListBox.SelectedIndex = candidates.Count > 0 ? 0 : -1;
        CandidateTextBox.Text = string.IsNullOrEmpty(_candidateText) ? "<无候选>" : _candidateText;
        EvidenceText.Text = string.IsNullOrEmpty(data.Evidence) ? "<无>" : data.Evidence;
        SelectedTextBox.Text = string.IsNullOrEmpty(_candidateText) ? data.CurrentText : _candidateText;
        UseCandidateButton.IsEnabled = !string.IsNullOrEmpty(_candidateText);
    }

    private void OnUseCandidate(object sender, RoutedEventArgs e)
    {
        if (CandidateListBox.SelectedItem is DbTextLabelCandidateData candidate
            && !string.IsNullOrEmpty(candidate.Text))
            SelectedTextBox.Text = candidate.Text;
        else
            SelectedTextBox.Text = _candidateText;

        SelectedTextBox.Focus();
        SelectedTextBox.SelectAll();
    }

    private void OnCandidateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateListBox.SelectedItem is not DbTextLabelCandidateData candidate)
            return;

        CandidateTextBox.Text = string.IsNullOrEmpty(candidate.Text) ? "<无候选>" : candidate.Text;
        UseCandidateButton.IsEnabled = !string.IsNullOrEmpty(candidate.Text);
    }

    private void OnRepair(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SelectedTextBox.Text))
        {
            MessageBox.Show(
                "正确文本不能为空。",
                "AFR — DBText 人工确认",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedAction = DbTextLabelDialogAction.Repair;
        DialogResult = true;
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        SelectedAction = DbTextLabelDialogAction.Keep;
        DialogResult = true;
    }

    private void OnGlyphIssue(object sender, RoutedEventArgs e)
    {
        SelectedAction = DbTextLabelDialogAction.GlyphIssue;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        SelectedAction = DbTextLabelDialogAction.None;
        DialogResult = false;
    }

    private static IReadOnlyList<DbTextLabelCandidateData> BuildSingleCandidate(string candidateText)
    {
        if (string.IsNullOrEmpty(candidateText))
            return Array.Empty<DbTextLabelCandidateData>();

        return new[]
        {
            new DbTextLabelCandidateData
            {
                Text = candidateText,
                Source = "candidate",
                Reason = string.Empty
            }
        };
    }
}
