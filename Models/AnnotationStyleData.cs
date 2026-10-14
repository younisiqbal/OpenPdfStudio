using System;
using Avalonia.Media;

namespace OpenPdfStudio.Models;

/// <summary>Serializable form of <see cref="AnnotationStyle"/>, with colours as ARGB numbers.</summary>
public sealed class AnnotationStyleData
{
    public uint Color { get; set; } = 0xFFFFFF00;
    public double Opacity { get; set; } = 1;
    public string Font { get; set; } = nameof(AnnotationFont.Helvetica);
    public double FontSize { get; set; } = 12;
    public uint TextColor { get; set; } = 0xFF000000;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public string Align { get; set; } = nameof(AnnotationTextAlign.Left);
    public double LineSpacing { get; set; } = 1;
    public uint? BorderColor { get; set; }
    public double BorderWidth { get; set; } = 1;
    public uint? FillColor { get; set; }
    public string Icon { get; set; } = nameof(NoteIcon.Comment);

    public static AnnotationStyleData From(AnnotationStyle style) => new()
    {
        Color = style.Color.ToUInt32(),
        Opacity = style.Opacity,
        Font = style.Font.ToString(),
        FontSize = style.FontSize,
        TextColor = style.TextColor.ToUInt32(),
        Bold = style.Bold,
        Italic = style.Italic,
        Underline = style.Underline,
        Strikethrough = style.Strikethrough,
        Align = style.Align.ToString(),
        LineSpacing = style.LineSpacing,
        BorderColor = style.BorderColor?.ToUInt32(),
        BorderWidth = style.BorderWidth,
        FillColor = style.FillColor?.ToUInt32(),
        Icon = style.Icon.ToString()
    };

    public AnnotationStyle ToStyle() => new()
    {
        Color = Avalonia.Media.Color.FromUInt32(Color),
        Opacity = Math.Clamp(Opacity, 0, 1),
        Font = Enum.TryParse(Font, out AnnotationFont font) ? font : AnnotationFont.Helvetica,
        FontSize = FontSize > 0 ? FontSize : 12,
        TextColor = Avalonia.Media.Color.FromUInt32(TextColor),
        Bold = Bold,
        Italic = Italic,
        Underline = Underline,
        Strikethrough = Strikethrough,
        Align = Enum.TryParse(Align, out AnnotationTextAlign align) ? align : AnnotationTextAlign.Left,
        LineSpacing = LineSpacing > 0 ? LineSpacing : 1,
        BorderColor = BorderColor is { } border ? Avalonia.Media.Color.FromUInt32(border) : null,
        BorderWidth = Math.Max(0, BorderWidth),
        FillColor = FillColor is { } fill ? Avalonia.Media.Color.FromUInt32(fill) : null,
        Icon = Enum.TryParse(Icon, out NoteIcon icon) ? icon : NoteIcon.Comment
    };
}
