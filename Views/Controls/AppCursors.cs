using System;
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

    private const string PalmPath =
        "M9,8 A2,2 0 0 1 13,8 V18 H9 Z " +
        "M13.5,6 A2,2 0 0 1 17.5,6 V18 H13.5 Z " +
        "M18,7 A2,2 0 0 1 22,7 V18 H18 Z " +
        "M22.5,10 A1.75,1.75 0 0 1 26,10 V19 H22.5 Z " +
        "M9,16 H26 V22 C26,26 23,28 19,28 H15 C11,28 9,26 9,23 Z " +
        "M9,18 L5.5,14.5 A2,2 0 0 0 2.7,17.3 L9,25 Z";

    private const string FistPath =
        "M9,13 A2,2 0 0 1 13,13 V18 H9 Z " +
        "M13.5,12 A2,2 0 0 1 17.5,12 V18 H13.5 Z " +
        "M18,12.5 A2,2 0 0 1 22,12.5 V18 H18 Z " +
        "M22.5,14 A1.75,1.75 0 0 1 26,14 V19 H22.5 Z " +
        "M9,16 H26 V22 C26,26 23,28 19,28 H15 C11,28 9,26 9,23 Z " +
        "M9,18 L6.5,17 A1.75,1.75 0 0 0 5.5,20.3 L9,24 Z";

    private static Cursor? _ibeam;
    private static Cursor? _palm;
    private static Cursor? _fist;
    private static Cursor? _moveArrows;
    private static Cursor? _pointingFinger;

    public static Cursor Ibeam => _ibeam ??= new Cursor(StandardCursorType.Ibeam);

    public static Cursor GetHover(HandCursorStyle style) => style switch
    {
        HandCursorStyle.PalmAndFist or HandCursorStyle.Palm => _palm ??= Draw(PalmPath),
        HandCursorStyle.MoveArrows => _moveArrows ??= new Cursor(StandardCursorType.SizeAll),
        _ => PointingFinger
    };

    public static Cursor GetDrag(HandCursorStyle style) => style switch
    {
        HandCursorStyle.PalmAndFist => _fist ??= Draw(FistPath),
        _ => GetHover(style)
    };

    private static Cursor PointingFinger => _pointingFinger ??= new Cursor(StandardCursorType.Hand);

    private static Cursor Draw(string pathData)
    {
        try
        {
            var geometry = StreamGeometry.Parse(pathData);
            var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                // Stroke first, then fill on top: only the outer outline and the gaps between fingers stay black.
                context.DrawGeometry(null, new Pen(Brushes.Black, 2.4, lineJoin: PenLineJoin.Round), geometry);
                context.DrawGeometry(Brushes.White, null, geometry);
            }

            return new Cursor(bitmap, new PixelPoint(Size / 2, Size / 2));
        }
        catch (Exception)
        {
            return PointingFinger;
        }
    }
}
