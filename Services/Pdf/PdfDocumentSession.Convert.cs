using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using OpenPdfStudio.Services.Convert;
using PDFiumCore;
using SkiaSharp;

namespace OpenPdfStudio.Services.Pdf;

/// <summary>Reads pages as positioned text and images for conversion to a word processor.</summary>
public sealed partial class PdfDocumentSession
{
    private const int PageObjectImage = 3;
    private const int FontFlagItalic = 0x40;
    private const int BoldWeight = 600;
    private const int BitmapFormatGray = 1;
    private const int BitmapFormatBgr = 2;
    private const double MinPictureSize = 4;
    private const double BackgroundShare = 0.85;
    private const double ScanPixelsPerPoint = 2;
    private const double ScanMargin = 18;

    /// <summary>Rebuilds one page as paragraphs and images. A page with no text becomes a single picture of the page.</summary>
    public Task<PdfFlowPage> ExtractFlowPageAsync(int pageIndex, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= _pageSizes.Length)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));

            var size = _pageSizes[pageIndex];
            var text = GetTextPage(pageIndex);
            var chars = ReadFlowChars(text);
            if (!HasVisibleText(chars))
                return ScannedPage(pageIndex, size);

            var pictures = ReadPictures(text, size);
            return PdfFlowLayout.Build(size.Width, size.Height, chars, pictures);
        }, cancellationToken);

    private static bool HasVisibleText(List<PdfFlowChar> chars)
    {
        foreach (var c in chars)
        {
            if (!c.IsLineBreak && !char.IsWhiteSpace(c.Value))
                return true;
        }

        return false;
    }

    private static List<PdfFlowChar> ReadFlowChars(TextPage text)
    {
        var count = fpdf_text.FPDFTextCountChars(text.Text);
        var chars = new List<PdfFlowChar>(Math.Max(count, 0));
        var styles = new Dictionary<PdfFlowRunStyle, PdfFlowRunStyle>();
        var fontNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var nameBuffer = Marshal.AllocHGlobal(256);
        var previousBox = new Rect();

        try
        {
            for (var i = 0; i < count; i++)
            {
                var code = fpdf_text.FPDFTextGetUnicode(text.Text, i);
                if (code is 0 or 0xFFFE or 0xFFFF)
                    continue;

                if (code is '\r' or '\n')
                {
                    chars.Add(new PdfFlowChar('\n', previousBox, Plain, IsLineBreak: true));
                    continue;
                }

                var box = previousBox;
                double left = 0, right = 0, bottom = 0, top = 0;
                if (fpdf_text.FPDFTextGetCharBox(text.Text, i, ref left, ref right, ref bottom, ref top) != 0)
                {
                    var a = PageToDevice(text, left, top);
                    var b = PageToDevice(text, right, bottom);
                    box = new Rect(a, b).Normalize();
                }

                var value = (char)code;
                if (!char.IsWhiteSpace(value))
                    previousBox = box;

                chars.Add(new PdfFlowChar(value, box, ReadStyle(text, i, nameBuffer, fontNames, styles), false));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
        }

        return chars;
    }

    private static readonly PdfFlowRunStyle Plain = new("Calibri", 11, false, false, 0);

    private static PdfFlowRunStyle ReadStyle(TextPage text, int index, IntPtr nameBuffer,
        Dictionary<string, string> fontNames, Dictionary<PdfFlowRunStyle, PdfFlowRunStyle> styles)
    {
        var size = fpdf_text.FPDFTextGetFontSize(text.Text, index);
        if (size <= 0)
            size = 11;

        var flags = 0;
        var length = fpdf_text.FPDFTextGetFontInfo(text.Text, index, nameBuffer, 256, ref flags);
        var raw = length > 1 ? Marshal.PtrToStringUTF8(nameBuffer, (int)Math.Min(length - 1, 255)) ?? "" : "";
        if (!fontNames.TryGetValue(raw, out var family))
        {
            family = PdfFlowLayout.CleanFontName(raw);
            fontNames[raw] = family;
        }

        var weight = fpdf_text.FPDFTextGetFontWeight(text.Text, index);
        var bold = weight >= BoldWeight || raw.Contains("Bold", StringComparison.OrdinalIgnoreCase)
                                        || raw.Contains("Black", StringComparison.OrdinalIgnoreCase)
                                        || raw.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
        var italic = (flags & FontFlagItalic) != 0 || raw.Contains("Italic", StringComparison.OrdinalIgnoreCase)
                                                  || raw.Contains("Oblique", StringComparison.OrdinalIgnoreCase);

        uint r = 0, g = 0, b = 0, a = 0;
        var color = fpdf_text.FPDFTextGetFillColor(text.Text, index, ref r, ref g, ref b, ref a) != 0
            ? (int)((r << 16) | (g << 8) | b)
            : 0;

        var style = new PdfFlowRunStyle(family, Math.Round(size * 2) / 2, bold, italic, color)
        {
            Kind = PdfFlowLayout.GuessFontKind(family, flags)
        };
        if (styles.TryGetValue(style, out var shared))
            return shared;

        styles[style] = style;
        return style;
    }

    private List<PdfFlowPicture> ReadPictures(TextPage text, Size pageSize)
    {
        var pictures = new List<PdfFlowPicture>();
        var count = fpdf_edit.FPDFPageCountObjects(text.Page);
        for (var i = 0; i < count; i++)
        {
            var obj = fpdf_edit.FPDFPageGetObject(text.Page, i);
            if (obj is null || obj.__Instance == IntPtr.Zero || fpdf_edit.FPDFPageObjGetType(obj) != PageObjectImage)
                continue;

            float left = 0, bottom = 0, right = 0, top = 0;
            if (fpdf_edit.FPDFPageObjGetBounds(obj, ref left, ref bottom, ref right, ref top) == 0)
                continue;

            var bounds = new Rect(PageToDevice(text, left, top), PageToDevice(text, right, bottom)).Normalize();
            if (bounds.Width < MinPictureSize || bounds.Height < MinPictureSize)
                continue;

            // A picture covering nearly the whole page is a background or a scan under a text layer.
            if (bounds.Width * bounds.Height > pageSize.Width * pageSize.Height * BackgroundShare)
                continue;

            var bitmap = fpdf_edit.FPDFImageObjGetRenderedBitmap(_document, text.Page, obj);
            if (PdfHandles.IsNull(bitmap))
                continue;

            try
            {
                if (EncodePng(bitmap) is { } png)
                    pictures.Add(new PdfFlowPicture(bounds, png));
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(bitmap);
            }
        }

        return pictures;
    }

    private PdfFlowPage ScannedPage(int pageIndex, Size size)
    {
        var width = Math.Max(1, (int)Math.Round(size.Width * ScanPixelsPerPoint));
        var height = Math.Max(1, (int)Math.Round(size.Height * ScanPixelsPerPoint));
        var pixels = PdfPageRenderer.RenderToBuffer(_document, pageIndex, width, height, forPrinting: false);
        var png = EncodePng(pixels, width, height, width * 4, BitmapFormatBgra);

        var margins = new PdfFlowMargins(ScanMargin, ScanMargin, ScanMargin, ScanMargin);
        var boxWidth = size.Width - ScanMargin * 2;
        // A little short of the full height, so the picture's line never spills onto a new page.
        var boxHeight = size.Height - ScanMargin * 2 - 12;
        var scale = Math.Min(boxWidth / size.Width, boxHeight / size.Height);
        var w = size.Width * scale;
        var h = size.Height * scale;

        IReadOnlyList<PdfFlowBlock> blocks = png is null
            ? []
            : [new PdfFlowImage(ScanMargin, ScanMargin + h, 0, w, h, PdfFlowAlignment.Center, png)];
        return new PdfFlowPage(size.Width, size.Height, margins, blocks);
    }

    private static byte[]? EncodePng(FpdfBitmapT bitmap)
    {
        var width = fpdfview.FPDFBitmapGetWidth(bitmap);
        var height = fpdfview.FPDFBitmapGetHeight(bitmap);
        var stride = fpdfview.FPDFBitmapGetStride(bitmap);
        var format = fpdfview.FPDFBitmapGetFormat(bitmap);
        var buffer = fpdfview.FPDFBitmapGetBuffer(bitmap);
        if (width <= 0 || height <= 0 || buffer == IntPtr.Zero)
            return null;

        var source = new byte[stride * height];
        Marshal.Copy(buffer, source, 0, source.Length);
        return EncodePng(source, width, height, stride, format);
    }

    /// <summary>Encodes a PDFium bitmap buffer (gray, BGR, BGRx, or BGRA) as PNG.</summary>
    private static byte[]? EncodePng(byte[] source, int width, int height, int stride, int format)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var outRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var o = outRow + x * 4;
                switch (format)
                {
                    case BitmapFormatGray:
                        var v = source[row + x];
                        bgra[o] = v;
                        bgra[o + 1] = v;
                        bgra[o + 2] = v;
                        bgra[o + 3] = 255;
                        break;
                    case BitmapFormatBgr:
                        var p = row + x * 3;
                        bgra[o] = source[p];
                        bgra[o + 1] = source[p + 1];
                        bgra[o + 2] = source[p + 2];
                        bgra[o + 3] = 255;
                        break;
                    default:
                        var q = row + x * 4;
                        bgra[o] = source[q];
                        bgra[o + 1] = source[q + 1];
                        bgra[o + 2] = source[q + 2];
                        bgra[o + 3] = format == BitmapFormatBgra ? source[q + 3] : (byte)255;
                        break;
                }
            }
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(info, bgra);
        using var data = image?.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }
}
