using System.Collections.Generic;
using Avalonia;

namespace OpenPdfStudio.Services.Pdf;

public enum SearchMatchMode
{
    ExactPhrase,
    AnyWords,
    AllWords
}

public enum SearchResultKind
{
    PageText,
    Bookmark,
    Comment,
    Attachment
}

public sealed record PdfSearchOptions(
    string Query,
    SearchMatchMode Mode = SearchMatchMode.ExactPhrase,
    bool WholeWords = false,
    bool CaseSensitive = false,
    bool Stemming = false,
    bool IncludePageText = true,
    bool IncludeBookmarks = false,
    bool IncludeComments = false,
    bool IncludeAttachments = false);

/// <summary>
/// One search result. <see cref="Rects"/> are in PDF points with a top-left origin. Page index is -1 for
/// results that do not belong to a page, such as attachments.
/// </summary>
public sealed record PdfSearchHit(
    SearchResultKind Kind,
    int PageIndex,
    int CharStart,
    int CharCount,
    IReadOnlyList<Rect> Rects,
    string Snippet,
    int SnippetMatchStart,
    int SnippetMatchLength);

public readonly record struct TextMatch(int Start, int Length);

public sealed record PdfBookmark(string Title, int Depth, int PageIndex);

public sealed record PdfComment(string Text, Rect Bounds);
