using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

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
            return $"{score}{SourceDisplay} · {Text}";
        }
    }

    public string SourceDisplay => string.IsNullOrWhiteSpace(Source) ? "candidate" : Source;

    public string ReasonDisplay => string.IsNullOrWhiteSpace(Reason) ? "无说明" : Reason;

    public string ScoreText => HasNeuralScore ? $"AI {NeuralScore:0.000}" : string.Empty;
}

public partial class DbTextLabelWindow : Window
{
    private readonly DbTextLabelViewModel _viewModel;

    public DbTextLabelDialogAction SelectedAction => _viewModel.SelectedAction;

    public string SelectedText => _viewModel.SelectedText.Trim();

    public string Note => _viewModel.Note.Trim();

    public DbTextLabelWindow(DbTextLabelDialogData data)
    {
        InitializeComponent();

        _viewModel = new DbTextLabelViewModel(data);
        _viewModel.CloseRequested += OnCloseRequested;
        DataContext = _viewModel;

        Loaded += OnLoaded;
        WindowPositionHelper.SetupCenterOnParent(this);
    }

    private void OnCloseRequested(object? sender, UiDialogCloseRequestedEventArgs e)
    {
        DialogResult = e.DialogResult;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyDpiAwareBounds();
    }

    private void ApplyDpiAwareBounds()
    {
        double maxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width * 0.92);
        double maxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * 0.90);

        MaxWidth = maxWidth;
        MaxHeight = maxHeight;

        if (Width > maxWidth)
            Width = maxWidth;
        if (Height > maxHeight)
            Height = maxHeight;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
