using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.Services.Compare;

public enum PdfPageDiffKind
{
    Equal,
    Differ,
    OnlyLeft,
    OnlyRight
}

public sealed record PdfPageDiff(int PageIndex, PdfPageDiffKind Kind, string Summary);

public sealed record PdfCompareResult(
    IReadOnlyList<PdfPageDiff> Pages,
    int DifferCount,
    int OnlyLeftCount,
    int OnlyRightCount,
    int EqualCount)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (DifferCount > 0)
                parts.Add($"{DifferCount} of {DifferCount + EqualCount} shared page{(DifferCount + EqualCount == 1 ? "" : "s")} differ");
            else if (DifferCount + EqualCount > 0)
                parts.Add($"All {EqualCount} shared page{(EqualCount == 1 ? "" : "s")} match");
            if (OnlyLeftCount > 0)
                parts.Add($"left has {OnlyLeftCount} extra");
            if (OnlyRightCount > 0)
                parts.Add($"right has {OnlyRightCount} extra");
            return parts.Count == 0 ? "No pages to compare" : string.Join("; ", parts);
        }
    }
}

/// <summary>Compares two PDFs page by page using extracted text.</summary>
public static class PdfTextComparer
{
    public static async Task<PdfCompareResult> CompareAsync(
        PdfDocumentSession left,
        PdfDocumentSession right,
        CancellationToken cancellationToken = default)
    {
        var shared = Math.Min(left.PageCount, right.PageCount);
        var pages = new List<PdfPageDiff>(Math.Max(left.PageCount, right.PageCount));
        var differ = 0;
        var equal = 0;

        for (var i = 0; i < shared; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = Normalize(await left.GetPageTextAsync(i, cancellationToken));
            var b = Normalize(await right.GetPageTextAsync(i, cancellationToken));
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                equal++;
                pages.Add(new PdfPageDiff(i, PdfPageDiffKind.Equal, $"Page {i + 1} matches"));
            }
            else
            {
                differ++;
                pages.Add(new PdfPageDiff(i, PdfPageDiffKind.Differ, $"Page {i + 1} differs"));
            }
        }

        for (var i = shared; i < left.PageCount; i++)
            pages.Add(new PdfPageDiff(i, PdfPageDiffKind.OnlyLeft, $"Page {i + 1} only in left"));

        for (var i = shared; i < right.PageCount; i++)
            pages.Add(new PdfPageDiff(i, PdfPageDiffKind.OnlyRight, $"Page {i + 1} only in right"));

        return new PdfCompareResult(
            pages,
            differ,
            left.PageCount - shared,
            right.PageCount - shared,
            equal);
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var chars = text.AsSpan();
        var buffer = new char[chars.Length];
        var length = 0;
        var previousSpace = true;
        foreach (var c in chars)
        {
            if (char.IsWhiteSpace(c))
            {
                if (previousSpace)
                    continue;
                buffer[length++] = ' ';
                previousSpace = true;
            }
            else
            {
                buffer[length++] = c;
                previousSpace = false;
            }
        }

        if (length > 0 && buffer[length - 1] == ' ')
            length--;
        return new string(buffer, 0, length);
    }
}
