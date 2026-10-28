using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OpenPdfStudio.Models;
using OpenPdfStudio.ViewModels;

namespace OpenPdfStudio.Views;

public partial class NotePopup : UserControl
{
    private AnnotationViewModel? _annotation;
    private bool _dragging;
    private bool _resizing;
    private bool _suppressFormat;
    private Point _press;
    private Size _startSize;
    private Point _startCanvas;

    public NotePopup()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
        Editor.GotFocus += (_, _) =>
        {
            if (!_suppressFormat && _annotation is { } annotation)
                EditorFocused?.Invoke(annotation);
        };
        Editor.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!_suppressFormat && _annotation is { } annotation)
                EditorFocused?.Invoke(annotation);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Editor.LostFocus += OnLostFocus;
        Editor.KeyDown += OnEditorKeyDown;
        OptionsButton.Click += OnOptions;
        Header.PointerPressed += OnHeaderPressed;
        Header.PointerMoved += OnHeaderMoved;
        Header.PointerReleased += OnHeaderReleased;
        ResizeGrip.PointerPressed += OnResizePressed;
        ResizeGrip.PointerMoved += OnResizeMoved;
        ResizeGrip.PointerReleased += OnResizeReleased;
        PointerPressed += (_, e) => e.Handled = true;
    }

    public event Action<AnnotationViewModel, string>? ContentsChanged;
    public event Action<AnnotationViewModel, string>? Closed;
    public event Action<AnnotationViewModel>? Cancelled;
    public event Action<AnnotationViewModel>? DeleteRequested;
    public event Action<AnnotationViewModel>? EditorFocused;
    public event Action<AnnotationViewModel>? Moved;
    public event Action<AnnotationViewModel>? Resized;

    public AnnotationViewModel? Annotation => _annotation;
    public bool PointerOnLeft { get; private set; } = true;

    public void Begin(AnnotationViewModel annotation)
    {
        if (_annotation != annotation)
        {
            Detach();
            _annotation = annotation;
            annotation.PropertyChanged += OnAnnotationChanged;
            var model = annotation.Model;
            TitleText.Text = $"Note  {model.Modified.ToLocalTime():dd-MMM-yyyy h:mm:ss tt}";
            AuthorText.Text = string.IsNullOrEmpty(model.Author) ? Environment.UserName : model.Author;
            Editor.Text = model.Contents;
        }

        ApplyStyle(annotation.Model.Style);
        IsVisible = true;
        FocusEditor();
        Editor.CaretIndex = Editor.Text?.Length ?? 0;
    }

    public void FocusEditor()
    {
        _suppressFormat = true;
        Editor.Focus();
        Dispatcher.UIThread.Post(() => _suppressFormat = false);
    }

    public void SetPointerOnLeft(bool left)
    {
        PointerOnLeft = left;
        LeftPointer.IsVisible = left;
        RightPointer.IsVisible = !left;
        Card.Margin = left ? new Thickness(11, 0, 0, 0) : new Thickness(0, 0, 11, 0);
    }

    public void HidePopup()
    {
        Detach();
        IsVisible = false;
    }

    public string CurrentText => Editor.Text ?? string.Empty;

    private void Detach()
    {
        if (_annotation is not null)
            _annotation.PropertyChanged -= OnAnnotationChanged;
        _annotation = null;
    }

    private void OnAnnotationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_annotation is null || e.PropertyName is not (nameof(AnnotationViewModel.Model) or null))
            return;
        ApplyStyle(_annotation.Model.Style);
        TitleText.Text = $"Note  {_annotation.Model.Modified.ToLocalTime():dd-MMM-yyyy h:mm:ss tt}";
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
        Editor.TextAlignment = style.Align switch
        {
            AnnotationTextAlign.Center => TextAlignment.Center,
            AnnotationTextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
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
        if (_annotation is { } annotation && IsVisible)
            ContentsChanged?.Invoke(annotation, Editor.Text ?? string.Empty);
    }

    private void OnOptions(object? sender, RoutedEventArgs e)
    {
        if (_annotation is null)
            return;

        var menu = new ContextMenu
        {
            Items = { new MenuItem { Header = "Delete", Command = new Relay(Delete) } }
        };
        menu.Open(OptionsButton);
    }

    private void Delete()
    {
        if (_annotation is not { } annotation)
            return;
        DeleteRequested?.Invoke(annotation);
    }

    private void Close()
    {
        if (_annotation is not { } annotation)
            return;
        Closed?.Invoke(annotation, Editor.Text ?? string.Empty);
    }

    private void Cancel()
    {
        if (_annotation is not { } annotation)
            return;
        Cancelled?.Invoke(annotation);
    }

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Parent is not Visual parent)
            return;
        _dragging = true;
        _startCanvas = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
        _press = e.GetPosition(parent) - _startCanvas;
        e.Pointer.Capture(Header);
        e.Handled = true;
    }

    private void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Parent is not Visual parent)
            return;
        var pos = e.GetPosition(parent);
        Canvas.SetLeft(this, pos.X - _press.X);
        Canvas.SetTop(this, pos.Y - _press.Y);
        if (_annotation is { } annotation)
            Moved?.Invoke(annotation);
        e.Handled = true;
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        e.Pointer.Capture(null);
        if (_annotation is { } annotation)
            Moved?.Invoke(annotation);
        e.Handled = true;
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Parent is not Visual parent)
            return;
        _resizing = true;
        _press = e.GetPosition(parent);
        _startSize = Bounds.Size;
        e.Pointer.Capture(ResizeGrip);
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing || Parent is not Visual parent)
            return;
        var delta = e.GetPosition(parent) - _press;
        Width = Math.Max(MinWidth, _startSize.Width + delta.X);
        Height = Math.Max(MinHeight, _startSize.Height + delta.Y);
        e.Handled = true;
    }

    private void OnResizeReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing)
            return;
        _resizing = false;
        e.Pointer.Capture(null);
        if (_annotation is { } annotation)
            Resized?.Invoke(annotation);
        e.Handled = true;
    }

    private sealed class Relay(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
