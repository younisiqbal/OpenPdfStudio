using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.Services.Printing;

/// <summary>
/// Prints on Linux and macOS through CUPS: the document (with any unsaved edits) is written to a temporary
/// PDF and handed to <c>lp</c>, which does the rasterising.
/// </summary>
public sealed class CupsPrintService : IPrintService
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(10);

    public IReadOnlyList<string> GetPrinters()
    {
        var output = Run("lpstat", ["-e"]);
        return output is null
            ? []
            : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public string? GetDefaultPrinter()
    {
        var output = Run("lpstat", ["-d"]);
        var colon = output?.IndexOf(':') ?? -1;
        if (output is null || colon < 0)
            return null;

        var name = output[(colon + 1)..].Trim();
        return name.Length > 0 ? name : null;
    }

    public async Task PrintAsync(PdfDocumentSession session, string documentName, PrintJob job, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"openpdfstudio-print-{Guid.NewGuid():N}.pdf");
        try
        {
            progress.Report("Preparing the document for printing...");
            await session.SaveCopyAsync(temp);

            var arguments = new List<string>
            {
                "-d", job.PrinterName,
                "-n", Math.Max(1, job.Copies).ToString(CultureInfo.InvariantCulture),
                "-t", documentName,
                "-o", "fit-to-page",
                "-o", $"page-ranges={PageRange.Format(job.PageIndices)}"
            };
            if (job.Orientation == PrintOrientation.Landscape)
                arguments.AddRange(["-o", "orientation-requested=4"]);
            else if (job.Orientation == PrintOrientation.Portrait)
                arguments.AddRange(["-o", "orientation-requested=3"]);
            if (job.Copies > 1)
                arguments.AddRange(["-o", "collate=true"]);
            arguments.AddRange(["--", temp]);

            progress.Report($"Sending to {job.PrinterName}...");
            var (exitCode, error) = await RunAsync("lp", arguments, cancellationToken);
            if (exitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"lp exited with code {exitCode}." : error.Trim());
        }
        finally
        {
            // lp copies the file into the spool before it returns, so the temp file can go.
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string? Run(string tool, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(tool, arguments));
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(ToolTimeout))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(string tool, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = Process.Start(CreateStartInfo(tool, arguments));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("Printing needs CUPS (the lp command), which was not found.");
        }

        if (process is null)
            throw new InvalidOperationException("The print command could not be started.");

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await output;
            return (process.ExitCode, await error);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string tool, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }
}
