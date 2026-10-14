namespace OpenPdfStudio.Models;

/// <summary>The comment tool that page clicks and drags create annotations with. None leaves the hand or select tool in charge.</summary>
public enum AnnotationTool
{
    None,
    Note,
    Typewriter,
    Textbox,
    Callout,
    Highlight,
    Underline,
    StrikeOut,
    Squiggly
}

public static class AnnotationToolExtensions
{
    public static AnnotationKind? Kind(this AnnotationTool tool) => tool switch
    {
        AnnotationTool.Note => AnnotationKind.Note,
        AnnotationTool.Typewriter => AnnotationKind.Typewriter,
        AnnotationTool.Textbox => AnnotationKind.Textbox,
        AnnotationTool.Callout => AnnotationKind.Callout,
        AnnotationTool.Highlight => AnnotationKind.Highlight,
        AnnotationTool.Underline => AnnotationKind.Underline,
        AnnotationTool.StrikeOut => AnnotationKind.StrikeOut,
        AnnotationTool.Squiggly => AnnotationKind.Squiggly,
        _ => null
    };

    public static bool IsMarkup(this AnnotationTool tool) =>
        tool is AnnotationTool.Highlight or AnnotationTool.Underline or AnnotationTool.StrikeOut or AnnotationTool.Squiggly;

    public static string DisplayName(this AnnotationKind kind) => kind switch
    {
        AnnotationKind.StrikeOut => "Strikeout",
        _ => kind.ToString()
    };
}
