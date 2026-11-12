using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.ViewModels;

/// <summary>
/// Organize mode: every page as a tile that can be reordered, rotated, deleted, or joined by blank pages.
/// Nothing touches the PDF until Apply.
/// </summary>
public partial class DocumentViewModel
{
    private const double OrganizeThumbnailPixels = 400;
    private CancellationTokenSource? _organizeCts;
    private int _organizeAnchor = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReading), nameof(ShowSidePanel), nameof(ShowThumbnailPane), nameof(ShowBookmarkPane),
        nameof(ShowBottomBar))]
    private bool _isOrganizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOrganizeGrid), nameof(IsOrganizeList))]
    private OrganizeLayout _organizeLayout = ViewerPreferences.OrganizeLayout;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOrganizeSmall), nameof(IsOrganizeMedium), nameof(IsOrganizeLarge), nameof(OrganizeBox))]
    private OrganizeTileSize _organizeTileSize = ViewerPreferences.OrganizeTileSize;

    [ObservableProperty] private string _organizeSummary = string.Empty;
    [ObservableProperty] private bool _hasOrganizeSelection;
    [ObservableProperty] private bool _isOrganizeChanged;
    [ObservableProperty] private bool _isApplyingOrganize;

    public ObservableCollection<OrganizePageItem> OrganizeItems { get; } = new();

    /// <summary>Writes a new page list into the PDF and swaps in a fresh view. Returns false when it failed.</summary>
    public Func<IReadOnlyList<PageArrangement>, Task<bool>>? ApplyArrangementAsync { get; set; }

    /// <summary>Raised after the organize items change order, so the view can keep the focused tile in sight.</summary>
    public event Action<OrganizePageItem>? OrganizeFocusChanged;

    public bool IsReading => !IsOrganizing;
    public bool IsOrganizeGrid => OrganizeLayout == OrganizeLayout.Grid;
    public bool IsOrganizeList => OrganizeLayout == OrganizeLayout.List;
    public bool IsOrganizeSmall => OrganizeTileSize == OrganizeTileSize.Small;
    public bool IsOrganizeMedium => OrganizeTileSize == OrganizeTileSize.Medium;
    public bool IsOrganizeLarge => OrganizeTileSize == OrganizeTileSize.Large;

    public double OrganizeBox => OrganizeTileSize switch
    {
        OrganizeTileSize.Small => 96,
        OrganizeTileSize.Large => 220,
        _ => 140
    };

    public OrganizePageItem? FocusedOrganizeItem => OrganizeItems.FirstOrDefault(i => i.IsFocused);

    [RelayCommand]
    public void EnterOrganize()
    {
        if (IsOrganizing)
            return;

        ClearSelection();
        AnnotationTool = AnnotationTool.None;
        ReleaseOrganizeItems();
        foreach (var (size, index) in _session.PageSizes.Select((size, index) => (size, index)))
            OrganizeItems.Add(new OrganizePageItem(index, size) { Box = OrganizeBox });

        _organizeAnchor = -1;
        if (OrganizeItems.Count > 0)
        {
            var current = OrganizeItems[Math.Clamp(CurrentPageIndex, 0, OrganizeItems.Count - 1)];
            current.IsSelected = current.IsFocused = true;
            _organizeAnchor = current.Position;
        }

        RefreshOrganize();
        IsOrganizing = true;
        _ = RenderOrganizeThumbnailsAsync();
        _setStatus("Organize pages: drag to reorder. Apply writes the changes into the PDF.");
    }

    [RelayCommand]
    public void CancelOrganize()
    {
        if (!IsOrganizing || IsApplyingOrganize)
            return;

        IsOrganizing = false;
        ReleaseOrganizeItems();
        _setStatus("Organize cancelled");
    }

    [RelayCommand]
    private async Task ApplyOrganize()
    {
        if (!IsOrganizing || IsApplyingOrganize)
            return;

        if (!IsOrganizeChanged)
        {
            CancelOrganize();
            _setStatus("No page changes");
            return;
        }

        if (ApplyArrangementAsync is null)
            return;

        var pages = OrganizeItems
            .Select(i => i.IsBlank
                ? PageArrangement.BlankPage(i.PageSize.Width, i.PageSize.Height, i.Turns)
                : new PageArrangement(i.SourceIndex, i.Turns))
            .ToList();

        IsApplyingOrganize = true;
        try
        {
            if (!await ApplyArrangementAsync(pages))
                return;
        }
        finally
        {
            IsApplyingOrganize = false;
        }

        IsOrganizing = false;
        ReleaseOrganizeItems();
    }

    [RelayCommand]
    private void RotateOrganize(string? direction)
    {
        var turns = direction == "Left" ? -1 : 1;
        foreach (var item in OrganizeItems.Where(i => i.IsSelected))
            item.Turns = ((item.Turns + turns) % 4 + 4) % 4;
        RefreshOrganize();
    }

    [RelayCommand]
    public void DeleteOrganizeSelection()
    {
        var selected = OrganizeItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        if (selected.Count == OrganizeItems.Count)
        {
            OrganizeSummary = "A PDF needs at least one page. Leave at least one page unselected.";
            return;
        }

        var firstIndex = OrganizeItems.IndexOf(selected[0]);
        foreach (var item in selected)
        {
            item.Thumbnail?.Dispose();
            OrganizeItems.Remove(item);
        }

        var next = OrganizeItems[Math.Min(firstIndex, OrganizeItems.Count - 1)];
        SelectOrganizeOnly(next);
    }

    [RelayCommand]
    private void InsertBlankPage()
    {
        if (OrganizeItems.Count == 0)
            return;

        var first = OrganizeItems.FirstOrDefault(i => i.IsSelected);
        var index = first is null ? OrganizeItems.Count : OrganizeItems.IndexOf(first);
        var model = OrganizeItems[Math.Max(0, index - 1)];
        var blank = new OrganizePageItem(PageArrangement.Blank, model.DisplaySize) { Box = OrganizeBox };
        OrganizeItems.Insert(index, blank);
        SelectOrganizeOnly(blank);
    }

    [RelayCommand]
    public void SelectAllOrganize()
    {
        foreach (var item in OrganizeItems)
            item.IsSelected = true;
        RefreshOrganize();
    }

    [RelayCommand]
    private void SetOrganizeLayout(string? layout)
    {
        if (Enum.TryParse(layout, out OrganizeLayout value))
            OrganizeLayout = value;
    }

    [RelayCommand]
    private void SetOrganizeTileSize(string? size)
    {
        if (Enum.TryParse(size, out OrganizeTileSize value))
            OrganizeTileSize = value;
    }

    partial void OnOrganizeLayoutChanged(OrganizeLayout value) => ViewerPreferences.OrganizeLayout = value;

    partial void OnOrganizeTileSizeChanged(OrganizeTileSize value)
    {
        ViewerPreferences.OrganizeTileSize = value;
        foreach (var item in OrganizeItems)
            item.Box = OrganizeBox;
    }

    /// <summary>Click: select only this page. Ctrl toggles it; Shift selects the range from the last clicked page.</summary>
    public void ClickOrganizeItem(OrganizePageItem item, bool toggle, bool range)
    {
        var index = OrganizeItems.IndexOf(item);
        if (index < 0)
            return;

        if (range && _organizeAnchor >= 0)
        {
            SelectOrganizeRange(_organizeAnchor, index, keep: toggle);
        }
        else if (toggle)
        {
            item.IsSelected = !item.IsSelected;
            _organizeAnchor = index;
        }
        else
        {
            foreach (var other in OrganizeItems)
                other.IsSelected = other == item;
            _organizeAnchor = index;
        }

        SetOrganizeFocus(item);
        RefreshOrganize();
    }

    /// <summary>Arrow keys: move the focus by <paramref name="delta"/> tiles. Shift extends the selection.</summary>
    public void MoveOrganizeFocus(int delta, bool extend)
    {
        if (OrganizeItems.Count == 0)
            return;

        var current = FocusedOrganizeItem is { } focused ? OrganizeItems.IndexOf(focused) : 0;
        var target = Math.Clamp(current + delta, 0, OrganizeItems.Count - 1);
        var item = OrganizeItems[target];

        if (extend)
        {
            if (_organizeAnchor < 0)
                _organizeAnchor = current;
            SelectOrganizeRange(_organizeAnchor, target, keep: false);
        }
        else
        {
            foreach (var other in OrganizeItems)
                other.IsSelected = other == item;
            _organizeAnchor = target;
        }

        SetOrganizeFocus(item);
        RefreshOrganize();
    }

    /// <summary>Marquee: select the pages inside the box, plus <paramref name="keep"/> (the selection before a Ctrl-drag).</summary>
    public void SelectOrganizeItems(IReadOnlyCollection<OrganizePageItem> items, IReadOnlyCollection<OrganizePageItem> keep)
    {
        foreach (var item in OrganizeItems)
            item.IsSelected = items.Contains(item) || keep.Contains(item);

        if (items.Count > 0)
        {
            var first = OrganizeItems.First(items.Contains);
            _organizeAnchor = OrganizeItems.IndexOf(first);
            SetOrganizeFocus(first);
        }

        RefreshOrganize();
    }

    public void ClearOrganizeSelection()
    {
        foreach (var item in OrganizeItems)
            item.IsSelected = false;
        RefreshOrganize();
    }

    /// <summary>Drag and drop: moves the selected pages so they start at <paramref name="insertIndex"/> (counted before the move).</summary>
    public void MoveOrganizeSelection(int insertIndex)
    {
        var moving = OrganizeItems.Where(i => i.IsSelected).ToList();
        if (moving.Count == 0)
            return;

        var before = OrganizeItems.Take(Math.Clamp(insertIndex, 0, OrganizeItems.Count)).Count(i => i.IsSelected);
        var target = Math.Clamp(insertIndex - before, 0, OrganizeItems.Count - moving.Count);

        var rest = OrganizeItems.Where(i => !i.IsSelected).ToList();
        rest.InsertRange(target, moving);
        if (rest.SequenceEqual(OrganizeItems))
            return;

        for (var i = 0; i < rest.Count; i++)
        {
            var from = OrganizeItems.IndexOf(rest[i]);
            if (from != i)
                OrganizeItems.Move(from, i);
        }

        _organizeAnchor = OrganizeItems.IndexOf(moving[0]);
        SetOrganizeFocus(moving[0]);
        RefreshOrganize();
    }

    private void SelectOrganizeOnly(OrganizePageItem item)
    {
        foreach (var other in OrganizeItems)
            other.IsSelected = other == item;
        _organizeAnchor = OrganizeItems.IndexOf(item);
        SetOrganizeFocus(item);
        RefreshOrganize();
    }

    private void SelectOrganizeRange(int from, int to, bool keep)
    {
        var (low, high) = (Math.Min(from, to), Math.Max(from, to));
        for (var i = 0; i < OrganizeItems.Count; i++)
        {
            if (i >= low && i <= high)
                OrganizeItems[i].IsSelected = true;
            else if (!keep)
                OrganizeItems[i].IsSelected = false;
        }
    }

    private void SetOrganizeFocus(OrganizePageItem item)
    {
        foreach (var other in OrganizeItems)
            other.IsFocused = other == item;
        OrganizeFocusChanged?.Invoke(item);
    }

    private void RefreshOrganize()
    {
        for (var i = 0; i < OrganizeItems.Count; i++)
            OrganizeItems[i].Position = i;

        var selected = OrganizeItems.Count(i => i.IsSelected);
        var blanks = OrganizeItems.Count(i => i.IsBlank);
        var deleted = PageCount - OrganizeItems.Count(i => !i.IsBlank);

        HasOrganizeSelection = selected > 0;
        IsOrganizeChanged = blanks > 0 || deleted > 0 ||
                            OrganizeItems.Where((item, index) => item.SourceIndex != index || item.Turns != 0).Any();

        var parts = new List<string> { $"{OrganizeItems.Count} page{(OrganizeItems.Count == 1 ? "" : "s")}" };
        if (selected > 0) parts.Add($"{selected} selected");
        if (deleted > 0) parts.Add($"{deleted} deleted");
        if (blanks > 0) parts.Add($"{blanks} blank added");
        OrganizeSummary = string.Join(", ", parts) +
                          (selected == 0 ? ". Click a page, drag a box, or use the arrow keys to select." : "");
    }

    private async Task RenderOrganizeThumbnailsAsync()
    {
        _organizeCts?.Cancel();
        var cts = _organizeCts = new CancellationTokenSource();
        foreach (var item in OrganizeItems.Where(i => !i.IsBlank).ToList())
        {
            if (cts.IsCancellationRequested)
                return;

            var scale = OrganizeThumbnailPixels / Math.Max(1, Math.Max(item.PageSize.Width, item.PageSize.Height));
            var width = Math.Max(1, (int)Math.Round(item.PageSize.Width * scale));
            var height = Math.Max(1, (int)Math.Round(item.PageSize.Height * scale));
            try
            {
                var bitmap = await _session.RenderPageAsync(item.SourceIndex, width, height, cts.Token, lowPriority: true);
                if (cts.IsCancellationRequested || !OrganizeItems.Contains(item))
                    bitmap.Dispose();
                else
                    item.Thumbnail = bitmap;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
            }
        }
    }

    private void ReleaseOrganizeItems()
    {
        _organizeCts?.Cancel();
        _organizeCts = null;
        foreach (var item in OrganizeItems)
            item.Thumbnail?.Dispose();
        OrganizeItems.Clear();
    }
}
