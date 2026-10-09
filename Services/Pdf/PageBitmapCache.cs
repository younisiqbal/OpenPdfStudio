using System.Collections.Generic;

namespace OpenPdfStudio.Services.Pdf;

public interface ICachedPageBitmap
{
    /// <summary>Pinned entries (pages currently on screen) are never evicted.</summary>
    bool IsPinned { get; }

    void ReleaseBitmap();
}

/// <summary>Least-recently-used budget for rendered page bitmaps. UI thread only.</summary>
public sealed class PageBitmapCache(long maxBytes)
{
    private readonly LinkedList<ICachedPageBitmap> _order = new();
    private readonly Dictionary<ICachedPageBitmap, (LinkedListNode<ICachedPageBitmap> Node, long Bytes)> _entries = new();
    private long _totalBytes;

    public void Add(ICachedPageBitmap page, long bytes)
    {
        Remove(page);
        _entries[page] = (_order.AddFirst(page), bytes);
        _totalBytes += bytes;
        Trim();
    }

    public void Touch(ICachedPageBitmap page)
    {
        if (!_entries.TryGetValue(page, out var entry))
            return;

        _order.Remove(entry.Node);
        _order.AddFirst(entry.Node);
    }

    public void Remove(ICachedPageBitmap page)
    {
        if (!_entries.Remove(page, out var entry))
            return;

        _order.Remove(entry.Node);
        _totalBytes -= entry.Bytes;
    }

    public void Clear()
    {
        foreach (var page in _order)
            page.ReleaseBitmap();

        _order.Clear();
        _entries.Clear();
        _totalBytes = 0;
    }

    private void Trim()
    {
        var node = _order.Last;
        while (_totalBytes > maxBytes && node is not null)
        {
            var previous = node.Previous;
            var page = node.Value;
            if (!page.IsPinned)
            {
                Remove(page);
                page.ReleaseBitmap();
            }

            node = previous;
        }
    }
}
