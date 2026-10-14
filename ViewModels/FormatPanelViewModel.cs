using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;

namespace OpenPdfStudio.ViewModels;

/// <summary>
/// The Format panel. Edits the selected annotations, or the default style for the active comment tool
/// when nothing is selected.
/// </summary>
public partial class FormatPanelViewModel : ObservableObject
{
    public static readonly Color[] Palette =
    [
        Color.FromRgb(255, 221, 0), Color.FromRgb(255, 235, 0), Color.FromRgb(146, 208, 80),
        Color.FromRgb(0, 176, 240), Color.FromRgb(0, 120, 215), Color.FromRgb(230, 0, 0),
        Color.FromRgb(192, 0, 192), Color.FromRgb(255, 140, 0), Color.FromRgb(255, 105, 180),
        Color.FromRgb(0, 160, 60), Color.FromRgb(0, 0, 0), Color.FromRgb(255, 255, 255),
        Color.FromRgb(128, 128, 128), Color.FromRgb(0, 255, 255), Color.FromRgb(128, 0, 0),
        Color.FromRgb(0, 0, 255)
    ];

    public static readonly double[] FontSizes = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 36];
    public static readonly double[] BorderWidths = [0, 0.5, 1, 1.5, 2, 3, 4];
    public static readonly double[] LineSpacings = [1, 1.15, 1.5, 2];

    private readonly DocumentViewModel _document;
    private bool _suppress;

    public FormatPanelViewModel(DocumentViewModel document)
    {
        _document = document;
        Icons = Enum.GetValues<NoteIcon>();
        Sync();
    }

    public IReadOnlyList<NoteIcon> Icons { get; }
    public IReadOnlyList<Color> Swatches => Palette;
    public IReadOnlyList<AnnotationFont> Fonts { get; } = Enum.GetValues<AnnotationFont>();
    public IReadOnlyList<double> Sizes => FontSizes;
    public IReadOnlyList<double> Widths => BorderWidths;
    public IReadOnlyList<double> Spacings => LineSpacings;

    [ObservableProperty] private bool _showNoteStyle;
    [ObservableProperty] private bool _showTextStyle;
    [ObservableProperty] private bool _showBorderFill;
    [ObservableProperty] private bool _showArrange;
    [ObservableProperty] private bool _canPageCenter;
    [ObservableProperty] private bool _canAlign;
    [ObservableProperty] private bool _canDistribute;
    [ObservableProperty] private string _title = "Format";

    [ObservableProperty] private Color _color = Color.FromRgb(255, 221, 0);
    [ObservableProperty] private double _opacity = 1;
    [ObservableProperty] private NoteIcon _icon = NoteIcon.Comment;
    [ObservableProperty] private AnnotationFont _font = AnnotationFont.Helvetica;
    [ObservableProperty] private double _fontSize = 12;
    [ObservableProperty] private Color _textColor = Color.FromRgb(0, 0, 0);
    [ObservableProperty] private bool _bold;
    [ObservableProperty] private bool _italic;
    [ObservableProperty] private bool _underline;
    [ObservableProperty] private bool _strikethrough;
    [ObservableProperty] private AnnotationTextAlign _align = AnnotationTextAlign.Left;
    [ObservableProperty] private double _lineSpacing = 1;
    [ObservableProperty] private Color _borderColor = Color.FromRgb(230, 0, 0);
    [ObservableProperty] private double _borderWidth = 1;
    [ObservableProperty] private Color _fillColor = Color.FromRgb(255, 255, 255);
    [ObservableProperty] private bool _hasFill = true;

    public bool IsAlignLeft => Align == AnnotationTextAlign.Left;
    public bool IsAlignCenter => Align == AnnotationTextAlign.Center;
    public bool IsAlignRight => Align == AnnotationTextAlign.Right;

    public FontFamily PreviewFontFamily => Font switch
    {
        AnnotationFont.Times => new FontFamily("Times New Roman, Times, serif"),
        AnnotationFont.Courier => new FontFamily("Courier New, Courier, monospace"),
        _ => new FontFamily("Arial, Helvetica, sans-serif")
    };

    public FontWeight PreviewWeight => Bold ? FontWeight.Bold : FontWeight.Normal;
    public FontStyle PreviewStyle => Italic ? FontStyle.Italic : FontStyle.Normal;
    public TextDecorationCollection? PreviewDecorations
    {
        get
        {
            if (!Underline && !Strikethrough)
                return null;
            var list = new TextDecorationCollection();
            if (Underline)
                list.Add(TextDecorations.Underline[0]);
            if (Strikethrough)
                list.Add(TextDecorations.Strikethrough[0]);
            return list;
        }
    }

    public string PreviewText => ShowNoteStyle ? "Note" : "The quick brown fox";
    public string? PreviewIcon => ShowNoteStyle ? NoteIcons.Combined(Icon) : null;
    public IBrush PreviewFill => new SolidColorBrush(ShowNoteStyle || !ShowTextStyle ? Color : FillColor);
    public IBrush PreviewTextBrush => new SolidColorBrush(ShowNoteStyle ? Darken(Color) : TextColor);

    public void Sync()
    {
        _suppress = true;
        try
        {
            var selected = _document.SelectedAnnotations;
            ShowArrange = selected.Count > 0;
            CanPageCenter = selected.Count == 1;
            CanAlign = selected.Count >= 2;
            CanDistribute = selected.Count >= 3;

            AnnotationKind? kind = selected.Count == 1
                ? selected[0].Kind
                : selected.Count > 1
                    ? (selected.All(a => a.Kind == selected[0].Kind) ? selected[0].Kind : null)
                    : _document.AnnotationTool.Kind();

            Title = kind?.DisplayName() ?? (selected.Count > 1 ? "Format" : "Format");
            ShowNoteStyle = kind == AnnotationKind.Note;
            ShowTextStyle = kind is AnnotationKind.Typewriter or AnnotationKind.Textbox or AnnotationKind.Callout
                            or AnnotationKind.Note;
            ShowBorderFill = kind is AnnotationKind.Textbox or AnnotationKind.Callout;

            var style = selected.Count > 0
                ? selected[0].Model.Style
                : kind is { } k ? ViewerPreferences.GetAnnotationStyle(k) : AnnotationStyle.DefaultFor(AnnotationKind.Note);

            Color = style.Color;
            Opacity = style.Opacity;
            Icon = style.Icon;
            Font = style.Font;
            FontSize = style.FontSize;
            TextColor = style.TextColor;
            Bold = style.Bold;
            Italic = style.Italic;
            Underline = style.Underline;
            Strikethrough = style.Strikethrough;
            Align = style.Align;
            LineSpacing = style.LineSpacing;
            BorderColor = style.BorderColor ?? Color.FromRgb(230, 0, 0);
            BorderWidth = style.BorderWidth;
            FillColor = style.FillColor ?? Color.FromRgb(255, 255, 255);
            HasFill = style.FillColor is not null;
            RaisePreview();
        }
        finally
        {
            _suppress = false;
        }
    }

    partial void OnAlignChanged(AnnotationTextAlign value)
    {
        OnPropertyChanged(nameof(IsAlignLeft));
        OnPropertyChanged(nameof(IsAlignCenter));
        OnPropertyChanged(nameof(IsAlignRight));
    }

    [RelayCommand]
    private void SetColor(Color color)
    {
        if (ShowNoteStyle || !ShowTextStyle)
            Patch(s => s with { Color = color });
        else
            Patch(s => s with { TextColor = color });
    }

    [RelayCommand]
    private void SetNoteColor(Color color) => Patch(s => s with { Color = color });

    [RelayCommand]
    private void SetTextColor(Color color) => Patch(s => s with { TextColor = color });

    [RelayCommand]
    private void SetBorderColor(Color color) => Patch(s => s with { BorderColor = color });

    [RelayCommand]
    private void SetFillColor(Color color) => Patch(s => s with { FillColor = color });

    [RelayCommand]
    private void SetIcon(NoteIcon icon) => Patch(s => s with { Icon = icon });

    [RelayCommand]
    private void SetFont(AnnotationFont font) => Patch(s => s with { Font = font });

    [RelayCommand]
    private void SetFontSize(double size) => Patch(s => s with { FontSize = size });

    [RelayCommand]
    private void SetBorderWidth(double width) => Patch(s => s with { BorderWidth = width });

    [RelayCommand]
    private void SetLineSpacing(double spacing) => Patch(s => s with { LineSpacing = spacing });

    [RelayCommand]
    private void SetAlign(AnnotationTextAlign align) => Patch(s => s with { Align = align });

    [RelayCommand]
    private void ToggleBold() => Patch(s => s with { Bold = !s.Bold });

    [RelayCommand]
    private void ToggleItalic() => Patch(s => s with { Italic = !s.Italic });

    [RelayCommand]
    private void ToggleUnderline() => Patch(s => s with { Underline = !s.Underline });

    [RelayCommand]
    private void ToggleStrikethrough() => Patch(s => s with { Strikethrough = !s.Strikethrough });

    [RelayCommand]
    private void ToggleFill() => Patch(s => s with { FillColor = s.FillColor is null ? Color.FromRgb(255, 255, 255) : null });

    [RelayCommand]
    private void Close() => _document.CloseFormatPanelCommand.Execute(null);

    partial void OnOpacityChanged(double value)
    {
        if (!_suppress)
            Patch(s => s with { Opacity = Math.Clamp(value, 0, 1) }, mergeKey: "opacity");
    }

    partial void OnFontChanged(AnnotationFont value)
    {
        if (!_suppress)
            Patch(s => s with { Font = value });
    }

    partial void OnFontSizeChanged(double value)
    {
        if (!_suppress)
            Patch(s => s with { FontSize = value });
    }

    partial void OnLineSpacingChanged(double value)
    {
        if (!_suppress)
            Patch(s => s with { LineSpacing = value });
    }

    partial void OnBorderWidthChanged(double value)
    {
        if (!_suppress)
            Patch(s => s with { BorderWidth = value });
    }

    [RelayCommand]
    private Task PageCenter() => _document.ArrangeSelectedAsync(AnnotationArrange.PageCenter);

    [RelayCommand]
    private Task AlignLeft() => _document.ArrangeSelectedAsync(AnnotationArrange.Left);

    [RelayCommand]
    private Task AlignCenterH() => _document.ArrangeSelectedAsync(AnnotationArrange.Center);

    [RelayCommand]
    private Task AlignRight() => _document.ArrangeSelectedAsync(AnnotationArrange.Right);

    [RelayCommand]
    private Task AlignTop() => _document.ArrangeSelectedAsync(AnnotationArrange.Top);

    [RelayCommand]
    private Task AlignMiddle() => _document.ArrangeSelectedAsync(AnnotationArrange.Middle);

    [RelayCommand]
    private Task AlignBottom() => _document.ArrangeSelectedAsync(AnnotationArrange.Bottom);

    [RelayCommand]
    private Task DistributeH() => _document.ArrangeSelectedAsync(AnnotationArrange.DistributeH);

    [RelayCommand]
    private Task DistributeV() => _document.ArrangeSelectedAsync(AnnotationArrange.DistributeV);

    private void Patch(Func<AnnotationStyle, AnnotationStyle> patch, string? mergeKey = null)
    {
        if (_suppress)
            return;

        _ = _document.PatchStyleAsync(patch, mergeKey);
        RaisePreview();
    }

    private void RaisePreview()
    {
        OnPropertyChanged(nameof(PreviewFontFamily));
        OnPropertyChanged(nameof(PreviewWeight));
        OnPropertyChanged(nameof(PreviewStyle));
        OnPropertyChanged(nameof(PreviewDecorations));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(PreviewIcon));
        OnPropertyChanged(nameof(PreviewFill));
        OnPropertyChanged(nameof(PreviewTextBrush));
        OnPropertyChanged(nameof(IsAlignLeft));
        OnPropertyChanged(nameof(IsAlignCenter));
        OnPropertyChanged(nameof(IsAlignRight));
    }

    private static Color Darken(Color color) =>
        Color.FromRgb((byte)(color.R * 0.4), (byte)(color.G * 0.4), (byte)(color.B * 0.4));
}

public enum AnnotationArrange
{
    PageCenter,
    Left,
    Center,
    Right,
    Top,
    Middle,
    Bottom,
    DistributeH,
    DistributeV
}
