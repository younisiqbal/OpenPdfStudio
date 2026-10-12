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
    private const int AnnotLink = 2;
    private const int AnnotPopup = 16;
    private const int AnnotWidget = 20;

    private readonly PdfiumWorker _worker;
    private readonly FpdfDocumentT _document;
    private readonly IntPtr _buffer;
    private readonly Size[] _pageSizes;
    private readonly Dictionary<int, TextPage> _textPages = new();
    private readonly LinkedList<int> _textPageOrder = new();
    private readonly Dictionary<int, string> _pageTexts = new();
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
            return ComputeTextRects(GetTextPage(pageIndex), startIndex, count);
        });

    /// <summary>Returns every character on the page as one string. Cached for the life of the session.</summary>
    public Task<string> GetPageTextAsync(int pageIndex, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            return ReadPageText(pageIndex);
        }, cancellationToken, lowPriority: true);

    /// <summary>Searches the text of one page and returns each match with its highlight rects.</summary>
    public Task<IReadOnlyList<PdfSearchHit>> SearchPageTextAsync(int pageIndex, PdfSearchOptions options,
        CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync<IReadOnlyList<PdfSearchHit>>(() =>
        {
            ThrowIfDisposed();
            var pageText = ReadPageText(pageIndex);
            var matches = TextMatcher.Find(pageText, options);
            if (matches.Count == 0)
                return [];

            var text = GetTextPage(pageIndex);
            var hits = new List<PdfSearchHit>(matches.Count);
            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rects = ComputeTextRects(text, match.Start, match.Length);
                hits.Add(PdfSearchEngine.CreateHit(SearchResultKind.PageText, pageIndex, pageText, match, rects));
            }

            return hits;
        }, cancellationToken, lowPriority: true);

    /// <summary>Returns the document outline, flattened in reading order.</summary>
    public Task<IReadOnlyList<PdfBookmark>> GetBookmarksAsync(CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync<IReadOnlyList<PdfBookmark>>(() =>
        {
            ThrowIfDisposed();
            var bookmarks = new List<PdfBookmark>();
            var visited = new HashSet<IntPtr>();
            CollectBookmarks(fpdf_doc.FPDFBookmarkGetFirstChild(_document, null), 0, bookmarks, visited);
            return bookmarks;
        }, cancellationToken, lowPriority: true);

    /// <summary>Returns the text of comments and markup annotations on a page. Links and form fields are skipped.</summary>
    public Task<IReadOnlyList<PdfComment>> GetCommentsAsync(int pageIndex, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync<IReadOnlyList<PdfComment>>(() =>
        {
            ThrowIfDisposed();
            var text = GetTextPage(pageIndex);
            var count = fpdf_annot.FPDFPageGetAnnotCount(text.Page);
            var comments = new List<PdfComment>();

            for (int i = 0; i < count; i++)
            {
                var annot = fpdf_annot.FPDFPageGetAnnot(text.Page, i);
                if (PdfHandles.IsNull(annot))
                    continue;

                try
                {
                    var subtype = fpdf_annot.FPDFAnnotGetSubtype(annot);
                    if (subtype is AnnotLink or AnnotPopup or AnnotWidget)
                        continue;

                    var contents = ReadUtf16((ref ushort buffer, ulong length) =>
                        fpdf_annot.FPDFAnnotGetStringValue(annot, "Contents", ref buffer, length));
                    if (string.IsNullOrWhiteSpace(contents))
                        continue;

                    var bounds = new Rect();
                    var rect = new FS_RECTF_();
                    if (fpdf_annot.FPDFAnnotGetRect(annot, rect) != 0)
                    {
                        var a = PageToDevice(text, rect.Left, rect.Top);
                        var b = PageToDevice(text, rect.Right, rect.Bottom);
                        bounds = new Rect(a, b).Normalize();
                    }

                    comments.Add(new PdfComment(contents, bounds));
                }
                finally
                {
                    fpdf_annot.FPDFPageCloseAnnot(annot);
                }
            }

            return comments;
        }, cancellationToken, lowPriority: true);

    public Task<IReadOnlyList<string>> GetAttachmentNamesAsync(CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync<IReadOnlyList<string>>(() =>
        {
            ThrowIfDisposed();
            var count = fpdf_attachment.FPDFDocGetAttachmentCount(_document);
            var names = new List<string>(Math.Max(count, 0));
            for (int i = 0; i < count; i++)
            {
                var attachment = fpdf_attachment.FPDFDocGetAttachment(_document, i);
                if (PdfHandles.IsNull(attachment))
                    continue;

                var name = ReadUtf16((ref ushort buffer, ulong length) =>
                    fpdf_attachment.FPDFAttachmentGetName(attachment, ref buffer, length));
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }

            return names;
        }, cancellationToken, lowPriority: true);

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

    private string ReadPageText(int pageIndex)
    {
        if (_pageTexts.TryGetValue(pageIndex, out var cached))
            return cached;

        var text = GetTextPage(pageIndex);
        var count = fpdf_text.FPDFTextCountChars(text.Text);
        var value = string.Empty;
        if (count > 0)
        {
            var buffer = new ushort[count + 1];
            var written = fpdf_text.FPDFTextGetText(text.Text, 0, count, ref buffer[0]);
            var length = Math.Clamp(written - 1, 0, count);
            value = new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, length)));
        }

        _pageTexts[pageIndex] = value;
        return value;
    }

    private static List<Rect> ComputeTextRects(TextPage text, int startIndex, int count)
    {
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
    }

    private void CollectBookmarks(FpdfBookmarkT? bookmark, int depth, List<PdfBookmark> into, HashSet<IntPtr> visited)
    {
        while (!PdfHandles.IsNull(bookmark) && visited.Add(bookmark!.__Instance))
        {
            var title = ReadBookmarkTitle(bookmark);
            into.Add(new PdfBookmark(title, depth, BookmarkPageIndex(bookmark)));
            CollectBookmarks(fpdf_doc.FPDFBookmarkGetFirstChild(_document, bookmark), depth + 1, into, visited);
            bookmark = fpdf_doc.FPDFBookmarkGetNextSibling(_document, bookmark);
        }
    }

    private static string ReadBookmarkTitle(FpdfBookmarkT bookmark)
    {
        var bytes = fpdf_doc.FPDFBookmarkGetTitle(bookmark, IntPtr.Zero, 0);
        if (bytes <= 2)
            return string.Empty;

        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(bookmark, buffer, bytes);
            return Marshal.PtrToStringUni(buffer, (int)(bytes / 2) - 1) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private int BookmarkPageIndex(FpdfBookmarkT bookmark)
    {
        var dest = fpdf_doc.FPDFBookmarkGetDest(_document, bookmark);
        if (PdfHandles.IsNull(dest))
        {
            var action = fpdf_doc.FPDFBookmarkGetAction(bookmark);
            if (PdfHandles.IsNull(action))
                return -1;
            dest = fpdf_doc.FPDFActionGetDest(_document, action);
            if (PdfHandles.IsNull(dest))
                return -1;
        }

        var index = fpdf_doc.FPDFDestGetDestPageIndex(_document, dest);
        return index >= 0 && index < _pageSizes.Length ? index : -1;
    }

    private delegate ulong Utf16Reader(ref ushort buffer, ulong length);

    /// <summary>Reads a UTF-16LE string from a PDFium API that reports its size in bytes, terminator included.</summary>
    private static string ReadUtf16(Utf16Reader read)
    {
        ushort probe = 0;
        var bytes = read(ref probe, 0);
        if (bytes <= 2)
            return string.Empty;

        var buffer = new ushort[bytes / 2];
        read(ref buffer[0], bytes);
        return new string(MemoryMarshal.Cast<ushort, char>(buffer.AsSpan(0, buffer.Length - 1)));
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
        _pageTexts.Clear();
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
