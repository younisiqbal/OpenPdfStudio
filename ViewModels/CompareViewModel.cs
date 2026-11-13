using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services.Compare;

namespace OpenPdfStudio.ViewModels;

/// <summary>Two PDFs side by side with optional synced page navigation and a text-diff list.</summary>
public partial class CompareViewModel : ViewModelBase, IDisposable
{
    private readonly Action _close;
    private readonly Action<string> _openInTab;
    private bool _syncing;
    private bool _disposed;
    private CancellationTokenSource? _diffCts;

    public CompareViewModel(
        DocumentViewModel left,
        DocumentViewModel right,
        Action close,
        Action<string> openInTab)
    {
        Left = left;
        Right = right;
        _close = close;
        _openInTab = openInTab;

        Left.IsThumbnailsVisible = false;
        Right.IsThumbnailsVisible = false;
        Left.PropertyChanged += OnPanePropertyChanged;
        Right.PropertyChanged += OnPanePropertyChanged;

        DiffItems = new ObservableCollection<PdfPageDiff>();
        _ = RefreshDiffsAsync();
    }

    public DocumentViewModel Left { get; private set; }
    public DocumentViewModel Right { get; private set; }

    public ObservableCollection<PdfPageDiff> DiffItems { get; }

    [ObservableProperty] private bool _syncPages = true;
    [ObservableProperty] private string _summary = "Comparing...";
    [ObservableProperty] private bool _isComparing = true;
    [ObservableProperty] private bool _showOnlyDifferences = true;
    [ObservableProperty] private PdfPageDiff? _selectedDiff;

    public string LeftTitle => Left.FileName;
    public string RightTitle => Right.FileName;

    partial void OnShowOnlyDifferencesChanged(bool value) => ApplyFilter();

    partial void OnSelectedDiffChanged(PdfPageDiff? value)
    {
        if (value is null)
            return;
        GoToDiff(value);
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentViewModel.CurrentPageIndex) || !SyncPages || _syncing)
            return;

        _syncing = true;
        try
        {
            if (ReferenceEquals(sender, Left))
                Right.GoToPage(Left.CurrentPageIndex);
            else if (ReferenceEquals(sender, Right))
                Left.GoToPage(Right.CurrentPageIndex);
        }
        finally
        {
            _syncing = false;
        }
    }

    [RelayCommand]
    private void PreviousPage()
    {
        var index = Math.Min(Left.CurrentPageIndex, Right.CurrentPageIndex) - 1;
        JumpBoth(index);
    }

    [RelayCommand]
    private void NextPage()
    {
        var index = Math.Max(Left.CurrentPageIndex, Right.CurrentPageIndex) + 1;
        JumpBoth(index);
    }

    [RelayCommand]
    private void SwapSides()
    {
        Left.PropertyChanged -= OnPanePropertyChanged;
        Right.PropertyChanged -= OnPanePropertyChanged;

        (Left, Right) = (Right, Left);
        OnPropertyChanged(nameof(Left));
        OnPropertyChanged(nameof(Right));
        OnPropertyChanged(nameof(LeftTitle));
        OnPropertyChanged(nameof(RightTitle));

        Left.PropertyChanged += OnPanePropertyChanged;
        Right.PropertyChanged += OnPanePropertyChanged;
        _ = RefreshDiffsAsync();
    }

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private void OpenLeftInTab()
    {
        var path = Left.FilePath;
        _close();
        _openInTab(path);
    }

    [RelayCommand]
    private void OpenRightInTab()
    {
        var path = Right.FilePath;
        _close();
        _openInTab(path);
    }

    [RelayCommand]
    private void GoToDiff(PdfPageDiff? diff)
    {
        if (diff is null)
            return;

        _syncing = true;
        try
        {
            if (diff.Kind != PdfPageDiffKind.OnlyRight)
                Left.GoToPage(diff.PageIndex);
            if (diff.Kind != PdfPageDiffKind.OnlyLeft)
                Right.GoToPage(diff.PageIndex);
            if (diff.Kind == PdfPageDiffKind.OnlyLeft)
                Right.GoToPage(Math.Min(diff.PageIndex, Math.Max(Right.PageCount - 1, 0)));
            if (diff.Kind == PdfPageDiffKind.OnlyRight)
                Left.GoToPage(Math.Min(diff.PageIndex, Math.Max(Left.PageCount - 1, 0)));
        }
        finally
        {
            _syncing = false;
        }
    }

    private void JumpBoth(int index)
    {
        _syncing = true;
        try
        {
            Left.GoToPage(index);
            Right.GoToPage(index);
        }
        finally
        {
            _syncing = false;
        }
    }

    private PdfCompareResult? _fullResult;

    private async Task RefreshDiffsAsync()
    {
        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        var token = _diffCts.Token;

        IsComparing = true;
        Summary = "Comparing...";
        try
        {
            var result = await PdfTextComparer.CompareAsync(Left.Session, Right.Session, token);
            if (token.IsCancellationRequested)
                return;
            _fullResult = result;
            Summary = result.Summary;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer compare.
        }
        catch (Exception ex)
        {
            Summary = $"Could not compare: {ex.Message}";
            DiffItems.Clear();
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsComparing = false;
        }
    }

    private void ApplyFilter()
    {
        DiffItems.Clear();
        if (_fullResult is null)
            return;

        foreach (var page in _fullResult.Pages)
        {
            if (ShowOnlyDifferences && page.Kind == PdfPageDiffKind.Equal)
                continue;
            DiffItems.Add(page);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _diffCts?.Cancel();
        _diffCts?.Dispose();
        Left.PropertyChanged -= OnPanePropertyChanged;
        Right.PropertyChanged -= OnPanePropertyChanged;
        Left.Dispose();
        Right.Dispose();
    }
}
