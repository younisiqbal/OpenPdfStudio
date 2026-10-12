using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Avalonia;

namespace OpenPdfStudio.Services.Pdf;

public static class PdfSearchEngine
{
    private const int SnippetContext = 40;

    /// <summary>
    /// Searches one document and streams hits in reading order: bookmarks first, then each page's text and
    /// comments, then attachments. <paramref name="progress"/> receives (pages searched, page count).
    /// </summary>
    public static async IAsyncEnumerable<PdfSearchHit> SearchAsync(PdfDocumentSession session, PdfSearchOptions options,
        IProgress<(int Done, int Total)>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Query))
            yield break;

        if (options.IncludeBookmarks)
        {
            foreach (var bookmark in await session.GetBookmarksAsync(cancellationToken))
            {
                foreach (var match in FirstMatch(bookmark.Title, options))
                    yield return CreateHit(SearchResultKind.Bookmark, bookmark.PageIndex, bookmark.Title, match, []);
            }
        }

        var total = session.PageCount;
        for (int page = 0; page < total; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.IncludePageText)
            {
                foreach (var hit in await session.SearchPageTextAsync(page, options, cancellationToken))
                    yield return hit;
            }

            if (options.IncludeComments)
            {
                foreach (var comment in await session.GetCommentsAsync(page, cancellationToken))
                {
                    foreach (var match in FirstMatch(comment.Text, options))
                    {
                        IReadOnlyList<Rect> rects = comment.Bounds.Width > 0 ? [comment.Bounds] : [];
                        yield return CreateHit(SearchResultKind.Comment, page, comment.Text, match, rects);
                    }
                }
            }

            progress?.Report((page + 1, total));
        }

        if (options.IncludeAttachments)
        {
            foreach (var name in await session.GetAttachmentNamesAsync(cancellationToken))
            {
                foreach (var match in FirstMatch(name, options))
                    yield return CreateHit(SearchResultKind.Attachment, -1, name, match, []);
            }
        }
    }

    public static PdfSearchHit CreateHit(SearchResultKind kind, int pageIndex, string source, TextMatch match,
        IReadOnlyList<Rect> rects)
    {
        var from = Math.Max(0, match.Start - SnippetContext);
        var to = Math.Min(source.Length, match.Start + match.Length + SnippetContext);

        var builder = new StringBuilder();
        if (from > 0)
            builder.Append('…');

        var matchStart = -1;
        var matchLength = 0;
        var lastWasSpace = false;
        for (int i = from; i < to; i++)
        {
            if (i == match.Start)
                matchStart = builder.Length;

            var c = source[i];
            var isSpace = char.IsWhiteSpace(c) || char.IsControl(c);
            if (isSpace && lastWasSpace)
                continue;

            builder.Append(isSpace ? ' ' : c);
            lastWasSpace = isSpace;

            if (i == match.Start + match.Length - 1)
                matchLength = builder.Length - matchStart;
        }

        if (to < source.Length)
            builder.Append('…');

        return new PdfSearchHit(kind, pageIndex, match.Start, match.Length, rects, builder.ToString(),
            Math.Max(matchStart, 0), Math.Max(matchLength, 0));
    }

    private static IEnumerable<TextMatch> FirstMatch(string text, PdfSearchOptions options)
    {
        var matches = TextMatcher.Find(text, options);
        if (matches.Count > 0)
            yield return matches[0];
    }
}
