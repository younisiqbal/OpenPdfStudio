using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.ViewModels;

public partial class DocumentViewModel : ViewModelBase, IDisposable
{
    public const double MinZoom = 25;
    public const double MaxZoom = 400;
    public const double PageSpacing = 12;
    public const double ViewerPadding = 24;

    private const long CacheBudgetBytes = 256L * 1024 * 1024;
    private const long MaxRenderPixels = 16_000_000;
    private const double HoverHitTolerance = 2;
    private static readonly double[] ZoomSteps = [25, 33, 50, 67, 75, 100, 125, 150, 200, 300, 400];

    private readonly PdfDocumentSession _session;
    private readonly Action<string> _setStatus;
    private readonly Action<DocumentViewModel> _activate;
    private readonly Action<DocumentViewModel> _close;
    private readonly PageBitmapCache _cache = new(CacheBudgetBytes);
    private readonly DispatcherTimer _renderDebounce;
    private readonly HashSet<int> _wantedPages = new();

    private CancellationTokenSource _zoomCts = new();
    private bool _initialZoomApplied;
    private bool _disposed;

    private TextSelectionRange? _selection;
    private int _selectionGeneration;
    private int _pendingSelectionPage = -1;
    private Point? _pendingSelectionPoint;
    private int _pumpGeneration = -1;

    public DocumentViewModel(PdfDocumentSession session, Action<string> setStatus,
        Action<DocumentViewModel> activate, Action<DocumentViewModel> close)
    {
        _session = session;
        _setStatus = setStatus;
        _activate = activate;
        _close = close;

        FilePath = session.FilePath;
        FileName = Path.GetFileName(session.FilePath);
        Pages = new ObservableCollection<PdfPageViewModel>(
            session.PageSizes.Select((size, index) => new PdfPageViewModel(index, size)));

        _renderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _renderDebounce.Tick += (_, _) =>
        {
            _renderDebounce.Stop();
            RequestRenders();
        };

        ViewerPreferences.Changed += OnViewerPreferencesChanged;
        ApplyZoomToPages();
        if (Pages.Count > 0)
            Pages[0].IsCurrent = true;
        _pageInput = Pages.Count > 0 ? "1" : "0";
    }

    public string FilePath { get; }
    public string FileName { get; }
    public ObservableCollection<PdfPageViewModel> Pages { get; }
    public int PageCount => Pages.Count;

    public double RenderScaling { get; set; } = 1;
    public Size ViewportSize { get; private set; }
    public Vector SavedScrollOffset { get; set; }

    public Func<string, Task>? SetClipboardTextAsync { get; set; }

    /// <summary>Raised when the view should scroll so the page at this index is at the top.</summary>
    public event Action<int>? ScrollToPageRequested;

    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private double _zoomPercent = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageNumber), nameof(PageStatus))]
    [NotifyCanExecuteChangedFor(nameof(FirstPageCommand), nameof(PreviousPageCommand),
        nameof(NextPageCommand), nameof(LastPageCommand))]
    private int _currentPageIndex;

    [ObservableProperty] private string _pageInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHandTool), nameof(IsSelectTool))]
    private ViewerTool _tool = ViewerTool.Hand;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectionCommand))]
    private bool _hasSelection;

    [ObservableProperty] private bool _isThumbnailsVisible = true;

    /// <summary>View rotation in degrees clockwise (0, 90, 180, 270). The file itself is not changed.</summary>
    [ObservableProperty] private int _rotation;

    private bool IsSideways => Rotation is 90 or 270;

    [RelayCommand]
    private void ToggleThumbnails() => IsThumbnailsVisible = !IsThumbnailsVisible;

    [RelayCommand]
    private void RotateClockwise()
    {
        ClearSelection();
        Rotation = (Rotation + 90) % 360;
        foreach (var page in Pages)
            page.SetRotation(Rotation);

        _setStatus(Rotation == 0 ? "Rotation reset" : $"Rotated view {Rotation}°");
    }

    public int PageNumber => CurrentPageIndex + 1;
    public string PageStatus => $"Page {PageNumber} of {PageCount}";
    public string ZoomText => $"{Math.Round(ZoomPercent)}%";
    public bool IsHandTool => Tool == ViewerTool.Hand;
    public bool IsSelectTool => Tool == ViewerTool.Select;
    public int SelectionPageIndex => _selection?.PageIndex ?? -1;

    [RelayCommand]
    private void Activate() => _activate(this);

    [RelayCommand]
    private void Close() => _close(this);

    [RelayCommand]
    private void UseHandTool()
    {
        Tool = ViewerTool.Hand;
        ClearSelection();
    }

    [RelayCommand]
    private void UseSelectTool() => Tool = ViewerTool.Select;

    public bool IsPalmAndFistCursor => ViewerPreferences.HandCursorStyle == HandCursorStyle.PalmAndFist;
    public bool IsPalmCursor => ViewerPreferences.HandCursorStyle == HandCursorStyle.Palm;
    public bool IsMoveArrowsCursor => ViewerPreferences.HandCursorStyle == HandCursorStyle.MoveArrows;
    public bool IsPointingFingerCursor => ViewerPreferences.HandCursorStyle == HandCursorStyle.PointingFinger;

    [RelayCommand]
    private void SetHandCursorStyle(string? style)
    {
        if (!Enum.TryParse(style, out HandCursorStyle value))
            return;

        ViewerPreferences.HandCursorStyle = value;
        if (!IsHandTool)
            UseHandTool();
    }

    private void OnViewerPreferencesChanged()
    {
        OnPropertyChanged(nameof(IsPalmAndFistCursor));
        OnPropertyChanged(nameof(IsPalmCursor));
        OnPropertyChanged(nameof(IsMoveArrowsCursor));
        OnPropertyChanged(nameof(IsPointingFingerCursor));
    }

    [RelayCommand]
    private void ComingSoon(string? feature) => _setStatus($"{feature}: coming soon");

    partial void OnZoomPercentChanged(double value)
    {
        var clamped = Math.Clamp(value, MinZoom, MaxZoom);
        if (!clamped.Equals(value))
        {
            ZoomPercent = clamped;
            return;
        }

        ApplyZoomToPages();
        OnPropertyChanged(nameof(ZoomText));

        _zoomCts.Cancel();
        _zoomCts = new CancellationTokenSource();
        _renderDebounce.Stop();
        _renderDebounce.Start();
    }

    partial void OnCurrentPageIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Pages.Count)
            Pages[oldValue].IsCurrent = false;
        if (newValue >= 0 && newValue < Pages.Count)
            Pages[newValue].IsCurrent = true;

        PageInput = (newValue + 1).ToString(CultureInfo.InvariantCulture);
    }

    #region Zoom

    [RelayCommand]
    private void ZoomIn() => ZoomPercent = ZoomSteps.FirstOrDefault(s => s > ZoomPercent + 0.5, MaxZoom);

    [RelayCommand]
    private void ZoomOut() => ZoomPercent = ZoomSteps.LastOrDefault(s => s < ZoomPercent - 0.5, MinZoom);

    [RelayCommand]
    private void SetZoom(string? percent)
    {
        if (double.TryParse(percent, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            ZoomPercent = value;
    }

    [RelayCommand]
    private void FitWidth()
    {
        var fit = FitWidthPercent();
        if (fit > 0)
            ZoomPercent = fit;
    }

    [RelayCommand]
    private void FitPage()
    {
        if (Pages.Count == 0 || ViewportSize.Width <= 0 || ViewportSize.Height <= 0)
            return;

        var page = Pages[CurrentPageIndex];
        var heightPoints = IsSideways ? page.WidthPoints : page.HeightPoints;
        var byHeight = (ViewportSize.Height - ViewerPadding * 2) / (heightPoints * PdfPageViewModel.PointsToDip) * 100;
        ZoomPercent = Math.Min(FitWidthPercent(), byHeight);
    }

    [RelayCommand]
    private void ActualSize() => ZoomPercent = 100;

    public void ZoomByWheel(bool zoomIn) => ZoomPercent = zoomIn ? ZoomPercent * 1.1 : ZoomPercent / 1.1;

    public void OnViewportChanged(Size viewport)
    {
        ViewportSize = viewport;
        if (_initialZoomApplied || viewport.Width <= 0)
            return;

        _initialZoomApplied = true;
        var fit = FitWidthPercent();
        if (fit > 0)
            ZoomPercent = Math.Clamp(fit, MinZoom, 125);
    }

    private double FitWidthPercent()
    {
        if (Pages.Count == 0 || ViewportSize.Width <= 0)
            return 0;

        var widest = Pages.Max(p => IsSideways ? p.HeightPoints : p.WidthPoints) * PdfPageViewModel.PointsToDip;
        return (ViewportSize.Width - ViewerPadding * 2) / widest * 100;
    }

    private void ApplyZoomToPages()
    {
        var factor = ZoomPercent / 100;
        foreach (var page in Pages)
            page.SetZoom(factor);
    }

    #endregion

    #region Navigation

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void FirstPage() => GoToPage(0);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void PreviousPage() => GoToPage(CurrentPageIndex - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextPage() => GoToPage(CurrentPageIndex + 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void LastPage() => GoToPage(Pages.Count - 1);

    [RelayCommand]
    private void GoToPageInput()
    {
        if (int.TryParse(PageInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            GoToPage(number - 1);
        else
            PageInput = PageNumber.ToString(CultureInfo.InvariantCulture);
    }

    private bool CanGoBack() => CurrentPageIndex > 0;
    private bool CanGoForward() => CurrentPageIndex < Pages.Count - 1;

    public void GoToPage(int index)
    {
        if (Pages.Count == 0)
            return;

        index = Math.Clamp(index, 0, Pages.Count - 1);
        CurrentPageIndex = index;
        PageInput = PageNumber.ToString(CultureInfo.InvariantCulture);
        ScrollToPageRequested?.Invoke(index);
    }

    /// <summary>Called by the view as the user scrolls.</summary>
    public void SetVisiblePages(IReadOnlyCollection<int> visible, int currentIndex)
    {
        if (_disposed)
            return;

        foreach (var index in _wantedPages)
            Pages[index].IsVisibleInViewport = false;

        var wanted = new HashSet<int>();
        foreach (var index in visible)
        {
            wanted.Add(index);
            Pages[index].IsVisibleInViewport = true;
        }

        if (visible.Count > 0)
        {
            var min = visible.Min();
            var max = visible.Max();
            if (min > 0) wanted.Add(min - 1);
            if (max < Pages.Count - 1) wanted.Add(max + 1);
        }

        foreach (var index in _wantedPages)
        {
            if (!wanted.Contains(index))
                Pages[index].RenderCts?.Cancel();
        }

        _wantedPages.Clear();
        _wantedPages.UnionWith(wanted);

        if (currentIndex >= 0 && currentIndex != CurrentPageIndex)
            CurrentPageIndex = currentIndex;

        if (!_renderDebounce.IsEnabled)
            RequestRenders();
    }

    /// <summary>
    /// Called when the tab's view leaves the screen. Keeps the last visible pages so switching back is
    /// instant, and frees every other rendered page.
    /// </summary>
    public void OnViewDetached()
    {
        if (_disposed)
            return;

        ClearSelection();
        foreach (var page in Pages)
        {
            page.IsVisibleInViewport = false;
            if (!_wantedPages.Contains(page.Index) && page.Bitmap is not null)
            {
                _cache.Remove(page);
                page.ReleaseBitmap();
            }
        }
    }

    #endregion

    #region Rendering

    private void RequestRenders()
    {
        foreach (var index in _wantedPages)
        {
            var page = Pages[index];
            _ = RenderPageAsync(page, lowPriority: !page.IsVisibleInViewport);
        }
    }

    private async Task RenderPageAsync(PdfPageViewModel page, bool lowPriority)
    {
        var (width, height) = TargetPixelSize(page.DisplayWidth, page.DisplayHeight);
        if (width <= 0 || height <= 0)
            return;

        if (page.RenderedPixelWidth == width)
        {
            _cache.Touch(page);
            return;
        }

        if (page.PendingPixelWidth == width)
            return;

        page.RenderCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_zoomCts.Token);
        page.RenderCts = cts;
        page.PendingPixelWidth = width;

        try
        {
            var bitmap = await _session.RenderPageAsync(page.Index, width, height, cts.Token, lowPriority);
            if (_disposed || page.PendingPixelWidth != width)
            {
                bitmap.Dispose();
                return;
            }

            page.PendingPixelWidth = 0;
            page.SetBitmap(bitmap, width);
            _cache.Add(page, (long)width * height * 4);
        }
        catch (OperationCanceledException)
        {
            if (page.PendingPixelWidth == width)
                page.PendingPixelWidth = 0;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            page.PendingPixelWidth = 0;
            page.HasError = true;
        }
    }

    public async void RequestThumbnail(PdfPageViewModel page)
    {
        if (_disposed || page.Thumbnail is not null || page.ThumbnailCts is not null)
            return;

        var cts = new CancellationTokenSource();
        page.ThumbnailCts = cts;
        var (width, height) = TargetPixelSize(PdfPageViewModel.ThumbnailWidth, page.ThumbnailHeight);

        try
        {
            var bitmap = await _session.RenderPageAsync(page.Index, width, height, cts.Token, lowPriority: true);
            if (_disposed || page.ThumbnailCts != cts)
            {
                bitmap.Dispose();
                return;
            }

            page.Thumbnail = bitmap;
        }
        catch (Exception)
        {
            if (page.ThumbnailCts == cts)
                page.ThumbnailCts = null;
        }
    }

    public void ReleaseThumbnail(PdfPageViewModel page) => page.ReleaseThumbnail();

    private (int Width, int Height) TargetPixelSize(double displayWidth, double displayHeight)
    {
        var width = displayWidth * RenderScaling;
        var height = displayHeight * RenderScaling;
        var pixels = width * height;
        if (pixels > MaxRenderPixels)
        {
            var shrink = Math.Sqrt(MaxRenderPixels / pixels);
            width *= shrink;
            height *= shrink;
        }

        return ((int)Math.Ceiling(width), (int)Math.Ceiling(height));
    }

    #endregion

    #region Selection

    /// <summary>True when there is a character close under the point; used to switch the cursor to an I-beam.</summary>
    public async Task<bool> IsTextAtAsync(int pageIndex, Point point)
    {
        if (_disposed)
            return false;

        try
        {
            return await _session.HitTestCharAsync(pageIndex, point, HoverHitTolerance) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void BeginSelection(int pageIndex, Point point)
    {
        ClearSelection();
        _pendingSelectionPage = pageIndex;
        UpdateSelection(point);
    }

    public void UpdateSelection(Point point)
    {
        if (_pendingSelectionPage < 0)
            return;

        _pendingSelectionPoint = point;
        if (_pumpGeneration != _selectionGeneration)
            _ = PumpSelectionAsync();
    }

    public void EndSelection()
    {
        _pendingSelectionPage = -1;
    }

    public void ClearSelection()
    {
        _selectionGeneration++;
        _pendingSelectionPoint = null;
        _pendingSelectionPage = -1;

        if (_selection is { } previous && previous.PageIndex < Pages.Count)
            Pages[previous.PageIndex].SelectionRects = null;

        _selection = null;
        HasSelection = false;
    }

    private async Task PumpSelectionAsync()
    {
        var generation = _selectionGeneration;
        _pumpGeneration = generation;

        try
        {
            while (_pendingSelectionPoint is { } point && generation == _selectionGeneration)
            {
                _pendingSelectionPoint = null;
                var pageIndex = _pendingSelectionPage >= 0 ? _pendingSelectionPage : _selection?.PageIndex ?? -1;
                if (pageIndex < 0)
                    break;

                var charIndex = await _session.HitTestCharAsync(pageIndex, point);
                if (generation != _selectionGeneration || charIndex < 0)
                    continue;

                var range = _selection is { } current
                    ? current with { ActiveIndex = charIndex }
                    : new TextSelectionRange(pageIndex, charIndex, charIndex);
                _selection = range;

                if (range.AnchorIndex == range.ActiveIndex)
                {
                    Pages[pageIndex].SelectionRects = null;
                    HasSelection = false;
                    continue;
                }

                var rects = await _session.GetTextRectsAsync(pageIndex, range.Start, range.Count);
                if (generation != _selectionGeneration)
                    break;

                Pages[pageIndex].SelectionRects = rects;
                HasSelection = rects.Count > 0;
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception)
        {
            _setStatus("Text selection failed on this page");
        }
        finally
        {
            if (_pumpGeneration == generation)
                _pumpGeneration = -1;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopySelection()
    {
        if (_selection is not { } range || SetClipboardTextAsync is null)
            return;

        try
        {
            var text = await _session.GetTextAsync(range.PageIndex, range.Start, range.Count);
            await SetClipboardTextAsync(text);
            _setStatus($"Copied {text.Length} characters");
        }
        catch (Exception)
        {
            _setStatus("Could not copy the selected text");
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ViewerPreferences.Changed -= OnViewerPreferencesChanged;
        _renderDebounce.Stop();
        _zoomCts.Cancel();
        _cache.Clear();

        foreach (var page in Pages)
        {
            page.ReleaseBitmap();
            page.ReleaseThumbnail();
        }

        _session.Dispose();
    }
}
