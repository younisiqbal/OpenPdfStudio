using System.Collections.Generic;

namespace OpenPdfStudio.Services.Convert;

/// <summary>A .docx read into the parts the PDF renderer can lay out. All lengths are in points.</summary>
public sealed class WordDocumentModel
{
    public List<WordSection> Sections { get; } = [];
}

public sealed class WordSection
{
    public double PageWidth { get; set; } = 612;
    public double PageHeight { get; set; } = 792;
    public double MarginTop { get; set; } = 72;
    public double MarginBottom { get; set; } = 72;
    public double MarginLeft { get; set; } = 72;
    public double MarginRight { get; set; } = 72;
    public double HeaderDistance { get; set; } = 36;
    public double FooterDistance { get; set; } = 36;
    public bool DifferentFirstPage { get; set; }

    public List<WordBlock> Blocks { get; } = [];
    public List<WordBlock>? Header { get; set; }
    public List<WordBlock>? Footer { get; set; }
    public List<WordBlock>? FirstHeader { get; set; }
    public List<WordBlock>? FirstFooter { get; set; }

    public double ContentWidth => PageWidth - MarginLeft - MarginRight;
}

public abstract class WordBlock;

public enum WordAlignment
{
    Left,
    Center,
    Right,
    Justify
}

public enum WordLineRule
{
    Multiple,
    Exact,
    AtLeast
}

public sealed class WordParagraph : WordBlock
{
    public string? StyleId { get; set; }
    public WordAlignment Alignment { get; set; }
    public double SpaceBefore { get; set; }
    public double SpaceAfter { get; set; }
    public bool ContextualSpacing { get; set; }
    public WordLineRule LineRule { get; set; } = WordLineRule.Multiple;

    /// <summary>A multiple of single spacing for <see cref="WordLineRule.Multiple"/>, otherwise points.</summary>
    public double LineValue { get; set; } = 1;

    public double IndentLeft { get; set; }
    public double IndentRight { get; set; }

    /// <summary>Positive for a first-line indent, negative for a hanging indent.</summary>
    public double FirstLine { get; set; }

    public bool KeepWithNext { get; set; }
    public bool PageBreakBefore { get; set; }
    public string? Shading { get; set; }
    public string? ListLabel { get; set; }
    public WordRunStyle? ListLabelStyle { get; set; }

    /// <summary>The paragraph's own text style, used to size an empty paragraph.</summary>
    public required WordRunStyle MarkStyle { get; init; }

    public List<WordInline> Inlines { get; } = [];
}

public abstract record WordInline;

/// <summary>Text, where '\n' is a line break and '\t' a tab.</summary>
public sealed record WordText(string Text, WordRunStyle Style) : WordInline;

public sealed record WordPicture(byte[] Data, double Width, double Height) : WordInline;

public sealed record WordPageBreak : WordInline
{
    public static readonly WordPageBreak Instance = new();
}

public enum WordFieldKind
{
    PageNumber,
    PageCount
}

public sealed record WordField(WordFieldKind Kind, WordRunStyle Style) : WordInline;

public enum WordVerticalPosition
{
    Baseline,
    Superscript,
    Subscript
}

/// <summary>Colours are "RRGGBB" hex, or null for the default.</summary>
public sealed record WordRunStyle(
    string Font,
    double Size,
    bool Bold,
    bool Italic,
    bool Underline,
    bool Strike,
    string? Color,
    string? Highlight,
    WordVerticalPosition Position,
    bool Caps);

public sealed class WordTable : WordBlock
{
    public List<double> ColumnWidths { get; } = [];
    public List<WordTableRow> Rows { get; } = [];
    public bool Borders { get; set; }
    public double IndentLeft { get; set; }
    public WordAlignment Alignment { get; set; }
}

public sealed class WordTableRow
{
    public List<WordTableCell> Cells { get; } = [];
    public double MinHeight { get; set; }
}

public enum WordCellAlignment
{
    Top,
    Center,
    Bottom
}

public sealed class WordTableCell
{
    public int Column { get; set; }
    public int ColumnSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;

    /// <summary>True for the lower parts of a vertically merged cell, which draw nothing.</summary>
    public bool IsMergedAway { get; set; }

    public bool StartsVerticalMerge { get; set; }
    public string? Shading { get; set; }
    public WordCellAlignment VerticalAlignment { get; set; }
    public List<WordBlock> Blocks { get; } = [];
}
