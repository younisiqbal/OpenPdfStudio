using System;
using System.Collections.Generic;
using System.Text;
using PDFiumCore;

namespace OpenPdfStudio.Services.Pdf.Annotations;

internal sealed record LaidOutLine(string Text, double Width);

/// <summary>
/// Measures and wraps text with a PDFium standard font. Widths come from the font's own metrics, so the
/// layout matches the glyph outlines written into the appearance stream. Worker thread only.
/// </summary>
internal sealed class TextLayout
{
    private const double MissingGlyphWidth = 0.5;

    private readonly FpdfFontT _font;
    private readonly double _size;
    private readonly Dictionary<char, double> _widths = new();

    public TextLayout(FpdfFontT font, double size)
    {
        _font = font;
        _size = size;

        float ascent = 0, descent = 0;
        fpdf_edit.FPDFFontGetAscent(font, (float)size, ref ascent);
        fpdf_edit.FPDFFontGetDescent(font, (float)size, ref descent);
        Ascent = ascent > 0 ? ascent : size * 0.8;
        Descent = descent < 0 ? -descent : size * 0.2;
    }

    public double Ascent { get; }
    public double Descent { get; }

    public double CharWidth(char c)
    {
        if (_widths.TryGetValue(c, out var cached))
            return cached;

        float width = 0;
        var ok = fpdf_edit.FPDFFontGetGlyphWidth(_font, c, (float)_size, ref width) != 0;
        var value = ok && width > 0 ? width : (c == ' ' ? _size * 0.28 : _size * MissingGlyphWidth);
        _widths[c] = value;
        return value;
    }

    public double Measure(string text)
    {
        double width = 0;
        foreach (var c in text)
            width += CharWidth(c);
        return width;
    }

    /// <summary>Splits text into lines. With no <paramref name="maxWidth"/>, only explicit line breaks wrap.</summary>
    public List<LaidOutLine> Layout(string text, double? maxWidth)
    {
        var lines = new List<LaidOutLine>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (maxWidth is not { } max || max <= 0)
            {
                lines.Add(new LaidOutLine(paragraph, Measure(paragraph)));
                continue;
            }

            WrapParagraph(paragraph, max, lines);
        }

        return lines;
    }

    private void WrapParagraph(string paragraph, double max, List<LaidOutLine> lines)
    {
        if (paragraph.Length == 0)
        {
            lines.Add(new LaidOutLine(string.Empty, 0));
            return;
        }

        var line = new StringBuilder();
        double lineWidth = 0;

        foreach (var word in SplitKeepingSpaces(paragraph))
        {
            var wordWidth = Measure(word);
            if (lineWidth + wordWidth <= max || line.Length == 0 && wordWidth <= max)
            {
                line.Append(word);
                lineWidth += wordWidth;
                continue;
            }

            if (line.Length > 0)
            {
                Flush(line, lines);
                lineWidth = 0;
            }

            var trimmed = word.TrimStart(' ');
            foreach (var c in trimmed)
            {
                var w = CharWidth(c);
                if (lineWidth + w > max && line.Length > 0)
                {
                    Flush(line, lines);
                    lineWidth = 0;
                }

                line.Append(c);
                lineWidth += w;
            }
        }

        Flush(line, lines);
    }

    private void Flush(StringBuilder line, List<LaidOutLine> lines)
    {
        var text = line.ToString().TrimEnd(' ');
        lines.Add(new LaidOutLine(text, Measure(text)));
        line.Clear();
    }

    /// <summary>Words with their leading spaces attached, so wrapping never starts a line with a space.</summary>
    private static IEnumerable<string> SplitKeepingSpaces(string text)
    {
        var start = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || (text[i] == ' ' && text[i - 1] != ' '))
            {
                yield return text[start..i];
                start = i;
            }
        }
    }
}
