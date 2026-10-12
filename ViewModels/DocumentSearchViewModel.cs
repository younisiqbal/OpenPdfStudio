using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.ViewModels;

/// <summary>Find-in-document state for one tab: the Ctrl+F box, its options, the hits and their highlights.</summary>
public partial class DocumentSearchViewModel : ObservableObject, IDisposable
{
    private readonly DocumentViewModel _document;
    private readonly PdfDocumentSession _session;
    private readonly Action<string> _setStatus;
    private readonly DispatcherTimer _debounce;
    private readonly List<PdfSearchHit> _hits = new();
    private readonly Dictionary<int, List<Rect>> _rectsByPage = new();

    private CancellationTokenSource? _searchCts;
    private int _currentIndex = -1;
    private int _currentPage = -1;
    private bool _suppressSearch;
    private bool _searchPending;
    private bool _disposed;

    public DocumentSearchViewModel(DocumentViewModel document, PdfDocumentSession session, Action<string> setStatus)
    {
        _document = document;
        _session = session;
        _setStatus = setStatus;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunSearchAsync();
        };
    }

    /// <summary>Raised when the find box should take keyboard focus.</summary>
    public event Action? FocusRequested;

    /// <summary>Raised when the find box closes and focus should go back to the pages.</summary>
    public event Action? Closed;

    /// <summary>Opens the Advanced Search panel with these options. Set by the main window's view model.</summary>
    public Action<PdfSearchOptions>? OpenAdvancedSearch { get; set; }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private string _resultText = string.Empty;
    [ObservableProperty] private bool _isSearching;

    [ObservableProperty] private bool _wholeWords;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _includePageText = true;
    [ObservableProperty] private bool _includeBookmarks;
    [ObservableProperty] private bool _includeComments;
    [ObservableProperty] private bool _highlightAll = true;

    public bool HasHits => _hits.Count > 0;

    public PdfSearchOptions Options => new(Query, SearchMatchMode.ExactPhrase, WholeWords, CaseSensitive, false,
        IncludePageText, IncludeBookmarks, IncludeComments);

    partial void OnQueryChanged(string value) => ScheduleSearch();
    partial void OnWholeWordsChanged(bool value) => ScheduleSearch();
    partial void OnCaseSensitiveChanged(bool value) => ScheduleSearch();
    partial void OnIncludePageTextChanged(bool value) => ScheduleSearch();
    partial void OnIncludeBookmarksChanged(bool value) => ScheduleSearch();
    partial void OnIncludeCommentsChanged(bool value) => ScheduleSearch();

    partial void OnHighlightAllChanged(bool value) => ApplyAllHighlights();

    /// <summary>Re-runs an active search so pasted text is found and stale highlights are dropped.</summary>
    public void OnDocumentEdited()
    {
        if (!string.IsNullOrWhiteSpace(Query) && (IsOpen || _hits.Count > 0))
            ScheduleSearch();
    }

    public void Open(string? initialQuery = null, bool? highlightAll = null)
    {
        if (highlightAll is { } highlight)
            HighlightAll = highlight;

        IsOpen = true;
        if (!string.IsNullOrWhiteSpace(initialQuery) && initialQuery != Query)
            Query = initialQuery;
        else if (!string.IsNullOrWhiteSpace(Query) && _hits.Count == 0 && !IsSearching)
            ScheduleSearch(immediate: true);

        FocusRequested?.Invoke();
    }

    /// <summary>
    /// Shows hits found elsewhere (the Advanced Search panel) without searching again, and makes one current.
    /// </summary>
    public void ShowHits(PdfSearchOptions options, IReadOnlyList<PdfSearchHit> hits, PdfSearchHit current, bool highlightAll)
    {
        CancelSearch();
        _suppressSearch = true;
        try
        {
            Query = options.Query;
            WholeWords = options.WholeWords;
            CaseSensitive = options.CaseSensitive;
            IncludePageText = options.IncludePageText;
            IncludeBookmarks = options.IncludeBookmarks;
            IncludeComments = options.IncludeComments;
            HighlightAll = highlightAll;
        }
        finally
        {
            _suppressSearch = false;
        }

        ClearHits();
        foreach (var hit in hits.Where(h => h.PageIndex >= 0))
            AddHit(hit);
        ApplyAllHighlights();

        IsOpen = true;
        var index = _hits.IndexOf(current);
        if (index >= 0)
            Select(index);
        else
            UpdateResultText();
    }

    [RelayCommand]
    private void Next() => Step(1);

    [RelayCommand]
    private void Previous() => Step(-1);

    [RelayCommand]
    private void Close()
    {
        CancelSearch();
        _debounce.Stop();
        ClearHits();
        IsOpen = false;
        ResultText = string.Empty;
        Closed?.Invoke();
    }

    [RelayCommand]
    private void ToggleOption(string? option)
    {
        switch (option)
        {
            case nameof(WholeWords): WholeWords = !WholeWords; break;
            case nameof(CaseSensitive): CaseSensitive = !CaseSensitive; break;
            case nameof(IncludePageText): IncludePageText = !IncludePageText; break;
            case nameof(IncludeBookmarks): IncludeBookmarks = !IncludeBookmarks; break;
            case nameof(IncludeComments): IncludeComments = !IncludeComments; break;
            case nameof(HighlightAll): HighlightAll = !HighlightAll; break;
        }
    }

    [RelayCommand]
    private void Properties() => OpenAdvancedSearch?.Invoke(Options);

    private void Step(int direction)
    {
        if (!IsOpen)
        {
            Open();
            return;
        }

        if (_searchPending)
        {
            _debounce.Stop();
            _ = RunSearchAsync(selectAfterwards: direction > 0 ? 0 : -1);
            return;
        }

        if (_hits.Count == 0)
            return;

        var next = _currentIndex < 0
            ? (direction > 0 ? 0 : _hits.Count - 1)
            : (_currentIndex + direction + _hits.Count) % _hits.Count;
        Select(next);
    }

    private void ScheduleSearch(bool immediate = false)
    {
        if (_suppressSearch || _disposed)
            return;

        _searchPending = true;
        _debounce.Stop();
        if (immediate)
            _ = RunSearchAsync();
        else
            _debounce.Start();
    }

    private async Task RunSearchAsync(int? selectAfterwards = null)
    {
        _searchPending = false;
        CancelSearch();
        ClearHits();

        var options = Options;
        if (string.IsNullOrWhiteSpace(options.Query) || !IsOpen)
        {
            ResultText = string.Empty;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        ResultText = "Searching...";
        var startPage = _document.CurrentPageIndex;
        var flushedPage = -1;

        try
        {
            await foreach (var hit in PdfSearchEngine.SearchAsync(_session, options, null, cts.Token))
            {
                if (hit.PageIndex < 0)
                    continue;

                if (flushedPage >= 0 && hit.PageIndex != flushedPage)
                    FlushPage(flushedPage);
                flushedPage = hit.PageIndex;

                AddHit(hit);
                if (_currentIndex < 0 && selectAfterwards is null && hit.PageIndex >= startPage)
                    Select(_hits.Count - 1);
                else
                    UpdateResultText();
            }

            if (flushedPage >= 0)
                FlushPage(flushedPage);

            if (_hits.Count > 0 && _currentIndex < 0)
                Select(selectAfterwards is -1 ? _hits.Count - 1 : 0);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception)
        {
            _setStatus("Search failed on this document");
        }
        finally
        {
            if (_searchCts == cts)
            {
                _searchCts = null;
                IsSearching = false;
            }
        }

        if (_searchCts is null)
        {
            UpdateResultText();
            _setStatus(_hits.Count == 0
                ? $"No results for \"{options.Query}\""
                : $"Found {_hits.Count} result{(_hits.Count == 1 ? "" : "s")} for \"{options.Query}\"");
        }
    }

    private void AddHit(PdfSearchHit hit)
    {
        _hits.Add(hit);
        if (hit.Rects.Count == 0)
            return;

        if (!_rectsByPage.TryGetValue(hit.PageIndex, out var rects))
            _rectsByPage[hit.PageIndex] = rects = new List<Rect>();
        rects.AddRange(hit.Rects);
    }

    private void FlushPage(int pageIndex)
    {
        if (HighlightAll && pageIndex < _document.Pages.Count && _rectsByPage.TryGetValue(pageIndex, out var rects))
            _document.Pages[pageIndex].SearchRects = rects.ToArray();
    }

    private void ApplyAllHighlights()
    {
        foreach (var page in _document.Pages)
        {
            page.SearchRects = HighlightAll && _rectsByPage.TryGetValue(page.Index, out var rects)
                ? rects.ToArray()
                : null;
        }
    }

    private void Select(int index)
    {
        if (_currentPage >= 0 && _currentPage < _document.Pages.Count)
            _document.Pages[_currentPage].CurrentMatchRects = null;

        _currentIndex = index;
        var hit = _hits[index];
        _currentPage = hit.PageIndex;

        var page = _document.Pages[hit.PageIndex];
        page.CurrentMatchRects = hit.Rects.Count > 0 ? hit.Rects : null;
        if (hit.Rects.Count > 0)
            _document.RevealRect(hit.PageIndex, hit.Rects[0]);
        else
            _document.GoToPage(hit.PageIndex);

        UpdateResultText();
    }

    private void UpdateResultText()
    {
        OnPropertyChanged(nameof(HasHits));
        if (_hits.Count == 0)
            ResultText = IsSearching ? "Searching..." : string.IsNullOrWhiteSpace(Query) ? string.Empty : "No results";
        else
            ResultText = $"{Math.Max(_currentIndex + 1, 0)} of {_hits.Count}{(IsSearching ? "+" : "")}";
    }

    private void ClearHits()
    {
        foreach (var pageIndex in _rectsByPage.Keys)
        {
            if (pageIndex < _document.Pages.Count)
                _document.Pages[pageIndex].SearchRects = null;
        }

        if (_currentPage >= 0 && _currentPage < _document.Pages.Count)
            _document.Pages[_currentPage].CurrentMatchRects = null;

        _hits.Clear();
        _rectsByPage.Clear();
        _currentIndex = -1;
        _currentPage = -1;
        OnPropertyChanged(nameof(HasHits));
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        IsSearching = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _debounce.Stop();
        CancelSearch();
    }
}
