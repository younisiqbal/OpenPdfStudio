using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace OpenPdfStudio.Services.Convert;

/// <summary>
/// Writes rebuilt PDF pages as a .docx. Each PDF page becomes its own section, so page sizes, margins,
/// and page breaks follow the PDF.
/// </summary>
public static class DocxWriter
{
    private const string DefaultFont = "Calibri";
    private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    public static void Write(string path, IReadOnlyList<PdfFlowPage> pages)
    {
        var fullPath = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var document = WordprocessingDocument.Create(temp, WordprocessingDocumentType.Document))
                Fill(document, pages);

            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void Fill(WordprocessingDocument document, IReadOnlyList<PdfFlowPage> pages)
    {
        var main = document.AddMainDocumentPart();
        AddStyles(main);
        AddFontTable(main, pages);

        var body = new Body();
        var images = new Images(main);
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var paragraphs = page.Blocks.Select(block => block switch
            {
                PdfFlowParagraph p => TextParagraph(p),
                PdfFlowImage image => ImageParagraph(image, images),
                _ => new Paragraph()
            }).ToList();

            if (paragraphs.Count == 0)
                paragraphs.Add(new Paragraph());

            foreach (var picture in page.Pictures)
            {
                var (relationshipId, id) = images.Add(picture.Png);
                paragraphs[0].Append(new Run(FloatingPicture(relationshipId, id, picture.Bounds)));
            }

            var section = Section(page);
            if (i < pages.Count - 1)
            {
                var last = paragraphs[^1];
                (last.ParagraphProperties ??= new ParagraphProperties()).Append(section);
            }

            body.Append(paragraphs);
            if (i == pages.Count - 1)
                body.Append(section);
        }

        main.Document = new Document(body);
        main.Document.Save();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    Fonts(DefaultFont),
                    new FontSize { Val = "22" },
                    new FontSizeComplexScript { Val = "22" },
                    new Languages { Val = "en-US" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle())
            {
                Type = StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            });
        part.Styles.Save();
    }

    /// <summary>
    /// Lists each font with its kind, which Word uses to pick a look-alike when the font is not installed.
    /// </summary>
    private static void AddFontTable(MainDocumentPart main, IReadOnlyList<PdfFlowPage> pages)
    {
        var fonts = pages.SelectMany(p => p.Blocks).OfType<PdfFlowParagraph>()
            .SelectMany(p => p.Runs)
            .Select(r => (FontName: r.Style.FontName, r.Style.Kind))
            .Append((FontName: DefaultFont, Kind: PdfFlowFontKind.Sans))
            .DistinctBy(f => f.FontName, StringComparer.OrdinalIgnoreCase);

        var part = main.AddNewPart<FontTablePart>();
        part.Fonts = new DocumentFormat.OpenXml.Wordprocessing.Fonts(fonts.Select(f => new Font(
            new FontFamily
            {
                Val = f.Kind switch
                {
                    PdfFlowFontKind.Mono => FontFamilyValues.Modern,
                    PdfFlowFontKind.Serif => FontFamilyValues.Roman,
                    _ => FontFamilyValues.Swiss
                }
            },
            new Pitch { Val = f.Kind == PdfFlowFontKind.Mono ? FontPitchValues.Fixed : FontPitchValues.Variable })
        {
            Name = f.FontName
        }));
        part.Fonts.Save();
    }

    private static SectionProperties Section(PdfFlowPage page)
    {
        var size = new PageSize { Width = (uint)Twips(page.Width), Height = (uint)Twips(page.Height) };
        if (page.Width > page.Height)
            size.Orient = PageOrientationValues.Landscape;

        return new SectionProperties(
            new SectionType { Val = SectionMarkValues.NextPage },
            size,
            new PageMargin
            {
                Top = Twips(page.Margins.Top),
                Bottom = Twips(page.Margins.Bottom),
                Left = (uint)Twips(page.Margins.Left),
                Right = (uint)Twips(page.Margins.Right),
                Header = 360,
                Footer = 360,
                Gutter = 0
            });
    }

    private static Paragraph TextParagraph(PdfFlowParagraph block)
    {
        var properties = new ParagraphProperties();
        if (block.TabStops.Count > 0)
        {
            properties.Append(new Tabs(block.TabStops.Select(Twips).Distinct().Order().Select(position =>
                new TabStop { Val = TabStopValues.Left, Position = position })));
        }

        properties.Append(new SpacingBetweenLines
        {
            Before = Twips(block.SpaceBefore).ToString(),
            After = "0",
            Line = Twips(block.LineSpacing).ToString(),
            LineRule = LineSpacingRuleValues.AtLeast
        });

        if (block.IndentLeft > 0 || block.FirstLineIndent != 0)
        {
            var indent = new Indentation { Left = Twips(block.IndentLeft).ToString() };
            if (block.FirstLineIndent > 0)
                indent.FirstLine = Twips(block.FirstLineIndent).ToString();
            else if (block.FirstLineIndent < 0)
                indent.Hanging = Twips(-block.FirstLineIndent).ToString();
            properties.Append(indent);
        }

        properties.Append(new Justification { Val = Justify(block.Alignment) });

        var paragraph = new Paragraph(properties);
        foreach (var run in block.Runs)
            paragraph.Append(TextRun(run));
        return paragraph;
    }

    private static Run TextRun(PdfFlowRun source)
    {
        var style = source.Style;
        var properties = new RunProperties(Fonts(style.FontName));
        if (style.Bold)
            properties.Append(new Bold());
        if (style.Italic)
            properties.Append(new Italic());
        if (style.Color != 0)
            properties.Append(new Color { Val = style.Color.ToString("X6") });

        var halfPoints = Math.Max(2, (int)Math.Round(style.FontSize * 2)).ToString();
        properties.Append(new FontSize { Val = halfPoints }, new FontSizeComplexScript { Val = halfPoints });

        var run = new Run(properties);
        var parts = source.Text.Split('\t');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                run.Append(new TabChar());
            if (parts[i].Length > 0)
                run.Append(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
        }

        return run;
    }

    private static RunFonts Fonts(string family) =>
        new() { Ascii = family, HighAnsi = family, ComplexScript = family, EastAsia = family };

    private static Paragraph ImageParagraph(PdfFlowImage image, Images images)
    {
        var properties = new ParagraphProperties(new SpacingBetweenLines
        {
            Before = Twips(image.SpaceBefore).ToString(),
            After = "0",
            Line = "240",
            LineRule = LineSpacingRuleValues.Auto
        });
        if (image.IndentLeft > 0)
            properties.Append(new Indentation { Left = Twips(image.IndentLeft).ToString() });
        properties.Append(new Justification { Val = Justify(image.Alignment) });

        var (relationshipId, id) = images.Add(image.Png);
        return new Paragraph(properties, new Run(Picture(relationshipId, id, image.Width, image.Height)));
    }

    private static Drawing Picture(string relationshipId, uint id, double width, double height)
    {
        var cx = Emu(width);
        var cy = Emu(height);
        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = $"Picture {id}" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                Graphic(relationshipId, id, cx, cy))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });
    }

    /// <summary>A picture pinned to a spot on the page that text does not wrap around.</summary>
    private static Drawing FloatingPicture(string relationshipId, uint id, Avalonia.Rect bounds)
    {
        var cx = Emu(bounds.Width);
        var cy = Emu(bounds.Height);
        return new Drawing(
            new DW.Anchor(
                new DW.SimplePosition { X = 0L, Y = 0L },
                new DW.HorizontalPosition(new DW.PositionOffset(Emu(bounds.Left).ToString()))
                {
                    RelativeFrom = DW.HorizontalRelativePositionValues.Page
                },
                new DW.VerticalPosition(new DW.PositionOffset(Emu(bounds.Top).ToString()))
                {
                    RelativeFrom = DW.VerticalRelativePositionValues.Page
                },
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.WrapNone(),
                new DW.DocProperties { Id = id, Name = $"Picture {id}" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                Graphic(relationshipId, id, cx, cy))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
                SimplePos = false,
                RelativeHeight = id,
                BehindDoc = false,
                Locked = false,
                LayoutInCell = true,
                AllowOverlap = true
            });
    }

    private static A.Graphic Graphic(string relationshipId, uint id, long cx, long cy) =>
        new(new A.GraphicData(
            new PIC.Picture(
                new PIC.NonVisualPictureProperties(
                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"image{id}.png" },
                    new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(
                    new A.Blip { Embed = relationshipId },
                    new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = 0L, Y = 0L },
                        new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
        { Uri = PictureUri });

    private static JustificationValues Justify(PdfFlowAlignment alignment) => alignment switch
    {
        PdfFlowAlignment.Center => JustificationValues.Center,
        PdfFlowAlignment.Right => JustificationValues.Right,
        PdfFlowAlignment.Justify => JustificationValues.Both,
        _ => JustificationValues.Left
    };

    private static int Twips(double points) => (int)Math.Round(points * 20);

    private static long Emu(double points) => (long)Math.Round(points * 12700);

    /// <summary>Stores each distinct picture once, so a logo repeated on every page is not copied each time.</summary>
    private sealed class Images(MainDocumentPart main)
    {
        private readonly Dictionary<string, string> _byHash = new(StringComparer.Ordinal);
        private uint _nextId = 1;

        public (string RelationshipId, uint Id) Add(byte[] png)
        {
            var id = _nextId++;
            var hash = System.Convert.ToHexString(SHA256.HashData(png));
            if (_byHash.TryGetValue(hash, out var existing))
                return (existing, id);

            var part = main.AddImagePart(ImagePartType.Png);
            using (var stream = new MemoryStream(png))
                part.FeedData(stream);

            var relationshipId = main.GetIdOfPart(part);
            _byHash[hash] = relationshipId;
            return (relationshipId, id);
        }
    }
}
