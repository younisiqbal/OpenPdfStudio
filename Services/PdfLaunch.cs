using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenPdfStudio.Services;

internal static class PdfLaunch
{
    public static IReadOnlyList<string> PathsFromArgs(IEnumerable<string>? args)
    {
        if (args is null)
            return [];

        var list = args as IList<string> ?? args.ToList();
        var skip = new HashSet<int>();
        if (TryGetComparePair(list, out _, out _, out var leftIndex, out var rightIndex))
        {
            skip.Add(leftIndex);
            skip.Add(rightIndex);
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], "--compare", StringComparison.OrdinalIgnoreCase))
                    skip.Add(i);
            }
        }

        var paths = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            if (skip.Contains(i))
                continue;

            var arg = list[i];
            if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-'))
                continue;

            var path = arg.Trim('"');
            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || File.Exists(path))
                paths.Add(path);
        }

        return paths;
    }

    /// <summary>Parses <c>--compare left.pdf right.pdf</c> from launch args.</summary>
    public static bool TryGetComparePair(IEnumerable<string>? args, out string left, out string right) =>
        TryGetComparePair(args as IList<string> ?? args?.ToList() ?? [], out left, out right, out _, out _);

    private static bool TryGetComparePair(
        IList<string> args,
        out string left,
        out string right,
        out int leftIndex,
        out int rightIndex)
    {
        left = "";
        right = "";
        leftIndex = -1;
        rightIndex = -1;
        for (var i = 0; i < args.Count - 2; i++)
        {
            if (!string.Equals(args[i], "--compare", StringComparison.OrdinalIgnoreCase))
                continue;

            left = args[i + 1].Trim('"');
            right = args[i + 2].Trim('"');
            leftIndex = i + 1;
            rightIndex = i + 2;
            return left.Length > 0 && right.Length > 0;
        }

        return false;
    }
}
