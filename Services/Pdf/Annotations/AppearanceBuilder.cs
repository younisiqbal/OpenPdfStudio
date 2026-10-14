using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;
using OpenPdfStudio.Models;
using PDFiumCore;

namespace OpenPdfStudio.Services.Pdf.Annotations;

/// <summary>Result of laying out a text annotation: the final box and the appearance stream.</summary>
internal sealed record FreeTextAppearance(Rect Bounds, string Content);

/// <summary>
/// Builds annotation appearance streams. Geometry is worked out in the view's top-left point space and each
/// point is mapped into PDF user space, so rotated pages and offset media boxes come out right.
/// Text is written as glyph outlines because PDFium's SetAP cannot attach font resources. Worker thread only.
/// </summary>
internal static class AppearanceBuilder
{
    public const double TypewriterPadding = 2;
    public const double MinBoxWidth = 24;
    private const double LineHeightFactor = 1.2;
    private const double ArrowLength = 9;
    private const double ArrowHalfWidth = 4;
    private const double IconOutline = 0.8;
    private const double IconDetail = 1.3;

    private static readonly Dictionary<(IntPtr Font, char Char), GlyphSegment[]> GlyphCache = new();

    private readonly record struct GlyphSegment(int Type, float X, float Y, bool Close);

    public static double LineHeight(AnnotationStyle style) => style.FontSize * LineHeightFactor * Math.Max(0.5, style.LineSpacing);

    public static double Padding(PdfAnnotation annotation) => annotation.Kind == AnnotationKind.Typewriter
        ? TypewriterPadding
        : Math.Max(0, annotation.Style.BorderColor is null ? 0 : annotation.Style.BorderWidth) + 3;

    public static FreeTextAppearance BuildFreeText(PdfAnnotation annotation, FpdfFontT font, PdfDocumentSession.PageSpaceMap map)
    {
        var style = annotation.Style;
        var layout = new TextLayout(font, style.FontSize);
        var padding = Padding(annotation);
        var lineHeight = LineHeight(style);
        var bounds = annotation.Bounds;

        List<LaidOutLine> lines;
        if (annotation.Kind == AnnotationKind.Typewriter)
        {
            lines = layout.Layout(annotation.Contents, null);
            var width = Math.Max(MinBoxWidth, Max(lines) + padding * 2);
            bounds = new Rect(bounds.X, bounds.Y, width, lines.Count * lineHeight + padding * 2);
        }
        else
        {
            var width = Math.Max(MinBoxWidth, bounds.Width);
            lines = layout.Layout(annotation.Contents, width - padding * 2);
            var height = Math.Max(bounds.Height, lines.Count * lineHeight + padding * 2);
            bounds = new Rect(bounds.X, bounds.Y, width, height);
        }

        var w = new ContentWriter(map);
        w.BeginOpacity(style.Opacity);

        if (annotation.Kind == AnnotationKind.Callout && annotation.CalloutTarget is { } target)
            WriteCalloutLine(w, bounds, target, style);

        if (annotation.Kind != AnnotationKind.Typewriter)
        {
            if (style.FillColor is { } fill)
            {
                w.SetFill(fill);
                w.Rect(bounds);
                w.Op("f");
            }

            if (style.BorderColor is { } border && style.BorderWidth > 0)
            {
                w.SetStroke(border);
                w.LineWidth(style.BorderWidth);
                w.Rect(bounds.Deflate(style.BorderWidth / 2));
                w.Op("S");
            }
        }

        w.SetFill(style.TextColor);
        w.SetStroke(style.TextColor);
        w.LineWidth(Math.Max(0.5, style.FontSize * 0.06));

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var baseline = bounds.Y + padding + layout.Ascent + i * lineHeight;
            var x = style.Align switch
            {
                AnnotationTextAlign.Center => bounds.X + (bounds.Width - line.Width) / 2,
                AnnotationTextAlign.Right => bounds.Right - padding - line.Width,
                _ => bounds.X + padding
            };

            if (line.Text.Length == 0)
                continue;

            var pen = x;
            var any = false;
            foreach (var c in line.Text)
            {
                any |= WriteGlyph(w, font, c, new Point(pen, baseline), style.FontSize);
                pen += layout.CharWidth(c);
            }

            if (any)
                w.Op("f");

            if (style.Underline)
                w.Line(new Point(x, baseline + style.FontSize * 0.12), new Point(x + line.Width, baseline + style.FontSize * 0.12));
            if (style.Strikethrough)
                w.Line(new Point(x, baseline - style.FontSize * 0.28), new Point(x + line.Width, baseline - style.FontSize * 0.28));
        }

        w.EndOpacity(style.Opacity);
        return new FreeTextAppearance(bounds, w.ToString());
    }

    public static string BuildNote(PdfAnnotation annotation, PdfDocumentSession.PageSpaceMap map)
    {
        var style = annotation.Style;
        var (body, detail) = NoteIcons.Get(style.Icon);
        var scale = annotation.Bounds.Width / NoteIcons.Size;
        var origin = annotation.Bounds.TopLeft;

        var w = new ContentWriter(map);
        w.BeginOpacity(style.Opacity);
        w.Op("1 j 1 J");
        w.SetFill(style.Color);
        w.SetStroke(Darken(style.Color));
        w.LineWidth(IconOutline * scale);
        w.SvgPath(body, origin, scale);
        w.Op("B");

        if (!string.IsNullOrEmpty(detail))
        {
            w.SetStroke(Color.FromRgb(40, 40, 40));
            w.LineWidth(IconDetail * scale);
            w.SvgPath(detail, origin, scale);
            w.Op("S");
        }

        w.EndOpacity(style.Opacity);
        return w.ToString();
    }

    /// <summary>The point on the box edge the callout line starts from: the edge midpoint nearest the target.</summary>
    public static Point CalloutAnchor(Rect box, Point target)
    {
        Point[] anchors =
        [
            new(box.Center.X, box.Top), new(box.Center.X, box.Bottom),
            new(box.Left, box.Center.Y), new(box.Right, box.Center.Y)
        ];

        var best = anchors[0];
        var bestDistance = double.MaxValue;
        foreach (var anchor in anchors)
        {
            var distance = Vector.DistanceSquared(new Vector(anchor.X, anchor.Y), new Vector(target.X, target.Y));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = anchor;
            }
        }

        return best;
    }

    private static void WriteCalloutLine(ContentWriter w, Rect box, Point target, AnnotationStyle style)
    {
        var anchor = CalloutAnchor(box, target);
        var color = style.BorderColor ?? style.TextColor;
        var direction = new Vector(target.X - anchor.X, target.Y - anchor.Y);
        var length = direction.Length;
        if (length < 1)
            return;

        var unit = direction / length;
        var normal = new Vector(-unit.Y, unit.X);
        var arrowBase = target - unit * Math.Min(ArrowLength, length);

        w.SetStroke(color);
        w.SetFill(color);
        w.LineWidth(Math.Max(1, style.BorderWidth));
        w.Line(anchor, arrowBase);

        w.MoveTo(target);
        w.LineTo(arrowBase + normal * ArrowHalfWidth);
        w.LineTo(arrowBase - normal * ArrowHalfWidth);
        w.Op("h f");
    }

    private static bool WriteGlyph(ContentWriter w, FpdfFontT font, char c, Point origin, double size)
    {
        if (char.IsWhiteSpace(c))
            return false;

        var segments = GlyphSegments(font, c);
        if (segments.Length == 0)
            return false;

        Point At(GlyphSegment s) => new(origin.X + s.X * size, origin.Y - s.Y * size);

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            switch (segment.Type)
            {
                case 2:
                    w.MoveTo(At(segment));
                    break;
                case 1 when i + 2 < segments.Length:
                    w.CurveTo(At(segment), At(segments[i + 1]), At(segments[i + 2]));
                    i += 2;
                    segment = segments[i];
                    break;
                default:
                    w.LineTo(At(segment));
                    break;
            }

            if (segment.Close)
                w.Op("h");
        }

        return true;
    }

    private static GlyphSegment[] GlyphSegments(FpdfFontT font, char c)
    {
        var key = (font.__Instance, c);
        if (GlyphCache.TryGetValue(key, out var cached))
            return cached;

        var path = fpdf_edit.FPDFFontGetGlyphPath(font, c, 1);
        var result = Array.Empty<GlyphSegment>();
        if (path is not null && path.__Instance != IntPtr.Zero)
        {
            var count = fpdf_edit.FPDFGlyphPathCountGlyphSegments(path);
            result = new GlyphSegment[Math.Max(0, count)];
            for (int i = 0; i < count; i++)
            {
                var segment = fpdf_edit.FPDFGlyphPathGetGlyphPathSegment(path, i);
                float x = 0, y = 0;
                fpdf_edit.FPDFPathSegmentGetPoint(segment, ref x, ref y);
                result[i] = new GlyphSegment(fpdf_edit.FPDFPathSegmentGetType(segment), x, y,
                    fpdf_edit.FPDFPathSegmentGetClose(segment) != 0);
            }
        }

        GlyphCache[key] = result;
        return result;
    }

    /// <summary>Fonts are closed when their document closes; their cached outlines go with them.</summary>
    public static void ForgetFont(FpdfFontT font)
    {
        var stale = new List<(IntPtr, char)>();
        foreach (var key in GlyphCache.Keys)
        {
            if (key.Font == font.__Instance)
                stale.Add(key);
        }

        foreach (var key in stale)
            GlyphCache.Remove(key);
    }

    private static Color Darken(Color color) =>
        Color.FromRgb((byte)(color.R * 0.55), (byte)(color.G * 0.55), (byte)(color.B * 0.55));

    private static double Max(List<LaidOutLine> lines)
    {
        double max = 0;
        foreach (var line in lines)
            max = Math.Max(max, line.Width);
        return max;
    }

    /// <summary>Writes PDF content operators, mapping every point from view space into user space.</summary>
    private sealed class ContentWriter(PdfDocumentSession.PageSpaceMap map)
    {
        private readonly StringBuilder _content = new();

        public void Op(string op) => _content.Append(op).Append('\n');

        public void BeginOpacity(double opacity)
        {
            Op("q");
            // PDFium adds a /GS ExtGState to the appearance resources when the annotation's /CA is below 1.
            if (opacity < 1)
                Op("/GS gs");
        }

        public void EndOpacity(double opacity) => Op("Q");

        public void SetFill(Color color) => Op($"{N(color.R / 255.0)} {N(color.G / 255.0)} {N(color.B / 255.0)} rg");
        public void SetStroke(Color color) => Op($"{N(color.R / 255.0)} {N(color.G / 255.0)} {N(color.B / 255.0)} RG");
        public void LineWidth(double width) => Op($"{N(width)} w");

        public void MoveTo(Point p) => Op($"{P(p)} m");
        public void LineTo(Point p) => Op($"{P(p)} l");
        public void CurveTo(Point a, Point b, Point c) => Op($"{P(a)} {P(b)} {P(c)} c");

        public void Line(Point from, Point to)
        {
            MoveTo(from);
            LineTo(to);
            Op("S");
        }

        public void Rect(Rect rect)
        {
            MoveTo(rect.TopLeft);
            LineTo(rect.TopRight);
            LineTo(rect.BottomRight);
            LineTo(rect.BottomLeft);
            Op("h");
        }

        /// <summary>Writes a path made of absolute M, L, C and Z commands, scaled and offset.</summary>
        public void SvgPath(string path, Point origin, double scale)
        {
            var tokens = path.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = 'M';
            var numbers = new List<double>();

            Point Pt(int i) => new(origin.X + numbers[i] * scale, origin.Y + numbers[i + 1] * scale);

            void Flush()
            {
                switch (command)
                {
                    case 'M' when numbers.Count >= 2:
                        MoveTo(Pt(0));
                        for (int i = 2; i + 1 < numbers.Count; i += 2)
                            LineTo(Pt(i));
                        break;
                    case 'L':
                        for (int i = 0; i + 1 < numbers.Count; i += 2)
                            LineTo(Pt(i));
                        break;
                    case 'C':
                        for (int i = 0; i + 5 < numbers.Count; i += 6)
                            CurveTo(Pt(i), Pt(i + 2), Pt(i + 4));
                        break;
                    case 'Z':
                        Op("h");
                        break;
                }

                numbers.Clear();
            }

            foreach (var token in tokens)
            {
                if (char.IsLetter(token[0]))
                {
                    Flush();
                    command = char.ToUpperInvariant(token[0]);
                    if (token.Length > 1)
                        numbers.Add(double.Parse(token[1..], CultureInfo.InvariantCulture));
                    continue;
                }

                numbers.Add(double.Parse(token, CultureInfo.InvariantCulture));
            }

            Flush();
        }

        private string P(Point point)
        {
            var page = map.ToPage(point);
            return $"{N(page.X)} {N(page.Y)}";
        }

        private static string N(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

        public override string ToString() => _content.ToString();
    }
}
