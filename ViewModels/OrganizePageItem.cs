using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenPdfStudio.ViewModels;

/// <summary>One page in the organize view: an existing page or a new blank one, with its pending rotation.</summary>
public sealed partial class OrganizePageItem : ObservableObject
{
    public OrganizePageItem(int sourceIndex, Size pageSize)
    {
        SourceIndex = sourceIndex;
        PageSize = pageSize;
    }

    /// <summary>The page's index in the document when organizing began, or -1 for a new blank page.</summary>
    public int SourceIndex { get; }

    /// <summary>Size in points as the page is shown now, before any pending rotation.</summary>
    public Size PageSize { get; }

    public bool IsBlank => SourceIndex < 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Transform), nameof(DisplaySize), nameof(RotationText), nameof(HasRotation))]
    private int _turns;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isFocused;
    [ObservableProperty] private bool _isDragging;
    [ObservableProperty] private Bitmap? _thumbnail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(Detail))]
    private int _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageWidth), nameof(ImageHeight), nameof(TileWidth))]
    private double _box = 140;

    public double TileWidth => Box + 20;
    public bool HasRotation => Turns != 0;

    public string RotationText => Turns switch
    {
        1 => "Rotated 90° right",
        2 => "Rotated 180°",
        3 => "Rotated 90° left",
        _ => string.Empty
    };

    public string SizeText => $"{PageSize.Width / 72:0.##} × {PageSize.Height / 72:0.##} in";

    public string Label => (Position + 1).ToString();

    public string Detail => IsBlank
        ? "New blank page"
        : SourceIndex == Position ? $"Page {SourceIndex + 1}" : $"Was page {SourceIndex + 1}";

    public double ImageWidth => PageSize.Width * Scale;
    public double ImageHeight => PageSize.Height * Scale;
    public ITransform Transform => new RotateTransform(Turns * 90);

    /// <summary>Size in points as the page will be after the pending rotation.</summary>
    public Size DisplaySize => Turns % 2 == 0 ? PageSize : new Size(PageSize.Height, PageSize.Width);

    private double Scale => Box / Math.Max(1, Math.Max(PageSize.Width, PageSize.Height));
}
