using System;
using System.Collections.Generic;
using System.IO;

namespace OpenPdfStudio.Services;

internal static class PdfLaunch
{
    public static IReadOnlyList<string> PathsFromArgs(IEnumerable<string>? args)
    {
        if (args is null)
            return [];

        var paths = new List<string>();
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
                continue;

            var path = arg.Trim('"');
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || File.Exists(path))
                paths.Add(path);
        }

        return paths;
    }
}
