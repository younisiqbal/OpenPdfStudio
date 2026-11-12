using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenPdfStudio.Services.Pdf;
using OpenPdfStudio.Services.Printing;
using OpenPdfStudio.ViewModels;
using OpenPdfStudio.Views.Dialogs;

namespace OpenPdfStudio.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private bool _closeConfirmed;
    private bool _isPrinting;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        WindowDecorationProperties.SetElementRole(TitleDragArea, WindowDecorationsElementRole.TitleBar);
        Opened += (_, _) => UseSquareCorners();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            var maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.IsVisible = !maximized;
            RestoreGlyph.IsVisible = maximized;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => OnMaximizeClick(sender, e);

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void UseSquareCorners()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || TryGetPlatformHandle() is not { } handle)
            return;

        int preference = DwmWindowCornerDoNotRound;
        DwmSetWindowAttribute(handle.Handle, DwmWindowCornerPreference, ref preference, sizeof(int));
    }

    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerDoNotRound = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasPdf = DroppedPdfPaths(e).Count > 0;
        e.DragEffects = hasPdf ? DragDropEffects.Copy : DragDropEffects.None;
        if (_vm is not null)
            _vm.Home.IsFileDragOver = hasPdf;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        // Moving between child controls also raises leave; only clear once the pointer is outside the window.
        var point = e.GetPosition(this);
        if (_vm is not null && !new Avalonia.Rect(Bounds.Size).Contains(point))
            _vm.Home.IsFileDragOver = false;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is null)
            return;

        _vm.Home.IsFileDragOver = false;
        e.Handled = true;
        var paths = DroppedPdfPaths(e);
        if (paths.Count == 0)
        {
            _vm.Status = "Only PDF files can be opened";
            return;
        }

        foreach (var path in paths)
            await _vm.OpenPathAsync(path);
    }

    private static List<string> DroppedPdfPaths(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .OfType<string>()
            .Where(path => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.PickPdfAsync = null;
            _vm.RequestClose = null;
            _vm.RequestActivate = null;
            _vm.RequestPasswordAsync = null;
            _vm.ShowErrorAsync = null;
            _vm.PickSavePathAsync = null;
            _vm.ConfirmSaveChangesAsync = null;
            _vm.PrintDocumentAsync = null;
            _vm.ShowSettingsAsync = null;
            _vm.ShowSplitAsync = null;
            _vm.ShowMergeAsync = null;
            _vm.ConfirmAsync = null;
            _vm.ShowAboutAsync = null;
            _vm.AdvancedSearch.PickFolderAsync = null;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is null)
            return;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.PickPdfAsync = PickPdfAsync;
        _vm.RequestClose = Close;
        _vm.RequestActivate = ActivateWindow;
        _vm.RequestPasswordAsync = (fileName, error) => new PasswordDialog(fileName, error).ShowDialog<string?>(this);
        _vm.ShowErrorAsync = (title, message) => new MessageDialog(title, message).ShowDialog(this);
        _vm.PickSavePathAsync = PickSavePathAsync;
        _vm.ConfirmSaveChangesAsync = async fileName =>
            await new SaveChangesDialog(fileName).ShowDialog<SaveChangesChoice?>(this) ?? SaveChangesChoice.Cancel;
        _vm.PrintDocumentAsync = PrintDocumentAsync;
        _vm.ShowSettingsAsync = () => new SettingsDialog(_vm).ShowDialog(this);
        _vm.ShowSplitAsync = document => new SplitDialog(document).ShowDialog<IReadOnlyList<SplitOutput>?>(this);
        _vm.ShowMergeAsync = files => new MergeDialog(files).ShowDialog<IReadOnlyList<string>?>(this);
        _vm.ConfirmAsync = (title, message, yes) => new ConfirmDialog(title, message, yes).ShowDialog<bool>(this);
        _vm.ShowAboutAsync = () => new AboutDialog(_vm.Version).ShowDialog(this);
        _vm.AdvancedSearch.PickFolderAsync = PickFolderAsync;
    }

    private void ActivateWindow()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsFullScreen) || _vm is null)
            return;

        if (_vm.IsFullScreen)
        {
            if (WindowState != WindowState.FullScreen)
                _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullScreen;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _vm?.ExitPresentation() == true)
            e.Handled = true;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _vm is null)
            return;

        e.Cancel = true;
        if (!await _vm.ConfirmCloseAllAsync())
            return;

        _closeConfirmed = true;
        Dispatcher.UIThread.Post(Close);
    }

    private async Task PrintDocumentAsync(DocumentViewModel document)
    {
        if (_vm is null)
            return;

        if (_isPrinting)
        {
            _vm.Status = "A print job is already being sent";
            return;
        }

        var service = PrintServices.ForCurrentPlatform();
        var printers = await Task.Run(service.GetPrinters);
        if (printers.Count == 0)
        {
            await new MessageDialog("No printers found",
                "No printers are installed. Add a printer in your system settings and try again.").ShowDialog(this);
            return;
        }

        var defaultPrinter = await Task.Run(service.GetDefaultPrinter);
        var job = await new PrintDialog(document.FileName, printers, defaultPrinter, document.PageCount, document.CurrentPageIndex)
            .ShowDialog<PrintJob?>(this);
        if (job is null)
        {
            _vm.Status = "Print cancelled";
            return;
        }

        _isPrinting = true;
        var progress = new Progress<string>(status => _vm.Status = status);
        try
        {
            await service.PrintAsync(document.Session, document.FileName, job, progress, CancellationToken.None);
            _vm.Status = $"Sent {job.PageIndices.Count} page{(job.PageIndices.Count == 1 ? "" : "s")} to {job.PrinterName}";
        }
        catch (Exception ex)
        {
            _vm.Status = "Printing failed";
            await new MessageDialog("Could not print", ex.Message).ShowDialog(this);
        }
        finally
        {
            _isPrinting = false;
        }
    }

    private async Task<string?> PickSavePathAsync(string currentPath)
    {
        var folder = Path.GetDirectoryName(currentPath);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save PDF as",
            SuggestedFileName = Path.GetFileName(currentPath),
            SuggestedStartLocation = folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(folder),
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("PDF files") { Patterns = ["*.pdf"] }]
        });

        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to search",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private async Task<string?> PickPdfAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF files") { Patterns = ["*.pdf"] },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count == 0)
            return null;

        return files[0].TryGetLocalPath();
    }
}
