using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace OpenPdfStudio.Views.Controls;

/// <summary>
/// Draws text-selection rectangles over a page. Rects are in PDF points (top-left origin) and are
/// scaled to the control's current size, so zooming needs no recalculation.
/// </summary>
public class SelectionOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<Rect>?> RectsProperty =
        AvaloniaProperty.Register<SelectionOverlay, IReadOnlyList<Rect>?>(nameof(Rects));

    public static readonly StyledProperty<double> PageWidthPointsProperty =
        AvaloniaProperty.Register<SelectionOverlay, double>(nameof(PageWidthPoints), 612);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SelectionOverlay, IBrush?>(nameof(Fill),
            new SolidColorBrush(Color.FromArgb(0x4D, 0x00, 0x78, 0xD7)));

    static SelectionOverlay()
    {
        AffectsRender<SelectionOverlay>(RectsProperty, PageWidthPointsProperty, FillProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<SelectionOverlay>(false);
    }

    public IReadOnlyList<Rect>? Rects
    {
        get => GetValue(RectsProperty);
        set => SetValue(RectsProperty, value);
    }

    public double PageWidthPoints
    {
        get => GetValue(PageWidthPointsProperty);
        set => SetValue(PageWidthPointsProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var rects = Rects;
        if (rects is null || rects.Count == 0 || Fill is null || PageWidthPoints <= 0)
            return;

        var scale = Bounds.Width / PageWidthPoints;
        foreach (var rect in rects)
            context.FillRectangle(Fill, new Rect(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale));
    }
}
