using System.Collections.Generic;

namespace OpenPdfStudio.Services.Convert;

/// <summary>
/// One PDF page rebuilt as flowing content for a word processor. Positions are in points with a top-left origin.
/// </summary>
public sealed record PdfFlowPage(
    double Width,
    double Height,
    PdfFlowMargins Margins,
    IReadOnlyList<PdfFlowBlock> Blocks)
{
    /// <summary>Pictures placed at fixed positions on the page, outside the text flow.</summary>
    public IReadOnlyList<PdfFlowPicture> Pictures { get; init; } = [];
}

public readonly record struct PdfFlowMargins(double Top, double Bottom, double Left, double Right);

public enum PdfFlowAlignment
{
    Left,
    Center,
    Right,
    Justify
}

public abstract record PdfFlowBlock(double Top, double Bottom)
{
    /// <summary>Space between this block and the one above it, beyond normal line spacing.</summary>
    public double SpaceBefore { get; init; }
}

public sealed record PdfFlowParagraph(
    double Top,
    double Bottom,
    double IndentLeft,
    double FirstLineIndent,
    double LineSpacing,
    PdfFlowAlignment Alignment,
    IReadOnlyList<PdfFlowRun> Runs) : PdfFlowBlock(Top, Bottom)
{
    /// <summary>Left-aligned tab stops, in points from the left margin, for the tabs in <see cref="Runs"/>.</summary>
    public IReadOnlyList<double> TabStops { get; init; } = [];
}

/// <summary>An image as PNG bytes, drawn at <paramref name="Width"/> by <paramref name="Height"/> points.</summary>
public sealed record PdfFlowImage(
    double Top,
    double Bottom,
    double IndentLeft,
    double Width,
    double Height,
    PdfFlowAlignment Alignment,
    byte[] Png) : PdfFlowBlock(Top, Bottom);

public sealed record PdfFlowRun(string Text, PdfFlowRunStyle Style);

public enum PdfFlowFontKind
{
    Sans,
    Serif,
    Mono
}

/// <summary><paramref name="Color"/> is 0xRRGGBB.</summary>
public sealed record PdfFlowRunStyle(string FontName, double FontSize, bool Bold, bool Italic, int Color)
{
    /// <summary>Lets a word processor pick a similar font when this one is not installed.</summary>
    public PdfFlowFontKind Kind { get; init; }
}
