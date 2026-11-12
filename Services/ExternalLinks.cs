using System;
using System.Diagnostics;

namespace OpenPdfStudio.Services;

/// <summary>Project web pages, opened in the default browser.</summary>
public static class ExternalLinks
{
    public const string Website = "https://openpdfstudio.github.io/";
    public const string Repository = "https://github.com/younisiqbal/OpenPdfStudio";
    public const string NewIssue = Repository + "/issues/new";
    public const string License = Repository + "/blob/main/LICENSE";

    /// <summary>Opens a URL in the default browser. Returns false when no browser could be started.</summary>
    public static bool Open(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", url)?.Dispose();
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url)?.Dispose();
            else
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
