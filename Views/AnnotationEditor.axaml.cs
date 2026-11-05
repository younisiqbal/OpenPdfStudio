using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenPdfStudio.Models;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class AnnotationEditor : UserControl
{
    private static readonly IBrush Caret = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));

    private AnnotationViewModel? _annotation;
    private bool _committed;

    public double MinimumLineHeight => Editor.FontSize + Editor.Padding.Top + Editor.Padding.Bottom;

    public AnnotationEditor()
    {
        InitializeComponent();
        Editor.LostFocus += OnLostFocus;
        Editor.KeyDown += OnEditorKeyDown;
        Editor.TextChanged += (_, _) => TextChanged?.Invoke();
    }

    public event Action? TextChanged;
    public event Action<AnnotationViewModel, string>? Committed;
    public event Action<AnnotationViewModel>? Cancelled;

    public AnnotationViewModel? Annotation => _annotation;
    public string Text => Editor.Text ?? string.Empty;

    public void Begin(AnnotationViewModel annotation, Size size)
    {
        _annotation = annotation;
        _committed = false;
        ApplyStyle(annotation.Model);
        Fit(size);
        Editor.Text = annotation.Model.Contents;
        IsVisible = true;
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
        ApplyCaretBrush();
        Dispatcher.UIThread.Post(() =>
        {
            if (_annotation != annotation || _committed)
                return;
            ApplyCaretBrush();
            Editor.CaretIndex = Editor.Text?.Length ?? 0;
            Editor.Focus();
        }, DispatcherPriority.Input);
    }

    public void Fit(Size size)
    {
        Width = Math.Max(24, size.Width);
        Height = Math.Max(MinimumLineHeight, size.Height);
    }

    public void HideEditor()
    {
        _annotation = null;
        IsVisible = false;
    }

    private void ApplyStyle(PdfAnnotation model)
    {
        var style = model.Style;
        Editor.FontFamily = style.Font switch
        {
            AnnotationFont.Times => new FontFamily("Times New Roman, Times, serif"),
            AnnotationFont.Courier => new FontFamily("Courier New, Courier, monospace"),
            _ => new FontFamily("Arial, Helvetica, sans-serif")
        };
        Editor.FontSize = style.FontSize * 96 / 72;
        Editor.FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal;
        Editor.FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal;
        Editor.Foreground = new SolidColorBrush(style.TextColor);
        ApplyCaretBrush();
        Editor.TextAlignment = style.Align switch
        {
            AnnotationTextAlign.Center => TextAlignment.Center,
            AnnotationTextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };

        var fill = new SolidColorBrush(style.FillColor ?? Colors.White);
        Editor.Background = fill;
        Background = fill;
    }

    private void ApplyCaretBrush()
    {
        Editor.CaretBrush = Caret;
        Editor.ApplyTemplate();
        foreach (var presenter in Editor.GetVisualDescendants().OfType<TextPresenter>())
            presenter.CaretBrush = Caret;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _annotation is { } annotation)
        {
            Commit(annotation);
            e.Handled = true;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_annotation is { } annotation && !_committed)
            Commit(annotation);
    }

    private void Commit(AnnotationViewModel annotation)
    {
        if (_committed)
            return;

        _committed = true;
        Committed?.Invoke(annotation, Editor.Text ?? string.Empty);
    }

    private void Cancel()
    {
        if (_annotation is not { } annotation || _committed)
            return;

        _committed = true;
        Cancelled?.Invoke(annotation);
    }
}
