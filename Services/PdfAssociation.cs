using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace OpenPdfStudio.Services;

/// <summary>Offers OpenPdfStudio as the default app for PDF files. The user still confirms on Windows and macOS.</summary>
public static class PdfAssociation
{
    public static async Task<string> MakeDefaultAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true
            });
            return "Choose OpenPdfStudio for PDF files in Windows Settings.";
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-mime",
                    Arguments = "default openpdfstudio.desktop application/pdf",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });
                if (process is null)
                    return "Could not set the default PDF app.";

                await process.WaitForExitAsync();
                return process.ExitCode == 0
                    ? "OpenPdfStudio is the default PDF app for this account."
                    : "Could not set the default. Use Open With in the file manager, or install the .deb package.";
            }
            catch (Exception)
            {
                return "Could not set the default. Use Open With in the file manager, or install the .deb package.";
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            RegisterMacApp();
            return "In Finder, select a PDF, choose File > Get Info, then Open With > OpenPdfStudio and Change All.";
        }

        return "This system does not support setting a default PDF app.";
    }

    private static void RegisterMacApp()
    {
        var app = AppBundlePath();
        if (app is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
                Arguments = "-f \"" + app + "\"",
                UseShellExecute = false
            })?.Dispose();
        }
        catch (Exception)
        {
            // Launch Services registration is best-effort.
        }
    }

    private static string? AppBundlePath()
    {
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(directory))
        {
            if (directory.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return directory;
            directory = Path.GetDirectoryName(directory) ?? string.Empty;
        }

        return null;
    }
}
