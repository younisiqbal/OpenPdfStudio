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

/// <summary>Char span in PDFium page text indices (same space as <c>GetTextRectsAsync</c>).</summary>
public sealed record PdfTextSpan(int PageIndex, int StartIndex, int Count);

public sealed record PdfCompareResult(
    IReadOnlyList<PdfPageDiff> Pages,
    IReadOnlyList<PdfTextSpan> LeftDeletes,
    IReadOnlyList<PdfTextSpan> RightInserts,
    IReadOnlyList<PdfTextSpan> LeftMatches,
    IReadOnlyList<PdfTextSpan> RightMatches,
    int DifferCount,
    int OnlyLeftCount,
    int OnlyRightCount,
    int EqualCount,
    double? MatchRatio = null)
{
    public string Summary
    {
        get
        {
            if (MatchRatio is not null && Pages.Count == 1 &&
                Pages[0].Kind is PdfPageDiffKind.Equal or PdfPageDiffKind.Differ)
                return Pages[0].Summary;

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

/// <summary>Compares two PDFs page by page and emits delete / insert / match char spans.</summary>
public static class PdfTextComparer
{
    private const long MaxTokenProduct = 2_000_000;

    public static async Task<PdfCompareResult> CompareAsync(
        PdfDocumentSession left,
        PdfDocumentSession right,
        CancellationToken cancellationToken = default)
    {
        var shared = Math.Min(left.PageCount, right.PageCount);
        var pages = new List<PdfPageDiff>(Math.Max(left.PageCount, right.PageCount));
        var deletes = new List<PdfTextSpan>();
        var inserts = new List<PdfTextSpan>();
        var leftMatches = new List<PdfTextSpan>();
        var rightMatches = new List<PdfTextSpan>();
        var differ = 0;
        var equal = 0;

        for (var i = 0; i < shared; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = await left.GetPageTextAsync(i, cancellationToken);
            var b = await right.GetPageTextAsync(i, cancellationToken);
            DiffPagePair(i, i, a, b, pages, deletes, inserts, leftMatches, rightMatches,
                ref equal, ref differ, pairSummary: false);
        }

        for (var i = shared; i < left.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(new PdfPageDiff(i, PdfPageDiffKind.OnlyLeft, $"Page {i + 1} only in left"));
            var text = await left.GetPageTextAsync(i, cancellationToken);
            if (text.Length > 0)
                deletes.Add(new PdfTextSpan(i, 0, text.Length));
        }

        for (var i = shared; i < right.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages.Add(new PdfPageDiff(i, PdfPageDiffKind.OnlyRight, $"Page {i + 1} only in right"));
            var text = await right.GetPageTextAsync(i, cancellationToken);
            if (text.Length > 0)
                inserts.Add(new PdfTextSpan(i, 0, text.Length));
        }

        return new PdfCompareResult(
            pages,
            MergeSpans(deletes),
            MergeSpans(inserts),
            MergeSpans(leftMatches),
            MergeSpans(rightMatches),
            differ,
            left.PageCount - shared,
            right.PageCount - shared,
            equal);
    }

    /// <summary>Compare a single left page to a single right page (0-based indices).</summary>
    public static async Task<PdfCompareResult> ComparePagesAsync(
        PdfDocumentSession left,
        PdfDocumentSession right,
        int leftPageIndex,
        int rightPageIndex,
        CancellationToken cancellationToken = default)
    {
        if (leftPageIndex < 0 || leftPageIndex >= left.PageCount)
            throw new ArgumentOutOfRangeException(nameof(leftPageIndex));
        if (rightPageIndex < 0 || rightPageIndex >= right.PageCount)
            throw new ArgumentOutOfRangeException(nameof(rightPageIndex));

        cancellationToken.ThrowIfCancellationRequested();
        var a = await left.GetPageTextAsync(leftPageIndex, cancellationToken);
        var b = await right.GetPageTextAsync(rightPageIndex, cancellationToken);

        var pages = new List<PdfPageDiff>(1);
        var deletes = new List<PdfTextSpan>();
        var inserts = new List<PdfTextSpan>();
        var leftMatches = new List<PdfTextSpan>();
        var rightMatches = new List<PdfTextSpan>();
        var equal = 0;
        var differ = 0;

        var leftTokens = Tokenize(a);
        var rightTokens = Tokenize(b);
        DiffPagePair(leftPageIndex, rightPageIndex, a, b, pages, deletes, inserts, leftMatches, rightMatches,
            ref equal, ref differ, pairSummary: true);

        var matched = CountMatchedTokens(leftTokens, rightTokens);
        var total = Math.Max(leftTokens.Count, rightTokens.Count);
        double? ratio = total == 0 ? 1.0 : (double)matched / total;

        // Fix summary for cross-page pairing (DiffPagePair stores left index only).
        if (pages.Count == 1)
        {
            var kind = pages[0].Kind;
            var pct = (int)Math.Round((ratio ?? 0) * 100);
            pages[0] = kind == PdfPageDiffKind.Equal
                ? new PdfPageDiff(leftPageIndex, kind,
                    $"Page {leftPageIndex + 1} vs page {rightPageIndex + 1}: match ({pct}% shared)")
                : new PdfPageDiff(leftPageIndex, kind,
                    $"Page {leftPageIndex + 1} vs page {rightPageIndex + 1}: differ ({pct}% shared)");
        }

        return new PdfCompareResult(
            pages,
            MergeSpans(deletes),
            MergeSpans(inserts),
            MergeSpans(leftMatches),
            MergeSpans(rightMatches),
            differ,
            0,
            0,
            equal,
            ratio);
    }

    private static void DiffPagePair(
        int leftPageIndex,
        int rightPageIndex,
        string a,
        string b,
        List<PdfPageDiff> pages,
        List<PdfTextSpan> deletes,
        List<PdfTextSpan> inserts,
        List<PdfTextSpan> leftMatches,
        List<PdfTextSpan> rightMatches,
        ref int equal,
        ref int differ,
        bool pairSummary)
    {
        var leftTokens = Tokenize(a);
        var rightTokens = Tokenize(b);

        if (TokensEqual(leftTokens, rightTokens))
        {
            equal++;
            pages.Add(pairSummary
                ? new PdfPageDiff(leftPageIndex, PdfPageDiffKind.Equal,
                    $"Page {leftPageIndex + 1} vs page {rightPageIndex + 1}: match")
                : new PdfPageDiff(leftPageIndex, PdfPageDiffKind.Equal, $"Page {leftPageIndex + 1} matches"));
            AddAllTokensAsMatches(leftPageIndex, leftTokens, leftMatches);
            AddAllTokensAsMatches(rightPageIndex, rightTokens, rightMatches);
        }
        else
        {
            differ++;
            pages.Add(pairSummary
                ? new PdfPageDiff(leftPageIndex, PdfPageDiffKind.Differ,
                    $"Page {leftPageIndex + 1} vs page {rightPageIndex + 1}: differ")
                : new PdfPageDiff(leftPageIndex, PdfPageDiffKind.Differ, $"Page {leftPageIndex + 1} differs"));
            DiffTokens(leftPageIndex, rightPageIndex, leftTokens, rightTokens, a.Length, b.Length,
                deletes, inserts, leftMatches, rightMatches);
        }
    }

    private readonly record struct Token(string Value, int Start, int Count);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(text))
            return tokens;

        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                break;

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;
            tokens.Add(new Token(text[start..i], start, i - start));
        }

        return tokens;
    }

    private static bool TokensEqual(List<Token> a, List<Token> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Value, b[i].Value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static void AddAllTokensAsMatches(int pageIndex, List<Token> tokens, List<PdfTextSpan> matches)
    {
        foreach (var token in tokens)
            matches.Add(new PdfTextSpan(pageIndex, token.Start, token.Count));
    }

    private static int CountMatchedTokens(List<Token> left, List<Token> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;
        if ((long)left.Count * right.Count > MaxTokenProduct)
            return 0;

        var n = left.Count;
        var m = right.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(left[i].Value, right[j].Value, StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        return dp[0, 0];
    }

    private static void DiffTokens(
        int leftPageIndex,
        int rightPageIndex,
        List<Token> left,
        List<Token> right,
        int leftLength,
        int rightLength,
        List<PdfTextSpan> deletes,
        List<PdfTextSpan> inserts,
        List<PdfTextSpan> leftMatches,
        List<PdfTextSpan> rightMatches)
    {
        if (left.Count == 0 && right.Count == 0)
            return;

        if ((long)left.Count * right.Count > MaxTokenProduct)
        {
            if (leftLength > 0)
                deletes.Add(new PdfTextSpan(leftPageIndex, 0, leftLength));
            if (rightLength > 0)
                inserts.Add(new PdfTextSpan(rightPageIndex, 0, rightLength));
            return;
        }

        var n = left.Count;
        var m = right.Count;
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(left[i].Value, right[j].Value, StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var iPos = 0;
        var jPos = 0;
        while (iPos < n && jPos < m)
        {
            if (string.Equals(left[iPos].Value, right[jPos].Value, StringComparison.Ordinal))
            {
                leftMatches.Add(new PdfTextSpan(leftPageIndex, left[iPos].Start, left[iPos].Count));
                rightMatches.Add(new PdfTextSpan(rightPageIndex, right[jPos].Start, right[jPos].Count));
                iPos++;
                jPos++;
            }
            else if (dp[iPos + 1, jPos] >= dp[iPos, jPos + 1])
            {
                deletes.Add(new PdfTextSpan(leftPageIndex, left[iPos].Start, left[iPos].Count));
                iPos++;
            }
            else
            {
                inserts.Add(new PdfTextSpan(rightPageIndex, right[jPos].Start, right[jPos].Count));
                jPos++;
            }
        }

        while (iPos < n)
        {
            deletes.Add(new PdfTextSpan(leftPageIndex, left[iPos].Start, left[iPos].Count));
            iPos++;
        }

        while (jPos < m)
        {
            inserts.Add(new PdfTextSpan(rightPageIndex, right[jPos].Start, right[jPos].Count));
            jPos++;
        }
    }

    private static IReadOnlyList<PdfTextSpan> MergeSpans(List<PdfTextSpan> spans)
    {
        if (spans.Count <= 1)
            return spans;

        spans.Sort(static (a, b) =>
        {
            var page = a.PageIndex.CompareTo(b.PageIndex);
            return page != 0 ? page : a.StartIndex.CompareTo(b.StartIndex);
        });

        var merged = new List<PdfTextSpan>(spans.Count);
        var current = spans[0];
        for (var i = 1; i < spans.Count; i++)
        {
            var next = spans[i];
            var currentEnd = current.StartIndex + current.Count;
            if (next.PageIndex == current.PageIndex && next.StartIndex <= currentEnd + 1)
            {
                var end = Math.Max(currentEnd, next.StartIndex + next.Count);
                current = new PdfTextSpan(current.PageIndex, current.StartIndex, end - current.StartIndex);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }
}
