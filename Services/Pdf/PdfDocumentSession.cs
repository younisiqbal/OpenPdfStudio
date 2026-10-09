using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using PDFiumCore;

namespace OpenPdfStudio.Services.Pdf;

public enum PdfOpenError
{
    FileNotFound,
    InvalidFormat,
    PasswordRequired,
    UnsupportedSecurity,
    Unknown
}

public sealed class PdfOpenException(PdfOpenError error, string message) : Exception(message)
{
    public PdfOpenError Error { get; } = error;
}

/// <summary>
/// One open PDF document. All public methods marshal onto <see cref="PdfiumWorker"/>.
/// Page coordinates exposed here are in PDF points with a top-left origin, matching how pages are drawn.
/// </summary>
public sealed class PdfDocumentSession : IDisposable
{
    private const int MaxCachedTextPages = 8;
    private const double DeviceScale = 20;
    private const double HitTolerance = 6;
    private const double LinePaddingX = 0.5;
    private const double LinePaddingY = 1;

    private readonly PdfiumWorker _worker;
    private readonly FpdfDocumentT _document;
    private readonly IntPtr _buffer;
    private readonly Size[] _pageSizes;
    private readonly Dictionary<int, TextPage> _textPages = new();
    private readonly LinkedList<int> _textPageOrder = new();
    private volatile bool _disposed;

    private sealed record TextPage(FpdfPageT Page, FpdfTextpageT Text, int DeviceWidth, int DeviceHeight);

    private PdfDocumentSession(PdfiumWorker worker, string filePath, FpdfDocumentT document, IntPtr buffer, Size[] pageSizes)
    {
        _worker = worker;
        _document = document;
        _buffer = buffer;
        _pageSizes = pageSizes;
        FilePath = filePath;
    }

    public string FilePath { get; }
    public int PageCount => _pageSizes.Length;
    public IReadOnlyList<Size> PageSizes => _pageSizes;

    public static Task<PdfDocumentSession> OpenAsync(string filePath, string? password)
    {
        var worker = PdfiumWorker.Instance;
        return worker.InvokeAsync(() => OpenOnWorker(worker, filePath, password));
    }

    public Task<WriteableBitmap> RenderPageAsync(int pageIndex, int pixelWidth, int pixelHeight,
        CancellationToken cancellationToken, bool lowPriority = false) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            return PdfPageRenderer.Render(_document, pageIndex, pixelWidth, pixelHeight);
        }, cancellationToken, lowPriority);

    /// <summary>Returns the character index under a point, or -1 when there is no text there.</summary>
    public Task<int> HitTestCharAsync(int pageIndex, Point point, double tolerance = HitTolerance) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            var text = GetTextPage(pageIndex);
            DeviceToPage(text, point, out var x, out var y);
            return fpdf_text.FPDFTextGetCharIndexAtPos(text.Text, x, y, tolerance, tolerance);
        });

    public Task<IReadOnlyList<Rect>> GetTextRectsAsync(int pageIndex, int startIndex, int count) =>
        _worker.InvokeAsync<IReadOnlyList<Rect>>(() =>
        {
            ThrowIfDisposed();
            var text = GetTextPage(pageIndex);
            var rectCount = fpdf_text.FPDFTextCountRects(text.Text, startIndex, count);
            var rects = new List<Rect>(Math.Max(rectCount, 0));
            for (int i = 0; i < rectCount; i++)
            {
                double left = 0, top = 0, right = 0, bottom = 0;
                if (fpdf_text.FPDFTextGetRect(text.Text, i, ref left, ref top, ref right, ref bottom) == 0)
                    continue;

                var a = PageToDevice(text, left, top);
                var b = PageToDevice(text, right, bottom);
                rects.Add(new Rect(a, b).Normalize());
            }

            return MergeIntoLines(rects);
        });

    public Task<string> GetTextAsync(int pageIndex, int startIndex, int count) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            if (count <= 0)
                return string.Empty;

            var text = GetTextPage(pageIndex);
            var buffer = new ushort[count + 1];
            var written = fpdf_text.FPDFTextGetText(text.Text, startIndex, count, ref buffer[0]);
            var length = Math.Clamp(written - 1, 0, count);
            return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, length)));
        });

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _ = _worker.InvokeAsync(CloseOnWorker);
    }

    private static PdfDocumentSession OpenOnWorker(PdfiumWorker worker, string filePath, string? password)
    {
        if (!File.Exists(filePath))
            throw new PdfOpenException(PdfOpenError.FileNotFound, "The file could not be found.");

        var buffer = IntPtr.Zero;
        FpdfDocumentT? document;

        if (IsAscii(filePath))
        {
            document = fpdfview.FPDF_LoadDocument(filePath, password);
        }
        else
        {
            var bytes = File.ReadAllBytes(filePath);
            buffer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            document = fpdfview.FPDF_LoadMemDocument64(buffer, (ulong)bytes.Length, password);
        }

        if (PdfHandles.IsNull(document))
        {
            var error = fpdfview.FPDF_GetLastError();
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);

            throw error switch
            {
                2 => new PdfOpenException(PdfOpenError.FileNotFound, "The file could not be found or opened."),
                3 => new PdfOpenException(PdfOpenError.InvalidFormat, "The file is not a valid PDF, or it is damaged."),
                4 => new PdfOpenException(PdfOpenError.PasswordRequired, "This PDF is password protected."),
                5 => new PdfOpenException(PdfOpenError.UnsupportedSecurity, "This PDF uses an unsupported security handler."),
                _ => new PdfOpenException(PdfOpenError.Unknown, "The PDF could not be opened.")
            };
        }

        var pageCount = Math.Max(fpdfview.FPDF_GetPageCount(document), 0);
        var sizes = new Size[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            double width = 0, height = 0;
            fpdfview.FPDF_GetPageSizeByIndex(document, i, ref width, ref height);
            sizes[i] = new Size(width > 0 ? width : 612, height > 0 ? height : 792);
        }

        return new PdfDocumentSession(worker, filePath, document!, buffer, sizes);
    }

    private TextPage GetTextPage(int pageIndex)
    {
        if (_textPages.TryGetValue(pageIndex, out var cached))
        {
            _textPageOrder.Remove(pageIndex);
            _textPageOrder.AddFirst(pageIndex);
            return cached;
        }

        var page = fpdfview.FPDF_LoadPage(_document, pageIndex);
        if (PdfHandles.IsNull(page))
            throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");

        var text = fpdf_text.FPDFTextLoadPage(page);
        if (PdfHandles.IsNull(text))
        {
            fpdfview.FPDF_ClosePage(page);
            throw new InvalidOperationException($"Text on page {pageIndex + 1} could not be read.");
        }

        var size = _pageSizes[pageIndex];
        var entry = new TextPage(page, text,
            (int)Math.Round(size.Width * DeviceScale),
            (int)Math.Round(size.Height * DeviceScale));

        _textPages[pageIndex] = entry;
        _textPageOrder.AddFirst(pageIndex);

        while (_textPageOrder.Count > MaxCachedTextPages)
        {
            var evicted = _textPageOrder.Last!.Value;
            _textPageOrder.RemoveLast();
            CloseTextPage(_textPages[evicted]);
            _textPages.Remove(evicted);
        }

        return entry;
    }

    /// <summary>
    /// Joins PDFium's per-run rects into one box per line so the highlight has an even height.
    /// Runs far apart on the same baseline (for example two columns) stay separate.
    /// </summary>
    private static List<Rect> MergeIntoLines(List<Rect> rects)
    {
        var lines = new List<Rect>(rects.Count);
        foreach (var rect in rects)
        {
            if (lines.Count > 0 && IsSameLine(lines[^1], rect))
                lines[^1] = lines[^1].Union(rect);
            else
                lines.Add(rect);
        }

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            lines[i] = new Rect(line.X - LinePaddingX, line.Y - LinePaddingY,
                line.Width + LinePaddingX * 2, line.Height + LinePaddingY * 2);
        }

        return lines;
    }

    private static bool IsSameLine(Rect line, Rect rect)
    {
        var overlap = Math.Min(line.Bottom, rect.Bottom) - Math.Max(line.Top, rect.Top);
        var smallerHeight = Math.Min(line.Height, rect.Height);
        if (smallerHeight <= 0 || overlap < smallerHeight * 0.5)
            return false;

        var gap = Math.Max(rect.Left - line.Right, line.Left - rect.Right);
        return gap <= Math.Max(line.Height, rect.Height) * 2;
    }

    private static void DeviceToPage(TextPage text, Point point, out double x, out double y)
    {
        x = 0;
        y = 0;
        fpdfview.FPDF_DeviceToPage(text.Page, 0, 0, text.DeviceWidth, text.DeviceHeight, 0,
            (int)Math.Round(point.X * DeviceScale), (int)Math.Round(point.Y * DeviceScale), ref x, ref y);
    }

    private static Point PageToDevice(TextPage text, double x, double y)
    {
        int deviceX = 0, deviceY = 0;
        fpdfview.FPDF_PageToDevice(text.Page, 0, 0, text.DeviceWidth, text.DeviceHeight, 0, x, y, ref deviceX, ref deviceY);
        return new Point(deviceX / DeviceScale, deviceY / DeviceScale);
    }

    private void CloseOnWorker()
    {
        foreach (var text in _textPages.Values)
            CloseTextPage(text);

        _textPages.Clear();
        _textPageOrder.Clear();
        fpdfview.FPDF_CloseDocument(_document);

        if (_buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_buffer);
    }

    private static void CloseTextPage(TextPage text)
    {
        fpdf_text.FPDFTextClosePage(text.Text);
        fpdfview.FPDF_ClosePage(text.Page);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PdfDocumentSession));
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 127)
                return false;
        }

        return true;
    }
}
