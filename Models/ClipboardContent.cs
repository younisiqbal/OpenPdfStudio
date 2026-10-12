using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.Models;

/// <summary>What the clipboard held when the user pasted. Only one of the two is set; text wins when both are offered.</summary>
public sealed record ClipboardContent(string? Text, PdfImageData? Image);
