using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace OpenPdfStudio.Services.Convert;

/// <summary>Lays a <see cref="WordDocumentModel"/> out as a PDF with QuestPDF.</summary>
public sealed class WordPdfRenderer : IDisposable
{
    private const string FallbackFont = "Lato";
    private const float CellPadding = 5.4f;

    private readonly Dictionary<byte[], Image?> _images = new(ReferenceEqualityComparer.Instance);

    public static void Convert(string docxPath, string pdfPath)
    {
        var model = DocxReader.Read(docxPath);
        using var renderer = new WordPdfRenderer();
        renderer.Write(model, pdfPath);
    }

    public void Write(WordDocumentModel model, string pdfPath)
    {
        var document = Document.Create(container =>
        {
            foreach (var section in model.Sections)
                container.Page(page => ComposePage(page, section));
        });

        var directory = Path.GetDirectoryName(Path.GetFullPath(pdfPath)) ?? ".";
        var temp = Path.Combine(directory, $".{Path.GetFileName(pdfPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            document.GeneratePdf(temp);
            File.Move(temp, pdfPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public void Dispose()
    {
        foreach (var image in _images.Values)
            image?.Dispose();
        _images.Clear();
    }

    private void ComposePage(PageDescriptor page, WordSection section)
    {
        var width = Math.Max(72, section.ContentWidth);
        var headerSpace = Math.Max(0, section.MarginTop - section.HeaderDistance);
        var footerSpace = Math.Max(0, section.MarginBottom - section.FooterDistance);
        var bodyHeight = Math.Max(72, section.PageHeight - section.MarginTop - section.MarginBottom);

        page.Size((float)section.PageWidth, (float)section.PageHeight, Unit.Point);
        page.MarginTop((float)Math.Min(section.HeaderDistance, section.MarginTop));
        page.MarginBottom((float)Math.Min(section.FooterDistance, section.MarginBottom));
        page.MarginLeft((float)section.MarginLeft);
        page.MarginRight((float)section.MarginRight);
        page.DefaultTextStyle(style => style.FontFamily(FallbackFont).FontSize(11));

        page.Header().MinHeight((float)headerSpace).Column(column =>
            ComposeRepeating(column, section.Header, section.DifferentFirstPage ? section.FirstHeader ?? [] : null,
                width, bodyHeight));

        page.Footer().MinHeight((float)footerSpace).AlignBottom().Column(column =>
            ComposeRepeating(column, section.Footer, section.DifferentFirstPage ? section.FirstFooter ?? [] : null,
                width, bodyHeight));

        page.Content().Column(column => ComposeBlocks(column, section.Blocks, width, bodyHeight));
    }

    private void ComposeRepeating(ColumnDescriptor column, List<WordBlock>? blocks, List<WordBlock>? first,
        double width, double maxHeight)
    {
        if (first is null)
        {
            if (blocks is not null)
                ComposeBlocks(column, blocks, width, maxHeight);
            return;
        }

        column.Item().ShowOnce().Column(inner => ComposeBlocks(inner, first, width, maxHeight));
        if (blocks is not null)
            column.Item().SkipOnce().Column(inner => ComposeBlocks(inner, blocks, width, maxHeight));
    }

    private void ComposeBlocks(ColumnDescriptor column, IReadOnlyList<WordBlock> blocks, double width, double maxHeight)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            switch (blocks[i])
            {
                case WordParagraph paragraph:
                    var previous = i > 0 ? blocks[i - 1] as WordParagraph : null;
                    var next = i + 1 < blocks.Count ? blocks[i + 1] as WordParagraph : null;
                    var before = SharesContextualSpacing(previous, paragraph) ? 0 : paragraph.SpaceBefore;
                    var after = SharesContextualSpacing(paragraph, next) ? 0 : paragraph.SpaceAfter;
                    ComposeParagraph(column, paragraph, before, after, width, maxHeight);
                    break;
                case WordTable table:
                    column.Item().Element(container => ComposeTable(container, table, width, maxHeight));
                    break;
            }
        }
    }

    private static bool SharesContextualSpacing(WordParagraph? first, WordParagraph? second) =>
        first is not null && second is not null && first.StyleId == second.StyleId
        && (first.ContextualSpacing || second.ContextualSpacing);

    // ---- Paragraphs --------------------------------------------------------------------------

    private void ComposeParagraph(ColumnDescriptor column, WordParagraph paragraph, double before, double after,
        double width, double maxHeight)
    {
        var segments = SplitOnPageBreaks(paragraph.Inlines);
        if (paragraph.PageBreakBefore)
            column.Item().PageBreak();

        for (var index = 0; index < segments.Count; index++)
        {
            if (index > 0)
                column.Item().PageBreak();

            var first = index == 0;
            var last = index == segments.Count - 1;
            if (!first && segments[index].Count == 0 && last)
                break;

            IContainer item = column.Item();
            if (paragraph.KeepWithNext && first)
                item = item.EnsureSpace(60);

            var left = Math.Max(0, paragraph.IndentLeft);
            var right = Math.Max(0, paragraph.IndentRight);
            item = item
                .PaddingTop(first ? (float)Math.Max(0, before) : 0)
                .PaddingBottom(last ? (float)Math.Max(0, after) : 0);
            if (paragraph.Shading is { } shading)
                item = item.Background(Color.FromHex("#" + shading));

            var hanging = paragraph.FirstLine < 0 ? -paragraph.FirstLine : 0;
            if (paragraph.ListLabel is { } label && first)
            {
                var labelWidth = Math.Max(hanging, 18);
                var labelStart = Math.Max(0, left - labelWidth);
                var textWidth = Math.Max(36, width - labelStart - labelWidth - right);
                item.PaddingLeft((float)labelStart).PaddingRight((float)right).Row(row =>
                {
                    row.ConstantItem((float)labelWidth).Text(text =>
                    {
                        var span = text.Span(label);
                        ApplyStyle(span, paragraph.ListLabelStyle ?? paragraph.MarkStyle, paragraph);
                    });
                    row.RelativeItem().Element(container =>
                        ComposeText(container, paragraph, segments[index], textWidth, maxHeight, firstLine: false));
                });
            }
            else
            {
                var textWidth = Math.Max(36, width - left - right);
                ComposeText(item.PaddingLeft((float)left).PaddingRight((float)right), paragraph, segments[index],
                    textWidth, maxHeight, firstLine: first);
            }
        }
    }

    private static List<List<WordInline>> SplitOnPageBreaks(List<WordInline> inlines)
    {
        var segments = new List<List<WordInline>> { new() };
        foreach (var inline in inlines)
        {
            if (inline is WordPageBreak)
                segments.Add([]);
            else
                segments[^1].Add(inline);
        }

        return segments;
    }

    private void ComposeText(IContainer container, WordParagraph paragraph, List<WordInline> inlines, double width,
        double maxHeight, bool firstLine)
    {
        container.Text(text =>
        {
            switch (paragraph.Alignment)
            {
                case WordAlignment.Center:
                    text.AlignCenter();
                    break;
                case WordAlignment.Right:
                    text.AlignRight();
                    break;
                case WordAlignment.Justify:
                    text.Justify();
                    break;
                default:
                    text.AlignLeft();
                    break;
            }

            if (firstLine && paragraph.FirstLine > 0)
                text.ParagraphFirstLineIndentation((float)paragraph.FirstLine);

            var drewSomething = false;
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case WordText run:
                        var value = run.Text.Replace("\t", "    ");
                        if (run.Style.Caps)
                            value = value.ToUpperInvariant();
                        ApplyStyle(text.Span(value), run.Style, paragraph);
                        drewSomething = true;
                        break;
                    case WordField field:
                        var number = field.Kind == WordFieldKind.PageNumber ? text.CurrentPageNumber() : text.TotalPages();
                        ApplyStyle(number, field.Style, paragraph);
                        drewSomething = true;
                        break;
                    case WordPicture picture when LoadImage(picture.Data) is { } image:
                        var scale = Math.Min(1, Math.Min(width / picture.Width, maxHeight * 0.95 / picture.Height));
                        text.Element(TextInjectedElementAlignment.AboveBaseline)
                            .Width((float)(picture.Width * scale))
                            .Height((float)(picture.Height * scale))
                            .Image(image)
                            .FitArea();
                        drewSomething = true;
                        break;
                }
            }

            // An empty paragraph still takes up one line in Word.
            if (!drewSomething)
                ApplyStyle(text.Span(" "), paragraph.MarkStyle, paragraph);
        });
    }

    private static void ApplyStyle(TextSpanDescriptor span, WordRunStyle style, WordParagraph paragraph)
    {
        var size = (float)Math.Clamp(style.Size, 1, 400);
        span.FontFamily(FontFamilies(style.Font)).FontSize(size);
        if (style.Bold) span.Bold();
        if (style.Italic) span.Italic();
        if (style.Underline) span.Underline();
        if (style.Strike) span.Strikethrough();
        if (style.Color is { } color) span.FontColor(Color.FromHex("#" + color));
        if (style.Highlight is { } highlight) span.BackgroundColor(Color.FromHex("#" + highlight));
        if (style.Position == WordVerticalPosition.Superscript) span.Superscript();
        else if (style.Position == WordVerticalPosition.Subscript) span.Subscript();

        // QuestPDF line height is a multiple of the font size; Word's single spacing is about 1.2 of it.
        var lineHeight = paragraph.LineRule switch
        {
            WordLineRule.Exact => paragraph.LineValue / size,
            WordLineRule.AtLeast => Math.Max(1.2, paragraph.LineValue / size),
            _ => Math.Abs(paragraph.LineValue - 1) < 0.01 ? (double?)null : paragraph.LineValue * 1.2
        };
        if (lineHeight is { } factor)
            span.LineHeight((float)Math.Clamp(factor, 0.5, 10));
    }

    /// <summary>
    /// The font, then metric-compatible stand-ins found on Linux and macOS, then the bundled font.
    /// QuestPDF uses the first family in the list that is installed.
    /// </summary>
    private static string[] FontFamilies(string font)
    {
        string[] substitutes = font.ToLowerInvariant() switch
        {
            "calibri" or "calibri light" => ["Carlito", "Liberation Sans", "Arial"],
            "aptos" or "aptos display" or "aptos narrow" => ["Calibri", "Carlito", "Liberation Sans", "Arial"],
            "cambria" => ["Caladea", "Liberation Serif", "Times New Roman"],
            "arial" or "helvetica" or "segoe ui" or "verdana" or "tahoma" => ["Liberation Sans", "Arimo", "Helvetica", "Arial"],
            "times new roman" or "times" or "georgia" or "garamond" or "book antiqua" =>
                ["Liberation Serif", "Tinos", "Times New Roman", "Times"],
            "courier new" or "courier" or "consolas" => ["Liberation Mono", "Cousine", "Courier New", "Menlo"],
            _ => []
        };
        return [font, .. substitutes, FallbackFont];
    }

    private Image? LoadImage(byte[] data)
    {
        if (_images.TryGetValue(data, out var cached))
            return cached;

        Image? image;
        try
        {
            image = Image.FromBinaryData(data);
        }
        catch (Exception)
        {
            // EMF, WMF and other formats Skia can't decode are left out.
            image = null;
        }

        _images[data] = image;
        return image;
    }

    // ---- Tables ------------------------------------------------------------------------------

    private void ComposeTable(IContainer container, WordTable table, double width, double maxHeight)
    {
        var columnCount = table.ColumnWidths.Count;
        if (columnCount == 0 || table.Rows.Count == 0)
            return;

        var indent = Math.Max(0, table.IndentLeft);
        var total = table.ColumnWidths.Sum();
        var available = Math.Max(36, width - indent);
        var scale = total > available ? available / total : 1;
        var widths = table.ColumnWidths.Select(w => w * scale).ToArray();

        container = container.PaddingLeft((float)indent);
        container = table.Alignment switch
        {
            WordAlignment.Center => container.AlignCenter(),
            WordAlignment.Right => container.AlignRight(),
            _ => container.AlignLeft()
        };

        container.Table(grid =>
        {
            grid.ColumnsDefinition(columns =>
            {
                foreach (var columnWidth in widths)
                    columns.ConstantColumn((float)columnWidth);
            });

            for (var r = 0; r < table.Rows.Count; r++)
            {
                var row = table.Rows[r];
                foreach (var cell in row.Cells)
                {
                    if (cell.IsMergedAway || cell.Column >= columnCount)
                        continue;

                    var span = Math.Min(cell.ColumnSpan, columnCount - cell.Column);
                    var rowSpan = Math.Max(1, Math.Min(cell.RowSpan, table.Rows.Count - r));
                    var cellWidth = widths.Skip(cell.Column).Take(span).Sum();

                    IContainer box = grid.Cell()
                        .Row((uint)(r + 1))
                        .Column((uint)(cell.Column + 1))
                        .ColumnSpan((uint)span)
                        .RowSpan((uint)rowSpan);

                    if (table.Borders)
                        box = box.Border(0.5f).BorderColor(Colors.Black);
                    if (cell.Shading is { } shading)
                        box = box.Background(Color.FromHex("#" + shading));
                    if (row.MinHeight > 0)
                        box = box.MinHeight((float)row.MinHeight);

                    box = box.PaddingHorizontal(CellPadding).PaddingVertical(1);
                    box = cell.VerticalAlignment switch
                    {
                        WordCellAlignment.Center => box.AlignMiddle(),
                        WordCellAlignment.Bottom => box.AlignBottom(),
                        _ => box
                    };

                    var innerWidth = Math.Max(12, cellWidth - 2 * CellPadding);
                    box.Column(column => ComposeBlocks(column, cell.Blocks, innerWidth, maxHeight));
                }
            }
        });
    }
}
