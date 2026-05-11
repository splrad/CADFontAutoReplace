using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AFR.UI;

internal sealed class DbTextLabelViewModel : INotifyPropertyChanged
{
    private readonly UiRelayCommand _useCandidateCommand;
    private readonly UiRelayCommand _repairCommand;
    private DbTextLabelCandidateData? _selectedCandidate;
    private string _selectedText;
    private string _note = string.Empty;

    public string Metadata { get; }

    public string CurrentText { get; }

    public string EvidenceText { get; }

    public IReadOnlyList<DbTextLabelCandidateData> Candidates { get; }

    public string CandidateCountText { get; }

    public ICommand UseCandidateCommand => _useCandidateCommand;

    public ICommand RepairCommand => _repairCommand;

    public ICommand KeepCommand { get; }

    public ICommand GlyphIssueCommand { get; }

    public ICommand CancelCommand { get; }

    public DbTextLabelDialogAction SelectedAction { get; private set; }

    public bool HasCandidates => Candidates.Count > 0;

    public bool HasNoCandidates => !HasCandidates;

    public DbTextLabelCandidateData? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (ReferenceEquals(_selectedCandidate, value)) return;

            _selectedCandidate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CandidatePreview));
            OnPropertyChanged(nameof(CanUseCandidate));
            _useCandidateCommand.RaiseCanExecuteChanged();
        }
    }

    public string CandidatePreview
    {
        get
        {
            string text = SelectedCandidate?.Text ?? string.Empty;
            return string.IsNullOrEmpty(text) ? "<无候选>" : text;
        }
    }

    public bool CanUseCandidate => !string.IsNullOrWhiteSpace(SelectedCandidate?.Text);

    public string SelectedText
    {
        get => _selectedText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_selectedText, next, StringComparison.Ordinal)) return;

            _selectedText = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRepair));
            OnPropertyChanged(nameof(HasSelectedTextError));
            _repairCommand.RaiseCanExecuteChanged();
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(_note, next, StringComparison.Ordinal)) return;

            _note = next;
            OnPropertyChanged();
        }
    }

    public bool CanRepair => !string.IsNullOrWhiteSpace(SelectedText);

    public bool HasSelectedTextError => !CanRepair;

    public event EventHandler<UiDialogCloseRequestedEventArgs>? CloseRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DbTextLabelViewModel(DbTextLabelDialogData data)
    {
        Metadata = data.Metadata;
        CurrentText = data.CurrentText;
        EvidenceText = string.IsNullOrWhiteSpace(data.Evidence) ? "<无>" : data.Evidence;
        Candidates = NormalizeCandidates(data).ToArray();
        CandidateCountText = Candidates.Count == 0 ? "无候选" : $"{Candidates.Count} 个候选";
        _selectedCandidate = Candidates.FirstOrDefault();
        _selectedText = CanUseCandidate ? SelectedCandidate!.Text : data.CurrentText;

        _useCandidateCommand = new UiRelayCommand(UseSelectedCandidate, () => CanUseCandidate);
        _repairCommand = new UiRelayCommand(
            () => Confirm(DbTextLabelDialogAction.Repair, true),
            () => CanRepair);
        KeepCommand = new UiRelayCommand(() => Confirm(DbTextLabelDialogAction.Keep, true));
        GlyphIssueCommand = new UiRelayCommand(() => Confirm(DbTextLabelDialogAction.GlyphIssue, true));
        CancelCommand = new UiRelayCommand(() => Confirm(DbTextLabelDialogAction.None, false));
    }

    private void UseSelectedCandidate()
    {
        if (!CanUseCandidate)
            return;

        SelectedText = SelectedCandidate!.Text;
    }

    private void Confirm(DbTextLabelDialogAction action, bool? dialogResult)
    {
        SelectedAction = action;
        OnPropertyChanged(nameof(SelectedAction));
        CloseRequested?.Invoke(this, new UiDialogCloseRequestedEventArgs(dialogResult));
    }

    private static IReadOnlyList<DbTextLabelCandidateData> NormalizeCandidates(DbTextLabelDialogData data)
    {
        if (data.Candidates.Count > 0)
            return data.Candidates;

        if (string.IsNullOrEmpty(data.CandidateText))
            return Array.Empty<DbTextLabelCandidateData>();

        return new[]
        {
            new DbTextLabelCandidateData
            {
                Text = data.CandidateText,
                Source = "candidate",
                Reason = string.Empty
            }
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
