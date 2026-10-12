using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.ViewModels;

/// <summary>The search box in the top bar and its dropdown of recent searches and search actions.</summary>
public partial class TopSearchViewModel : ViewModelBase
{
    private readonly Func<DocumentViewModel?> _activeDocument;
    private readonly AdvancedSearchViewModel _advanced;
    private readonly Action<string> _setStatus;

    public TopSearchViewModel(Func<DocumentViewModel?> activeDocument, AdvancedSearchViewModel advanced,
        Action<string> setStatus)
    {
        _activeDocument = activeDocument;
        _advanced = advanced;
        _setStatus = setStatus;
        RecentSearches.Changed += RefreshRecent;
        RefreshRecent();
    }

    public ObservableCollection<string> RecentItems { get; } = new();

    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _isDropDownOpen;
    [ObservableProperty] private bool _hasRecentItems;
    [ObservableProperty] private bool _hasDocument;

    /// <summary>Raised after an action so the box can give focus back to the document.</summary>
    public event Action? ActionCompleted;

    partial void OnQueryChanged(string value) => RefreshRecent();

    public void OnActiveDocumentChanged() => HasDocument = _activeDocument() is not null;

    [RelayCommand]
    private void ShowMatchingResults()
    {
        if (!HasQuery())
            return;

        RecentSearches.Add(Query);
        _advanced.Open(new PdfSearchOptions(Query.Trim()), run: _activeDocument() is not null);
        Finish();
    }

    [RelayCommand]
    private void OpenAdvancedSearch()
    {
        _advanced.Open(string.IsNullOrWhiteSpace(Query) ? null : _advanced.Options with { Query = Query.Trim() });
        Finish();
    }

    [RelayCommand]
    private void SearchAndHighlight()
    {
        if (_activeDocument() is not { } document)
        {
            _setStatus("Open a PDF to search and highlight");
            return;
        }

        if (!string.IsNullOrWhiteSpace(Query))
            RecentSearches.Add(Query);
        document.Search.Open(Query.Trim(), highlightAll: true);
        Finish();
    }

    [RelayCommand]
    private void UseRecent(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        Query = query;
        if (_activeDocument() is { } document)
        {
            RecentSearches.Add(query);
            document.Search.Open(query);
            Finish();
        }
        else
        {
            ShowMatchingResults();
        }
    }

    [RelayCommand]
    private void ClearRecent() => RecentSearches.Clear();

    private bool HasQuery()
    {
        if (!string.IsNullOrWhiteSpace(Query))
            return true;

        _setStatus("Type a word or phrase to search for");
        return false;
    }

    private void Finish()
    {
        IsDropDownOpen = false;
        ActionCompleted?.Invoke();
    }

    private void RefreshRecent()
    {
        var filter = Query.Trim();
        var items = RecentSearches.All
            .Where(q => filter.Length == 0 || q.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        RecentItems.Clear();
        foreach (var item in items)
            RecentItems.Add(item);
        HasRecentItems = RecentItems.Count > 0;
    }
}
