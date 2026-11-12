using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services;

namespace OpenPdfStudio.ViewModels;

public enum SidePanel
{
    Pages,
    Bookmarks
}

/// <summary>One row in the bookmarks pane: an outline entry from the PDF, or a bookmark the user added.</summary>
public sealed class BookmarkItemViewModel(string title, int pageIndex, int depth, bool isUser)
{
    private const double IndentPerLevel = 14;

    public string Title { get; } = title;
    public int PageIndex { get; } = pageIndex;
    public bool IsUser { get; } = isUser;
    public bool HasPage => PageIndex >= 0;
    public string PageLabel => HasPage ? (PageIndex + 1).ToString() : "";
    public Thickness Indent { get; } = new(depth * IndentPerLevel, 0, 0, 0);
}

public partial class DocumentViewModel
{
    private const int MaxBookmarkTitleLength = 80;

    private bool _outlineLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThumbnailPane), nameof(ShowBookmarkPane), nameof(IsPagesOpen), nameof(IsBookmarksOpen))]
    private SidePanel _sidePanel = SidePanel.Pages;

    [ObservableProperty] private bool _hasOutline;
    [ObservableProperty] private bool _hasUserBookmarks;
    [ObservableProperty] private bool _isOutlineLoading;

    public ObservableCollection<BookmarkItemViewModel> OutlineItems { get; } = new();
    public ObservableCollection<BookmarkItemViewModel> UserBookmarkItems { get; } = new();

    public bool ShowThumbnailPane => ShowSidePanel && SidePanel == SidePanel.Pages;
    public bool ShowBookmarkPane => ShowSidePanel && SidePanel == SidePanel.Bookmarks;
    public bool IsPagesOpen => IsThumbnailsVisible && SidePanel == SidePanel.Pages;
    public bool IsBookmarksOpen => IsThumbnailsVisible && SidePanel == SidePanel.Bookmarks;

    [RelayCommand]
    private void ToggleThumbnails() => ToggleSidePanel(SidePanel.Pages);

    [RelayCommand]
    private void ToggleBookmarks() => ToggleSidePanel(SidePanel.Bookmarks);

    [RelayCommand]
    private void CloseSidePanel() => IsThumbnailsVisible = false;

    private void ToggleSidePanel(SidePanel panel)
    {
        if (IsThumbnailsVisible && SidePanel == panel)
        {
            IsThumbnailsVisible = false;
            return;
        }

        SidePanel = panel;
        IsThumbnailsVisible = true;
        if (panel == SidePanel.Bookmarks)
            _ = LoadOutlineAsync();
    }

    [RelayCommand]
    private void OpenBookmark(BookmarkItemViewModel? item)
    {
        if (item is { HasPage: true })
            GoToPage(item.PageIndex);
    }

    /// <summary>Adds a bookmark on the selected text's page (titled with the text), or on the current page.</summary>
    [RelayCommand]
    private async Task AddBookmark()
    {
        var pageIndex = SelectionPageIndex >= 0 ? SelectionPageIndex : CurrentPageIndex;
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            return;

        var title = BookmarkTitle(await GetSelectedTextAsync()) ?? $"Page {pageIndex + 1}";
        var items = UserBookmarkItems.Select(i => new UserBookmark(i.Title, i.PageIndex))
            .Append(new UserBookmark(title, pageIndex));
        UserBookmarks.Set(FilePath, items);
        LoadUserBookmarks();

        ClearSelection();
        SidePanel = SidePanel.Bookmarks;
        IsThumbnailsVisible = true;
        _ = LoadOutlineAsync();
        _setStatus($"Bookmarked page {pageIndex + 1}: {title}");
    }

    [RelayCommand]
    private void RemoveBookmark(BookmarkItemViewModel? item)
    {
        if (item is not { IsUser: true })
            return;

        UserBookmarks.Set(FilePath, UserBookmarkItems.Where(i => !ReferenceEquals(i, item))
            .Select(i => new UserBookmark(i.Title, i.PageIndex)));
        LoadUserBookmarks();
        _setStatus($"Removed bookmark: {item.Title}");
    }

    private void LoadUserBookmarks()
    {
        UserBookmarkItems.Clear();
        foreach (var bookmark in UserBookmarks.For(FilePath))
        {
            if (bookmark.PageIndex >= 0 && bookmark.PageIndex < Pages.Count)
                UserBookmarkItems.Add(new BookmarkItemViewModel(bookmark.Title, bookmark.PageIndex, 0, isUser: true));
        }

        HasUserBookmarks = UserBookmarkItems.Count > 0;
    }

    private async Task LoadOutlineAsync(bool reload = false)
    {
        if ((_outlineLoaded && !reload) || IsOutlineLoading || _disposed)
            return;

        IsOutlineLoading = true;
        try
        {
            var bookmarks = await _session.GetBookmarksAsync();
            OutlineItems.Clear();
            foreach (var bookmark in bookmarks)
                OutlineItems.Add(new BookmarkItemViewModel(bookmark.Title, bookmark.PageIndex, bookmark.Depth, isUser: false));
            HasOutline = OutlineItems.Count > 0;
            _outlineLoaded = true;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            _setStatus("Could not read this document's bookmarks");
        }
        finally
        {
            IsOutlineLoading = false;
        }
    }

    private static string? BookmarkTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var title = Regex.Replace(text, @"\s+", " ").Trim();
        return title.Length <= MaxBookmarkTitleLength ? title : title[..MaxBookmarkTitleLength].TrimEnd() + "…";
    }
}
