using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;

namespace OpenPdfStudio.ViewModels;

/// <summary>One row of the single-page or two-page layout: a page, and the page beside it in two-page view.</summary>
public sealed class PageSpread(PdfPageViewModel left, PdfPageViewModel? right)
{
    public PdfPageViewModel Left { get; } = left;
    public PdfPageViewModel? Right { get; } = right;
    public bool HasRight => Right is not null;
}

public partial class DocumentViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContinuousLayout), nameof(IsSinglePageLayout), nameof(IsTwoPageLayout),
        nameof(UsesSpreads))]
    private PageLayoutMode _layoutMode;

    /// <summary>The rows shown in single-page and two-page layout. Empty in continuous layout.</summary>
    public ObservableCollection<PageSpread> Spreads { get; } = new();

    public bool IsContinuousLayout => LayoutMode == PageLayoutMode.Continuous;
    public bool IsSinglePageLayout => LayoutMode == PageLayoutMode.SinglePage;
    public bool IsTwoPageLayout => LayoutMode == PageLayoutMode.TwoPage;
    public bool UsesSpreads => !IsContinuousLayout;

    [RelayCommand]
    private void SetLayout(string? mode)
    {
        if (Enum.TryParse(mode, out PageLayoutMode value))
            LayoutMode = value;
    }

    partial void OnLayoutModeChanged(PageLayoutMode value)
    {
        ViewerPreferences.PageLayout = value;
        ClearSelection();
        RebuildSpreads();
        NotifyNavigationChanged();
        if (IsFitWidth)
            FitWidth();
        else if (IsFitPage)
            FitPage();

        _setStatus(value switch
        {
            PageLayoutMode.SinglePage => "Single page view",
            PageLayoutMode.TwoPage => "Two-page view",
            _ => "Continuous scroll"
        });
    }

    private void RebuildSpreads()
    {
        Spreads.Clear();
        if (Pages.Count == 0)
            return;

        if (IsSinglePageLayout)
        {
            Spreads.Add(new PageSpread(Pages[Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1)], null));
            return;
        }

        if (!IsTwoPageLayout)
            return;

        Spreads.Add(new PageSpread(Pages[0], null));
        for (var i = 1; i < Pages.Count; i += 2)
            Spreads.Add(new PageSpread(Pages[i], i + 1 < Pages.Count ? Pages[i + 1] : null));
    }

    /// <summary>In single-page layout, swaps the one shown page for the current one.</summary>
    private void SyncSinglePage()
    {
        if (!IsSinglePageLayout || Pages.Count == 0)
            return;

        var page = Pages[Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1)];
        if (Spreads.Count == 1 && ReferenceEquals(Spreads[0].Left, page))
            return;

        Spreads.Clear();
        Spreads.Add(new PageSpread(page, null));
    }

    /// <summary>The row a page sits in: its own index in continuous layout, its spread otherwise.</summary>
    public int RowOfPage(int pageIndex) => LayoutMode switch
    {
        PageLayoutMode.SinglePage => 0,
        PageLayoutMode.TwoPage => pageIndex <= 0 ? 0 : (pageIndex + 1) / 2,
        _ => pageIndex
    };

    private static int FirstPageOfSpread(int spread) => spread <= 0 ? 0 : spread * 2 - 1;

    private int NextPageIndex() =>
        IsTwoPageLayout ? FirstPageOfSpread(RowOfPage(CurrentPageIndex) + 1) : CurrentPageIndex + 1;

    private int PreviousPageIndex() =>
        IsTwoPageLayout ? FirstPageOfSpread(RowOfPage(CurrentPageIndex) - 1) : CurrentPageIndex - 1;

    private void NotifyNavigationChanged()
    {
        FirstPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The zoom at which the widest row fills the viewport, allowing for the gap between two pages.</summary>
    private double FitWidthPercent()
    {
        if (Pages.Count == 0 || ViewportSize.Width <= 0)
            return 0;

        double Width(PdfPageViewModel page) => (IsSideways ? page.HeightPoints : page.WidthPoints) * PdfPageViewModel.PointsToDip;
        double Fit(double pagesWidth, double gap) => (ViewportSize.Width - ViewerPadding * 2 - gap) / pagesWidth * 100;

        if (!IsTwoPageLayout)
            return Fit(Pages.Max(Width), 0);

        return Spreads.Min(s => s.Right is { } right
            ? Fit(Width(s.Left) + Width(right), PageSpacing)
            : Fit(Width(s.Left), 0));
    }
}
