using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.ViewModels;

public enum SearchScope
{
    CurrentDocument,
    OpenDocuments,
    Folder
}

/// <summary>The Advanced Search side panel: searches the current PDF, every open tab, or a folder of PDFs.</summary>
public partial class AdvancedSearchViewModel : ViewModelBase
{
    private const int MaxResults = 5000;

    private readonly Func<DocumentViewModel?> _activeDocument;
    private readonly Func<IReadOnlyList<DocumentViewModel>> _openDocuments;
    private readonly Func<string, Task<DocumentViewModel?>> _openDocument;
    private readonly Action<string> _setStatus;

    private CancellationTokenSource? _searchCts;
    private PdfSearchOptions? _lastOptions;
    private int _resultCount;

    public AdvancedSearchViewModel(Func<DocumentViewModel?> activeDocument,
        Func<IReadOnlyList<DocumentViewModel>> openDocuments,
        Func<string, Task<DocumentViewModel?>> openDocument,
        Action<string> setStatus)
    {
        _activeDocument = activeDocument;
        _openDocuments = openDocuments;
        _openDocument = openDocument;
        _setStatus = setStatus;
        _folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>Shows a folder picker. Set by the main window.</summary>
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public static IReadOnlyList<string> ScopeNames { get; } =
        ["In the current PDF document", "In all open PDF documents", "In a folder"];

    public static IReadOnlyList<string> MatchModeNames { get; } =
        ["Match exact word or phrase", "Match any of the words", "Match all of the words"];

    public ObservableCollection<SearchResultGroup> Results { get; } = new();

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderScope))]
    private int _scopeIndex;

    [ObservableProperty] private int _matchModeIndex;
    [ObservableProperty] private string _folderPath;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private string _query = string.Empty;

    [ObservableProperty] private bool _wholeWords;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _includePageText = true;
    [ObservableProperty] private bool _includeBookmarks;
    [ObservableProperty] private bool _includeComments;
    [ObservableProperty] private bool _includeAttachments;
    [ObservableProperty] private bool _stemming;
    [ObservableProperty] private bool _highlightAll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreLessText))]
    private bool _showOptions = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchButtonText))]
    private bool _isSearching;

    [ObservableProperty] private string _summary = string.Empty;

    public bool IsFolderScope => ScopeIndex == (int)SearchScope.Folder;
    public string MoreLessText => ShowOptions ? "Less<<" : "More>>";
    public string SearchButtonText => IsSearching ? "Stop" : "Search";

    public PdfSearchOptions Options => new(Query, (SearchMatchMode)MatchModeIndex, WholeWords, CaseSensitive, Stemming,
        IncludePageText, IncludeBookmarks, IncludeComments, IncludeAttachments);

    /// <summary>Opens the panel. When <paramref name="options"/> is given, its query and options are copied in.</summary>
    public void Open(PdfSearchOptions? options = null, bool run = false)
    {
        if (options is not null)
        {
            Query = options.Query;
            MatchModeIndex = (int)options.Mode;
            WholeWords = options.WholeWords;
            CaseSensitive = options.CaseSensitive;
            Stemming = options.Stemming;
            IncludePageText = options.IncludePageText;
            IncludeBookmarks = options.IncludeBookmarks;
            IncludeComments = options.IncludeComments;
            IncludeAttachments = options.IncludeAttachments;
        }

        if (_activeDocument() is null && ScopeIndex == (int)SearchScope.CurrentDocument)
            ScopeIndex = (int)SearchScope.Folder;
        else if (run && _activeDocument() is not null)
            ScopeIndex = (int)SearchScope.CurrentDocument;

        if (_activeDocument() is { } document && ScopeIndex != (int)SearchScope.Folder &&
            Path.GetDirectoryName(document.FilePath) is { Length: > 0 } folder)
            FolderPath = folder;

        IsOpen = true;
        if (run)
            _ = RunAsync();
    }

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    [RelayCommand]
    private void Close()
    {
        StopSearch();
        IsOpen = false;
    }

    [RelayCommand]
    private Task SearchOrStop()
    {
        if (IsSearching)
        {
            StopSearch();
            return Task.CompletedTask;
        }

        return RunAsync();
    }

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (PickFolderAsync is null)
            return;

        var folder = await PickFolderAsync();
        if (!string.IsNullOrEmpty(folder))
        {
            FolderPath = folder;
            ScopeIndex = (int)SearchScope.Folder;
        }
    }

    [RelayCommand]
    private async Task OpenResult(SearchResultItem? item)
    {
        if (item is null || _lastOptions is not { } options)
            return;

        if (item.Hit.Kind == SearchResultKind.Attachment)
        {
            _setStatus($"Attachment \"{item.Hit.Snippet.Trim('…')}\" in {item.Group.FileName}");
            return;
        }

        var document = await _openDocument(item.Group.FilePath);
        if (document is null)
            return;

        var hits = item.Group.Items.Select(i => i.Hit).Where(h => h.PageIndex >= 0).ToList();
        document.Search.ShowHits(options, hits, item.Hit, HighlightAll);
    }

    private void StopSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        IsSearching = false;
    }

    private async Task RunAsync()
    {
        StopSearch();
        Results.Clear();
        _resultCount = 0;

        var options = Options;
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            Summary = "Type a word or phrase to search for.";
            return;
        }

        if (!options.IncludePageText && !options.IncludeBookmarks && !options.IncludeComments && !options.IncludeAttachments)
        {
            Summary = "Choose at least one place to search: page text, bookmarks, comments, or attachments.";
            return;
        }

        RecentSearches.Add(options.Query);
        _lastOptions = options;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearching = true;
        ShowOptions = false;

        var searched = 0;
        var skipped = 0;
        var limited = false;

        try
        {
            var targets = BuildTargets();
            if (targets.Count == 0)
            {
                Summary = (SearchScope)ScopeIndex switch
                {
                    SearchScope.Folder => "No PDF files were found in that folder.",
                    _ => "Open a PDF first, or choose \"In a folder\"."
                };
                return;
            }

            for (int i = 0; i < targets.Count && !limited; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var (path, openSession) = targets[i];
                var fileLabel = targets.Count > 1 ? $"file {i + 1} of {targets.Count}" : Path.GetFileName(path);
                Summary = $"Searching {fileLabel}...";

                var session = openSession;
                if (session is null)
                {
                    try
                    {
                        session = await PdfDocumentSession.OpenAsync(path, null);
                    }
                    catch (Exception)
                    {
                        skipped++;
                        continue;
                    }
                }

                try
                {
                    SearchResultGroup? group = null;
                    var pageProgress = new Progress<(int Done, int Total)>(p =>
                    {
                        if (_searchCts == cts && targets.Count == 1)
                            Summary = $"Searching page {p.Done} of {p.Total}...";
                    });

                    await foreach (var hit in PdfSearchEngine.SearchAsync(session, options, pageProgress, cts.Token))
                    {
                        if (group is null)
                        {
                            group = new SearchResultGroup(path);
                            Results.Add(group);
                        }

                        group.Add(new SearchResultItem(group, hit));
                        if (++_resultCount >= MaxResults)
                        {
                            limited = true;
                            break;
                        }
                    }

                    searched++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skipped++;
                }
                finally
                {
                    if (openSession is null)
                        session.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Summary = $"Stopped. {Describe(searched, skipped)}";
            return;
        }
        finally
        {
            if (_searchCts == cts)
            {
                _searchCts = null;
                IsSearching = false;
            }
        }

        Summary = (limited ? $"Showing the first {MaxResults} results. " : "") + Describe(searched, skipped);
        _setStatus(Summary);
    }

    private string Describe(int searched, int skipped)
    {
        var text = _resultCount == 0
            ? $"No results for \"{_lastOptions?.Query}\""
            : $"{_resultCount} result{(_resultCount == 1 ? "" : "s")} in {Results.Count} document{(Results.Count == 1 ? "" : "s")}";

        if (searched > 1 || skipped > 0)
            text += $" ({searched} searched)";
        if (skipped > 0)
            text += $". Skipped {skipped} file{(skipped == 1 ? "" : "s")} (password protected or unreadable)";

        return text + ".";
    }

    /// <summary>Files to search, with the session to reuse when the file is already open in a tab.</summary>
    private List<(string Path, PdfDocumentSession? Session)> BuildTargets()
    {
        switch ((SearchScope)ScopeIndex)
        {
            case SearchScope.CurrentDocument:
                return _activeDocument() is { } active ? [(active.FilePath, active.Session)] : [];

            case SearchScope.OpenDocuments:
                return _openDocuments().Select(d => (d.FilePath, (PdfDocumentSession?)d.Session)).ToList();

            default:
                if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
                    return [];

                var open = _openDocuments().ToDictionary(d => d.FilePath, d => d.Session, StringComparer.OrdinalIgnoreCase);
                var files = Directory.EnumerateFiles(FolderPath, "*.pdf", new EnumerationOptions
                {
                    RecurseSubdirectories = IncludeSubfolders,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive
                });

                return files
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => (f, open.TryGetValue(f, out var s) ? s : null))
                    .ToList();
        }
    }
}

public sealed class SearchResultGroup(string filePath) : ObservableObject
{
    private bool _isExpanded = true;

    public string FilePath { get; } = filePath;
    public string FileName { get; } = Path.GetFileName(filePath);
    public string? Folder { get; } = Path.GetDirectoryName(filePath);
    public ObservableCollection<SearchResultItem> Items { get; } = new();
    public string CountText => Items.Count == 1 ? "1 result" : $"{Items.Count} results";

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public void Add(SearchResultItem item)
    {
        Items.Add(item);
        OnPropertyChanged(nameof(CountText));
    }
}

public sealed class SearchResultItem
{
    public SearchResultItem(SearchResultGroup group, PdfSearchHit hit)
    {
        Group = group;
        Hit = hit;

        var start = Math.Clamp(hit.SnippetMatchStart, 0, hit.Snippet.Length);
        var length = Math.Clamp(hit.SnippetMatchLength, 0, hit.Snippet.Length - start);
        Before = hit.Snippet[..start];
        Match = hit.Snippet.Substring(start, length);
        After = hit.Snippet[(start + length)..];
    }

    public SearchResultGroup Group { get; }
    public PdfSearchHit Hit { get; }
    public string Before { get; }
    public string Match { get; }
    public string After { get; }

    public bool IsPageText => Hit.Kind == SearchResultKind.PageText;
    public bool IsBookmark => Hit.Kind == SearchResultKind.Bookmark;
    public bool IsComment => Hit.Kind == SearchResultKind.Comment;
    public bool IsAttachment => Hit.Kind == SearchResultKind.Attachment;

    public string Location => Hit.Kind switch
    {
        SearchResultKind.Bookmark => Hit.PageIndex >= 0 ? $"Bookmark, page {Hit.PageIndex + 1}" : "Bookmark",
        SearchResultKind.Comment => $"Comment, page {Hit.PageIndex + 1}",
        SearchResultKind.Attachment => "Attachment",
        _ => $"Page {Hit.PageIndex + 1}"
    };
}
