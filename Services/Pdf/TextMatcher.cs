using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenPdfStudio.Services.Pdf;

/// <summary>
/// Finds matches of a query in plain text. Offsets are UTF-16 indices into the text, which line up with
/// PDFium character indices for page text.
/// </summary>
public static class TextMatcher
{
    private static readonly string[] Suffixes =
        ["ments", "ment", "ness", "ions", "ion", "ings", "ing", "est", "ers", "er", "ies", "ied", "es", "ed", "ly", "s"];

    public static IReadOnlyList<TextMatch> Find(string text, PdfSearchOptions options)
    {
        var query = options.Query.Trim();
        if (text.Length == 0 || query.Length == 0)
            return [];

        var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var terms = options.Mode == SearchMatchMode.ExactPhrase ? [query] : SplitWords(query);
        if (terms.Count == 0)
            return [];

        var wholeWords = options.WholeWords || options.Stemming;
        var perTerm = terms
            .Select(term => options.Stemming && !term.Any(char.IsWhiteSpace)
                ? FindStemmed(text, term, comparison)
                : FindTerm(text, term, comparison, wholeWords))
            .ToList();

        if (options.Mode == SearchMatchMode.AllWords && perTerm.Any(m => m.Count == 0))
            return [];

        return perTerm
            .SelectMany(m => m)
            .OrderBy(m => m.Start)
            .Aggregate(new List<TextMatch>(), (kept, match) =>
            {
                if (kept.Count == 0 || match.Start >= kept[^1].Start + kept[^1].Length)
                    kept.Add(match);
                return kept;
            });
    }

    /// <summary>Returns true when the text contains the query, using the same rules as <see cref="Find"/>.</summary>
    public static bool IsMatch(string text, PdfSearchOptions options) => Find(text, options).Count > 0;

    public static string Stem(string word)
    {
        var lower = word.ToLowerInvariant();
        foreach (var suffix in Suffixes)
        {
            if (lower.Length - suffix.Length < 3 || !lower.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var stem = lower[..^suffix.Length];
            if (suffix is "ies" or "ied")
                stem += "y";
            else if (stem.Length >= 4 && stem[^1] == stem[^2] && !"aeiouls".Contains(stem[^1]))
                stem = stem[..^1];
            return stem;
        }

        return lower;
    }

    private static List<string> SplitWords(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Finds a term. Whitespace in the term matches any run of whitespace, including line breaks.</summary>
    private static List<TextMatch> FindTerm(string text, string term, StringComparison comparison, bool wholeWords)
    {
        var parts = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<TextMatch>();
        var from = 0;

        while (from < text.Length)
        {
            var start = text.IndexOf(parts[0], from, comparison);
            if (start < 0)
                break;

            var end = start + parts[0].Length;
            for (var i = 1; i < parts.Length && end >= 0; i++)
            {
                var gapEnd = end;
                while (gapEnd < text.Length && IsSpace(text[gapEnd]))
                    gapEnd++;

                end = gapEnd > end && string.Compare(text, gapEnd, parts[i], 0, parts[i].Length, comparison) == 0
                    ? gapEnd + parts[i].Length
                    : -1;
            }

            if (end > start && (!wholeWords || IsWordBoundary(text, start, end)))
            {
                matches.Add(new TextMatch(start, end - start));
                from = end;
            }
            else
            {
                from = start + 1;
            }
        }

        return matches;
    }

    private static List<TextMatch> FindStemmed(string text, string term, StringComparison comparison)
    {
        var target = Stem(term);
        var matches = new List<TextMatch>();
        var i = 0;

        while (i < text.Length)
        {
            if (!IsWordChar(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && IsWordChar(text[i]))
                i++;

            var word = text[start..i];
            if (string.Equals(Stem(word), target, StringComparison.Ordinal) &&
                (comparison == StringComparison.OrdinalIgnoreCase || StartsWithSameCase(word, term)))
                matches.Add(new TextMatch(start, i - start));
        }

        return matches;
    }

    private static bool StartsWithSameCase(string word, string term)
    {
        var length = Math.Min(word.Length, term.Length);
        for (var i = 0; i < length; i++)
        {
            if (char.ToLowerInvariant(word[i]) != char.ToLowerInvariant(term[i]))
                break;
            if (word[i] != term[i])
                return false;
        }

        return true;
    }

    private static bool IsWordBoundary(string text, int start, int end) =>
        (start == 0 || !IsWordChar(text[start - 1])) && (end >= text.Length || !IsWordChar(text[end]));

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '_';

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is '\u00AD' or '\uFFFE' or '\u0002';
}
