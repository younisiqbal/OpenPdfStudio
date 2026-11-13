using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services.Compare;

namespace OpenPdfStudio.ViewModels;

public enum CompareHighlightFilter
{
    Differences,
    Matching,
    NoHighlights
}

public enum CompareScope
{
    WholeDocuments,
    SelectedPages
}

public sealed record CompareChoice(object Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Two PDFs side by side with synced scroll and filterable text-diff highlights.</summary>
public partial class CompareViewModel : ViewModelBase, IDisposable
{
    private readonly Action _close;
    private readonly Action<string> _openInTab;
    private readonly Action<int, double> _leftScrollHandler;
    private readonly Action<int, double> _rightScrollHandler;
    private bool _syncing;
    private bool _disposed;
    private int _lastSyncedPage = -1;
    private double _lastSyncedFraction = double.NaN;
    private CancellationTokenSource? _diffCts;
    private PdfCompareResult? _lastResult;

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

        _leftComparePage = Math.Clamp(Left.CurrentPageIndex, 0, Math.Max(Left.PageCount - 1, 0));
        _rightComparePage = Math.Clamp(Right.CurrentPageIndex, 0, Math.Max(Right.PageCount - 1, 0));
        _selectedHighlightFilter = HighlightFilterOptions[0];
        _selectedCompareScope = CompareScopeOptions[0];

        _leftScrollHandler = (page, fraction) => OnScrollAnchorChanged(Left, page, fraction);
        _rightScrollHandler = (page, fraction) => OnScrollAnchorChanged(Right, page, fraction);
        Left.ScrollAnchorChanged += _leftScrollHandler;
        Right.ScrollAnchorChanged += _rightScrollHandler;

        _ = RefreshDiffsAsync();
    }

    public DocumentViewModel Left { get; private set; }
    public DocumentViewModel Right { get; private set; }

    public IReadOnlyList<CompareChoice> HighlightFilterOptions { get; } =
    [
        new(CompareHighlightFilter.Differences, "Differences"),
        new(CompareHighlightFilter.Matching, "Matching"),
        new(CompareHighlightFilter.NoHighlights, "No highlights")
    ];

    public IReadOnlyList<CompareChoice> CompareScopeOptions { get; } =
    [
        new(CompareScope.WholeDocuments, "Whole documents"),
        new(CompareScope.SelectedPages, "Selected pages")
    ];

    [ObservableProperty] private bool _syncPages = true;
    [ObservableProperty] private string _summary = "Comparing...";
    [ObservableProperty] private bool _isComparing = true;
    [ObservableProperty] private CompareHighlightFilter _highlightFilter = CompareHighlightFilter.Differences;
    [ObservableProperty] private CompareScope _compareScope = CompareScope.WholeDocuments;
    [ObservableProperty] private int _leftComparePage;
    [ObservableProperty] private int _rightComparePage;
    [ObservableProperty] private CompareChoice? _selectedHighlightFilter;
    [ObservableProperty] private CompareChoice? _selectedCompareScope;

    public string LeftTitle => Left.FileName;
    public string RightTitle => Right.FileName;

    public bool IsSelectedPagesScope => CompareScope == CompareScope.SelectedPages;

    public string LeftComparePageDisplay
    {
        get => (LeftComparePage + 1).ToString();
        set
        {
            if (int.TryParse(value, out var n))
                LeftComparePage = Math.Clamp(n - 1, 0, Math.Max(Left.PageCount - 1, 0));
            OnPropertyChanged();
        }
    }

    public string RightComparePageDisplay
    {
        get => (RightComparePage + 1).ToString();
        set
        {
            if (int.TryParse(value, out var n))
                RightComparePage = Math.Clamp(n - 1, 0, Math.Max(Right.PageCount - 1, 0));
            OnPropertyChanged();
        }
    }

    partial void OnHighlightFilterChanged(CompareHighlightFilter value) => _ = ApplyFilterOverlaysAsync();

    partial void OnSelectedHighlightFilterChanged(CompareChoice? value)
    {
        if (value?.Value is CompareHighlightFilter filter && filter != HighlightFilter)
            HighlightFilter = filter;
    }

    partial void OnSelectedCompareScopeChanged(CompareChoice? value)
    {
        if (value?.Value is CompareScope scope && scope != CompareScope)
            CompareScope = scope;
    }

    partial void OnCompareScopeChanged(CompareScope value)
    {
        OnPropertyChanged(nameof(IsSelectedPagesScope));
        if (value == CompareScope.SelectedPages)
            UseCurrentPages();
        _ = RefreshDiffsAsync();
    }

    partial void OnLeftComparePageChanged(int value) => OnPropertyChanged(nameof(LeftComparePageDisplay));

    partial void OnRightComparePageChanged(int value) => OnPropertyChanged(nameof(RightComparePageDisplay));

    private void OnScrollAnchorChanged(DocumentViewModel source, int pageIndex, double fraction)
    {
        if (!SyncPages || _syncing)
            return;

        if (pageIndex == _lastSyncedPage &&
            !double.IsNaN(_lastSyncedFraction) &&
            Math.Abs(fraction - _lastSyncedFraction) < 0.004)
            return;

        var target = ReferenceEquals(source, Left) ? Right : Left;
        if (target.PageCount == 0)
            return;

        int index;
        if (CompareScope == CompareScope.SelectedPages)
        {
            // Keep each pane on its chosen compare page; only mirror Y fraction.
            index = ReferenceEquals(target, Left) ? LeftComparePage : RightComparePage;
            index = Math.Clamp(index, 0, target.PageCount - 1);
        }
        else
        {
            index = Math.Clamp(pageIndex, 0, target.PageCount - 1);
        }

        _syncing = true;
        _lastSyncedPage = pageIndex;
        _lastSyncedFraction = fraction;
        target.ScrollToAnchor(index, fraction);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _syncing = false,
            Avalonia.Threading.DispatcherPriority.Input);
    }

    [RelayCommand]
    private void PreviousPage()
    {
        if (CompareScope == CompareScope.SelectedPages)
            return;
        var index = Math.Min(Left.CurrentPageIndex, Right.CurrentPageIndex) - 1;
        JumpBoth(index);
    }

    [RelayCommand]
    private void NextPage()
    {
        if (CompareScope == CompareScope.SelectedPages)
            return;
        var index = Math.Max(Left.CurrentPageIndex, Right.CurrentPageIndex) + 1;
        JumpBoth(index);
    }

    [RelayCommand]
    private void UseCurrentPages()
    {
        LeftComparePage = Math.Clamp(Left.CurrentPageIndex, 0, Math.Max(Left.PageCount - 1, 0));
        RightComparePage = Math.Clamp(Right.CurrentPageIndex, 0, Math.Max(Right.PageCount - 1, 0));
        OnPropertyChanged(nameof(LeftComparePageDisplay));
        OnPropertyChanged(nameof(RightComparePageDisplay));
    }

    [RelayCommand]
    private void CompareSelectedPages() => _ = RefreshDiffsAsync();

    [RelayCommand]
    private void SwapSides()
    {
        Left.ScrollAnchorChanged -= _leftScrollHandler;
        Right.ScrollAnchorChanged -= _rightScrollHandler;

        (Left, Right) = (Right, Left);
        (LeftComparePage, RightComparePage) = (RightComparePage, LeftComparePage);
        OnPropertyChanged(nameof(Left));
        OnPropertyChanged(nameof(Right));
        OnPropertyChanged(nameof(LeftTitle));
        OnPropertyChanged(nameof(RightTitle));
        OnPropertyChanged(nameof(LeftComparePageDisplay));
        OnPropertyChanged(nameof(RightComparePageDisplay));

        Left.ScrollAnchorChanged += _leftScrollHandler;
        Right.ScrollAnchorChanged += _rightScrollHandler;
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

    private async Task RefreshDiffsAsync()
    {
        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        var token = _diffCts.Token;

        IsComparing = true;
        Summary = "Comparing...";
        ClearHighlights(Left);
        ClearHighlights(Right);
        try
        {
            PdfCompareResult result;
            if (CompareScope == CompareScope.SelectedPages)
            {
                var leftPage = Math.Clamp(LeftComparePage, 0, Math.Max(Left.PageCount - 1, 0));
                var rightPage = Math.Clamp(RightComparePage, 0, Math.Max(Right.PageCount - 1, 0));
                LeftComparePage = leftPage;
                RightComparePage = rightPage;
                result = await PdfTextComparer.ComparePagesAsync(
                    Left.Session, Right.Session, leftPage, rightPage, token);
            }
            else
            {
                result = await PdfTextComparer.CompareAsync(Left.Session, Right.Session, token);
            }

            if (token.IsCancellationRequested)
                return;
            _lastResult = result;
            Summary = result.Summary;
            await ApplyHighlightsAsync(result, token);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer compare.
        }
        catch (Exception ex)
        {
            Summary = $"Could not compare: {ex.Message}";
            _lastResult = null;
            ClearHighlights(Left);
            ClearHighlights(Right);
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsComparing = false;
        }
    }

    private async Task ApplyFilterOverlaysAsync()
    {
        if (_lastResult is null)
            return;

        _diffCts?.Cancel();
        _diffCts?.Dispose();
        _diffCts = new CancellationTokenSource();
        var token = _diffCts.Token;
        try
        {
            await ApplyHighlightsAsync(_lastResult, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplyHighlightsAsync(PdfCompareResult result, CancellationToken token)
    {
        ClearHighlights(Left);
        ClearHighlights(Right);

        switch (HighlightFilter)
        {
            case CompareHighlightFilter.Differences:
                // Both panes use the red layer so changed words read the same on left and right.
                await ApplySpansAsync(Left, result.LeftDeletes, toMatchLayer: false, token);
                await ApplySpansAsync(Right, result.RightInserts, toMatchLayer: false, token);
                break;
            case CompareHighlightFilter.Matching:
                await ApplySpansAsync(Left, result.LeftMatches, toMatchLayer: true, token);
                await ApplySpansAsync(Right, result.RightMatches, toMatchLayer: true, token);
                break;
            case CompareHighlightFilter.NoHighlights:
                break;
        }
    }

    private static async Task ApplySpansAsync(
        DocumentViewModel document,
        IReadOnlyList<PdfTextSpan> spans,
        bool toMatchLayer,
        CancellationToken token)
    {
        if (spans.Count == 0)
            return;

        foreach (var group in spans.GroupBy(s => s.PageIndex))
        {
            token.ThrowIfCancellationRequested();
            if (group.Key < 0 || group.Key >= document.Pages.Count)
                continue;

            var rects = new List<Rect>();
            foreach (var span in group)
            {
                if (span.Count <= 0)
                    continue;
                var pageRects = await document.Session.GetTextRectsAsync(span.PageIndex, span.StartIndex, span.Count);
                rects.AddRange(pageRects);
            }

            if (rects.Count == 0)
                continue;

            if (toMatchLayer)
                document.Pages[group.Key].DiffMatchRects = rects;
            else
                document.Pages[group.Key].DiffDeleteRects = rects;
        }
    }

    private static void ClearHighlights(DocumentViewModel document)
    {
        foreach (var page in document.Pages)
        {
            page.DiffDeleteRects = null;
            page.DiffInsertRects = null;
            page.DiffMatchRects = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _diffCts?.Cancel();
        _diffCts?.Dispose();
        Left.ScrollAnchorChanged -= _leftScrollHandler;
        Right.ScrollAnchorChanged -= _rightScrollHandler;
        ClearHighlights(Left);
        ClearHighlights(Right);
        Left.Dispose();
        Right.Dispose();
    }
}
