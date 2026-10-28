using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services.Pdf.Annotations;
using PDFiumCore;

namespace OpenPdfStudio.Services.Pdf;

public sealed partial class PdfDocumentSession
{
    private const int AnnotText = 1;
    private const int AnnotFreeText = 3;
    private const int AnnotHighlight = 9;
    private const int AnnotUnderline = 10;
    private const int AnnotSquiggly = 11;
    private const int AnnotStrikeOut = 12;
    private const int FlagHidden = 2;
    private const int FlagPrint = 4;
    private const int AppearanceNormal = 0;

    /// <summary>
    /// Private key holding our layout and style as JSON. The fpdf_annot API can only write strings, so the
    /// callout box, arrow target and full style live here; standard keys are still written for other readers.
    /// </summary>
    private const string DataKey = "OPSData";

    private readonly Dictionary<string, FpdfFontT> _fonts = new();

    public Task<IReadOnlyList<PdfAnnotation>> GetAnnotationsAsync(int pageIndex, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync<IReadOnlyList<PdfAnnotation>>(() => WithPage(pageIndex, (page, map) =>
        {
            var result = new List<PdfAnnotation>();
            var count = fpdf_annot.FPDFPageGetAnnotCount(page);
            for (int i = 0; i < count; i++)
            {
                var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                if (PdfHandles.IsNull(annot))
                    continue;

                try
                {
                    if (ReadAnnotation(annot!, pageIndex, map) is { } annotation)
                        result.Add(annotation);
                }
                finally
                {
                    fpdf_annot.FPDFPageCloseAnnot(annot);
                }
            }

            return result;
        }, edits: false), cancellationToken, lowPriority: true);

    /// <summary>Adds an annotation. Returns it with its final bounds (text boxes grow to fit their text).</summary>
    public Task<PdfAnnotation> AddAnnotationAsync(PdfAnnotation annotation) =>
        _worker.InvokeAsync(() => WithPage(annotation.PageIndex, (page, map) =>
        {
            var annot = fpdf_annot.FPDFPageCreateAnnot(page, SubtypeOf(annotation.Kind));
            if (PdfHandles.IsNull(annot))
                throw new InvalidOperationException("PDFium could not create the annotation.");

            try
            {
                return WriteAnnotation(annot!, annotation, map, hidden: false, isNew: true);
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }));

    /// <summary>Rewrites an existing annotation in place, keeping its position in the page's list.</summary>
    public Task<PdfAnnotation> UpdateAnnotationAsync(PdfAnnotation annotation, bool hidden = false) =>
        _worker.InvokeAsync(() => WithPage(annotation.PageIndex, (page, map) =>
        {
            var (annot, _) = FindAnnotation(page, annotation.Id);
            if (annot is null)
                throw new InvalidOperationException("The annotation no longer exists.");

            try
            {
                return WriteAnnotation(annot, annotation, map, hidden, isNew: false);
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }));

    public Task RemoveAnnotationAsync(int pageIndex, string id) =>
        _worker.InvokeAsync(() => WithPage(pageIndex, (page, _) =>
        {
            var (annot, index) = FindAnnotation(page, id);
            if (annot is null)
                return false;

            fpdf_annot.FPDFPageCloseAnnot(annot);
            return fpdf_annot.FPDFPageRemoveAnnot(page, index) != 0;
        }));

    /// <summary>Hides an annotation while it is edited on screen, so the old appearance does not show through.</summary>
    public Task SetAnnotationHiddenAsync(int pageIndex, string id, bool hidden) =>
        _worker.InvokeAsync(() => WithPage(pageIndex, (page, _) =>
        {
            var (annot, _) = FindAnnotation(page, id);
            if (annot is null)
                return false;

            try
            {
                return fpdf_annot.FPDFAnnotSetFlags(annot, hidden ? FlagHidden | FlagPrint : FlagPrint) != 0;
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }));

    /// <summary>Measures a text annotation without changing the document, for live previews.</summary>
    public Task<Rect> MeasureFreeTextAsync(PdfAnnotation annotation) =>
        _worker.InvokeAsync(() => WithPage(annotation.PageIndex, (_, map) =>
            AppearanceBuilder.BuildFreeText(annotation, GetFont(annotation.Style), map).Bounds, edits: false));

    private T WithPage<T>(int pageIndex, Func<FpdfPageT, PageSpaceMap, T> action, bool edits = true)
    {
        ThrowIfDisposed();
        if (pageIndex < 0 || pageIndex >= _pageSizes.Length)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        if (edits)
            ForgetPage(pageIndex);

        var page = fpdfview.FPDF_LoadPage(_document, pageIndex);
        if (PdfHandles.IsNull(page))
            throw new InvalidOperationException($"Page {pageIndex + 1} could not be loaded.");

        try
        {
            return action(page, PageSpaceMap.For(page, _pageSizes[pageIndex]));
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static (FpdfAnnotationT? Annot, int Index) FindAnnotation(FpdfPageT page, string id)
    {
        var count = fpdf_annot.FPDFPageGetAnnotCount(page);
        for (int i = 0; i < count; i++)
        {
            var annot = fpdf_annot.FPDFPageGetAnnot(page, i);
            if (PdfHandles.IsNull(annot))
                continue;

            if (GetString(annot!, "NM") == id)
                return (annot, i);

            fpdf_annot.FPDFPageCloseAnnot(annot);
        }

        return (null, -1);
    }

    private PdfAnnotation? ReadAnnotation(FpdfAnnotationT annot, int pageIndex, PageSpaceMap map)
    {
        var subtype = fpdf_annot.FPDFAnnotGetSubtype(annot);
        AnnotationKind? kind = subtype switch
        {
            AnnotText => AnnotationKind.Note,
            AnnotFreeText => AnnotationKind.Textbox,
            AnnotHighlight => AnnotationKind.Highlight,
            AnnotUnderline => AnnotationKind.Underline,
            AnnotSquiggly => AnnotationKind.Squiggly,
            AnnotStrikeOut => AnnotationKind.StrikeOut,
            _ => null
        };
        if (kind is null)
            return null;

        var id = GetString(annot, "NM");
        if (string.IsNullOrEmpty(id))
        {
            id = PdfAnnotation.NewId();
            SetString(annot, "NM", id);
        }

        var rect = new FS_RECTF_();
        var bounds = fpdf_annot.FPDFAnnotGetRect(annot, rect) != 0
            ? ToViewRect(map, rect.Left, rect.Top, rect.Right, rect.Bottom)
            : default;

        var annotation = new PdfAnnotation
        {
            Id = id,
            PageIndex = pageIndex,
            Kind = kind.Value,
            Bounds = bounds,
            Contents = GetString(annot, "Contents"),
            Author = GetString(annot, "T"),
            Modified = PdfDate.Parse(GetString(annot, "M")) ?? DateTimeOffset.MinValue,
            Style = AnnotationStyle.DefaultFor(kind.Value)
        };

        if (AnnotationData.TryParse(GetString(annot, DataKey)) is { } data)
        {
            annotation = annotation with
            {
                Kind = data.Kind,
                Bounds = data.Box ?? annotation.Bounds,
                CalloutTarget = data.Target,
                PopupBounds = data.Popup,
                Style = data.Style
            };
        }
        else
        {
            annotation = annotation with { Style = ReadForeignStyle(annot, annotation) };
        }

        if (annotation.IsMarkup)
        {
            var quads = ReadQuads(annot, map);
            if (quads.Count > 0)
            {
                var union = quads[0];
                foreach (var quad in quads)
                    union = union.Union(quad);
                annotation = annotation with { Quads = quads, Bounds = union };
            }
        }

        return annotation;
    }

    /// <summary>Style for annotations made by other apps, from the colour, /CA and /DA they carry.</summary>
    private static AnnotationStyle ReadForeignStyle(FpdfAnnotationT annot, PdfAnnotation annotation)
    {
        var style = annotation.Style;
        uint r = 0, g = 0, b = 0, a = 255;
        if (fpdf_annot.FPDFAnnotGetColor(annot, FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color, ref r, ref g, ref b, ref a) != 0)
        {
            var color = Color.FromRgb((byte)r, (byte)g, (byte)b);
            style = annotation.IsFreeText
                ? style with { BorderColor = color, Opacity = a / 255.0 }
                : style with { Color = color, Opacity = a / 255.0 };
        }

        if (annotation.Kind == AnnotationKind.Note &&
            Enum.TryParse(GetString(annot, "Name"), ignoreCase: true, out NoteIcon icon))
            style = style with { Icon = icon };

        if (annotation.IsFreeText)
        {
            var da = GetString(annot, "DA");
            var size = Regex.Match(da, @"([\d.]+)\s+Tf");
            if (size.Success && double.TryParse(size.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize) && fontSize > 0)
                style = style with { FontSize = fontSize };

            var rgb = Regex.Match(da, @"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg");
            if (rgb.Success)
            {
                byte C(int group) => (byte)Math.Clamp(double.Parse(rgb.Groups[group].Value, CultureInfo.InvariantCulture) * 255, 0, 255);
                style = style with { TextColor = Color.FromRgb(C(1), C(2), C(3)) };
            }
        }

        return style;
    }

    private static List<Rect> ReadQuads(FpdfAnnotationT annot, PageSpaceMap map)
    {
        var quads = new List<Rect>();
        var count = (int)fpdf_annot.FPDFAnnotCountAttachmentPoints(annot);
        for (int i = 0; i < count; i++)
        {
            var q = new FS_QUADPOINTSF();
            if (fpdf_annot.FPDFAnnotGetAttachmentPoints(annot, (ulong)i, q) == 0)
                continue;

            var a = map.ToView(q.X1, q.Y1);
            var b = map.ToView(q.X2, q.Y2);
            var c = map.ToView(q.X3, q.Y3);
            var d = map.ToView(q.X4, q.Y4);
            var left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
            var top = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
            var right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            var bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
            quads.Add(new Rect(left, top, right - left, bottom - top));
        }

        return quads;
    }

    private PdfAnnotation WriteAnnotation(FpdfAnnotationT annot, PdfAnnotation annotation, PageSpaceMap map, bool hidden, bool isNew)
    {
        var style = annotation.Style;

        // PDFium refuses colour changes while an appearance stream exists, so drop it first.
        fpdf_annot.FPDFAnnotSetAP(annot, AppearanceNormal, ref Unsafe.NullRef<ushort>());

        SetString(annot, "NM", annotation.Id);
        SetString(annot, "Contents", annotation.Contents);
        SetString(annot, "T", annotation.Author);
        SetString(annot, "M", PdfDate.Format(annotation.Modified));
        if (isNew)
            SetString(annot, "CreationDate", PdfDate.Format(annotation.Modified));
        fpdf_annot.FPDFAnnotSetFlags(annot, hidden ? FlagHidden | FlagPrint : FlagPrint);

        var alpha = (uint)Math.Clamp(Math.Round(style.Opacity * 255), 0, 255);
        var mainColor = annotation.IsFreeText ? style.BorderColor ?? style.TextColor : style.Color;
        fpdf_annot.FPDFAnnotSetColor(annot, FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color, mainColor.R, mainColor.G, mainColor.B, alpha);

        var result = annotation;
        string? appearance = null;

        if (annotation.IsMarkup)
        {
            WriteQuads(annot, annotation.Quads, map, isNew);
            SetRect(annot, map, annotation.Bounds);
        }
        else if (annotation.Kind == AnnotationKind.Note)
        {
            SetString(annot, "Name", style.Icon.ToString());
            SetRect(annot, map, annotation.Bounds);
            appearance = AppearanceBuilder.BuildNote(annotation, map);
        }
        else
        {
            var built = AppearanceBuilder.BuildFreeText(annotation, GetFont(style), map);
            result = annotation with { Bounds = built.Bounds };
            appearance = built.Content;
            SetString(annot, "DA", $"/Helv {N(style.FontSize)} Tf {N(style.TextColor.R / 255.0)} {N(style.TextColor.G / 255.0)} {N(style.TextColor.B / 255.0)} rg");
            if (style.FillColor is { } fill)
                fpdf_annot.FPDFAnnotSetColor(annot, FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_InteriorColor, fill.R, fill.G, fill.B, alpha);

            var extent = result.Extent;
            SetRect(annot, map, extent.Inflate(result.Kind == AnnotationKind.Callout ? 6 : 0));
        }

        SetString(annot, DataKey, AnnotationData.Serialize(result));

        if (appearance is not null)
        {
            var chars = ToUtf16(appearance);
            if (fpdf_annot.FPDFAnnotSetAP(annot, AppearanceNormal, ref chars[0]) == 0)
                throw new InvalidOperationException("PDFium could not store the annotation's appearance.");
        }

        return result;
    }

    private static void WriteQuads(FpdfAnnotationT annot, IReadOnlyList<Rect> quads, PageSpaceMap map, bool isNew)
    {
        var existing = isNew ? 0 : (int)fpdf_annot.FPDFAnnotCountAttachmentPoints(annot);
        for (int i = 0; i < quads.Count; i++)
        {
            var r = quads[i];
            var tl = map.ToPage(r.TopLeft);
            var tr = map.ToPage(r.TopRight);
            var bl = map.ToPage(r.BottomLeft);
            var br = map.ToPage(r.BottomRight);
            var q = new FS_QUADPOINTSF
            {
                X1 = (float)tl.X, Y1 = (float)tl.Y,
                X2 = (float)tr.X, Y2 = (float)tr.Y,
                X3 = (float)bl.X, Y3 = (float)bl.Y,
                X4 = (float)br.X, Y4 = (float)br.Y
            };

            if (i < existing)
                fpdf_annot.FPDFAnnotSetAttachmentPoints(annot, (ulong)i, q);
            else
                fpdf_annot.FPDFAnnotAppendAttachmentPoints(annot, q);
        }
    }

    private static void SetRect(FpdfAnnotationT annot, PageSpaceMap map, Rect bounds)
    {
        var a = map.ToPage(bounds.TopLeft);
        var b = map.ToPage(bounds.TopRight);
        var c = map.ToPage(bounds.BottomLeft);
        var d = map.ToPage(bounds.BottomRight);
        var rect = new FS_RECTF_
        {
            Left = (float)Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
            Right = (float)Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
            Bottom = (float)Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
            Top = (float)Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y))
        };
        fpdf_annot.FPDFAnnotSetRect(annot, rect);
    }

    private static Rect ToViewRect(PageSpaceMap map, double left, double top, double right, double bottom)
    {
        var a = map.ToView(left, top);
        var b = map.ToView(right, bottom);
        var c = map.ToView(left, bottom);
        var d = map.ToView(right, top);
        var x = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        var y = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        return new Rect(x, y,
            Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) - x,
            Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) - y);
    }

    private FpdfFontT GetFont(AnnotationStyle style)
    {
        var name = StandardFontName(style.Font, style.Bold, style.Italic);
        if (_fonts.TryGetValue(name, out var font))
            return font;

        font = fpdf_edit.FPDFTextLoadStandardFont(_document, name);
        if (font is null || font.__Instance == IntPtr.Zero)
            throw new InvalidOperationException($"The font {name} could not be loaded.");

        _fonts[name] = font;
        return font;
    }

    private static string StandardFontName(AnnotationFont font, bool bold, bool italic) => font switch
    {
        AnnotationFont.Times => (bold, italic) switch
        {
            (true, true) => "Times-BoldItalic",
            (true, false) => "Times-Bold",
            (false, true) => "Times-Italic",
            _ => "Times-Roman"
        },
        AnnotationFont.Courier => (bold, italic) switch
        {
            (true, true) => "Courier-BoldOblique",
            (true, false) => "Courier-Bold",
            (false, true) => "Courier-Oblique",
            _ => "Courier"
        },
        _ => (bold, italic) switch
        {
            (true, true) => "Helvetica-BoldOblique",
            (true, false) => "Helvetica-Bold",
            (false, true) => "Helvetica-Oblique",
            _ => "Helvetica"
        }
    };

    private void CloseFonts()
    {
        foreach (var font in _fonts.Values)
        {
            AppearanceBuilder.ForgetFont(font);
            fpdf_edit.FPDFFontClose(font);
        }

        _fonts.Clear();
    }

    private static int SubtypeOf(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Note => AnnotText,
        AnnotationKind.Highlight => AnnotHighlight,
        AnnotationKind.Underline => AnnotUnderline,
        AnnotationKind.Squiggly => AnnotSquiggly,
        AnnotationKind.StrikeOut => AnnotStrikeOut,
        _ => AnnotFreeText
    };

    private static string GetString(FpdfAnnotationT annot, string key) =>
        ReadUtf16((ref ushort buffer, ulong length) => fpdf_annot.FPDFAnnotGetStringValue(annot, key, ref buffer, length));

    private static void SetString(FpdfAnnotationT annot, string key, string value)
    {
        var chars = ToUtf16(value);
        fpdf_annot.FPDFAnnotSetStringValue(annot, key, ref chars[0]);
    }

    private static ushort[] ToUtf16(string value)
    {
        var chars = new ushort[value.Length + 1];
        for (int i = 0; i < value.Length; i++)
            chars[i] = value[i];
        return chars;
    }

    private static string N(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>PDF date strings: D:YYYYMMDDHHmmSS+HH'mm'.</summary>
    private static class PdfDate
    {
        public static string Format(DateTimeOffset value)
        {
            var offset = value.Offset;
            var sign = offset < TimeSpan.Zero ? '-' : '+';
            offset = offset.Duration();
            return $"D:{value:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'";
        }

        public static DateTimeOffset? Parse(string text)
        {
            var match = Regex.Match(text, @"^D?:?(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?([+\-Z])?(\d{2})?'?(\d{2})?");
            if (!match.Success)
                return null;

            int Part(int group, int fallback) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : fallback;

            try
            {
                var offset = TimeSpan.Zero;
                if (match.Groups[7].Value is "+" or "-")
                {
                    offset = new TimeSpan(Part(8, 0), Part(9, 0), 0);
                    if (match.Groups[7].Value == "-")
                        offset = -offset;
                }

                return new DateTimeOffset(Part(1, 2000), Part(2, 1), Part(3, 1), Part(4, 0), Part(5, 0), Part(6, 0), offset);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }

    /// <summary>The JSON stored under <see cref="DataKey"/>.</summary>
    private sealed class AnnotationData
    {
        public string Kind { get; set; } = string.Empty;
        public double[]? Box { get; set; }
        public double[]? Target { get; set; }
        public double[]? Popup { get; set; }
        public AnnotationStyleData Style { get; set; } = new();

        public static string Serialize(PdfAnnotation annotation) => JsonSerializer.Serialize(new AnnotationData
        {
            Kind = annotation.Kind.ToString(),
            Box = [annotation.Bounds.X, annotation.Bounds.Y, annotation.Bounds.Width, annotation.Bounds.Height],
            Target = annotation.CalloutTarget is { } t ? [t.X, t.Y] : null,
            Popup = annotation.PopupBounds is { } p ? [p.X, p.Y, p.Width, p.Height] : null,
            Style = AnnotationStyleData.From(annotation.Style)
        });

        public static (AnnotationKind Kind, Rect? Box, Point? Target, Rect? Popup, AnnotationStyle Style)? TryParse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                var data = JsonSerializer.Deserialize<AnnotationData>(json);
                if (data is null || !Enum.TryParse(data.Kind, out AnnotationKind kind))
                    return null;

                Rect? box = data.Box is { Length: 4 } b ? new Rect(b[0], b[1], b[2], b[3]) : null;
                Point? target = data.Target is { Length: 2 } p ? new Point(p[0], p[1]) : null;
                Rect? popup = data.Popup is { Length: 4 } pop ? new Rect(pop[0], pop[1], pop[2], pop[3]) : null;
                return (kind, box, target, popup, data.Style.ToStyle());
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
