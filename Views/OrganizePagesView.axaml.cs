using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

/// <summary>
/// All pages of a document as tiles. Drag tiles to reorder, drag on the background to box-select,
/// and use the arrow keys with Shift to extend the selection.
/// </summary>
public partial class OrganizePagesView : UserControl
{
    private const double DragThreshold = 6;
    private const double AutoScrollMargin = 40;
    private const double AutoScrollStep = 18;
    private const double CollapsedWidth = 152;
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(220);

    private DocumentViewModel? _vm;
    private DispatcherTimer? _animation;

    private enum PointerMode { None, PendingDrag, Dragging, Marquee }

    private PointerMode _mode;
    private Point _pressPoint;
    private OrganizePageItem? _pressedItem;
    private bool _selectOnRelease;
    private int _dropIndex = -1;
    private HashSet<OrganizePageItem> _marqueeKeep = new();

    public OrganizePagesView()
    {
        InitializeComponent();
        IsVisible = false;
        DataContextChanged += OnDataContextChanged;

        Surface.AddHandler(PointerPressedEvent, OnSurfacePressed, RoutingStrategies.Bubble, handledEventsToo: true);
        Surface.AddHandler(PointerMovedEvent, OnSurfaceMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        Surface.AddHandler(PointerReleasedEvent, OnSurfaceReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Surface.PointerCaptureLost += (_, _) => ResetPointer();
        KeyDown += OnKeyDown;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.OrganizeFocusChanged -= OnFocusChanged;
        }

        _vm = DataContext as DocumentViewModel;
        ResetPointer();
        if (_vm is null)
        {
            IsVisible = false;
            return;
        }

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.OrganizeFocusChanged += OnFocusChanged;
        StopAnimation();
        IsVisible = _vm.IsOrganizing;
        Width = double.NaN;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DocumentViewModel.IsOrganizing) || _vm is null)
            return;

        if (_vm.IsOrganizing)
            Expand();
        else
            Collapse();
    }

    #region Expand and collapse

    /// <summary>Grows from the width of the thumbnail strip to the full page area, so the pages spread out to the right.</summary>
    private void Expand()
    {
        ResetPointer();
        Scroller.Offset = default;
        var full = Parent is Control parent ? parent.Bounds.Width : Bounds.Width;
        IsVisible = true;
        Animate(CollapsedWidth, full, 0.6, 1, () => { Focus(); });
    }

    private void Collapse()
    {
        ResetPointer();
        if (!IsVisible)
            return;

        Animate(Bounds.Width, CollapsedWidth, 1, 0, () => IsVisible = false);
    }

    private void Animate(double fromWidth, double toWidth, double fromOpacity, double toOpacity, Action done)
    {
        StopAnimation();
        if (toWidth <= 0 || Math.Abs(toWidth - fromWidth) < 1)
        {
            FinishAnimation(done);
            return;
        }

        HorizontalAlignment = HorizontalAlignment.Left;
        Width = fromWidth;
        Opacity = fromOpacity;
        var started = DateTime.UtcNow;
        _animation = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            var t = Math.Clamp((DateTime.UtcNow - started) / ExpandDuration, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            Width = fromWidth + (toWidth - fromWidth) * eased;
            Opacity = fromOpacity + (toOpacity - fromOpacity) * eased;
            if (t >= 1)
                FinishAnimation(done);
        });
        _animation.Start();
    }

    private void FinishAnimation(Action done)
    {
        StopAnimation();
        Width = double.NaN;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Opacity = 1;
        done();
    }

    private void StopAnimation()
    {
        _animation?.Stop();
        _animation = null;
    }

    #endregion

    #region Pointer: select, drag to reorder, box select

    private ItemsControl ActiveHost => _vm is { IsOrganizeList: true } ? ListHost : GridHost;

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || !e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed || IsOnScrollBar(e.Source))
            return;

        Focus();
        var toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var range = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _pressPoint = e.GetPosition(Surface);
        _pressedItem = ItemAt(e.Source);
        _selectOnRelease = false;

        if (_pressedItem is { } item)
        {
            if (toggle || range)
            {
                _vm.ClickOrganizeItem(item, toggle, range);
                if (!item.IsSelected)
                {
                    e.Handled = true;
                    return;
                }
            }
            else if (item.IsSelected)
            {
                // Pressing a selected page may start dragging the whole selection; a plain click selects only it.
                _selectOnRelease = true;
            }
            else
            {
                _vm.ClickOrganizeItem(item, toggle: false, range: false);
            }

            _mode = PointerMode.PendingDrag;
        }
        else
        {
            _marqueeKeep = toggle ? _vm.OrganizeItems.Where(i => i.IsSelected).ToHashSet() : new HashSet<OrganizePageItem>();
            if (!toggle)
                _vm.ClearOrganizeSelection();
            _mode = PointerMode.Marquee;
        }

        e.Pointer.Capture(Surface);
        e.Handled = true;
    }

    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        if (_vm is null || _mode == PointerMode.None)
            return;

        var point = e.GetPosition(Surface);
        switch (_mode)
        {
            case PointerMode.PendingDrag:
                if (Distance(point, _pressPoint) < DragThreshold)
                    return;

                if (_pressedItem is { IsSelected: false } pressed)
                    _vm.ClickOrganizeItem(pressed, toggle: false, range: false);
                foreach (var item in _vm.OrganizeItems.Where(i => i.IsSelected))
                    item.IsDragging = true;
                _selectOnRelease = false;
                _mode = PointerMode.Dragging;
                goto case PointerMode.Dragging;

            case PointerMode.Dragging:
                AutoScroll(point);
                _dropIndex = DropIndexAt(point);
                ShowDropIndicator(_dropIndex);
                break;

            case PointerMode.Marquee:
                AutoScroll(point);
                UpdateMarquee(point);
                break;
        }

        e.Handled = true;
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is null || _mode == PointerMode.None)
            return;

        if (_mode == PointerMode.Dragging && _dropIndex >= 0)
            _vm.MoveOrganizeSelection(_dropIndex);
        else if (_mode == PointerMode.PendingDrag && _selectOnRelease && _pressedItem is { } item)
            _vm.ClickOrganizeItem(item, toggle: false, range: false);

        e.Pointer.Capture(null);
        ResetPointer();
        e.Handled = true;
    }

    private void ResetPointer()
    {
        if (_vm is not null)
        {
            foreach (var item in _vm.OrganizeItems)
                item.IsDragging = false;
        }

        _mode = PointerMode.None;
        _pressedItem = null;
        _selectOnRelease = false;
        _dropIndex = -1;
        Marquee.IsVisible = false;
        DropIndicator.IsVisible = false;
    }

    /// <summary>Where dropped pages would land: before the first tile that is below the pointer or to its right on the same row.</summary>
    private int DropIndexAt(Point point)
    {
        var tiles = TileBounds();
        var list = _vm is { IsOrganizeList: true };
        for (var i = 0; i < tiles.Count; i++)
        {
            var bounds = tiles[i];
            if (list)
            {
                if (point.Y < bounds.Center.Y)
                    return i;
                continue;
            }

            if (point.Y < bounds.Top)
                return i;
            if (point.Y <= bounds.Bottom && point.X < bounds.Center.X)
                return i;
        }

        return tiles.Count;
    }

    private void ShowDropIndicator(int index)
    {
        var tiles = TileBounds();
        if (tiles.Count == 0 || index < 0)
        {
            DropIndicator.IsVisible = false;
            return;
        }

        Rect line;
        if (_vm is { IsOrganizeList: true })
        {
            var y = index < tiles.Count ? tiles[index].Top - 2 : tiles[^1].Bottom + 2;
            var reference = tiles[Math.Min(index, tiles.Count - 1)];
            line = new Rect(reference.Left, y - 1.5, reference.Width, 3);
        }
        else
        {
            var reference = index < tiles.Count ? tiles[index] : tiles[^1];
            var x = index < tiles.Count ? reference.Left - 2 : reference.Right + 2;
            line = new Rect(x - 1.5, reference.Top, 3, reference.Height);
        }

        Canvas.SetLeft(DropIndicator, line.X);
        Canvas.SetTop(DropIndicator, line.Y);
        DropIndicator.Width = line.Width;
        DropIndicator.Height = line.Height;
        DropIndicator.IsVisible = true;
    }

    private void UpdateMarquee(Point point)
    {
        if (_vm is null)
            return;

        var box = new Rect(_pressPoint, point).Normalize();
        Canvas.SetLeft(Marquee, box.X);
        Canvas.SetTop(Marquee, box.Y);
        Marquee.Width = box.Width;
        Marquee.Height = box.Height;
        Marquee.IsVisible = true;

        var tiles = TileBounds();
        var hits = new HashSet<OrganizePageItem>();
        for (var i = 0; i < tiles.Count && i < _vm.OrganizeItems.Count; i++)
        {
            if (tiles[i].Intersects(box))
                hits.Add(_vm.OrganizeItems[i]);
        }

        _vm.SelectOrganizeItems(hits, _marqueeKeep);
    }

    /// <summary>Scrolls while dragging near the top or bottom edge. The press point moves with the content.</summary>
    private void AutoScroll(Point point)
    {
        var maxY = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        var delta = point.Y < AutoScrollMargin ? -AutoScrollStep
            : point.Y > Surface.Bounds.Height - AutoScrollMargin ? AutoScrollStep
            : 0;
        var y = Math.Clamp(Scroller.Offset.Y + delta, 0, maxY);
        if (Math.Abs(y - Scroller.Offset.Y) < 0.5)
            return;

        _pressPoint = new Point(_pressPoint.X, _pressPoint.Y - (y - Scroller.Offset.Y));
        Scroller.Offset = new Vector(Scroller.Offset.X, y);
    }

    /// <summary>Each tile's bounds in <see cref="Surface"/> coordinates, in page order.</summary>
    private List<Rect> TileBounds()
    {
        var bounds = new List<Rect>();
        if (_vm is null)
            return bounds;

        var host = ActiveHost;
        for (var i = 0; i < _vm.OrganizeItems.Count; i++)
        {
            if (host.ContainerFromIndex(i) is not { } container ||
                container.TranslatePoint(default, Surface) is not { } origin)
            {
                bounds.Add(default);
                continue;
            }

            bounds.Add(new Rect(origin, container.Bounds.Size));
        }

        return bounds;
    }

    private static OrganizePageItem? ItemAt(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border border && (border.Classes.Contains("orgTile") || border.Classes.Contains("orgRow")))
                return border.DataContext as OrganizePageItem;
        }

        return null;
    }

    private static bool IsOnScrollBar(object? source) =>
        source is Visual visual && visual.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null;

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    #endregion

    #region Keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { IsOrganizing: true } vm)
            return;

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Left:
                vm.MoveOrganizeFocus(-1, shift);
                break;
            case Key.Right:
                vm.MoveOrganizeFocus(1, shift);
                break;
            case Key.Up:
                vm.MoveOrganizeFocus(-ColumnCount(), shift);
                break;
            case Key.Down:
                vm.MoveOrganizeFocus(ColumnCount(), shift);
                break;
            case Key.Home:
                vm.MoveOrganizeFocus(-vm.OrganizeItems.Count, shift);
                break;
            case Key.End:
                vm.MoveOrganizeFocus(vm.OrganizeItems.Count, shift);
                break;
            case Key.A when ctrl:
                vm.SelectAllOrganize();
                break;
            case Key.B when ctrl:
                vm.InsertBlankPageCommand.Execute(null);
                break;
            case Key.Delete:
            case Key.Back:
                vm.DeleteOrganizeSelection();
                break;
            case Key.Escape:
                vm.CancelOrganize();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Tiles per row in grid view, so Up and Down move a whole row.</summary>
    private int ColumnCount()
    {
        if (_vm is not { IsOrganizeGrid: true })
            return 1;

        var tiles = TileBounds();
        if (tiles.Count == 0)
            return 1;

        var top = tiles[0].Top;
        return Math.Max(1, tiles.Count(t => Math.Abs(t.Top - top) < 1));
    }

    private void OnFocusChanged(OrganizePageItem item)
    {
        if (_vm is null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is not null && ActiveHost.ContainerFromItem(item) is { } container)
                container.BringIntoView();
        }, DispatcherPriority.Background);
    }

    #endregion
}
