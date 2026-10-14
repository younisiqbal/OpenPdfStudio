using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OpenPdfStudio.Models;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class NotePopup : UserControl
{
    private AnnotationViewModel? _annotation;
    private bool _committed;

    public NotePopup()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Commit();
        Editor.LostFocus += OnLostFocus;
        Editor.KeyDown += OnEditorKeyDown;
        OptionsButton.Click += OnOptions;
        PointerPressed += (_, e) => e.Handled = true;
    }

    public event Action<AnnotationViewModel, string>? Committed;
    public event Action<AnnotationViewModel>? Cancelled;
    public event Action<AnnotationViewModel>? DeleteRequested;

    public AnnotationViewModel? Annotation => _annotation;

    public void Begin(AnnotationViewModel annotation)
    {
        _annotation = annotation;
        _committed = false;
        var model = annotation.Model;
        TitleText.Text = $"Note  {model.Modified.ToLocalTime():dd-MMM-yyyy HH:mm:ss}";
        AuthorText.Text = string.IsNullOrEmpty(model.Author) ? Environment.UserName : model.Author;
        ApplyStyle(model.Style);
        Editor.Text = model.Contents;
        IsVisible = true;
        Editor.Focus();
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
    }

    public void HidePopup()
    {
        _annotation = null;
        IsVisible = false;
    }

    private void ApplyStyle(AnnotationStyle style)
    {
        Editor.FontFamily = style.Font switch
        {
            AnnotationFont.Times => new FontFamily("Times New Roman, Times, serif"),
            AnnotationFont.Courier => new FontFamily("Courier New, Courier, monospace"),
            _ => new FontFamily("Arial, Helvetica, sans-serif")
        };
        Editor.FontSize = style.FontSize;
        Editor.FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal;
        Editor.FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal;
        Editor.Foreground = new SolidColorBrush(style.TextColor);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (IsPointerOver)
            return;
        Commit();
    }

    private void OnOptions(object? sender, RoutedEventArgs e)
    {
        if (_annotation is null)
            return;

        var menu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "Delete", Command = new Relay(Delete) }
            }
        };
        menu.Open(OptionsButton);
    }

    private void Delete()
    {
        if (_annotation is not { } annotation || _committed)
            return;

        _committed = true;
        DeleteRequested?.Invoke(annotation);
    }

    private void Commit()
    {
        if (_annotation is not { } annotation || _committed)
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

    private sealed class Relay(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
