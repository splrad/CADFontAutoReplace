using System.Windows;

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
    public string Evidence { get; init; } = string.Empty;
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

        _candidateText = data.CandidateText;
        MetadataText.Text = data.Metadata;
        CurrentTextBox.Text = data.CurrentText;
        CandidateTextBox.Text = string.IsNullOrEmpty(data.CandidateText) ? "<无候选>" : data.CandidateText;
        EvidenceText.Text = string.IsNullOrEmpty(data.Evidence) ? "<无>" : data.Evidence;
        SelectedTextBox.Text = string.IsNullOrEmpty(data.CandidateText) ? data.CurrentText : data.CandidateText;
        UseCandidateButton.IsEnabled = !string.IsNullOrEmpty(data.CandidateText);
    }

    private void OnUseCandidate(object sender, RoutedEventArgs e)
    {
        SelectedTextBox.Text = _candidateText;
        SelectedTextBox.Focus();
        SelectedTextBox.SelectAll();
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
}
