using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace OpenPdfStudio.Services.Convert;

/// <summary>Reads a .docx into a <see cref="WordDocumentModel"/>, resolving styles, lists and sections.</summary>
public sealed class DocxReader
{
    private readonly MainDocumentPart _main;
    private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);
    private readonly Style? _defaultParagraphStyle;
    private readonly OpenXmlElement? _defaultParagraphProps;
    private readonly OpenXmlElement? _defaultRunProps;
    private readonly string _minorFont;
    private readonly string _majorFont;
    private readonly Numbering? _numbering;
    private readonly Dictionary<int, int[]> _listCounters = [];

    private DocxReader(MainDocumentPart main)
    {
        _main = main;
        var styles = main.StyleDefinitionsPart?.Styles;
        if (styles is not null)
        {
            foreach (var style in styles.Elements<Style>())
            {
                if (style.StyleId?.Value is { } id)
                    _styles.TryAdd(id, style);
            }

            _defaultParagraphStyle = styles.Elements<Style>().FirstOrDefault(s =>
                s.Type?.Value == StyleValues.Paragraph && s.Default?.Value == true);
            _defaultParagraphProps = styles.DocDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle;
            _defaultRunProps = styles.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle;
        }

        var fonts = main.ThemePart?.Theme?.ThemeElements?.FontScheme;
        _minorFont = fonts?.MinorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } minor ? minor : "Calibri";
        _majorFont = fonts?.MajorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } major ? major : "Calibri Light";
        _numbering = main.NumberingDefinitionsPart?.Numbering;
    }

    public static WordDocumentModel Read(string path)
    {
        if (!path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".docm", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Only .docx files can be converted. Open the file in Word or LibreOffice and save it as .docx first.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        WordprocessingDocument document;
        try
        {
            document = WordprocessingDocument.Open(stream, false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException)
        {
            throw new InvalidDataException("This file is not a valid Word document.", ex);
        }

        using (document)
        {
            var main = document.MainDocumentPart ?? throw new InvalidDataException("The Word document has no content.");
            return new DocxReader(main).ReadDocument();
        }
    }

    private WordDocumentModel ReadDocument()
    {
        var model = new WordDocumentModel();
        var body = _main.Document?.Body;
        if (body is null)
        {
            model.Sections.Add(new WordSection());
            return model;
        }

        var blocks = new List<WordBlock>();
        WordSection? previous = null;
        ReadBlocks(body.ChildElements, blocks, _main, sectionEnd =>
        {
            previous = BuildSection(sectionEnd, blocks, previous);
            model.Sections.Add(previous);
            blocks = [];
        });

        var last = BuildSection(body.Elements<SectionProperties>().LastOrDefault(), blocks, previous);
        if (blocks.Count > 0 || model.Sections.Count == 0)
            model.Sections.Add(last);
        return model;
    }

    private WordSection BuildSection(SectionProperties? properties, List<WordBlock> blocks, WordSection? previous)
    {
        var section = new WordSection();
        section.Blocks.AddRange(blocks);

        if (properties?.GetFirstChild<PageSize>() is { } size)
        {
            if (size.Width?.Value is { } width) section.PageWidth = width / 20.0;
            if (size.Height?.Value is { } height) section.PageHeight = height / 20.0;
        }

        if (properties?.GetFirstChild<PageMargin>() is { } margin)
        {
            if (margin.Top?.Value is { } top) section.MarginTop = Math.Abs(top) / 20.0;
            if (margin.Bottom?.Value is { } bottom) section.MarginBottom = Math.Abs(bottom) / 20.0;
            if (margin.Left?.Value is { } left) section.MarginLeft = left / 20.0;
            if (margin.Right?.Value is { } right) section.MarginRight = right / 20.0;
            if (margin.Header?.Value is { } header) section.HeaderDistance = header / 20.0;
            if (margin.Footer?.Value is { } footer) section.FooterDistance = footer / 20.0;
        }

        section.DifferentFirstPage = On(properties?.GetFirstChild<TitlePage>());

        section.Header = previous?.Header;
        section.FirstHeader = previous?.FirstHeader;
        section.Footer = previous?.Footer;
        section.FirstFooter = previous?.FirstFooter;

        foreach (var reference in properties?.Elements<HeaderReference>() ?? [])
        {
            if (reference.Id?.Value is not { } id || _main.GetPartById(id) is not HeaderPart part || part.Header is null)
                continue;
            var content = ReadPartBlocks(part.Header, part);
            if (reference.Type?.Value == HeaderFooterValues.First) section.FirstHeader = content;
            else if (reference.Type?.Value != HeaderFooterValues.Even) section.Header = content;
        }

        foreach (var reference in properties?.Elements<FooterReference>() ?? [])
        {
            if (reference.Id?.Value is not { } id || _main.GetPartById(id) is not FooterPart part || part.Footer is null)
                continue;
            var content = ReadPartBlocks(part.Footer, part);
            if (reference.Type?.Value == HeaderFooterValues.First) section.FirstFooter = content;
            else if (reference.Type?.Value != HeaderFooterValues.Even) section.Footer = content;
        }

        return section;
    }

    private List<WordBlock> ReadPartBlocks(OpenXmlElement root, OpenXmlPart part)
    {
        var blocks = new List<WordBlock>();
        ReadBlocks(root.ChildElements, blocks, part, null);
        return blocks;
    }

    private void ReadBlocks(IEnumerable<OpenXmlElement> elements, List<WordBlock> into, OpenXmlPart part,
        Action<SectionProperties>? onSectionEnd)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                    into.Add(ReadParagraph(paragraph, part));
                    if (onSectionEnd is not null && paragraph.ParagraphProperties?.SectionProperties is { } section)
                        onSectionEnd(section);
                    break;
                case Table table:
                    into.Add(ReadTable(table, part));
                    break;
                case SdtBlock sdt when sdt.SdtContentBlock is { } content:
                    ReadBlocks(content.ChildElements, into, part, onSectionEnd);
                    break;
                case CustomXmlBlock custom:
                    ReadBlocks(custom.ChildElements, into, part, onSectionEnd);
                    break;
            }
        }
    }

    // ---- Paragraphs --------------------------------------------------------------------------

    private WordParagraph ReadParagraph(Paragraph paragraph, OpenXmlPart part)
    {
        var direct = paragraph.ParagraphProperties;
        var styleChain = StyleChain(direct?.ParagraphStyleId?.Val?.Value, _defaultParagraphStyle);

        var props = new ParagraphProps();
        props.Apply(_defaultParagraphProps);
        foreach (var style in styleChain)
            props.Apply(style.StyleParagraphProperties);
        props.Apply(direct);

        var level = props.NumId is > 0 ? FindLevel(props.NumId.Value, props.Level ?? 0) : null;
        if (level?.PreviousParagraphProperties is { } levelProps)
        {
            // Direct formatting wins over the list level, which wins over the style.
            props = new ParagraphProps();
            props.Apply(_defaultParagraphProps);
            foreach (var style in styleChain)
                props.Apply(style.StyleParagraphProperties);
            props.Apply(levelProps);
            props.Apply(direct);
        }

        var baseRun = new RunProps();
        baseRun.Apply(_defaultRunProps);
        foreach (var style in styleChain)
            baseRun.Apply(style.StyleRunProperties);

        var markRun = baseRun.Clone();
        markRun.Apply(direct?.ParagraphMarkRunProperties);

        var result = new WordParagraph
        {
            StyleId = styleChain.LastOrDefault()?.StyleId?.Value,
            Alignment = props.Alignment,
            SpaceBefore = props.SpaceBefore,
            SpaceAfter = props.SpaceAfter,
            ContextualSpacing = props.ContextualSpacing,
            LineRule = props.LineRule,
            LineValue = props.LineValue,
            IndentLeft = props.IndentLeft,
            IndentRight = props.IndentRight,
            FirstLine = props.FirstLine,
            KeepWithNext = props.KeepNext,
            PageBreakBefore = props.PageBreakBefore,
            Shading = props.Shading,
            MarkStyle = ToStyle(markRun)
        };

        if (level is not null && props.NumId is { } numId)
        {
            var labelRun = markRun.Clone();
            labelRun.Apply(level.NumberingSymbolRunProperties);
            var label = ListLabel(numId, props.Level ?? 0, level);
            if (!string.IsNullOrEmpty(label))
            {
                result.ListLabel = label;
                // Symbol fonts have been mapped to Unicode, so draw the label in the text font.
                result.ListLabelStyle = ToStyle(labelRun) with { Font = ToStyle(markRun).Font };
            }
        }

        var context = new InlineContext(part, baseRun);
        ReadInlines(paragraph, result.Inlines, context);
        return result;
    }

    private void ReadInlines(OpenXmlElement container, List<WordInline> into, InlineContext context)
    {
        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case ParagraphProperties:
                case DeletedRun:
                case MoveFromRun:
                    break;
                case Run run:
                    ReadRun(run, into, context);
                    break;
                case SimpleField field:
                    var kind = FieldKind(field.Instruction?.Value);
                    if (kind is { } fieldKind)
                    {
                        var run = field.GetFirstChild<Run>();
                        into.Add(new WordField(fieldKind, ToStyle(RunStyle(run, context))));
                    }
                    else
                    {
                        ReadInlines(field, into, context);
                    }

                    break;
                default:
                    if (child.HasChildren)
                        ReadInlines(child, into, context);
                    break;
            }
        }
    }

    private void ReadRun(Run run, List<WordInline> into, InlineContext context)
    {
        var props = RunStyle(run, context);
        if (props.Hidden)
            return;
        var style = ToStyle(props);

        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case FieldChar fieldChar:
                    var type = fieldChar.FieldCharType?.Value;
                    if (type == FieldCharValues.Begin)
                    {
                        context.Fields.Push(new FieldState());
                    }
                    else if (type == FieldCharValues.Separate && context.Fields.TryPeek(out var open))
                    {
                        open.Separated = true;
                        open.Kind = FieldKind(open.Instruction.ToString());
                        if (open.Kind is { } kind)
                            into.Add(new WordField(kind, style));
                    }
                    else if (type == FieldCharValues.End && context.Fields.TryPop(out var closed))
                    {
                        if (!closed.Separated && FieldKind(closed.Instruction.ToString()) is { } kind)
                            into.Add(new WordField(kind, style));
                    }

                    break;
                case FieldCode code:
                    if (context.Fields.TryPeek(out var current) && !current.Separated)
                        current.Instruction.Append(code.Text);
                    break;
                default:
                    if (context.SuppressText)
                        break;
                    ReadRunContent(child, into, style, context);
                    break;
            }
        }
    }

    private void ReadRunContent(OpenXmlElement child, List<WordInline> into, WordRunStyle style, InlineContext context)
    {
        switch (child)
        {
            case RunProperties:
            case DeletedText:
            case SoftHyphen:
                return;
            case Text text:
                AddText(into, text.Text, style);
                return;
            case TabChar or PositionalTab:
                AddText(into, "\t", style);
                return;
            case Break br:
                if (br.Type?.Value == BreakValues.Page)
                    into.Add(WordPageBreak.Instance);
                else
                    AddText(into, "\n", style);
                return;
            case CarriageReturn:
                AddText(into, "\n", style);
                return;
            case NoBreakHyphen:
                AddText(into, "-", style);
                return;
            case SymbolChar symbol:
                if (symbol.Char?.Value is { } hex
                    && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                {
                    AddText(into, MapSymbol(code, symbol.Font?.Value), style);
                }

                return;
            case Drawing drawing:
                ReadDrawing(drawing, into, style, context);
                return;
            case Picture picture:
                ReadVmlPicture(picture, into, context);
                return;
        }

        if (child.Descendants<Drawing>().FirstOrDefault() is { } nested)
            ReadDrawing(nested, into, style, context);
    }

    private void ReadDrawing(Drawing drawing, List<WordInline> into, WordRunStyle style, InlineContext context)
    {
        var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        if (blip?.Embed?.Value is { } id)
        {
            var extent = drawing.Descendants<DW.Extent>().FirstOrDefault();
            var width = (extent?.Cx?.Value ?? 0) / 12700.0;
            var height = (extent?.Cy?.Value ?? 0) / 12700.0;
            if (ImageBytes(context.Part, id) is { } data && width > 0 && height > 0)
                into.Add(new WordPicture(data, width, height));
            return;
        }

        // A text box: keep its words in the flow rather than dropping them.
        var lines = drawing.Descendants<Paragraph>().Select(p => p.InnerText).Where(t => t.Length > 0).ToList();
        if (lines.Count > 0)
            AddText(into, string.Join("\n", lines) + "\n", style);
    }

    private static void ReadVmlPicture(Picture picture, List<WordInline> into, InlineContext context)
    {
        var imageData = picture.Descendants<DocumentFormat.OpenXml.Vml.ImageData>().FirstOrDefault();
        if (imageData?.RelationshipId?.Value is not { } id || ImageBytes(context.Part, id) is not { } data)
            return;

        var shapeStyle = picture.Descendants<DocumentFormat.OpenXml.Vml.Shape>().FirstOrDefault()?.Style?.Value ?? "";
        var width = CssLength(shapeStyle, "width");
        var height = CssLength(shapeStyle, "height");
        if (width > 0 && height > 0)
            into.Add(new WordPicture(data, width, height));
    }

    private static byte[]? ImageBytes(OpenXmlPart part, string id)
    {
        try
        {
            if (part.GetPartById(id) is not ImagePart image)
                return null;
            using var source = image.GetStream(FileMode.Open, FileAccess.Read);
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            return copy.ToArray();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private static double CssLength(string css, string name)
    {
        foreach (var declaration in css.Split(';'))
        {
            var pair = declaration.Split(':', 2);
            if (pair.Length != 2 || !pair[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = pair[1].Trim();
            var unit = value.Length > 2 ? value[^2..] : "";
            var number = double.TryParse(value.TrimEnd('p', 't', 'x', 'i', 'n', 'c', 'm'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return unit switch
            {
                "in" => number * 72,
                "cm" => number * 72 / 2.54,
                "mm" => number * 72 / 25.4,
                "px" => number * 0.75,
                _ => number
            };
        }

        return 0;
    }

    private static void AddText(List<WordInline> into, string text, WordRunStyle style)
    {
        if (text.Length == 0)
            return;
        if (into.Count > 0 && into[^1] is WordText last && last.Style == style)
            into[^1] = last with { Text = last.Text + text };
        else
            into.Add(new WordText(text, style));
    }

    private RunProps RunStyle(Run? run, InlineContext context)
    {
        var props = context.ParagraphRun.Clone();
        if (run?.RunProperties is { } direct)
        {
            foreach (var style in StyleChain(direct.RunStyle?.Val?.Value, null))
                props.Apply(style.StyleRunProperties);
            props.Apply(direct);
        }

        return props;
    }

    private static WordFieldKind? FieldKind(string? instruction)
    {
        var name = instruction?.Trim().Split(' ', 2)[0].ToUpperInvariant();
        return name switch
        {
            "PAGE" => WordFieldKind.PageNumber,
            "NUMPAGES" or "SECTIONPAGES" => WordFieldKind.PageCount,
            _ => null
        };
    }

    private WordRunStyle ToStyle(RunProps props)
    {
        var font = props.Font ?? props.ThemeFont switch
        {
            "major" => _majorFont,
            _ => _minorFont
        };
        var size = props.HalfPoints is { } half ? half / 2.0 : 10;
        return new WordRunStyle(font, size, props.Bold, props.Italic, props.Underline, props.Strike,
            props.Color, props.Highlight, props.Position, props.Caps);
    }

    // ---- Styles ------------------------------------------------------------------------------

    /// <summary>The style and the styles it is based on, base first.</summary>
    private List<Style> StyleChain(string? styleId, Style? fallback)
    {
        var chain = new List<Style>();
        var style = styleId is not null && _styles.TryGetValue(styleId, out var found) ? found : fallback;
        while (style is not null && chain.Count < 16 && !chain.Contains(style))
        {
            chain.Add(style);
            var basedOn = style.BasedOn?.Val?.Value;
            style = basedOn is not null && _styles.TryGetValue(basedOn, out var parent) ? parent : null;
        }

        chain.Reverse();
        return chain;
    }

    private static bool On(OnOffType? element) => element is not null && (element.Val is null || element.Val.Value);

    private static double Twips(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var twips) ? twips / 20.0 : 0;

    private sealed class ParagraphProps
    {
        public WordAlignment Alignment;
        public double SpaceBefore;
        public double SpaceAfter;
        public bool ContextualSpacing;
        public WordLineRule LineRule = WordLineRule.Multiple;
        public double LineValue = 1;
        public double IndentLeft;
        public double IndentRight;
        public double FirstLine;
        public bool KeepNext;
        public bool PageBreakBefore;
        public string? Shading;
        public int? NumId;
        public int? Level;

        public void Apply(OpenXmlElement? props)
        {
            if (props is null)
                return;

            if (props.GetFirstChild<Justification>()?.Val?.Value is { } jc)
            {
                if (jc == JustificationValues.Center) Alignment = WordAlignment.Center;
                else if (jc == JustificationValues.Right || jc == JustificationValues.End) Alignment = WordAlignment.Right;
                else if (jc == JustificationValues.Both || jc == JustificationValues.Distribute) Alignment = WordAlignment.Justify;
                else Alignment = WordAlignment.Left;
            }

            if (props.GetFirstChild<SpacingBetweenLines>() is { } spacing)
            {
                if (spacing.Before?.Value is { } before) SpaceBefore = Twips(before);
                if (spacing.After?.Value is { } after) SpaceAfter = Twips(after);
                if (spacing.BeforeAutoSpacing?.Value == true) SpaceBefore = 14;
                if (spacing.AfterAutoSpacing?.Value == true) SpaceAfter = 14;
                if (spacing.Line?.Value is { } line
                    && double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                {
                    var rule = spacing.LineRule?.Value;
                    if (rule == LineSpacingRuleValues.Exact)
                    {
                        LineRule = WordLineRule.Exact;
                        LineValue = amount / 20;
                    }
                    else if (rule == LineSpacingRuleValues.AtLeast)
                    {
                        LineRule = WordLineRule.AtLeast;
                        LineValue = amount / 20;
                    }
                    else
                    {
                        LineRule = WordLineRule.Multiple;
                        LineValue = amount / 240;
                    }
                }
            }

            if (props.GetFirstChild<ContextualSpacing>() is { } contextual)
                ContextualSpacing = On(contextual);

            if (props.GetFirstChild<Indentation>() is { } indent)
            {
                if ((indent.Left?.Value ?? indent.Start?.Value) is { } left) IndentLeft = Twips(left);
                if ((indent.Right?.Value ?? indent.End?.Value) is { } right) IndentRight = Twips(right);
                if (indent.FirstLine?.Value is { } first) FirstLine = Twips(first);
                if (indent.Hanging?.Value is { } hanging) FirstLine = -Twips(hanging);
            }

            if (props.GetFirstChild<KeepNext>() is { } keepNext) KeepNext = On(keepNext);
            if (props.GetFirstChild<PageBreakBefore>() is { } pageBreak) PageBreakBefore = On(pageBreak);

            if (props.GetFirstChild<Shading>()?.Fill?.Value is { } fill)
                Shading = Hex(fill);

            if (props.GetFirstChild<NumberingProperties>() is { } numbering)
            {
                if (numbering.NumberingId?.Val?.Value is { } numId) NumId = numId;
                if (numbering.NumberingLevelReference?.Val?.Value is { } level) Level = level;
            }
        }
    }

    private sealed class RunProps
    {
        public string? Font;
        public string? ThemeFont;
        public double? HalfPoints;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strike;
        public bool Caps;
        public bool Hidden;
        public string? Color;
        public string? Highlight;
        public WordVerticalPosition Position;

        public RunProps Clone() => (RunProps)MemberwiseClone();

        public void Apply(OpenXmlElement? props)
        {
            if (props is null)
                return;

            if (props.GetFirstChild<RunFonts>() is { } fonts)
            {
                var theme = fonts.AsciiTheme?.Value ?? fonts.HighAnsiTheme?.Value;
                if (theme is { } value)
                {
                    Font = null;
                    ThemeFont = value == ThemeFontValues.MajorAscii || value == ThemeFontValues.MajorHighAnsi
                        || value == ThemeFontValues.MajorBidi || value == ThemeFontValues.MajorEastAsia
                        ? "major"
                        : "minor";
                }
                else if ((fonts.Ascii?.Value ?? fonts.HighAnsi?.Value) is { Length: > 0 } name)
                {
                    Font = name;
                }
            }

            if (props.GetFirstChild<FontSize>()?.Val?.Value is { } size
                && double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out var half))
            {
                HalfPoints = half;
            }

            if (props.GetFirstChild<Bold>() is { } bold) Bold = On(bold);
            if (props.GetFirstChild<Italic>() is { } italic) Italic = On(italic);
            if (props.GetFirstChild<Strike>() is { } strike) Strike = On(strike);
            if (props.GetFirstChild<DoubleStrike>() is { } doubleStrike) Strike = Strike || On(doubleStrike);
            if (props.GetFirstChild<Caps>() is { } caps) Caps = On(caps);
            if (props.GetFirstChild<Vanish>() is { } vanish) Hidden = On(vanish);
            if (props.GetFirstChild<Underline>() is { } underline)
                Underline = underline.Val?.Value is not { } kind || kind != UnderlineValues.None;
            if (props.GetFirstChild<Color>()?.Val?.Value is { } color)
                Color = Hex(color);
            if (props.GetFirstChild<Highlight>()?.Val?.Value is { } highlight)
                Highlight = HighlightHex(highlight);
            else if (props.GetFirstChild<Shading>()?.Fill?.Value is { } fill && Hex(fill) is { } shade)
                Highlight = shade;
            if (props.GetFirstChild<VerticalTextAlignment>()?.Val?.Value is { } position)
            {
                Position = position == VerticalPositionValues.Superscript ? WordVerticalPosition.Superscript
                    : position == VerticalPositionValues.Subscript ? WordVerticalPosition.Subscript
                    : WordVerticalPosition.Baseline;
            }
        }
    }

    private static string? Hex(string value) =>
        value.Length == 6 && !value.Equals("auto", StringComparison.OrdinalIgnoreCase)
        && value.All(Uri.IsHexDigit)
            ? value.ToUpperInvariant()
            : null;

    private static string? HighlightHex(HighlightColorValues value)
    {
        if (value == HighlightColorValues.Yellow) return "FFFF00";
        if (value == HighlightColorValues.Green) return "00FF00";
        if (value == HighlightColorValues.Cyan) return "00FFFF";
        if (value == HighlightColorValues.Magenta) return "FF00FF";
        if (value == HighlightColorValues.Blue) return "0000FF";
        if (value == HighlightColorValues.Red) return "FF0000";
        if (value == HighlightColorValues.DarkBlue) return "000080";
        if (value == HighlightColorValues.DarkCyan) return "008080";
        if (value == HighlightColorValues.DarkGreen) return "008000";
        if (value == HighlightColorValues.DarkMagenta) return "800080";
        if (value == HighlightColorValues.DarkRed) return "800000";
        if (value == HighlightColorValues.DarkYellow) return "808000";
        if (value == HighlightColorValues.DarkGray) return "808080";
        if (value == HighlightColorValues.LightGray) return "C0C0C0";
        if (value == HighlightColorValues.Black) return "000000";
        if (value == HighlightColorValues.White) return "FFFFFF";
        return null;
    }

    // ---- Lists -------------------------------------------------------------------------------

    private Level? FindLevel(int numId, int level)
    {
        var instance = _numbering?.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
        var abstractId = instance?.AbstractNumId?.Val?.Value;
        if (abstractId is null)
            return null;

        var overridden = instance!.Elements<LevelOverride>()
            .FirstOrDefault(o => o.LevelIndex?.Value == level)?.Level;
        if (overridden is not null)
            return overridden;

        var definition = _numbering!.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
        return definition?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
    }

    private string ListLabel(int numId, int level, Level definition)
    {
        level = Math.Clamp(level, 0, 8);
        if (!_listCounters.TryGetValue(numId, out var counters))
        {
            counters = new int[9];
            _listCounters[numId] = counters;
        }

        var start = definition.StartNumberingValue?.Val?.Value ?? 1;
        counters[level] = counters[level] == 0 ? start : counters[level] + 1;
        for (var deeper = level + 1; deeper < counters.Length; deeper++)
            counters[deeper] = 0;

        var format = definition.NumberingFormat?.Val?.Value;
        var text = definition.LevelText?.Val?.Value ?? "";

        if (format == NumberFormatValues.None)
            return "";
        if (format == NumberFormatValues.Bullet)
            return new string(text.Select(c => MapSymbol(c, null)[0]).ToArray()) is { Length: > 0 } bullet ? bullet : "•";

        var label = new StringBuilder(text);
        for (var i = 0; i <= level; i++)
        {
            var placeholder = "%" + (i + 1).ToString(CultureInfo.InvariantCulture);
            if (!text.Contains(placeholder, StringComparison.Ordinal))
                continue;
            var levelFormat = i == level ? format : FindLevel(numId, i)?.NumberingFormat?.Val?.Value;
            var value = counters[i] == 0 ? FindLevel(numId, i)?.StartNumberingValue?.Val?.Value ?? 1 : counters[i];
            label.Replace(placeholder, FormatNumber(value, levelFormat));
        }

        return label.ToString();
    }

    private static string FormatNumber(int value, NumberFormatValues? format)
    {
        if (format == NumberFormatValues.LowerLetter) return Letters(value).ToLowerInvariant();
        if (format == NumberFormatValues.UpperLetter) return Letters(value);
        if (format == NumberFormatValues.LowerRoman) return Roman(value).ToLowerInvariant();
        if (format == NumberFormatValues.UpperRoman) return Roman(value);
        if (format == NumberFormatValues.DecimalZero) return value.ToString("00", CultureInfo.InvariantCulture);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Letters(int value)
    {
        if (value < 1) return value.ToString(CultureInfo.InvariantCulture);
        var letter = (char)('A' + (value - 1) % 26);
        return new string(letter, (value - 1) / 26 + 1);
    }

    private static string Roman(int value)
    {
        if (value is < 1 or > 3999) return value.ToString(CultureInfo.InvariantCulture);
        ReadOnlySpan<int> numbers = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
        string[] symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
        var roman = new StringBuilder();
        for (var i = 0; i < numbers.Length; i++)
        {
            while (value >= numbers[i])
            {
                roman.Append(symbols[i]);
                value -= numbers[i];
            }
        }

        return roman.ToString();
    }

    /// <summary>Maps Symbol and Wingdings code points, common in list bullets, to Unicode.</summary>
    private static string MapSymbol(int code, string? font)
    {
        var low = code >= 0xF000 ? code - 0xF000 : code;
        var symbolFont = code >= 0xF000 || font is not null
            && (font.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
                || font.Contains("Wingdings", StringComparison.OrdinalIgnoreCase));
        if (!symbolFont)
        {
            return code switch
            {
                'o' when font?.Contains("Courier", StringComparison.OrdinalIgnoreCase) == true => "◦",
                _ => char.ConvertFromUtf32(code)
            };
        }

        return low switch
        {
            0xB7 => "•",
            0xA7 => "▪",
            0xA8 => "◆",
            0xD8 => "➢",
            0x76 => "❖",
            0xFC => "✓",
            0xFB => "✗",
            0x6E => "■",
            0x71 => "❑",
            0x6C => "●",
            0xE0 => "→",
            _ => "•"
        };
    }

    // ---- Tables ------------------------------------------------------------------------------

    private WordTable ReadTable(Table table, OpenXmlPart part)
    {
        var result = new WordTable();
        var properties = table.GetFirstChild<TableProperties>();

        foreach (var column in table.GetFirstChild<TableGrid>()?.Elements<GridColumn>() ?? [])
            result.ColumnWidths.Add(Twips(column.Width?.Value));

        result.Borders = HasBorders(properties?.TableBorders)
                         ?? StyleChain(properties?.TableStyle?.Val?.Value, null)
                             .Select(s => HasBorders(s.StyleTableProperties?.TableBorders))
                             .LastOrDefault(b => b is not null)
                         ?? false;

        if (properties?.TableJustification?.Val?.Value is { } jc)
        {
            result.Alignment = jc == TableRowAlignmentValues.Center ? WordAlignment.Center
                : jc == TableRowAlignmentValues.Right ? WordAlignment.Right
                : WordAlignment.Left;
        }

        if (properties?.TableIndentation?.Width?.Value is { } indent)
            result.IndentLeft = indent / 20.0;

        foreach (var row in table.Elements<TableRow>())
        {
            var resultRow = new WordTableRow();
            var rowProps = row.TableRowProperties;
            var column = rowProps?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
            if (rowProps?.GetFirstChild<TableRowHeight>()?.Val?.Value is { } height)
                resultRow.MinHeight = height / 20.0;

            foreach (var cell in row.Elements<TableCell>())
            {
                var cellProps = cell.TableCellProperties;
                var span = Math.Max(1, cellProps?.GridSpan?.Val?.Value ?? 1);
                var merge = cellProps?.VerticalMerge;
                var resultCell = new WordTableCell
                {
                    Column = column,
                    ColumnSpan = span,
                    IsMergedAway = merge is not null && merge.Val?.Value != MergedCellValues.Restart,
                    StartsVerticalMerge = merge?.Val?.Value == MergedCellValues.Restart,
                    Shading = cellProps?.Shading?.Fill?.Value is { } fill ? Hex(fill) : null
                };

                var valign = cellProps?.TableCellVerticalAlignment?.Val?.Value;
                resultCell.VerticalAlignment = valign == TableVerticalAlignmentValues.Center ? WordCellAlignment.Center
                    : valign == TableVerticalAlignmentValues.Bottom ? WordCellAlignment.Bottom
                    : WordCellAlignment.Top;

                ReadBlocks(cell.ChildElements, resultCell.Blocks, part, null);
                resultRow.Cells.Add(resultCell);
                column += span;
            }

            result.Rows.Add(resultRow);
        }

        var columnCount = Math.Max(result.ColumnWidths.Count,
            result.Rows.Select(r => r.Cells.Select(c => c.Column + c.ColumnSpan).DefaultIfEmpty(0).Max())
                .DefaultIfEmpty(0).Max());
        while (result.ColumnWidths.Count < columnCount)
            result.ColumnWidths.Add(0);
        var known = result.ColumnWidths.Where(w => w > 0).DefaultIfEmpty(72).Average();
        for (var i = 0; i < result.ColumnWidths.Count; i++)
        {
            if (result.ColumnWidths[i] <= 0)
                result.ColumnWidths[i] = known;
        }

        for (var r = 0; r < result.Rows.Count; r++)
        {
            foreach (var cell in result.Rows[r].Cells.Where(c => c.StartsVerticalMerge))
            {
                var below = r + 1;
                while (below < result.Rows.Count
                       && result.Rows[below].Cells.Any(c => c.IsMergedAway && c.Column == cell.Column))
                {
                    below++;
                }

                cell.RowSpan = below - r;
            }
        }

        return result;
    }

    private static bool? HasBorders(TableBorders? borders)
    {
        if (borders is null)
            return null;
        return borders.ChildElements.OfType<BorderType>().Any(b =>
            b.Val?.Value is { } kind && kind != BorderValues.None && kind != BorderValues.Nil);
    }

    private sealed class FieldState
    {
        public StringBuilder Instruction { get; } = new();
        public bool Separated { get; set; }
        public WordFieldKind? Kind { get; set; }
    }

    private sealed class InlineContext(OpenXmlPart part, RunProps paragraphRun)
    {
        public OpenXmlPart Part { get; } = part;
        public RunProps ParagraphRun { get; } = paragraphRun;
        public Stack<FieldState> Fields { get; } = new();

        /// <summary>
        /// True inside a field's instruction, or inside the cached result of a page-number field,
        /// which is replaced by the live value.
        /// </summary>
        public bool SuppressText => Fields.Any(f => !f.Separated || f.Kind is not null);
    }
}
