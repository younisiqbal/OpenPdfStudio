using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OpenPdfStudio.Models;

namespace OpenPdfStudio.Views.Controls;

/// <summary>
/// Cursors used by the page viewer. The palm and fist are drawn at runtime from our own paths,
/// so there are no image assets to ship or license. Instances are created once and reused.
/// </summary>
public static class AppCursors
{
    private const int Size = 32;

    // Outline open hand (same silhouette as HandIcon): four fingers up, thumb left.
    private const string PalmPath =
        "M10.2,4.8 A1.8,1.8 0 0,1 13.8,4.8 V14.2 H14.8 V3.4 A1.8,1.8 0 0,1 18.4,3.4 V14.2 H19.4 V6.6 " +
        "A1.8,1.8 0 0,1 23,6.6 V16.6 A7,7 0 0,1 16,23.6 H14.8 A6.2,6.2 0 0,1 9.6,20.4 L5,14.4 " +
        "A1.7,1.7 0 1,1 7.8,12.2 L10.2,15.2 V4.8 Z";

    private const string FistPath =
        "M10.4,11.4 A1.6,1.6 0 0,1 13.6,11.4 V16.2 H14.6 V10.8 A1.6,1.6 0 0,1 17.8,10.8 V16.2 H18.8 V12.2 " +
        "A1.6,1.6 0 0,1 22,12.2 V17.2 A5.6,5.6 0 0,1 16.4,22.8 H15.2 A5,5 0 0,1 11,20.2 L7.6,16.2 " +
        "A1.5,1.5 0 1,1 10,14.2 L10.4,14.6 V11.4 Z";

    // Same outline as HighlightIcon: rounded barrel, short collar, square chisel tip.
    private const string HighlighterOuter =
        "M4.8,19 L6.4,17.4 L7.2,14.2 L16,5.4 A2.6,2.6 0 0 1 19.6,9 L10.8,17.8 L8.2,19.2 L6.4,20.6 A1.3,1.3 0 0 1 4.8,19 Z";
    private const string HighlighterBarrelHole =
        "M9,13.6 L16.4,6.2 A1.2,1.2 0 0 1 18,7.8 L10.6,15.2 Z";
    private const string HighlighterTipHole =
        "M5.8,18.8 L6.6,18 L7.4,18.8 L6.6,19.6 Z";

    private static Cursor? _ibeam;
    private static readonly Dictionary<uint, Cursor> HighlighterCache = new();
    private static Cursor? _palm;
    private static Cursor? _fist;
    private static Cursor? _moveArrows;
    private static Cursor? _pointingFinger;
    private static Cursor? _markupHover;

    public static Cursor Ibeam => _ibeam ??= new Cursor(StandardCursorType.Ibeam);

    /// <summary>Filled triangle with the tip up; used when a markup tool hovers an existing mark.</summary>
    public static Cursor MarkupHover => _markupHover ??= DrawMarkupHover();

    public static Cursor Highlighter =>
        HighlighterFor(Color.FromRgb(255, 235, 0));

    public static Cursor HighlighterFor(Color color)
    {
        if (HighlighterCache.TryGetValue(color.ToUInt32(), out var cursor))
            return cursor;

        cursor = DrawHighlighter(color);
        HighlighterCache[color.ToUInt32()] = cursor;
        return cursor;
    }

    public static Cursor GetHover(HandCursorStyle style) => style switch
    {
        HandCursorStyle.PalmAndFist or HandCursorStyle.Palm => _palm ??= Draw(PalmPath, new PixelPoint(12, 16)),
        HandCursorStyle.MoveArrows => _moveArrows ??= new Cursor(StandardCursorType.SizeAll),
        _ => PointingFinger
    };

    public static Cursor GetDrag(HandCursorStyle style) => style switch
    {
        HandCursorStyle.PalmAndFist => _fist ??= Draw(FistPath, new PixelPoint(13, 17)),
        _ => GetHover(style)
    };

    private static Cursor PointingFinger => _pointingFinger ??= new Cursor(StandardCursorType.Hand);

    private static Cursor DrawMarkupHover()
    {
        try
        {
            var geometry = StreamGeometry.Parse("M16,4 L28,26 L16,20 L4,26 Z");
            var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                // Tip stays at (16, 4); rotate CCW so the point matches the default arrow.
                var aroundTip =
                    Matrix.CreateTranslation(-16, -4) *
                    Matrix.CreateScale(0.72, 0.72) *
                    Matrix.CreateRotation(-28 * Math.PI / 180) *
                    Matrix.CreateTranslation(16, 4);
                using (context.PushTransform(aroundTip))
                {
                    var outline = new Pen(new SolidColorBrush(Color.FromRgb(20, 20, 20)), 1.1,
                        lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
                    context.DrawGeometry(Brushes.Black, outline, geometry);
                }
            }

            return new Cursor(bitmap, new PixelPoint(16, 4));
        }
        catch (Exception)
        {
            return new Cursor(StandardCursorType.Arrow);
        }
    }

    private static Cursor DrawHighlighter(Color color)
    {
        try
        {
            var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                using (context.PushTransform(Matrix.CreateScale(1.22, 1.22) * Matrix.CreateTranslation(1, 1)))
                {
                    var outer = StreamGeometry.Parse(HighlighterOuter);
                    var barrelHole = StreamGeometry.Parse(HighlighterBarrelHole);
                    var tipHole = StreamGeometry.Parse(HighlighterTipHole);
                    var outline = new Pen(new SolidColorBrush(Color.FromRgb(28, 28, 28)), 1.15,
                        lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);

                    context.DrawGeometry(new SolidColorBrush(color), outline, outer);
                    context.DrawGeometry(Brushes.White, null, barrelHole);
                    context.DrawGeometry(Brushes.White, null, tipHole);
                }
            }

            return new Cursor(bitmap, new PixelPoint(7, 24));
        }
        catch (Exception)
        {
            return Ibeam;
        }
    }

    private static Cursor Draw(string pathData, PixelPoint hotspot)
    {
        try
        {
            var geometry = StreamGeometry.Parse(pathData);
            var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                var outline = new Pen(new SolidColorBrush(Color.FromRgb(20, 20, 20)), 1.25,
                    lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
                context.DrawGeometry(Brushes.White, outline, geometry);
            }

            return new Cursor(bitmap, hotspot);
        }
        catch (Exception)
        {
            return PointingFinger;
        }
    }
}
