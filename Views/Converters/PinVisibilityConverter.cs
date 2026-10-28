using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;

namespace OpenPdfStudio.Views.Converters;

/// <summary>Shows a rail button when its id is pinned and, if it needs a document, one is open.</summary>
public sealed class PinVisibilityConverter : IMultiValueConverter
{
    public static readonly PinVisibilityConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is not string id || !RailCatalog.TryGet(id, out var entry))
            return false;

        if (!ViewerPreferences.IsPinned(id))
            return false;

        var hasDocument = values.Count > 1 && values[1] is true;
        return !entry.RequiresDocument || hasDocument;
    }
}
