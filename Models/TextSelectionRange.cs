using System;

namespace OpenPdfStudio.Models;

/// <summary>Inclusive range of PDFium character indices on one page.</summary>
public readonly record struct TextSelectionRange(int PageIndex, int AnchorIndex, int ActiveIndex)
{
    public int Start => Math.Min(AnchorIndex, ActiveIndex);
    public int Count => Math.Abs(ActiveIndex - AnchorIndex) + 1;
}
