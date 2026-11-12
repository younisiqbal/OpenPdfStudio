using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenPdfStudio.Services.Pdf;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views.Dialogs;

/// <summary>
/// Reorder, rotate, and delete pages. Returns the new page list, or null when cancelled.
/// Nothing changes in the document until Apply.
/// </summary>
public sealed class OrganizePagesDialog : Window
{
    private const double ThumbBox = 120;
    private const double PixelsPerDip = 2;

    private readonly PdfDocumentSession _session;
    private readonly List<Tile> _tiles = new();
    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private readonly Button _rotateLeft, _rotateRight, _moveEarlier, _moveLater, _delete, _apply;
    private readonly CancellationTokenSource _cts = new();
    private int _anchor = -1;

    private sealed class Tile
    {
        public required int Source { get; init; }
        public required Size PageSize { get; init; }
        public int Turns { get; set; }
        public bool Selected { get; set; }
        public Border View { get; set; } = null!;
        public Image Image { get; } = new() { Stretch = Stretch.Fill };
        public LayoutTransformControl Rotator { get; } = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        public TextBlock Label { get; } = new() { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
    }

    public OrganizePagesDialog(DocumentViewModel document)
    {
        _session = document.Session;
        Title = $"Organize pages - {document.FileName}";
        Width = 780;
        Height = 600;
        MinWidth = 520;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        for (var i = 0; i < _session.PageCount; i++)
            _tiles.Add(CreateTile(i, _session.PageSizes[i]));
        var allTiles = _tiles.ToList();

        _rotateLeft = ToolButton("Rotate left", () => Rotate(-1));
        _rotateRight = ToolButton("Rotate right", () => Rotate(1));
        _moveEarlier = ToolButton("Move earlier", () => Move(-1));
        _moveLater = ToolButton("Move later", () => Move(1));
        _delete = ToolButton("Delete", Delete);
        var selectAll = ToolButton("Select all", SelectAll);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        cancel.Click += (_, _) => Close(null);
        _apply = new Button { Content = "Apply", IsDefault = true, MinWidth = 88, Classes = { "primary" } };
        _apply.Click += (_, _) => Close(Result());

        var toolbar = new WrapPanel
        {
            Margin = new Thickness(16, 12),
            Children = { _rotateLeft, _rotateRight, _moveEarlier, _moveLater, _delete, selectAll }
        };

        var footer = new DockPanel { Margin = new Thickness(16, 12), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, _apply } };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_summary);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Content = _panel,
            Padding = new Thickness(12, 0),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        });
        Content = root;

        KeyDown += OnKeyDown;
        Opened += (_, _) => RenderThumbnails(allTiles);
        Closed += (_, _) =>
        {
            _cts.Cancel();
            foreach (var tile in allTiles)
                (tile.Image.Source as IDisposable)?.Dispose();
        };

        Rebuild();
    }

    private Tile CreateTile(int source, Size pageSize)
    {
        var tile = new Tile { Source = source, PageSize = pageSize };
        var scale = ThumbBox / Math.Max(pageSize.Width, pageSize.Height);
        tile.Image.Width = pageSize.Width * scale;
        tile.Image.Height = pageSize.Height * scale;
        tile.Rotator.Child = new Border
        {
            Background = Brushes.White,
            BoxShadow = BoxShadows.Parse("0 1 4 0 #40000000"),
            Child = tile.Image
        };

        tile.View = new Border
        {
            Width = ThumbBox + 28,
            Margin = new Thickness(4),
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new Panel { Width = ThumbBox, Height = ThumbBox, Children = { tile.Rotator } },
                    tile.Label
                }
            }
        };
        tile.View.PointerPressed += (_, e) => OnTilePressed(tile, e);
        return tile;
    }

    private static Button ToolButton(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Classes = { "ghost" } };
        button.Click += (_, _) => action();
        return button;
    }

    private async void RenderThumbnails(IReadOnlyList<Tile> tiles)
    {
        foreach (var tile in tiles)
        {
            if (_cts.IsCancellationRequested)
                return;

            try
            {
                var width = (int)Math.Max(1, Math.Round(tile.Image.Width * PixelsPerDip));
                var height = (int)Math.Max(1, Math.Round(tile.Image.Height * PixelsPerDip));
                tile.Image.Source = await _session.RenderPageAsync(tile.Source, width, height, _cts.Token, lowPriority: true);
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

    private void OnTilePressed(Tile tile, PointerPressedEventArgs e)
    {
        var index = _tiles.IndexOf(tile);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _anchor >= 0)
        {
            var (from, to) = (Math.Min(_anchor, index), Math.Max(_anchor, index));
            for (var i = 0; i < _tiles.Count; i++)
                _tiles[i].Selected = i >= from && i <= to;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            tile.Selected = !tile.Selected;
            _anchor = index;
        }
        else
        {
            foreach (var other in _tiles)
                other.Selected = other == tile;
            _anchor = index;
        }

        e.Handled = true;
        Refresh();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete:
                Delete();
                break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                SelectAll();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void SelectAll()
    {
        foreach (var tile in _tiles)
            tile.Selected = true;
        Refresh();
    }

    private void Rotate(int turns)
    {
        foreach (var tile in _tiles.Where(t => t.Selected))
            tile.Turns = ((tile.Turns + turns) % 4 + 4) % 4;
        Refresh();
    }

    /// <summary>Moves the selected pages one place earlier or later, keeping their relative order.</summary>
    private void Move(int direction)
    {
        var order = direction < 0
            ? Enumerable.Range(0, _tiles.Count)
            : Enumerable.Range(0, _tiles.Count).Reverse();

        foreach (var i in order)
        {
            var j = i + direction;
            if (!_tiles[i].Selected || j < 0 || j >= _tiles.Count || _tiles[j].Selected)
                continue;

            (_tiles[i], _tiles[j]) = (_tiles[j], _tiles[i]);
        }

        _anchor = _tiles.FindIndex(t => t.Selected);
        Rebuild();
    }

    private void Delete()
    {
        if (_tiles.All(t => t.Selected))
        {
            _summary.Text = "A PDF needs at least one page. Leave at least one page unselected.";
            return;
        }

        _tiles.RemoveAll(t => t.Selected);

        _anchor = -1;
        Rebuild();
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        foreach (var tile in _tiles)
            _panel.Children.Add(tile.View);
        Refresh();
    }

    private void Refresh()
    {
        var accent = this.TryFindResource("AccentBrush", ActualThemeVariant, out var brush) ? brush as IBrush : Brushes.DodgerBlue;
        for (var i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            tile.Rotator.LayoutTransform = new RotateTransform(tile.Turns * 90);
            tile.Label.Text = tile.Source == i ? $"{i + 1}" : $"{i + 1}  (was {tile.Source + 1})";
            tile.View.BorderBrush = tile.Selected ? accent : Brushes.Transparent;
        }

        var selected = _tiles.Count(t => t.Selected);
        var deleted = _session.PageCount - _tiles.Count;
        _rotateLeft.IsEnabled = _rotateRight.IsEnabled = _delete.IsEnabled = selected > 0;
        _moveEarlier.IsEnabled = selected > 0 && !_tiles[0].Selected;
        _moveLater.IsEnabled = selected > 0 && !_tiles[^1].Selected;
        _apply.IsEnabled = IsChanged();
        _summary.Text = $"{_tiles.Count} page{(_tiles.Count == 1 ? "" : "s")}" +
                        (deleted > 0 ? $", {deleted} deleted" : "") +
                        (selected > 0 ? $", {selected} selected" : ". Click a page to select it; Ctrl or Shift to select more.");
    }

    private bool IsChanged() =>
        _tiles.Count != _session.PageCount || _tiles.Where((t, i) => t.Source != i || t.Turns != 0).Any();

    private IReadOnlyList<PageArrangement> Result() =>
        _tiles.Select(t => new PageArrangement(t.Source, t.Turns)).ToList();
}
