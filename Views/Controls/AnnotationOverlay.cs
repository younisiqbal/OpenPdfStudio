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
/// Draws selection outlines, resize handles, callout targets and the in-progress drag box over a page.
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

    public static readonly StyledProperty<Point?> DraftCalloutTargetProperty =
        AvaloniaProperty.Register<AnnotationOverlay, Point?>(nameof(DraftCalloutTarget));

    private INotifyCollectionChanged? _collection;

    static AnnotationOverlay()
    {
        AffectsRender<AnnotationOverlay>(AnnotationsProperty, PageWidthPointsProperty, DraftRectProperty,
            DraftCalloutTargetProperty);
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

    public Point? DraftCalloutTarget
    {
        get => GetValue(DraftCalloutTargetProperty);
        set => SetValue(DraftCalloutTargetProperty, value);
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
                if (annotation.IsEditing || !annotation.IsSelected)
                    continue;

                var model = annotation.Model;
                if (model.IsMarkup)
                {
                    foreach (var quad in model.Quads)
                        context.DrawRectangle(null, stroke, Scale(quad, scale));
                }
                else
                {
                    context.DrawRectangle(null, stroke, Scale(model.Bounds, scale));
                    if (model.Kind is not AnnotationKind.Note)
                    {
                        foreach (var at in Handles(model.Bounds, scale))
                            DrawHandle(context, at, handleFill, handleStroke);
                    }

                    if (model.CalloutTarget is { } target)
                    {
                        var from = ScalePoint(AppearanceBuilderAnchor(model.Bounds, target), scale);
                        var to = ScalePoint(target, scale);
                        context.DrawLine(stroke, from, to);
                        DrawHandle(context, to, handleFill, handleStroke);
                    }
                }
            }
        }

        if (DraftRect is { } rect && rect.Width > 0 && rect.Height > 0)
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 120, 215)), draft, Scale(rect, scale));
            if (DraftCalloutTarget is { } target)
                context.DrawLine(draft, ScalePoint(AppearanceBuilderAnchor(rect, target), scale), ScalePoint(target, scale));
        }
    }

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

    private static Point AppearanceBuilderAnchor(Rect box, Point target) =>
        OpenPdfStudio.Services.Pdf.Annotations.AppearanceBuilder.CalloutAnchor(box, target);
}
