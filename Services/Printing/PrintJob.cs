using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.Services.Printing;

public enum PrintOrientation
{
    Auto,
    Portrait,
    Landscape
}

/// <summary>What to print. Page indices are zero-based and in print order.</summary>
public sealed record PrintJob(string PrinterName, int Copies, IReadOnlyList<int> PageIndices, PrintOrientation Orientation);

public interface IPrintService
{
    IReadOnlyList<string> GetPrinters();
    string? GetDefaultPrinter();

    /// <summary>Sends the job to the printer. Reports progress as status text.</summary>
    Task PrintAsync(PdfDocumentSession session, string documentName, PrintJob job, IProgress<string> progress,
        CancellationToken cancellationToken);
}

public static class PrintServices
{
    public static IPrintService ForCurrentPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsPrintService() : new CupsPrintService();
}

public static class PageRange
{
    /// <summary>
    /// Parses ranges such as "1-3, 5, 8-" into zero-based indices. Returns null when the text is not a valid
    /// range for a document with <paramref name="pageCount"/> pages.
    /// </summary>
    public static IReadOnlyList<int>? Parse(string text, int pageCount)
    {
        var pages = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            int first, last;
            if (dash < 0)
            {
                if (!TryPage(part, pageCount, out first))
                    return null;
                last = first;
            }
            else
            {
                var start = part[..dash].Trim();
                var end = part[(dash + 1)..].Trim();
                first = 1;
                last = pageCount;
                if (start.Length > 0 && !TryPage(start, pageCount, out first))
                    return null;
                if (end.Length > 0 && !TryPage(end, pageCount, out last))
                    return null;
                if (last < first)
                    return null;
            }

            for (int page = first; page <= last; page++)
                pages.Add(page - 1);
        }

        return pages.Count > 0 ? pages : null;
    }

    /// <summary>Formats zero-based indices as a compact one-based range, such as "1-3,5".</summary>
    public static string Format(IReadOnlyList<int> pageIndices)
    {
        var parts = new List<string>();
        for (int i = 0; i < pageIndices.Count; i++)
        {
            var start = pageIndices[i];
            var end = start;
            while (i + 1 < pageIndices.Count && pageIndices[i + 1] == end + 1)
            {
                i++;
                end++;
            }

            parts.Add(start == end
                ? (start + 1).ToString(CultureInfo.InvariantCulture)
                : $"{start + 1}-{end + 1}");
        }

        return string.Join(',', parts);
    }

    private static bool TryPage(string text, int pageCount, out int page) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1 && page <= pageCount;
}
