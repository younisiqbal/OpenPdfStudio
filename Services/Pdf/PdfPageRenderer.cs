using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PDFiumCore;

namespace OpenPdfStudio.Services.Pdf;

/// <summary>Renders a PDFium page straight into an Avalonia bitmap. Worker thread only.</summary>
internal static class PdfPageRenderer
{
    private const int BitmapFormatBgra = 4;
    private const int RenderAnnotations = 0x01;
    private const ulong White = 0xFFFFFFFF;

    public static WriteableBitmap Render(FpdfDocumentT document, int pageIndex, int pixelWidth, int pixelHeight)
    {
        var page = fpdfview.FPDF_LoadPage(document, pageIndex);
        if (PdfHandles.IsNull(page))
            throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");

        var bitmap = new WriteableBitmap(
            new PixelSize(pixelWidth, pixelHeight),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        try
        {
            using var frame = bitmap.Lock();
            var target = fpdfview.FPDFBitmapCreateEx(pixelWidth, pixelHeight, BitmapFormatBgra, frame.Address, frame.RowBytes);
            if (PdfHandles.IsNull(target))
                throw new InvalidOperationException("PDFium could not allocate a render target.");

            try
            {
                fpdfview.FPDFBitmapFillRect(target, 0, 0, pixelWidth, pixelHeight, White);
                fpdfview.FPDF_RenderPageBitmap(target, page, 0, 0, pixelWidth, pixelHeight, 0, RenderAnnotations);
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(target);
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }

        return bitmap;
    }
}

internal static class PdfHandles
{
    public static bool IsNull(FpdfDocumentT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfPageT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfTextpageT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfBitmapT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
}
