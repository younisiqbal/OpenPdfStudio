using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.Services.Printing;

/// <summary>Prints through the Windows spooler. Each page is rendered by PDFium and drawn scaled to fit the paper.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrintService : IPrintService
{
    private const int RenderDpi = 300;
    private const long MaxRenderPixels = 40_000_000;
    private const int MarginHundredths = 25;

    public IReadOnlyList<string> GetPrinters()
    {
        var printers = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
            printers.Add(name);
        return printers;
    }

    public string? GetDefaultPrinter()
    {
        var name = new PrinterSettings().PrinterName;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public Task PrintAsync(PdfDocumentSession session, string documentName, PrintJob job, IProgress<string> progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => Print(session, documentName, job, progress, cancellationToken), cancellationToken);

    private static void Print(PdfDocumentSession session, string documentName, PrintJob job, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        using var document = new PrintDocument();
        document.DocumentName = documentName;
        document.PrinterSettings.PrinterName = job.PrinterName;
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"The printer \"{job.PrinterName}\" is not available.");

        document.PrinterSettings.Copies = (short)Math.Clamp(job.Copies, 1, short.MaxValue);
        document.PrinterSettings.Collate = true;
        document.DefaultPageSettings.Margins = new Margins(MarginHundredths, MarginHundredths, MarginHundredths, MarginHundredths);

        var next = 0;
        document.QueryPageSettings += (_, e) =>
        {
            if (next >= job.PageIndices.Count)
                return;

            var size = session.PageSizes[job.PageIndices[next]];
            e.PageSettings.Landscape = job.Orientation switch
            {
                PrintOrientation.Landscape => true,
                PrintOrientation.Portrait => false,
                _ => size.Width > size.Height
            };
        };

        document.PrintPage += (_, e) =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                e.Cancel = true;
                return;
            }

            var pageIndex = job.PageIndices[next];
            progress.Report($"Printing page {next + 1} of {job.PageIndices.Count}...");
            DrawPage(session, pageIndex, e, cancellationToken);
            next++;
            e.HasMorePages = next < job.PageIndices.Count;
        };

        document.Print();
    }

    private static void DrawPage(PdfDocumentSession session, int pageIndex, PrintPageEventArgs e, CancellationToken cancellationToken)
    {
        var graphics = e.Graphics ?? throw new InvalidOperationException("The printer did not provide a drawing surface.");
        var size = session.PageSizes[pageIndex];
        var area = e.MarginBounds;

        // Graphics units here are hundredths of an inch; PDF points are 1/72 inch.
        var widthHundredths = size.Width / 72 * 100;
        var heightHundredths = size.Height / 72 * 100;
        var scale = Math.Min(area.Width / widthHundredths, area.Height / heightHundredths);
        var target = new RectangleF(
            (float)(area.X + (area.Width - widthHundredths * scale) / 2),
            (float)(area.Y + (area.Height - heightHundredths * scale) / 2),
            (float)(widthHundredths * scale),
            (float)(heightHundredths * scale));

        var pixelWidth = target.Width / 100 * RenderDpi;
        var pixelHeight = target.Height / 100 * RenderDpi;
        var pixels = (double)pixelWidth * pixelHeight;
        if (pixels > MaxRenderPixels)
        {
            var shrink = Math.Sqrt(MaxRenderPixels / pixels);
            pixelWidth *= (float)shrink;
            pixelHeight *= (float)shrink;
        }

        var width = Math.Max(1, (int)Math.Round(pixelWidth));
        var height = Math.Max(1, (int)Math.Round(pixelHeight));
        var buffer = session.RenderForPrintAsync(pageIndex, width, height, cancellationToken).GetAwaiter().GetResult();

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            graphics.DrawImage(bitmap, target);
        }
        finally
        {
            handle.Free();
        }
    }
}
