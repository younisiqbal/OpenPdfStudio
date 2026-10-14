using System.Collections.Generic;

namespace OpenPdfStudio.Models;

/// <summary>
/// Note icon outlines in a 20 x 20 box with a top-left origin. Only absolute M, L, C and Z commands are used,
/// so the same strings drive the PDF appearance stream and the Avalonia previews.
/// <c>Body</c> is filled with the note colour and outlined; <c>Detail</c> is stroked on top.
/// </summary>
public static class NoteIcons
{
    public const double Size = 20;

    private const string CircleBody = "M2 10 C2 5.6 5.6 2 10 2 C14.4 2 18 5.6 18 10 C18 14.4 14.4 18 10 18 C5.6 18 2 14.4 2 10 Z";

    private static readonly Dictionary<NoteIcon, (string Body, string Detail)> Shapes = new()
    {
        [NoteIcon.Comment] = ("M2 3 L18 3 L18 13 L9 13 L5 17 L5 13 L2 13 Z", "M5 6 L15 6 M5 8.5 L15 8.5 M5 11 L12 11"),
        [NoteIcon.Cross] = ("M5 2.5 L10 7.5 L15 2.5 L17.5 5 L12.5 10 L17.5 15 L15 17.5 L10 12.5 L5 17.5 L2.5 15 L7.5 10 L2.5 5 Z", ""),
        [NoteIcon.Help] = (CircleBody, "M7.5 7.5 C7.5 4.8 12.5 4.8 12.5 7.5 C12.5 9.6 10 9.6 10 12 M10 14 L10 15.2"),
        [NoteIcon.Insert] = ("M10 3 L17.5 16 L13.8 16 L10 9.3 L6.2 16 L2.5 16 Z", ""),
        [NoteIcon.Check] = ("M2 10.5 L4.5 8 L7.8 11.3 L15.5 3.5 L18 6 L7.8 16.3 Z", ""),
        [NoteIcon.RightArrow] = ("M2 7 L11 7 L11 3 L18 10 L11 17 L11 13 L2 13 Z", ""),
        [NoteIcon.Key] = ("M2.5 7.5 C2.5 4.7 4.7 2.5 7.5 2.5 C10.3 2.5 12.5 4.7 12.5 7.5 C12.5 10.3 10.3 12.5 7.5 12.5 C4.7 12.5 2.5 10.3 2.5 7.5 Z",
            "M11 11 L17.5 17.5 M14.5 14.5 L12.8 16.2 M16.5 16.5 L15 18 M6.5 6.5 L6.6 6.6"),
        [NoteIcon.Star] = ("M10 1.5 L12.5 7.2 L18.6 7.6 L13.9 11.6 L15.4 17.6 L10 14.3 L4.6 17.6 L6.1 11.6 L1.4 7.6 L7.5 7.2 Z", ""),
        [NoteIcon.Circle] = (CircleBody, ""),
        [NoteIcon.CrossHairs] = (CircleBody, "M10 1 L10 19 M1 10 L19 10"),
        [NoteIcon.RightPointer] = ("M3 3 L18 10 L3 17 L7 10 Z", ""),
        [NoteIcon.UpArrow] = ("M10 2 L17 9 L13 9 L13 18 L7 18 L7 9 L3 9 Z", ""),
        [NoteIcon.Note] = ("M4 2 L13 2 L17 6 L17 18 L4 18 Z", "M13 2 L13 6 L17 6 M6.5 9 L14.5 9 M6.5 12 L14.5 12 M6.5 15 L12 15"),
        [NoteIcon.Paragraph] = ("M3 3 L17 3 L17 17 L3 17 Z", "M11 5 L11 15 M14 5 L14 15 M14 5 L9 5 C6 5 6 10.5 9 10.5 L11 10.5"),
        [NoteIcon.NewParagraph] = ("M10 2.5 L17.5 16.5 L2.5 16.5 Z", "M10 7 L10 12 M10 14 L10 14.8"),
        [NoteIcon.UpLeftArrow] = ("M3 3 L13 3 L10 6 L17 13 L13 17 L6 10 L3 13 Z", "")
    };

    public static (string Body, string Detail) Get(NoteIcon icon) =>
        Shapes.TryGetValue(icon, out var shape) ? shape : Shapes[NoteIcon.Comment];

    /// <summary>Body and detail joined, for previews drawn as one geometry.</summary>
    public static string Combined(NoteIcon icon)
    {
        var (body, detail) = Get(icon);
        return string.IsNullOrEmpty(detail) ? body : body + " " + detail;
    }
}
