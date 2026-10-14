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

    private bool _isBoxing;
    private bool _waitingCalloutTarget;
    private int _boxPageIndex = -1;
    private Point _boxStart;
    private Point _boxCurrent;

    private bool _isMovingAnnotation;
    private bool _isResizingAnnotation;
    private AnnotationHandle _resizeHandle;
    private AnnotationViewModel? _dragAnnotation;
    private PdfAnnotation? _dragOriginal;
    private Point _dragStartPoint;
    private bool _moved;

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

        AnnotationEditorHost.Committed += (annotation, text) => _ = FinishEdit(annotation, text, cancel: false);
        AnnotationEditorHost.Cancelled += annotation => _ = FinishEdit(annotation, null, cancel: true);
        AnnotationEditorHost.TextChanged += () =>
        {
            if (_vm is null || AnnotationEditorHost.Annotation is not { } annotation)
                return;
            annotation.Model = annotation.Model with { Contents = AnnotationEditorHost.Text };
            PositionTextEditor(annotation);
        };
        NotePopupHost.Committed += (annotation, text) => _ = FinishEdit(annotation, text, cancel: false);
        NotePopupHost.Cancelled += annotation => _ = FinishEdit(annotation, null, cancel: true);
        NotePopupHost.DeleteRequested += annotation => _ = DeleteFromPopup(annotation);

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
        HideEditors();
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
            _vm.TextEditRequested -= OnTextEditRequested;
            _vm.NotePopupRequested -= OnNotePopupRequested;
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
        _vm.TextEditRequested += OnTextEditRequested;
        _vm.NotePopupRequested += OnNotePopupRequested;

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
            case nameof(DocumentViewModel.AnnotationTool):
                ResetTextHover();
                HidePopup();
                UpdateCursor();
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
        Cursor cursor;
        if (_vm.IsTypewriterTool || _vm.IsMarkupTool || _isSelecting)
            cursor = AppCursors.Ibeam;
        else if (_vm.HasAnnotationTool || _vm.IsSelectAnnotationTool || _isBoxing || _isMovingAnnotation || _isResizingAnnotation)
            cursor = new Cursor(StandardCursorType.Cross);
        else if (_vm.IsSelectTool || _overText)
            cursor = AppCursors.Ibeam;
        else if (_isPanning)
            cursor = AppCursors.GetDrag(style);
        else
            cursor = AppCursors.GetHover(style);

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
        PositionOpenEditors();
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
        if (_vm is not null && _contextAnchor is { } hit &&
            _vm.HitTestAnnotation(hit.Page.Index, hit.PointInPoints) is { } annotation)
            _vm.SelectAnnotation(annotation, add: false);
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
        var hit = HitTestPage(position);
        var add = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (_waitingCalloutTarget && hit is { } calloutHit)
        {
            _waitingCalloutTarget = false;
            var page = calloutHit.Page;
            var box = NormalizeDraft(_boxStart, _boxCurrent);
            page.DraftRect = null;
            page.DraftCalloutTarget = null;
            _ = _vm.AddAnnotationAsync(_vm.CreateTextbox(page.Index, box, AnnotationKind.Callout, calloutHit.PointInPoints), beginEdit: true);
            e.Handled = true;
            return;
        }

        if (_vm.HasAnnotationTool)
        {
            HandleAnnotationToolPress(e, position, hit);
            return;
        }

        if (hit is { } pageHit)
        {
            var handleSize = AnnotationOverlay.HandleSize / Math.Max(0.01, pageHit.Page.DisplayWidth / pageHit.Page.WidthPoints);
            foreach (var selected in _vm.SelectedAnnotations)
            {
                if (selected.PageIndex != pageHit.Page.Index)
                    continue;
                var handle = DocumentViewModel.HitHandle(selected.Model, pageHit.PointInPoints, handleSize);
                if (handle != AnnotationHandle.None)
                {
                    BeginAnnotationDrag(selected, pageHit.PointInPoints, handle);
                    e.Pointer.Capture(PageScroller);
                    e.Handled = true;
                    return;
                }
            }

            if (_vm.HitTestAnnotation(pageHit.Page.Index, pageHit.PointInPoints) is { } annotation)
            {
                if (e.ClickCount >= 2 && (annotation.Model.IsFreeText || annotation.Kind == AnnotationKind.Note))
                {
                    _ = _vm.BeginEditAsync(annotation);
                    e.Handled = true;
                    return;
                }

                _vm.SelectAnnotation(annotation, add);
                BeginAnnotationDrag(annotation, pageHit.PointInPoints, AnnotationHandle.Move);
                e.Pointer.Capture(PageScroller);
                e.Handled = true;
                return;
            }
        }

        if (!add)
            _vm.ClearAnnotationSelection();

        if (_vm.IsSelectAnnotationTool)
        {
            e.Handled = true;
            return;
        }

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

    private void HandleAnnotationToolPress(PointerPressedEventArgs e, Point position, (PdfPageViewModel Page, Point PointInPoints)? hit)
    {
        if (_vm is null || hit is null)
            return;

        var pageHit = hit.Value;
        if (_vm.IsNoteTool)
        {
            _ = _vm.AddAnnotationAsync(_vm.CreateNote(pageHit.Page.Index, pageHit.PointInPoints), beginEdit: true);
            e.Handled = true;
            return;
        }

        if (_vm.IsTypewriterTool)
        {
            _ = _vm.AddAnnotationAsync(_vm.CreateTypewriter(pageHit.Page.Index, pageHit.PointInPoints), beginEdit: true);
            e.Handled = true;
            return;
        }

        if (_vm.IsTextboxTool || _vm.IsCalloutTool)
        {
            _isBoxing = true;
            _boxPageIndex = pageHit.Page.Index;
            _boxStart = _boxCurrent = pageHit.PointInPoints;
            pageHit.Page.DraftRect = new Rect(_boxStart, new Size(1, 1));
            e.Pointer.Capture(PageScroller);
            e.Handled = true;
            return;
        }

        if (_vm.IsMarkupTool)
        {
            _isSelecting = true;
            _selectionPageIndex = pageHit.Page.Index;
            _vm.BeginSelection(_selectionPageIndex, pageHit.PointInPoints);
            e.Pointer.Capture(PageScroller);
            e.Handled = true;
        }
    }

    private void BeginAnnotationDrag(AnnotationViewModel annotation, Point point, AnnotationHandle handle)
    {
        _dragAnnotation = annotation;
        _dragOriginal = annotation.Model;
        _dragStartPoint = point;
        _moved = false;
        if (handle == AnnotationHandle.Move)
            _isMovingAnnotation = true;
        else
        {
            _isResizingAnnotation = true;
            _resizeHandle = handle;
        }

        UpdateCursor();
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

        if (_isBoxing && _boxPageIndex >= 0)
        {
            if (ToPagePoint(_boxPageIndex, position, clamp: true) is { } point)
            {
                _boxCurrent = point;
                var page = _vm.Pages[_boxPageIndex];
                page.DraftRect = NormalizeDraft(_boxStart, _boxCurrent);
                if (_waitingCalloutTarget || _vm.IsCalloutTool)
                    page.DraftCalloutTarget = null;
            }

            e.Handled = true;
            return;
        }

        if ((_isMovingAnnotation || _isResizingAnnotation) && _dragAnnotation is { } dragging && _dragOriginal is { } original
            && ToPagePoint(dragging.PageIndex, position, clamp: true) is { } current)
        {
            _moved = true;
            dragging.Model = _isMovingAnnotation
                ? DocumentViewModel.Offset(original, current - _dragStartPoint)
                : DocumentViewModel.Resize(original, _resizeHandle, _dragStartPoint, current);
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

        if (_vm.IsHandTool && !_vm.HasAnnotationTool)
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

        if (_isBoxing && _vm is not null && _boxPageIndex >= 0)
        {
            var page = _vm.Pages[_boxPageIndex];
            var box = NormalizeDraft(_boxStart, _boxCurrent);
            _isBoxing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            if (_vm.IsCalloutTool)
            {
                _waitingCalloutTarget = true;
                page.DraftRect = box;
                _vm.SetStatusMessage("Click where the callout arrow should point");
                UpdateCursor();
                return;
            }

            page.DraftRect = null;
            _ = _vm.AddAnnotationAsync(_vm.CreateTextbox(_boxPageIndex, box, AnnotationKind.Textbox), beginEdit: true);
            return;
        }

        if ((_isMovingAnnotation || _isResizingAnnotation) && _dragAnnotation is { } dragged && _dragOriginal is { } original)
        {
            _isMovingAnnotation = false;
            _isResizingAnnotation = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            UpdateCursor();
            if (_moved)
                _ = _vm?.MoveOrResizeAsync(dragged, dragged.Model);
            else
                dragged.Model = original;
            _dragAnnotation = null;
            _dragOriginal = null;
            return;
        }

        if (!_isSelecting)
            return;

        var wasMarkup = _vm?.IsMarkupTool == true;
        var kind = _vm?.AnnotationTool.Kind();
        _isSelecting = false;
        _vm?.EndSelection();
        e.Pointer.Capture(null);
        e.Handled = true;

        if (wasMarkup && kind is { } markup && _vm is not null)
            _ = _vm.MarkupSelection(markup.ToString());

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
                if (_waitingCalloutTarget)
                {
                    _waitingCalloutTarget = false;
                    if (_boxPageIndex >= 0)
                    {
                        _vm.Pages[_boxPageIndex].DraftRect = null;
                        _vm.Pages[_boxPageIndex].DraftCalloutTarget = null;
                    }
                }
                else if (_vm.HasAnnotationTool)
                    _vm.AnnotationTool = AnnotationTool.None;
                else if (_vm.HasSelectedAnnotation)
                    _vm.ClearAnnotationSelection();
                else if (_vm.Search.IsOpen && !_vm.HasSelection)
                    _vm.Search.CloseCommand.Execute(null);
                _vm.ClearSelection();
                HidePopup();
                HideEditors();
                break;
            case Key.Delete:
                if (_vm.HasSelectedAnnotation && _vm.DeleteSelectedAnnotationsCommand.CanExecute(null))
                    _vm.DeleteSelectedAnnotationsCommand.Execute(null);
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

    private void OnTextEditRequested(AnnotationViewModel annotation)
    {
        HideEditors();
        PositionTextEditor(annotation);
        var size = PageToLayerSize(annotation.PageIndex, annotation.Model.Bounds.Size);
        AnnotationEditorHost.Begin(annotation, size ?? new Size(120, 24));
    }

    private void OnNotePopupRequested(AnnotationViewModel annotation)
    {
        HideEditors();
        NotePopupHost.Begin(annotation);
        PositionNotePopup(annotation);
    }

    private void PositionOpenEditors()
    {
        if (AnnotationEditorHost.Annotation is { } text)
            PositionTextEditor(text);
        if (NotePopupHost.Annotation is { } note)
            PositionNotePopup(note);
    }

    private void PositionTextEditor(AnnotationViewModel annotation)
    {
        if (PageToLayer(annotation.PageIndex, annotation.Model.Bounds.TopLeft) is not { } origin ||
            PageToLayerSize(annotation.PageIndex, annotation.Model.Bounds.Size) is not { } size)
            return;

        AnnotationEditorHost.Width = Math.Max(24, size.Width);
        AnnotationEditorHost.Height = Math.Max(16, size.Height);
        Canvas.SetLeft(AnnotationEditorHost, origin.X);
        Canvas.SetTop(AnnotationEditorHost, origin.Y);
    }

    private void PositionNotePopup(AnnotationViewModel annotation)
    {
        if (PageToLayer(annotation.PageIndex, annotation.Model.Bounds.TopRight) is not { } origin)
            return;

        NotePopupHost.Measure(Size.Infinity);
        var x = origin.X + 8;
        var y = origin.Y;
        var layer = PopupLayer.Bounds.Size;
        if (x + NotePopupHost.DesiredSize.Width > layer.Width)
            x = Math.Max(4, origin.X - NotePopupHost.DesiredSize.Width - 8);
        Canvas.SetLeft(NotePopupHost, x);
        Canvas.SetTop(NotePopupHost, Math.Clamp(y, 4, Math.Max(4, layer.Height - NotePopupHost.DesiredSize.Height - 4)));
    }

    private Point? PageToLayer(int pageIndex, Point pagePoint)
    {
        if (PageSurface(pageIndex) is not { } surface || _vm is null)
            return null;

        var scale = surface.Bounds.Width / _vm.Pages[pageIndex].WidthPoints;
        return surface.TranslatePoint(new Point(pagePoint.X * scale, pagePoint.Y * scale), PopupLayer);
    }

    private Size? PageToLayerSize(int pageIndex, Size pageSize)
    {
        if (PageSurface(pageIndex) is not { } surface || _vm is null)
            return null;

        var scale = surface.Bounds.Width / _vm.Pages[pageIndex].WidthPoints;
        return new Size(pageSize.Width * scale, pageSize.Height * scale);
    }

    private async Task FinishEdit(AnnotationViewModel annotation, string? text, bool cancel)
    {
        if (_vm is null)
            return;

        if (cancel)
            await _vm.CancelEditAsync(annotation, deleteIfNew: true);
        else
            await _vm.CommitEditAsync(annotation, text ?? string.Empty);

        HideEditors();
    }

    private async Task DeleteFromPopup(AnnotationViewModel annotation)
    {
        if (_vm is null)
            return;

        await _vm.CancelEditAsync(annotation, deleteIfNew: false);
        _vm.SelectAnnotation(annotation, add: false);
        if (_vm.DeleteSelectedAnnotationsCommand.CanExecute(null))
            await _vm.DeleteSelectedAnnotationsCommand.ExecuteAsync(null);
        HideEditors();
    }

    private void HideEditors()
    {
        AnnotationEditorHost.HideEditor();
        NotePopupHost.HidePopup();
    }

    private static Rect NormalizeDraft(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Max(8, Math.Abs(b.X - a.X)), Math.Max(8, Math.Abs(b.Y - a.Y)));
    }
}
