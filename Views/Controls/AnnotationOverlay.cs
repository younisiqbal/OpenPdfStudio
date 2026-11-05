using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OpenPdfStudio.Models;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views.Controls;

/// <summary>
/// Draws selection outlines, resize handles and the in-progress drag box over a page.
/// Hit testing stays in DocumentView; this control does not receive pointer events.
/// </summary>
public class AnnotationOverlay : Control
{
    public const double HandleSize = 6;

    public static readonly StyledProperty<IEnumerable<AnnotationViewModel>?> AnnotationsProperty =
        AvaloniaProperty.Register<AnnotationOverlay, IEnumerable<AnnotationViewModel>?>(nameof(Annotations));

    public static readonly StyledProperty<double> PageWidthPointsProperty =
        AvaloniaProperty.Register<AnnotationOverlay, double>(nameof(PageWidthPoints), 612);

    public static readonly StyledProperty<Rect?> DraftRectProperty =
        AvaloniaProperty.Register<AnnotationOverlay, Rect?>(nameof(DraftRect));

    private INotifyCollectionChanged? _collection;

    static AnnotationOverlay()
    {
        AffectsRender<AnnotationOverlay>(AnnotationsProperty, PageWidthPointsProperty, DraftRectProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<AnnotationOverlay>(false);
        AnnotationsProperty.Changed.AddClassHandler<AnnotationOverlay>((c, _) => c.BindCollection());
    }

    public IEnumerable<AnnotationViewModel>? Annotations
    {
        get => GetValue(AnnotationsProperty);
        set => SetValue(AnnotationsProperty, value);
    }

    public double PageWidthPoints
    {
        get => GetValue(PageWidthPointsProperty);
        set => SetValue(PageWidthPointsProperty, value);
    }

    public Rect? DraftRect
    {
        get => GetValue(DraftRectProperty);
        set => SetValue(DraftRectProperty, value);
    }

    private void BindCollection()
    {
        if (_collection is not null)
        {
            _collection.CollectionChanged -= OnCollectionChanged;
            if (Annotations is not null)
            {
                foreach (var item in Annotations)
                    item.PropertyChanged -= OnItemChanged;
            }
        }

        _collection = Annotations as INotifyCollectionChanged;
        if (_collection is not null)
            _collection.CollectionChanged += OnCollectionChanged;
        if (Annotations is not null)
        {
            foreach (var item in Annotations)
                item.PropertyChanged += OnItemChanged;
        }

        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (AnnotationViewModel item in e.OldItems)
                item.PropertyChanged -= OnItemChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (AnnotationViewModel item in e.NewItems)
                item.PropertyChanged += OnItemChanged;
        }

        InvalidateVisual();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        if (PageWidthPoints <= 0)
            return;

        var scale = Bounds.Width / PageWidthPoints;
        var stroke = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1);
        var handleFill = Brushes.White;
        var handleStroke = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1);
        var draft = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1) { DashStyle = DashStyle.Dash };

        if (Annotations is not null)
        {
            foreach (var annotation in Annotations)
            {
                if ((annotation.IsEditing && annotation.Kind != AnnotationKind.Note) || !annotation.IsSelected)
                    continue;

                var model = annotation.Model;
                if (model.IsMarkup)
                    continue;

                if (model.Kind == AnnotationKind.Note)
                    DrawNoteIcon(context, model, scale);
                else
                {
                    context.DrawRectangle(null, stroke, Scale(model.Bounds, scale));
                    foreach (var at in Handles(model.Bounds, scale))
                        DrawHandle(context, at, handleFill, handleStroke);
                }
            }
        }

        if (DraftRect is { } rect && rect.Width > 0 && rect.Height > 0)
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 120, 215)), draft, Scale(rect, scale));
    }

    private static void DrawNoteIcon(DrawingContext context, PdfAnnotation model, double scale)
    {
        var dest = Scale(model.Bounds, scale);
        if (dest.Width <= 0 || dest.Height <= 0)
            return;

        var (body, detail) = NoteIcons.Get(model.Style.Icon);
        if (ParseIcon(body) is not { } bodyGeometry)
            return;

        var iconScale = dest.Width / NoteIcons.Size;
        using (context.PushTransform(Matrix.CreateScale(iconScale, iconScale) * Matrix.CreateTranslation(dest.X, dest.Y)))
        {
            var fill = new SolidColorBrush(model.Style.Color);
            var outline = new Pen(new SolidColorBrush(Darken(model.Style.Color)), 0.8);
            context.DrawGeometry(fill, outline, bodyGeometry);
            if (!string.IsNullOrEmpty(detail) && ParseIcon(detail) is { } detailGeometry)
                context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(40, 40, 40)), 1.3), detailGeometry);
        }
    }

    private static StreamGeometry? ParseIcon(string path)
    {
        try
        {
            return StreamGeometry.Parse(path);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Color Darken(Color color) =>
        Color.FromRgb((byte)(color.R * 0.55), (byte)(color.G * 0.55), (byte)(color.B * 0.55));

    private static void DrawHandle(DrawingContext context, Point center, IBrush fill, Pen stroke)
    {
        var size = HandleSize;
        context.DrawRectangle(fill, stroke, new Rect(center.X - size / 2, center.Y - size / 2, size, size));
    }

    private static IEnumerable<Point> Handles(Rect b, double scale)
    {
        yield return ScalePoint(b.TopLeft, scale);
        yield return ScalePoint(new Point(b.Center.X, b.Top), scale);
        yield return ScalePoint(b.TopRight, scale);
        yield return ScalePoint(new Point(b.Right, b.Center.Y), scale);
        yield return ScalePoint(b.BottomRight, scale);
        yield return ScalePoint(new Point(b.Center.X, b.Bottom), scale);
        yield return ScalePoint(b.BottomLeft, scale);
        yield return ScalePoint(new Point(b.Left, b.Center.Y), scale);
    }

    private static Rect Scale(Rect rect, double scale) =>
        new(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);

    private static Point ScalePoint(Point point, double scale) => new(point.X * scale, point.Y * scale);
}
