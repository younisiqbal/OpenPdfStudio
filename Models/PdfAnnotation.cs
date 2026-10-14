using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace OpenPdfStudio.Models;

public enum AnnotationKind
{
    Note,
    Typewriter,
    Textbox,
    Callout,
    Highlight,
    Underline,
    StrikeOut,
    Squiggly
}

public enum AnnotationFont
{
    Helvetica,
    Times,
    Courier
}

public enum AnnotationTextAlign
{
    Left,
    Center,
    Right
}

/// <summary>Sticky-note icons, in the order the Format panel shows them. Names match the PDF /Name values.</summary>
public enum NoteIcon
{
    Comment,
    Cross,
    Help,
    Insert,
    Check,
    RightArrow,
    Key,
    Star,
    Circle,
    CrossHairs,
    RightPointer,
    UpArrow,
    Note,
    Paragraph,
    NewParagraph,
    UpLeftArrow
}

/// <summary>
/// How an annotation looks. One style covers the whole annotation. <see cref="Color"/> is the note or markup
/// colour; text annotations use <see cref="TextColor"/>, <see cref="BorderColor"/> and <see cref="FillColor"/>.
/// </summary>
public sealed record AnnotationStyle
{
    public Color Color { get; init; } = Colors.Yellow;
    public double Opacity { get; init; } = 1;
    public AnnotationFont Font { get; init; } = AnnotationFont.Helvetica;
    public double FontSize { get; init; } = 12;
    public Color TextColor { get; init; } = Colors.Black;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Underline { get; init; }
    public bool Strikethrough { get; init; }
    public AnnotationTextAlign Align { get; init; } = AnnotationTextAlign.Left;
    public double LineSpacing { get; init; } = 1;
    public Color? BorderColor { get; init; }
    public double BorderWidth { get; init; } = 1;
    public Color? FillColor { get; init; }
    public NoteIcon Icon { get; init; } = NoteIcon.Comment;

    public static AnnotationStyle DefaultFor(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Note => new AnnotationStyle { Color = Color.FromRgb(255, 221, 0) },
        AnnotationKind.Typewriter => new AnnotationStyle { TextColor = Color.FromRgb(0, 0, 255) },
        AnnotationKind.Textbox => new AnnotationStyle { BorderColor = Color.FromRgb(230, 0, 0), FillColor = Colors.White, TextColor = Colors.Black },
        AnnotationKind.Callout => new AnnotationStyle { BorderColor = Color.FromRgb(230, 0, 0), FillColor = Colors.White, TextColor = Colors.Black },
        AnnotationKind.Highlight => new AnnotationStyle { Color = Color.FromRgb(255, 235, 0) },
        AnnotationKind.Underline => new AnnotationStyle { Color = Color.FromRgb(0, 120, 215) },
        AnnotationKind.StrikeOut => new AnnotationStyle { Color = Color.FromRgb(230, 0, 0) },
        AnnotationKind.Squiggly => new AnnotationStyle { Color = Color.FromRgb(0, 160, 60) },
        _ => new AnnotationStyle()
    };
}

/// <summary>
/// One annotation on a page. Geometry is in PDF points with a top-left origin, like text rects.
/// For Callout, <see cref="Bounds"/> is the text box and <see cref="CalloutTarget"/> is where the arrow points.
/// For markup, <see cref="Quads"/> are the marked line boxes and <see cref="Bounds"/> is their union.
/// </summary>
public sealed record PdfAnnotation
{
    public required string Id { get; init; }
    public required int PageIndex { get; init; }
    public required AnnotationKind Kind { get; init; }
    public required Rect Bounds { get; init; }
    public IReadOnlyList<Rect> Quads { get; init; } = [];
    public Point? CalloutTarget { get; init; }
    public string Contents { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public DateTimeOffset Modified { get; init; } = DateTimeOffset.Now;
    public required AnnotationStyle Style { get; init; }

    public bool IsFreeText => Kind is AnnotationKind.Typewriter or AnnotationKind.Textbox or AnnotationKind.Callout;
    public bool IsMarkup => Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut or AnnotationKind.Squiggly;

    /// <summary>The area the annotation covers on the page, callout arrow included.</summary>
    public Rect Extent
    {
        get
        {
            if (CalloutTarget is not { } target)
                return Bounds;

            // Rect.Union skips zero-size rects, so the target point is merged by hand.
            var left = Math.Min(Bounds.Left, target.X);
            var top = Math.Min(Bounds.Top, target.Y);
            return new Rect(left, top, Math.Max(Bounds.Right, target.X) - left, Math.Max(Bounds.Bottom, target.Y) - top);
        }
    }

    public static string NewId() => "ops-" + Guid.NewGuid().ToString("N");
}
