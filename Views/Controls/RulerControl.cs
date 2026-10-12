using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OpenPdfStudio.Models;

namespace OpenPdfStudio.Views.Controls;

/// <summary>
/// A measuring ruler drawn along one edge of the page view. Zero sits at <see cref="Origin"/>, the page's
/// leading edge in this control's pixels, and labels count outwards from there in whole units.
/// </summary>
public class RulerControl : Control
{
    private const double PointsPerInch = 72;
    private const double PointsPerCentimeter = 72 / 2.54;
    private const double MinTickGap = 4;
    private const double MinLabelGap = 28;

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<RulerControl, Orientation>(nameof(Orientation));

    public static readonly StyledProperty<double> OriginProperty =
        AvaloniaProperty.Register<RulerControl, double>(nameof(Origin));

    public static readonly StyledProperty<double> PixelsPerPointProperty =
        AvaloniaProperty.Register<RulerControl, double>(nameof(PixelsPerPoint), 1);

    public static readonly StyledProperty<double> PageLengthProperty =
        AvaloniaProperty.Register<RulerControl, double>(nameof(PageLength));

    public static readonly StyledProperty<RulerUnit> UnitProperty =
        AvaloniaProperty.Register<RulerControl, RulerUnit>(nameof(Unit));

    public static readonly StyledProperty<double?> PointerPositionProperty =
        AvaloniaProperty.Register<RulerControl, double?>(nameof(PointerPosition));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<RulerControl, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> PageBrushProperty =
        AvaloniaProperty.Register<RulerControl, IBrush?>(nameof(PageBrush));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<RulerControl, IBrush?>(nameof(Foreground), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<RulerControl, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<RulerControl, IBrush?>(nameof(MarkerBrush));

    static RulerControl()
    {
        AffectsRender<RulerControl>(OrientationProperty, OriginProperty, PixelsPerPointProperty, PageLengthProperty,
            UnitProperty, PointerPositionProperty, BackgroundProperty, PageBrushProperty, ForegroundProperty,
            BorderBrushProperty, MarkerBrushProperty);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double Origin
    {
        get => GetValue(OriginProperty);
        set => SetValue(OriginProperty, value);
    }

    public double PixelsPerPoint
    {
        get => GetValue(PixelsPerPointProperty);
        set => SetValue(PixelsPerPointProperty, value);
    }

    public double PageLength
    {
        get => GetValue(PageLengthProperty);
        set => SetValue(PageLengthProperty, value);
    }

    public RulerUnit Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public double? PointerPosition
    {
        get => GetValue(PointerPositionProperty);
        set => SetValue(PointerPositionProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? PageBrush
    {
        get => GetValue(PageBrushProperty);
        set => SetValue(PageBrushProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public IBrush? MarkerBrush
    {
        get => GetValue(MarkerBrushProperty);
        set => SetValue(MarkerBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var length = horizontal ? Bounds.Width : Bounds.Height;
        var thickness = horizontal ? Bounds.Height : Bounds.Width;
        if (length <= 0 || thickness <= 0)
            return;

        if (Background is { } background)
            context.FillRectangle(background, new Rect(Bounds.Size));

        if (PageBrush is { } pageBrush && PageLength > 0)
        {
            var start = Math.Max(0, Origin);
            var end = Math.Min(length, Origin + PageLength);
            if (end > start)
                context.FillRectangle(pageBrush, Along(horizontal, start, 0, end - start, thickness));
        }

        if (BorderBrush is { } borderBrush)
        {
            var edge = horizontal ? new Rect(0, thickness - 1, length, 1) : new Rect(thickness - 1, 0, 1, length);
            context.FillRectangle(borderBrush, edge);
        }

        DrawTicks(context, horizontal, length, thickness);

        if (PointerPosition is { } pointer && MarkerBrush is { } marker && pointer >= 0 && pointer <= length)
            context.FillRectangle(marker, Along(horizontal, pointer - 0.5, 0, 1, thickness));
    }

    private void DrawTicks(DrawingContext context, bool horizontal, double length, double thickness)
    {
        var foreground = Foreground;
        if (foreground is null || PixelsPerPoint <= 0)
            return;

        var inches = Unit == RulerUnit.Inches;
        var unitPixels = (inches ? PointsPerInch : PointsPerCentimeter) * PixelsPerPoint;
        if (unitPixels <= 0)
            return;

        var subdivisions = inches ? 8 : 10;
        var halfStep = subdivisions / 2;
        var stepPixels = unitPixels / subdivisions;
        while (stepPixels < MinTickGap && subdivisions > 1)
        {
            subdivisions = subdivisions % 2 == 0 ? subdivisions / 2 : 1;
            halfStep = Math.Max(1, subdivisions / 2);
            stepPixels = unitPixels / subdivisions;
        }

        var labelEvery = Math.Max(1, (int)Math.Ceiling(MinLabelGap / unitPixels));
        var first = (int)Math.Floor(-Origin / stepPixels) - 1;
        var last = (int)Math.Ceiling((length - Origin) / stepPixels) + 1;

        for (var i = first; i <= last; i++)
        {
            var position = Origin + i * stepPixels;
            if (position < 0 || position > length)
                continue;

            var major = i % subdivisions == 0;
            var half = !major && subdivisions > 1 && i % halfStep == 0 && subdivisions % 2 == 0;
            var tick = major ? thickness * 0.55 : half ? thickness * 0.35 : thickness * 0.2;
            context.FillRectangle(foreground, Along(horizontal, Math.Round(position) - 0.5, thickness - tick, 1, tick));

            if (!major)
                continue;

            var unitIndex = Math.Abs(i / subdivisions);
            if (unitIndex % labelEvery != 0)
                continue;

            DrawLabel(context, foreground, horizontal, position, unitIndex.ToString(CultureInfo.CurrentCulture));
        }
    }

    private static void DrawLabel(DrawingContext context, IBrush brush, bool horizontal, double position, string text)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 9, brush);

        if (horizontal)
        {
            context.DrawText(formatted, new Point(position + 3, 1));
            return;
        }

        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(1, position - 3)))
            context.DrawText(formatted, default);
    }

    private static Rect Along(bool horizontal, double offset, double cross, double length, double crossLength) =>
        horizontal ? new Rect(offset, cross, length, crossLength) : new Rect(cross, offset, crossLength, length);
}
