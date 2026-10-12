using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Pdf;
using OpenPdfStudio.ViewModels;
using OpenPdfStudio.Views.Controls;

namespace OpenPdfStudio.Views;

public partial class DocumentView : UserControl
{
    private const double PopupGap = 8;
    private const double HoverSlop = 4;
    private const double ArrowScrollStep = 60;

    private readonly DispatcherTimer _popupHideTimer;
    private DocumentViewModel? _vm;

    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;

    private bool _isSelecting;
    private int _selectionPageIndex = -1;
    private Point _lastPointer;

    private bool _overText;
    private bool _hoverProbeRunning;
    private int _hoverGeneration;
    private (int PageIndex, Point Point)? _pendingHover;

    private (int PageIndex, double Fraction)? _zoomAnchor;

    private bool _hasContextAnchor;
    private (PdfPageViewModel Page, Point PointInPoints)? _contextAnchor;

    public DocumentView()
    {
        InitializeComponent();

        _popupHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _popupHideTimer.Tick += (_, _) =>
        {
            _popupHideTimer.Stop();
            if (!SelectionPopupHost.IsPointerOver)
                HidePopup();
        };

        PageScroller.ScrollChanged += OnScrollChanged;
        PageScroller.AddHandler(PointerPressedEvent, OnPagePointerPressed, RoutingStrategies.Tunnel);
        PageScroller.AddHandler(PointerMovedEvent, OnPagePointerMoved, RoutingStrategies.Tunnel);
        PageScroller.AddHandler(PointerReleasedEvent, OnPagePointerReleased, RoutingStrategies.Tunnel);
        PageScroller.AddHandler(PointerWheelChangedEvent, OnPageWheel, RoutingStrategies.Tunnel);
        PageScroller.PointerExited += (_, _) =>
        {
            ResetTextHover();
            StartPopupHide();
            UpdateRulerPointer(null);
        };
        PageScroller.AddHandler(PointerMovedEvent, (_, e) => UpdateRulerPointer(e.GetPosition(PageScroller)),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        PageArea.AddHandler(ContextRequestedEvent, OnPageAreaContextRequested, RoutingStrategies.Tunnel);
        PageContextMenu.Closed += OnPageContextMenuClosed;

        ThumbnailHost.ContainerPrepared += (_, e) =>
        {
            if (_vm is not null && e.Index >= 0 && e.Index < _vm.Pages.Count)
                _vm.RequestThumbnail(_vm.Pages[e.Index]);
        };
        ThumbnailHost.ContainerClearing += (_, e) =>
        {
            if (e.Container.DataContext is PdfPageViewModel page)
                _vm?.ReleaseThumbnail(page);
        };

        SelectionPopupHost.PointerEntered += (_, _) => _popupHideTimer.Stop();
        SelectionPopupHost.PointerExited += (_, _) => StartPopupHide();
        SelectionPopupHost.AddHandler(Button.ClickEvent, (_, _) => HidePopup(), RoutingStrategies.Bubble, true);

        FindBarHost.ReturnFocusRequested += () => Focus();

        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ViewerPreferences.Changed += UpdateCursor;
        UpdateCursor();
        if (_vm is not null && TopLevel.GetTopLevel(this) is { } topLevel)
            _vm.RenderScaling = topLevel.RenderScaling;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ViewerPreferences.Changed -= UpdateCursor;
        ResetTextHover();
        HidePopup();
        _vm?.OnViewDetached();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ScrollToPageRequested -= OnScrollToPageRequested;
            _vm.ScrollToMatchRequested -= OnScrollToMatchRequested;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.SetClipboardTextAsync = null;
            _vm.ReadClipboardAsync = null;
            _vm.GetPasteAnchor = null;
            _vm.OnViewDetached();
        }

        _vm = DataContext as DocumentViewModel;
        ResetTextHover();
        HidePopup();
        if (_vm is null)
            return;

        _vm.ScrollToPageRequested += OnScrollToPageRequested;
        _vm.ScrollToMatchRequested += OnScrollToMatchRequested;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.SetClipboardTextAsync = async text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };
        _vm.ReadClipboardAsync = ReadClipboardAsync;
        _vm.GetPasteAnchor = GetPasteAnchor;

        if (TopLevel.GetTopLevel(this) is { } topLevel)
            _vm.RenderScaling = topLevel.RenderScaling;

        UpdateCursor();

        var offset = _vm.SavedScrollOffset;
        Dispatcher.UIThread.Post(() =>
        {
            PageScroller.Offset = offset;
            UpdateVisiblePages();
            Focus();
        }, DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.ZoomPercent):
                CaptureZoomAnchor();
                HidePopup();
                break;
            case nameof(DocumentViewModel.Tool):
                ResetTextHover();
                HidePopup();
                break;
            case nameof(DocumentViewModel.CurrentPageIndex) when _vm is not null:
                ThumbnailHost.ScrollIntoView(_vm.CurrentPageIndex);
                UpdateRulers();
                break;
            case nameof(DocumentViewModel.Rotation) when _vm is not null:
                HidePopup();
                var index = _vm.CurrentPageIndex;
                Dispatcher.UIThread.Post(() => ScrollToPage(index, 0), DispatcherPriority.Background);
                break;
            case nameof(DocumentViewModel.HasSelection) when _vm is { HasSelection: false }:
                HidePopup();
                break;
            case nameof(DocumentViewModel.IsRulerVisible):
                Dispatcher.UIThread.Post(UpdateRulers, DispatcherPriority.Background);
                break;
        }
    }

    private void UpdateCursor()
    {
        if (_vm is null)
            return;

        var style = ViewerPreferences.HandCursorStyle;
        var cursor = _vm.IsSelectTool || _isSelecting || _overText
            ? AppCursors.Ibeam
            : _isPanning
                ? AppCursors.GetDrag(style)
                : AppCursors.GetHover(style);

        if (!ReferenceEquals(PageScroller.Cursor, cursor))
            PageScroller.Cursor = cursor;
    }

    private void ResetTextHover()
    {
        _hoverGeneration++;
        _pendingHover = null;
        _overText = false;
        UpdateCursor();
    }

    /// <summary>
    /// Asks PDFium whether there is text under the pointer. Only one probe runs at a time; moves made
    /// while it runs are coalesced into the latest position.
    /// </summary>
    private async void ProbeTextUnderPointer(Point positionInScroller)
    {
        if (_vm is not { } vm)
            return;

        if (HitTestPage(positionInScroller) is not { } hit)
        {
            ResetTextHover();
            return;
        }

        _pendingHover = (hit.Page.Index, hit.PointInPoints);
        if (_hoverProbeRunning)
            return;

        _hoverProbeRunning = true;
        try
        {
            while (_pendingHover is { } pending)
            {
                _pendingHover = null;
                var generation = _hoverGeneration;
                var overText = await vm.IsTextAtAsync(pending.PageIndex, pending.Point);
                if (generation != _hoverGeneration || !ReferenceEquals(vm, _vm))
                    continue;

                _overText = overText;
                UpdateCursor();
            }
        }
        finally
        {
            _hoverProbeRunning = false;
        }
    }

    #region Scrolling and visible pages

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm is null)
            return;

        if (e.ViewportDelta != default)
            _vm.OnViewportChanged(PageScroller.Viewport);

        if (e.OffsetDelta != default)
            HidePopup();

        _vm.SavedScrollOffset = PageScroller.Offset;
        UpdateVisiblePages();
    }

    private void UpdateVisiblePages()
    {
        if (_vm is null)
            return;

        var viewportHeight = PageScroller.Viewport.Height;
        if (viewportHeight <= 0)
            return;

        var visible = new List<int>();
        var current = -1;
        var bestOverlap = 0.0;

        foreach (var container in PagesHost.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageViewModel page)
                continue;

            var origin = container.TranslatePoint(default, PageScroller);
            if (origin is null)
                continue;

            var top = origin.Value.Y;
            var overlap = Math.Min(top + container.Bounds.Height, viewportHeight) - Math.Max(top, 0);
            if (overlap <= 0)
                continue;

            visible.Add(page.Index);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                current = page.Index;
            }
        }

        _vm.SetVisiblePages(visible, current);
        UpdateRulers();
    }

    private void UpdateRulers()
    {
        if (_vm is not { IsRulerVisible: true } vm)
            return;

        var index = vm.CurrentPageIndex;
        if (index < 0 || index >= vm.Pages.Count ||
            (PagesHost.ContainerFromIndex(index) as ContentPresenter)?.Child is not LayoutTransformControl page)
            return;

        var model = vm.Pages[index];
        var pixelsPerPoint = model.WidthPoints > 0 ? model.DisplayWidth / model.WidthPoints : 1;

        if (page.TranslatePoint(default, TopRuler) is { } top)
        {
            TopRuler.Origin = top.X;
            TopRuler.PageLength = page.Bounds.Width;
            TopRuler.PixelsPerPoint = pixelsPerPoint;
        }

        if (page.TranslatePoint(default, LeftRuler) is { } left)
        {
            LeftRuler.Origin = left.Y;
            LeftRuler.PageLength = page.Bounds.Height;
            LeftRuler.PixelsPerPoint = pixelsPerPoint;
        }
    }

    private void UpdateRulerPointer(Point? positionInScroller)
    {
        if (positionInScroller is not { } position)
        {
            TopRuler.PointerPosition = null;
            LeftRuler.PointerPosition = null;
            return;
        }

        TopRuler.PointerPosition = PageScroller.TranslatePoint(position, TopRuler)?.X;
        LeftRuler.PointerPosition = PageScroller.TranslatePoint(position, LeftRuler)?.Y;
    }

    /// <summary>
    /// The ruler menu belongs to the rulers and the page menu to the page scroller; anything else in the
    /// page area (find bar, selection popup) gets no menu.
    /// </summary>
    private void OnPageAreaContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual source)
        {
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(source, TopRuler) || ReferenceEquals(source, LeftRuler) || ReferenceEquals(source, RulerCorner))
            return;

        if (!ReferenceEquals(source, PageScroller) && !PageScroller.IsVisualAncestorOf(source))
        {
            e.Handled = true;
            return;
        }

        HidePopup();
        _contextAnchor = e.TryGetPosition(PageScroller, out var position) ? HitTestPage(position) : null;
        _hasContextAnchor = true;
    }

    private void OnPageContextMenuClosed(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => _hasContextAnchor = false, DispatcherPriority.Background);

    private (int PageIndex, Point Point)? GetPasteAnchor()
    {
        if (_hasContextAnchor)
        {
            _hasContextAnchor = false;
            return _contextAnchor is { } hit ? (hit.Page.Index, hit.PointInPoints) : null;
        }

        return PageScroller.IsPointerOver && HitTestPage(_lastPointer) is { } pointer
            ? (pointer.Page.Index, pointer.PointInPoints)
            : null;
    }

    private async Task<ClipboardContent?> ReadClipboardAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return null;

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
            return new ClipboardContent(text, null);

        using var bitmap = await clipboard.TryGetBitmapAsync();
        return bitmap is null ? null : new ClipboardContent(null, ToImageData(bitmap));
    }

    /// <summary>Copies a bitmap into straight BGRA, which is what PDFium expects for an image object.</summary>
    private static PdfImageData ToImageData(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        using var target = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = new byte[size.Width * size.Height * 4];

        using (var frame = target.Lock())
        {
            bitmap.CopyPixels(frame);
            var rowBytes = size.Width * 4;
            for (int y = 0; y < size.Height; y++)
                Marshal.Copy(frame.Address + y * frame.RowBytes, pixels, y * rowBytes, rowBytes);
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is 0 or 255)
                continue;

            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
        }

        return new PdfImageData(size.Width, size.Height, pixels);
    }

    private void OnScrollToPageRequested(int index) => ScrollToPage(index, 0);

    private void OnScrollToMatchRequested(int index, double fraction) =>
        ScrollToPage(index, fraction, viewportOffset: PageScroller.Viewport.Height / 3);

    private void ScrollToPage(int index, double fraction, int attempt = 0, double viewportOffset = 0)
    {
        var container = PagesHost.ContainerFromIndex(index);
        if (container is null)
        {
            if (attempt >= 3)
                return;

            PagesHost.ScrollIntoView(index);
            Dispatcher.UIThread.Post(() => ScrollToPage(index, fraction, attempt + 1, viewportOffset),
                DispatcherPriority.Background);
            return;
        }

        var top = container.TranslatePoint(default, PagesHost);
        if (top is null)
            return;

        var y = PagesHost.Margin.Top + top.Value.Y + fraction * container.Bounds.Height - viewportOffset;
        if (fraction == 0)
            y -= DocumentViewModel.PageSpacing;

        PageScroller.Offset = new Vector(PageScroller.Offset.X, Math.Max(0, y));
    }

    private void CaptureZoomAnchor()
    {
        if (_vm is null || _zoomAnchor is not null)
            return;

        var index = _vm.CurrentPageIndex;
        var container = PagesHost.ContainerFromIndex(index);
        var origin = container?.TranslatePoint(default, PageScroller);
        var fraction = 0.0;
        if (container is not null && origin is not null && container.Bounds.Height > 0)
            fraction = Math.Clamp(-origin.Value.Y / container.Bounds.Height, 0, 1);

        _zoomAnchor = (index, fraction);
        Dispatcher.UIThread.Post(() =>
        {
            if (_zoomAnchor is { } anchor)
            {
                _zoomAnchor = null;
                ScrollToPage(anchor.PageIndex, anchor.Fraction);
            }
        }, DispatcherPriority.Background);
    }

    #endregion

    #region Pointer: pan, select, zoom

    private void OnPageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm is null || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        _vm.ZoomByWheel(e.Delta.Y > 0);
        e.Handled = true;
    }

    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || !e.GetCurrentPoint(PageScroller).Properties.IsLeftButtonPressed || IsOnScrollBar(e.Source))
            return;

        Focus();
        HidePopup();
        var position = e.GetPosition(PageScroller);

        if (_vm.IsHandTool && !_overText)
        {
            _vm.ClearSelection();
            _isPanning = true;
            _panStart = position;
            _panStartOffset = PageScroller.Offset;
            UpdateCursor();
            e.Pointer.Capture(PageScroller);
            e.Handled = true;
            return;
        }

        var hit = HitTestPage(position);
        if (hit is null)
        {
            _vm.ClearSelection();
            return;
        }

        _isSelecting = true;
        _selectionPageIndex = hit.Value.Page.Index;
        _vm.BeginSelection(_selectionPageIndex, hit.Value.PointInPoints);
        e.Pointer.Capture(PageScroller);
        e.Handled = true;
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is null)
            return;

        var position = e.GetPosition(PageScroller);
        _lastPointer = position;

        if (_isPanning)
        {
            var delta = position - _panStart;
            PageScroller.Offset = _panStartOffset - new Vector(delta.X, delta.Y);
            e.Handled = true;
            return;
        }

        if (_isSelecting)
        {
            if (ToPagePoint(_selectionPageIndex, position, clamp: true) is { } point)
                _vm.UpdateSelection(point);
            e.Handled = true;
            return;
        }

        if (_vm.IsHandTool)
            ProbeTextUnderPointer(position);

        UpdatePopupHover(position);
    }

    private void OnPagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            UpdateCursor();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_isSelecting)
            return;

        _isSelecting = false;
        _vm?.EndSelection();
        e.Pointer.Capture(null);
        e.Handled = true;

        var position = e.GetPosition(PageScroller);
        DispatcherTimer.RunOnce(() => UpdatePopupHover(position), TimeSpan.FromMilliseconds(150));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                if (_vm.Search.IsOpen && !_vm.HasSelection)
                    _vm.Search.CloseCommand.Execute(null);
                _vm.ClearSelection();
                HidePopup();
                break;
            case Key.Left:
                _vm.PreviousPageCommand.Execute(null);
                break;
            case Key.Right:
                _vm.NextPageCommand.Execute(null);
                break;
            case Key.Up:
                ScrollBy(-ArrowScrollStep);
                break;
            case Key.Down:
                ScrollBy(ArrowScrollStep);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ScrollBy(double deltaY)
    {
        var maxY = Math.Max(0, PageScroller.Extent.Height - PageScroller.Viewport.Height);
        var y = Math.Clamp(PageScroller.Offset.Y + deltaY, 0, maxY);
        PageScroller.Offset = new Vector(PageScroller.Offset.X, y);
    }

    private static bool IsOnScrollBar(object? source) =>
        source is Visual visual && visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null;

    private Control? PageSurface(int pageIndex) => SurfaceOf(PagesHost.ContainerFromIndex(pageIndex));

    /// <summary>The unrotated page surface inside a realized container; its coordinates match PDF points.</summary>
    private static Control? SurfaceOf(Control? container) =>
        (container as ContentPresenter)?.Child is LayoutTransformControl { Child: Control surface }
            ? surface
            : null;

    private (PdfPageViewModel Page, Point PointInPoints)? HitTestPage(Point positionInScroller)
    {
        foreach (var container in PagesHost.GetRealizedContainers())
        {
            if (container.DataContext is not PdfPageViewModel page || SurfaceOf(container) is not { } surface)
                continue;

            var local = PageScroller.TranslatePoint(positionInScroller, surface);
            if (local is null || !new Rect(surface.Bounds.Size).Contains(local.Value))
                continue;

            var scale = surface.Bounds.Width / page.WidthPoints;
            return (page, new Point(local.Value.X / scale, local.Value.Y / scale));
        }

        return null;
    }

    private Point? ToPagePoint(int pageIndex, Point positionInScroller, bool clamp)
    {
        if (_vm is null || pageIndex < 0 || PageSurface(pageIndex) is not { } surface)
            return null;

        var local = PageScroller.TranslatePoint(positionInScroller, surface);
        if (local is null)
            return null;

        var x = local.Value.X;
        var y = local.Value.Y;
        if (clamp)
        {
            x = Math.Clamp(x, 0, surface.Bounds.Width);
            y = Math.Clamp(y, 0, surface.Bounds.Height);
        }

        var scale = surface.Bounds.Width / _vm.Pages[pageIndex].WidthPoints;
        return new Point(x / scale, y / scale);
    }

    #endregion

    #region Selection popup

    private void UpdatePopupHover(Point positionInScroller)
    {
        if (_vm is not { HasSelection: true } || _isSelecting)
        {
            StartPopupHide();
            return;
        }

        var pageIndex = _vm.SelectionPageIndex;
        var rects = pageIndex >= 0 ? _vm.Pages[pageIndex].SelectionRects : null;
        if (rects is null || rects.Count == 0 || PageSurface(pageIndex) is not { } surface)
        {
            StartPopupHide();
            return;
        }

        var local = PageScroller.TranslatePoint(positionInScroller, surface);
        var scale = surface.Bounds.Width / _vm.Pages[pageIndex].WidthPoints;
        var scaled = rects.Select(r => new Rect(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale)).ToList();

        if (local is { } point && scaled.Any(r => r.Inflate(HoverSlop).Contains(point)))
            ShowPopup(surface, scaled);
        else
            StartPopupHide();
    }

    private void ShowPopup(Control surface, IReadOnlyList<Rect> rectsOnSurface)
    {
        _popupHideTimer.Stop();

        var union = rectsOnSurface.Aggregate((a, b) => a.Union(b));
        var corners = new[] { union.TopLeft, union.TopRight, union.BottomLeft, union.BottomRight }
            .Select(corner => surface.TranslatePoint(corner, PopupLayer))
            .ToList();
        if (corners.Any(c => c is null))
            return;

        var left = corners.Min(c => c!.Value.X);
        var top = corners.Min(c => c!.Value.Y);
        var bottomEdge = corners.Max(c => c!.Value.Y);

        SelectionPopupHost.IsVisible = true;
        SelectionPopupHost.Measure(Size.Infinity);
        var size = SelectionPopupHost.DesiredSize;
        var layer = PopupLayer.Bounds.Size;

        var x = Math.Clamp(left, 4, Math.Max(4, layer.Width - size.Width - 4));
        var y = top - size.Height - PopupGap;
        if (y < 4)
            y = bottomEdge + PopupGap;

        Canvas.SetLeft(SelectionPopupHost, x);
        Canvas.SetTop(SelectionPopupHost, Math.Min(y, Math.Max(4, layer.Height - size.Height - 4)));
    }

    private void StartPopupHide()
    {
        if (SelectionPopupHost.IsVisible && !_popupHideTimer.IsEnabled)
            _popupHideTimer.Start();
    }

    private void HidePopup()
    {
        _popupHideTimer.Stop();
        SelectionPopupHost.IsVisible = false;
    }

    #endregion

    private void OnThumbnailClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PdfPageViewModel page })
            _vm?.GoToPage(page.Index);
    }
}
