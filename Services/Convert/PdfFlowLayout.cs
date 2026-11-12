using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia;

namespace OpenPdfStudio.Services.Convert;

/// <summary>One character read from a PDF page. <paramref name="Box"/> is in points, top-left origin.</summary>
public readonly record struct PdfFlowChar(char Value, Rect Box, PdfFlowRunStyle Style, bool IsLineBreak);

/// <summary>An image placed on a PDF page, already encoded as PNG.</summary>
public sealed record PdfFlowPicture(Rect Bounds, byte[] Png);

/// <summary>
/// Turns positioned characters into lines, paragraphs, and styled runs. PDFs only say where each glyph goes,
/// so paragraphs, alignment, and spacing are inferred from the geometry.
/// </summary>
public static class PdfFlowLayout
{
    private const double MinMargin = 18;
    private const double MaxMargin = 144;
    private const double RightSlack = 6;
    private const double AscentShare = 0.8;
    private const double MaxSpaceBefore = 200;
    private const string BulletChars = "•●▪■◦○-–*";

    private sealed class Line
    {
        public readonly List<PdfFlowChar> Chars = [];
        public double Left = double.MaxValue;
        public double Right = double.MinValue;
        public double Top = double.MaxValue;
        public double Bottom = double.MinValue;
        public double FontSize;
        public double Baseline;

        /// <summary>Positions of the tab stops between side-by-side pieces, such as table cells.</summary>
        public readonly List<double> Tabs = [];

        public double Center => (Left + Right) / 2;
        public bool HasInk => Right >= Left;

        public void Add(PdfFlowChar c)
        {
            Chars.Add(c);
            if (char.IsWhiteSpace(c.Value) || c.Box.Width <= 0 && c.Box.Height <= 0)
                return;

            Left = Math.Min(Left, c.Box.Left);
            Right = Math.Max(Right, c.Box.Right);
            Top = Math.Min(Top, c.Box.Top);
            Bottom = Math.Max(Bottom, c.Box.Bottom);
            FontSize = Math.Max(FontSize, c.Style.FontSize);
        }

        public void Finish()
        {
            var bottoms = Chars.Where(c => !char.IsWhiteSpace(c.Value) && c.Box.Height > 0)
                .Select(c => c.Box.Bottom).OrderBy(b => b).ToList();
            Baseline = bottoms.Count > 0 ? bottoms[bottoms.Count / 2] : Bottom;
            if (FontSize <= 0)
                FontSize = Math.Max(Bottom - Top, 1);
        }

        public string FirstText() => new string(Chars.Select(c => c.Value).ToArray()).TrimStart();
    }

    private sealed class Paragraph
    {
        public readonly List<Line> Lines = [];
        public Line First => Lines[0];
        public Line Last => Lines[^1];
        public double BodyLeft => Lines.Count > 1 ? Lines[1].Left : Lines[0].Left;
        public double MaxRight => Lines.Max(l => l.Right);
        public double FontSize => Lines.Max(l => l.FontSize);
    }

    public static PdfFlowPage Build(double width, double height, IReadOnlyList<PdfFlowChar> chars,
        IReadOnlyList<PdfFlowPicture> pictures)
    {
        var lines = BuildLines(chars);
        var paragraphs = BuildParagraphs(lines);

        var contentLeft = Bound(lines.Select(l => l.Left), pictures.Select(p => p.Bounds.Left), Math.Min, MinMargin);
        var contentRight = Bound(lines.Select(l => l.Right), pictures.Select(p => p.Bounds.Right), Math.Max, width - MinMargin);
        var contentTop = Bound(lines.Select(l => l.Top), pictures.Select(p => p.Bounds.Top), Math.Min, MinMargin);
        var contentBottom = Bound(lines.Select(l => l.Bottom), pictures.Select(p => p.Bounds.Bottom), Math.Max, height - MinMargin);

        var margins = new PdfFlowMargins(
            Top: Math.Clamp(contentTop - 2, MinMargin, MaxMargin),
            Bottom: Math.Clamp(Math.Min(height - contentBottom, 36), 14, MaxMargin),
            Left: Math.Clamp(contentLeft, MinMargin, MaxMargin),
            Right: Math.Clamp(width - contentRight - RightSlack, MinMargin, MaxMargin));

        var blocks = paragraphs.Select(p => ToBlock(p, margins, contentLeft, contentRight)).ToList<PdfFlowBlock>();

        // Pictures float at their place on the page, so the text keeps its own positions around them.
        return new PdfFlowPage(width, height, margins, WithSpacing(blocks, margins.Top))
        {
            Pictures = pictures.OrderBy(p => p.Bounds.Top).ToList()
        };
    }

    private static double Bound(IEnumerable<double> a, IEnumerable<double> b, Func<double, double, double> pick, double fallback)
    {
        var all = a.Concat(b).Where(v => !double.IsInfinity(v) && v is > double.MinValue and < double.MaxValue).ToList();
        return all.Count == 0 ? fallback : all.Aggregate(pick);
    }

    private static List<Line> BuildLines(IReadOnlyList<PdfFlowChar> chars) => MergeRows(BuildFragments(chars));

    /// <summary>
    /// Splits the characters, in drawing order, into runs of text that sit together on one line.
    /// A table is drawn cell by cell, so each cell line becomes its own fragment.
    /// </summary>
    private static List<Line> BuildFragments(IReadOnlyList<PdfFlowChar> chars)
    {
        var lines = new List<Line>();
        var current = new Line();

        void Close()
        {
            if (current.HasInk)
            {
                current.Finish();
                lines.Add(current);
            }

            current = new Line();
        }

        foreach (var c in chars)
        {
            if (c.IsLineBreak)
            {
                Close();
                continue;
            }

            var isInk = !char.IsWhiteSpace(c.Value) && (c.Box.Width > 0 || c.Box.Height > 0);
            if (isInk && current.HasInk)
            {
                var center = (c.Box.Top + c.Box.Bottom) / 2;
                var tolerance = Math.Max(current.FontSize, c.Style.FontSize) * 0.3;
                var offLine = center < current.Top - tolerance || center > current.Bottom + tolerance;
                var jumpedBack = c.Box.Left < current.Left - c.Style.FontSize;
                var gap = c.Box.Left - current.Right > Math.Max(c.Style.FontSize * 2.5, 12);
                if (offLine || jumpedBack || gap)
                    Close();
            }

            if (isInk || current.Chars.Count > 0)
                current.Add(c);
        }

        Close();
        return lines;
    }

    /// <summary>
    /// Joins fragments on the same baseline into one line, left to right, with a tab stop at each
    /// fragment that starts after a wide gap. This keeps tables and columns on their rows.
    /// </summary>
    private static List<Line> MergeRows(List<Line> fragments)
    {
        var rows = new List<List<Line>>();
        foreach (var fragment in fragments.OrderBy(f => f.Baseline).ThenBy(f => f.Left))
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            var fits = row is not null
                       // Cells in one table row are often centred vertically, so their baselines differ a little.
                       && Math.Abs(fragment.Baseline - row[0].Baseline) <= Math.Max(fragment.FontSize, row[0].FontSize) * 0.6
                       && row.All(other => fragment.Right <= other.Left || fragment.Left >= other.Right);
            if (fits)
                row!.Add(fragment);
            else
                rows.Add([fragment]);
        }

        var lines = new List<Line>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Count == 1)
            {
                lines.Add(row[0]);
                continue;
            }

            var parts = row.OrderBy(f => f.Left).ToList();
            var line = new Line();
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0)
                {
                    var previous = parts[i - 1];
                    var size = Math.Max(previous.FontSize, parts[i].FontSize);
                    var gap = parts[i].Left - previous.Right;
                    var style = parts[i].Chars[0].Style;
                    var box = new Rect(previous.Right, parts[i].Top, Math.Max(gap, 0), 0);
                    if (gap > Math.Max(size * 1.2, 6))
                    {
                        line.Chars.Add(new PdfFlowChar('\t', box, style, false));
                        line.Tabs.Add(parts[i].Left);
                    }
                    else if (gap > size * 0.1 && !char.IsWhiteSpace(line.Chars[^1].Value))
                    {
                        line.Chars.Add(new PdfFlowChar(' ', box, style, false));
                    }
                }

                foreach (var c in parts[i].Chars)
                    line.Add(c);
            }

            line.Finish();
            lines.Add(line);
        }

        return lines;
    }

    private static List<Paragraph> BuildParagraphs(List<Line> lines)
    {
        var paragraphs = new List<Paragraph>();
        Paragraph? current = null;

        foreach (var line in lines)
        {
            if (current is null || !Continues(current, line))
            {
                current = new Paragraph();
                paragraphs.Add(current);
            }

            current.Lines.Add(line);
        }

        return paragraphs;
    }

    private static bool Continues(Paragraph paragraph, Line next)
    {
        var last = paragraph.Last;
        if (last.Tabs.Count > 0 || next.Tabs.Count > 0)
            return false;

        var size = Math.Max(last.FontSize, next.FontSize);
        var step = next.Baseline - last.Baseline;
        if (step <= 0 || step > size * 1.6)
            return false;

        var ratio = Math.Max(last.FontSize, next.FontSize) / Math.Max(Math.Min(last.FontSize, next.FontSize), 0.1);
        if (ratio > 1.15)
            return false;

        if (StartsWithBullet(next.FirstText()))
            return false;

        var lefts = Math.Abs(next.Left - paragraph.BodyLeft) <= size;
        var firstLineIndent = paragraph.Lines.Count == 1 && next.Left < last.Left && last.Left - next.Left <= size * 4;
        var centers = Math.Abs(next.Center - last.Center) <= size;
        if (!lefts && !firstLineIndent && !centers)
            return false;

        // A line that stops well short of the paragraph's right edge ends the paragraph.
        if (lefts || firstLineIndent)
        {
            var right = Math.Max(paragraph.MaxRight, next.Right);
            if (last.Right < right - size * 4 && !centers)
                return false;
        }

        return true;
    }

    private static bool StartsWithBullet(string text)
    {
        if (text.Length >= 2 && BulletChars.Contains(text[0]) && char.IsWhiteSpace(text[1]))
            return true;

        var i = 0;
        while (i < text.Length && i < 3 && char.IsDigit(text[i]))
            i++;
        return i > 0 && i + 1 < text.Length && text[i] is '.' or ')' && char.IsWhiteSpace(text[i + 1]);
    }

    private static PdfFlowParagraph ToBlock(Paragraph paragraph, PdfFlowMargins margins, double contentLeft, double contentRight)
    {
        var size = paragraph.FontSize;
        var tolerance = Math.Max(size * 0.6, 2);
        var tabs = paragraph.First.Tabs;
        var alignment = tabs.Count > 0
            ? PdfFlowAlignment.Left
            : GuessAlignment(paragraph, tolerance, contentLeft, contentRight);

        var spacing = paragraph.Lines.Count > 1
            ? (paragraph.Last.Baseline - paragraph.First.Baseline) / (paragraph.Lines.Count - 1)
            : size * 1.2;
        spacing = Math.Clamp(spacing, size, size * 2.5);

        double indentLeft = 0, firstLine = 0;
        if (alignment is PdfFlowAlignment.Left or PdfFlowAlignment.Justify)
        {
            indentLeft = Math.Max(0, paragraph.BodyLeft - margins.Left);
            if (paragraph.Lines.Count > 1)
                firstLine = paragraph.First.Left - paragraph.BodyLeft;
            if (Math.Abs(firstLine) < 1)
                firstLine = 0;
            if (indentLeft < 1)
                indentLeft = 0;
        }

        return new PdfFlowParagraph(paragraph.First.Baseline - spacing * AscentShare,
            paragraph.Last.Baseline + spacing * (1 - AscentShare),
            indentLeft, firstLine, spacing, alignment, BuildRuns(paragraph))
        {
            TabStops = tabs.Select(x => Math.Max(0, x - margins.Left)).ToList()
        };
    }

    private static PdfFlowAlignment GuessAlignment(Paragraph paragraph, double tolerance, double contentLeft, double contentRight)
    {
        var lines = paragraph.Lines;
        if (lines.Count == 1)
        {
            var line = lines[0];
            var contentWidth = Math.Max(contentRight - contentLeft, 1);
            var contentCenter = (contentLeft + contentRight) / 2;
            if (Math.Abs(line.Center - contentCenter) < contentWidth * 0.03 && line.Left > contentLeft + contentWidth * 0.1)
                return PdfFlowAlignment.Center;
            if (Math.Abs(line.Right - contentRight) < tolerance && line.Left > contentLeft + contentWidth * 0.3)
                return PdfFlowAlignment.Right;
            return PdfFlowAlignment.Left;
        }

        var bodyLeft = paragraph.BodyLeft;
        var maxRight = paragraph.MaxRight;
        var leftAligned = lines.Skip(1).All(l => Math.Abs(l.Left - bodyLeft) < tolerance);
        var rightAligned = lines.Take(lines.Count - 1).All(l => Math.Abs(l.Right - maxRight) < tolerance);

        if (leftAligned && rightAligned)
        {
            var leftsVary = Math.Abs(lines[0].Left - bodyLeft) > tolerance * 4;
            return leftsVary || lines.Count > 2 || lines[^1].Right < maxRight - tolerance
                ? PdfFlowAlignment.Justify
                : PdfFlowAlignment.Left;
        }

        if (leftAligned)
            return PdfFlowAlignment.Left;
        if (lines.All(l => Math.Abs(l.Center - lines[0].Center) < tolerance))
            return PdfFlowAlignment.Center;
        if (lines.All(l => Math.Abs(l.Right - maxRight) < tolerance))
            return PdfFlowAlignment.Right;
        return PdfFlowAlignment.Left;
    }

    private static List<PdfFlowRun> BuildRuns(Paragraph paragraph)
    {
        var runs = new List<PdfFlowRun>();
        var text = new StringBuilder();
        PdfFlowRunStyle? style = null;

        void Flush()
        {
            if (text.Length > 0 && style is not null)
                runs.Add(new PdfFlowRun(text.ToString(), style));
            text.Clear();
        }

        void Append(char value, PdfFlowRunStyle charStyle)
        {
            if (value == ' ')
            {
                var previous = text.Length > 0 ? text[^1] : runs.Count > 0 ? runs[^1].Text[^1] : ' ';
                if (previous == ' ')
                    return;

                // A space stays in the current run whatever its own style, so runs split on words.
                style ??= charStyle;
                text.Append(' ');
                return;
            }

            if (style is not null && text.Length > 0 && !Equals(style, charStyle))
                Flush();
            if (text.Length == 0)
                style = charStyle;
            text.Append(value);
        }

        for (var li = 0; li < paragraph.Lines.Count; li++)
        {
            var chars = paragraph.Lines[li].Chars;
            var end = chars.Count;
            while (end > 0 && char.IsWhiteSpace(chars[end - 1].Value))
                end--;

            var start = 0;
            while (start < end && char.IsWhiteSpace(chars[start].Value))
                start++;

            if (li > 0 && end > start)
            {
                // A word broken with a hyphen at the end of the previous line is joined back together.
                var joined = TrimTrailingHyphen(text, chars[start].Value);
                if (!joined)
                    Append(' ', chars[start].Style);
            }

            for (var i = start; i < end; i++)
            {
                var value = chars[i].Value;
                if (value is '\u00A0')
                    value = ' ';
                if (char.IsControl(value) && value != '\t')
                    continue;
                Append(value, chars[i].Style);
            }
        }

        Flush();
        return runs;
    }

    private static bool TrimTrailingHyphen(StringBuilder text, char nextChar)
    {
        if (!char.IsLower(nextChar))
            return false;

        if (text.Length >= 2 && text[^1] is '-' or '\u00AD' && char.IsLetter(text[^2]))
        {
            text.Length--;
            return true;
        }

        return false;
    }

    private static List<PdfFlowBlock> WithSpacing(List<PdfFlowBlock> blocks, double marginTop)
    {
        var result = new List<PdfFlowBlock>(blocks.Count);
        var previousExit = marginTop;
        foreach (var block in blocks)
        {
            var space = Math.Clamp(block.Top - previousExit, 0, MaxSpaceBefore);
            result.Add(block with { SpaceBefore = space < 1 ? 0 : space });
            previousExit = Math.Max(block.Bottom, block.Top);
        }

        return result;
    }

    /// <summary>
    /// Turns a PDF font name such as "ABCDEF+TimesNewRomanPS-BoldMT" into a family a word processor
    /// will recognise ("Times New Roman").
    /// </summary>
    public static string CleanFontName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Calibri";

        var name = raw;
        var plus = name.IndexOf('+');
        if (plus is > 0 and <= 7)
            name = name[(plus + 1)..];

        var comma = name.IndexOf(',');
        if (comma > 0)
            name = name[..comma];

        // "Noto-Sans-Bold" and "TimesNewRomanPS-BoldMT": drop the style parts after the family.
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (parts.Count > 1 && IsStyleWord(parts[^1]))
            parts.RemoveAt(parts.Count - 1);
        name = string.Join(" ", parts);

        foreach (var suffix in new[] { "PSMT", "PS", "MT" })
        {
            if (name.Length > suffix.Length + 2 && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        foreach (var style in new[] { "BoldItalic", "BoldOblique", "Bold", "Italic", "Oblique", "Regular" })
        {
            if (name.Length > style.Length + 2 && name.EndsWith(style, StringComparison.Ordinal))
            {
                name = name[..^style.Length];
                break;
            }
        }

        var spaced = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(name[i - 1]))
                spaced.Append(' ');
            spaced.Append(c);
        }

        var family = string.Join(' ', spaced.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return family switch
        {
            "" => "Calibri",
            "Helvetica" or "Arial MT" => "Arial",
            "Times" or "Times Roman" => "Times New Roman",
            "Courier" => "Courier New",
            _ => family
        };
    }

    private static bool IsStyleWord(string part)
    {
        var word = part.EndsWith("MT", StringComparison.Ordinal) && part.Length > 2 ? part[..^2] : part;
        return word.ToLowerInvariant() is "bold" or "italic" or "bolditalic" or "oblique" or "boldoblique" or "regular"
            or "light" or "lightitalic" or "medium" or "mediumitalic" or "semibold" or "semibolditalic" or "demibold"
            or "black" or "heavy" or "thin" or "extralight" or "extrabold" or "condensed" or "roman" or "book" or "it"
            or "";
    }

    private const int FontFlagFixedPitch = 0x1;
    private const int FontFlagSerif = 0x2;

    /// <summary>Sorts a font into sans, serif, or monospace from its name and its PDF font flags.</summary>
    public static PdfFlowFontKind GuessFontKind(string family, int flags)
    {
        var name = family.ToLowerInvariant();
        if ((flags & FontFlagFixedPitch) != 0 || name.Contains("mono") || name.Contains("courier")
            || name.Contains("consol") || name.Contains("menlo") || name.Contains("code"))
        {
            return PdfFlowFontKind.Mono;
        }

        if (name.Contains("sans"))
            return PdfFlowFontKind.Sans;
        if ((flags & FontFlagSerif) != 0 || name.Contains("times") || name.Contains("serif") || name.Contains("georgia")
            || name.Contains("garamond") || name.Contains("cambria") || name.Contains("roman") || name.Contains("book"))
        {
            return PdfFlowFontKind.Serif;
        }

        return PdfFlowFontKind.Sans;
    }
}
