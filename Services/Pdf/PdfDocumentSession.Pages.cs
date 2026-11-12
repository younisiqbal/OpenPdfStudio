using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PDFiumCore;

namespace OpenPdfStudio.Services.Pdf;

/// <summary>One page of a rearranged document: which current page it is, and how many quarter turns clockwise to add.</summary>
public sealed record PageArrangement(int SourceIndex, int QuarterTurns);

/// <summary>One file produced by a split: where it goes and which pages it holds.</summary>
public sealed record SplitOutput(string Path, IReadOnlyList<int> PageIndices);

public sealed partial class PdfDocumentSession
{
    /// <summary>
    /// Reorders, rotates, and deletes pages in place. <paramref name="pages"/> is the new page list; current pages
    /// missing from it are deleted. Rotation is written to each page's /Rotate entry.
    /// </summary>
    public Task ArrangePagesAsync(IReadOnlyList<PageArrangement> pages) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            var oldCount = _pageSizes.Length;
            if (pages.Count == 0)
                throw new ArgumentException("A PDF must keep at least one page.", nameof(pages));
            if (pages.Any(p => p.SourceIndex < 0 || p.SourceIndex >= oldCount) ||
                pages.Select(p => p.SourceIndex).Distinct().Count() != pages.Count)
                throw new ArgumentException("Each page can appear only once.", nameof(pages));

            ForgetAllPages();

            var kept = pages.Select(p => p.SourceIndex).ToHashSet();
            for (var i = oldCount - 1; i >= 0; i--)
            {
                if (!kept.Contains(i))
                    fpdf_edit.FPDFPageDelete(_document, i);
            }

            // After deleting, the kept pages sit in their original order; find each one's new position.
            var remaining = Enumerable.Range(0, oldCount).Where(kept.Contains).ToList();
            var order = pages.Select(p => remaining.IndexOf(p.SourceIndex)).ToArray();
            if (!order.SequenceEqual(Enumerable.Range(0, order.Length)) &&
                fpdf_edit.FPDF_MovePages(_document, ref order[0], (ulong)order.Length, 0) == 0)
                throw new InvalidOperationException("PDFium could not reorder the pages.");

            for (var i = 0; i < pages.Count; i++)
            {
                var turns = ((pages[i].QuarterTurns % 4) + 4) % 4;
                if (turns == 0)
                    continue;

                var page = fpdfview.FPDF_LoadPage(_document, i);
                if (PdfHandles.IsNull(page))
                    throw new InvalidOperationException($"Page {i + 1} could not be loaded.");

                try
                {
                    fpdf_edit.FPDFPageSetRotation(page, (fpdf_edit.FPDFPageGetRotation(page) + turns) % 4);
                }
                finally
                {
                    fpdfview.FPDF_ClosePage(page);
                }
            }

            _pageSizes = ReadPageSizes(_document);
        });

    /// <summary>Writes the given pages, in order, to a new PDF at <paramref name="path"/>.</summary>
    public Task ExportPagesAsync(IReadOnlyList<int> pageIndices, string path) =>
        _worker.InvokeAsync(() =>
        {
            ThrowIfDisposed();
            if (pageIndices.Count == 0 || pageIndices.Any(i => i < 0 || i >= _pageSizes.Length))
                throw new ArgumentOutOfRangeException(nameof(pageIndices));

            WriteNewDocument(path, target => Import(target, _document, pageIndices.ToArray(), 0));
        });

    /// <summary>Writes every page of each source, in order, to a new PDF at <paramref name="path"/>.</summary>
    public static Task MergeAsync(IReadOnlyList<PdfDocumentSession> sources, string path) =>
        PdfiumWorker.Instance.InvokeAsync(() =>
        {
            WriteNewDocument(path, target =>
            {
                var inserted = 0;
                foreach (var source in sources)
                {
                    source.ThrowIfDisposed();
                    var indices = Enumerable.Range(0, source._pageSizes.Length).ToArray();
                    if (indices.Length == 0)
                        continue;

                    Import(target, source._document, indices, inserted);
                    inserted += indices.Length;
                }
            });
        });

    private static void WriteNewDocument(string path, Action<FpdfDocumentT> fill)
    {
        var target = fpdf_edit.FPDF_CreateNewDocument();
        if (PdfHandles.IsNull(target))
            throw new InvalidOperationException("PDFium could not create a new document.");

        try
        {
            fill(target);
            WriteAtomically(target, path);
        }
        finally
        {
            fpdfview.FPDF_CloseDocument(target);
        }
    }

    private static void Import(FpdfDocumentT target, FpdfDocumentT source, int[] indices, int insertAt)
    {
        if (fpdf_ppo.FPDF_ImportPagesByIndex(target, source, ref indices[0], (ulong)indices.Length, insertAt) == 0)
            throw new InvalidOperationException("PDFium could not copy the pages.");
    }

    private void ForgetAllPages()
    {
        foreach (var text in _textPages.Values)
            CloseTextPage(text);

        _textPages.Clear();
        _textPageOrder.Clear();
        _pageTexts.Clear();
    }
}
