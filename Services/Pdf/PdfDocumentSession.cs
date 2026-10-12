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
    private const int BitmapFormatBgra = 4;
    private const string StandardFont = "Helvetica";
    private const double TextAscent = 0.8;
    public const double TextLineSpacing = 1.2;

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

    public string FilePath { get; private set; }
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

    /// <summary>Returns the font size of a character, or 0 when PDFium cannot tell.</summary>
    public Task<double> GetFontSizeAsync(int pageIndex, int charIndex) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            return fpdf_text.FPDFTextGetFontSize(GetTextPage(pageIndex).Text, charIndex);
        });

    /// <summary>Adds black Helvetica text to a page, one text object per line. <paramref name="topLeft"/> is in points.</summary>
    public Task InsertTextAsync(int pageIndex, IReadOnlyList<string> lines, Point topLeft, double fontSize) =>
        EditPageAsync(pageIndex, (page, map) =>
        {
            var lineHeight = fontSize * TextLineSpacing;
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrEmpty(lines[i]))
                    continue;

                var text = fpdf_edit.FPDFPageObjNewTextObj(_document, StandardFont, (float)fontSize);
                if (text is null || text.__Instance == IntPtr.Zero)
                    throw new InvalidOperationException("PDFium could not create a text object.");

                var chars = new ushort[lines[i].Length + 1];
                MemoryMarshal.Cast<char, ushort>(lines[i].AsSpan()).CopyTo(chars);
                fpdf_edit.FPDFTextSetText(text, ref chars[0]);
                fpdf_edit.FPDFPageObjSetFillColor(text, 0, 0, 0, 255);

                var baseline = new Point(topLeft.X, topLeft.Y + fontSize * TextAscent + i * lineHeight);
                map.Apply(text, baseline, new Vector(1, 0), new Vector(0, -1));
                fpdf_edit.FPDFPageInsertObject(page, text);
            }
        });

    /// <summary>Adds an image to a page, stretched to <paramref name="bounds"/> (in points).</summary>
    public Task InsertImageAsync(int pageIndex, PdfImageData image, Rect bounds) =>
        EditPageAsync(pageIndex, (page, map) =>
        {
            var handle = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
            try
            {
                var bitmap = fpdfview.FPDFBitmapCreateEx(image.Width, image.Height, BitmapFormatBgra,
                    handle.AddrOfPinnedObject(), image.Width * 4);
                if (PdfHandles.IsNull(bitmap))
                    throw new InvalidOperationException("PDFium could not allocate the image.");

                try
                {
                    var picture = fpdf_edit.FPDFPageObjNewImageObj(_document);
                    if (picture is null || picture.__Instance == IntPtr.Zero)
                        throw new InvalidOperationException("PDFium could not create an image object.");

                    if (fpdf_edit.FPDFImageObjSetBitmap(null, 0, picture, bitmap) == 0)
                    {
                        fpdf_edit.FPDFPageObjDestroy(picture);
                        throw new InvalidOperationException("PDFium could not store the image.");
                    }

                    map.Apply(picture, bounds.BottomLeft, new Vector(bounds.Width, 0), new Vector(0, -bounds.Height));
                    fpdf_edit.FPDFPageInsertObject(page, picture);
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bitmap);
                }
            }
            finally
            {
                handle.Free();
            }
        });

    /// <summary>
    /// Writes the document, with any edits, to <paramref name="path"/>. The file is written next to the target
    /// first and then moved over it, so a failed save never leaves a half-written PDF.
    /// </summary>
    public Task SaveAsync(string path) => SaveCoreAsync(path, adoptPath: true);

    /// <summary>Writes the document, with edits, to another file without making it this session's file.</summary>
    public Task SaveCopyAsync(string path) => SaveCoreAsync(path, adoptPath: false);

    private Task SaveCoreAsync(string path, bool adoptPath) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            var fullPath = Path.GetFullPath(path);
            var temp = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    PdfSaver.Save(_document, stream);

                File.Move(temp, fullPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }

            if (adoptPath)
                FilePath = fullPath;
        });

    /// <summary>Renders a page into a BGRA buffer for printing. Form fields and annotations are included.</summary>
    public Task<byte[]> RenderForPrintAsync(int pageIndex, int pixelWidth, int pixelHeight, CancellationToken cancellationToken) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            return PdfPageRenderer.RenderToBuffer(_document, pageIndex, pixelWidth, pixelHeight, forPrinting: true);
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _ = _worker.InvokeAsync(CloseOnWorker);
    }

    private Task EditPageAsync(int pageIndex, Action<FpdfPageT, PageSpaceMap> edit) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= _pageSizes.Length)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));

            // A cached text page holds its own copy of the page and would not see the new content.
            ForgetPage(pageIndex);

            var page = fpdfview.FPDF_LoadPage(_document, pageIndex);
            if (PdfHandles.IsNull(page))
                throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");

            try
            {
                edit(page, PageSpaceMap.For(page, _pageSizes[pageIndex]));
                if (fpdf_edit.FPDFPageGenerateContent(page) == 0)
                    throw new InvalidOperationException("PDFium could not update the page content.");
            }
            finally
            {
                fpdfview.FPDF_ClosePage(page);
            }
        });

    private void ForgetPage(int pageIndex)
    {
        _pageTexts.Remove(pageIndex);
        if (!_textPages.Remove(pageIndex, out var text))
            return;

        _textPageOrder.Remove(pageIndex);
        CloseTextPage(text);
    }

    private static PdfDocumentSession OpenOnWorker(PdfiumWorker worker, string filePath, string? password)
    {
        if (!File.Exists(filePath))
            throw new PdfOpenException(PdfOpenError.FileNotFound, "The file could not be found.");

        // Loaded from memory so the file is not held open; Save can then replace it.
        var bytes = File.ReadAllBytes(filePath);
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        var document = fpdfview.FPDF_LoadMemDocument64(buffer, (ulong)bytes.Length, password);

        if (PdfHandles.IsNull(document))
        {
            var error = fpdfview.FPDF_GetLastError();
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

    /// <summary>
    /// Maps the view's top-left point space onto PDF user space for one page. Built from PDFium's own
    /// device-to-page conversion, so /Rotate and offset media boxes are handled.
    /// </summary>
    private readonly record struct PageSpaceMap(Vector Origin, Vector PerPointX, Vector PerPointY)
    {
        public static PageSpaceMap For(FpdfPageT page, Size sizeInPoints)
        {
            var width = (int)Math.Round(sizeInPoints.Width * DeviceScale);
            var height = (int)Math.Round(sizeInPoints.Height * DeviceScale);
            var topLeft = ToPage(page, width, height, 0, 0);
            var topRight = ToPage(page, width, height, width, 0);
            var bottomLeft = ToPage(page, width, height, 0, height);
            return new PageSpaceMap(topLeft,
                (topRight - topLeft) / sizeInPoints.Width,
                (bottomLeft - topLeft) / sizeInPoints.Height);
        }

        /// <summary>Places an object: its unit X and Y axes land on the given vectors, its origin on the given point.</summary>
        public void Apply(FpdfPageobjectT obj, Point origin, Vector xAxis, Vector yAxis)
        {
            var at = Origin + PerPointX * origin.X + PerPointY * origin.Y;
            var x = PerPointX * xAxis.X + PerPointY * xAxis.Y;
            var y = PerPointX * yAxis.X + PerPointY * yAxis.Y;
            fpdf_edit.FPDFPageObjTransform(obj, x.X, x.Y, y.X, y.Y, at.X, at.Y);
        }

        private static Vector ToPage(FpdfPageT page, int width, int height, int deviceX, int deviceY)
        {
            double x = 0, y = 0;
            fpdfview.FPDF_DeviceToPage(page, 0, 0, width, height, 0, deviceX, deviceY, ref x, ref y);
            return new Vector(x, y);
        }
    }
}

/// <summary>Straight (not premultiplied) BGRA pixels, rows packed with no padding.</summary>
public sealed record PdfImageData(int Width, int Height, byte[] Bgra);
