using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.ViewModels;

public partial class PdfPageViewModel : ObservableObject, ICachedPageBitmap
{
    public const double PointsToDip = 96.0 / 72.0;
    public const double ThumbnailWidth = 112;

    public PdfPageViewModel(int index, Size sizeInPoints)
    {
        Index = index;
        WidthPoints = sizeInPoints.Width;
        HeightPoints = sizeInPoints.Height;
        ThumbnailHeight = ThumbnailWidth * HeightPoints / WidthPoints;
    }

    public int Index { get; }
    public int PageNumber => Index + 1;
    public double WidthPoints { get; }
    public double HeightPoints { get; }
    public double ThumbnailHeight { get; }

    [ObservableProperty] private double _displayWidth;
    [ObservableProperty] private double _displayHeight;
    [ObservableProperty] private Bitmap? _bitmap;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private IReadOnlyList<Rect>? _selectionRects;
    [ObservableProperty] private ITransform? _pageTransform;

    public int RenderedPixelWidth { get; private set; }
    public int PendingPixelWidth { get; set; }
    public bool IsVisibleInViewport { get; set; }
    public CancellationTokenSource? RenderCts { get; set; }
    public CancellationTokenSource? ThumbnailCts { get; set; }

    bool ICachedPageBitmap.IsPinned => IsVisibleInViewport;

    public void SetZoom(double zoomFactor)
    {
        DisplayWidth = WidthPoints * PointsToDip * zoomFactor;
        DisplayHeight = HeightPoints * PointsToDip * zoomFactor;
    }

    public void SetRotation(int degrees) =>
        PageTransform = degrees == 0 ? null : new RotateTransform(degrees);

    public void SetBitmap(Bitmap bitmap, int pixelWidth)
    {
        var previous = Bitmap;
        Bitmap = bitmap;
        RenderedPixelWidth = pixelWidth;
        HasError = false;
        previous?.Dispose();
    }

    public void ReleaseBitmap()
    {
        RenderCts?.Cancel();
        PendingPixelWidth = 0;
        RenderedPixelWidth = 0;

        var previous = Bitmap;
        Bitmap = null;
        previous?.Dispose();
    }

    public void ReleaseThumbnail()
    {
        ThumbnailCts?.Cancel();
        ThumbnailCts = null;

        var previous = Thumbnail;
        Thumbnail = null;
        previous?.Dispose();
    }
}
