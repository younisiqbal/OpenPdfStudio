using System;
using System.Runtime.InteropServices;
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
    private const int RenderForPrinting = 0x800;
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

    /// <summary>Renders into a packed BGRA buffer (stride = width * 4) on a white background.</summary>
    public static byte[] RenderToBuffer(FpdfDocumentT document, int pageIndex, int pixelWidth, int pixelHeight, bool forPrinting)
    {
        var page = fpdfview.FPDF_LoadPage(document, pageIndex);
        if (PdfHandles.IsNull(page))
            throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");

        var pixels = new byte[pixelWidth * pixelHeight * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var target = fpdfview.FPDFBitmapCreateEx(pixelWidth, pixelHeight, BitmapFormatBgra,
                handle.AddrOfPinnedObject(), pixelWidth * 4);
            if (PdfHandles.IsNull(target))
                throw new InvalidOperationException("PDFium could not allocate a render target.");

            try
            {
                var flags = RenderAnnotations | (forPrinting ? RenderForPrinting : 0);
                fpdfview.FPDFBitmapFillRect(target, 0, 0, pixelWidth, pixelHeight, White);
                fpdfview.FPDF_RenderPageBitmap(target, page, 0, 0, pixelWidth, pixelHeight, 0, flags);
            }
            finally
            {
                fpdfview.FPDFBitmapDestroy(target);
            }
        }
        finally
        {
            handle.Free();
            fpdfview.FPDF_ClosePage(page);
        }

        return pixels;
    }
}

internal static class PdfHandles
{
    public static bool IsNull(FpdfDocumentT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfPageT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfTextpageT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfBitmapT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfBookmarkT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfDestT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfActionT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfAnnotationT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
    public static bool IsNull(FpdfAttachmentT? handle) => handle is null || handle.__Instance == IntPtr.Zero;
}
