using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.PickPdfAsync = null;
            _vm.RequestClose = null;
            _vm.RequestPasswordAsync = null;
            _vm.ShowErrorAsync = null;
            _vm.PickSavePathAsync = null;
            _vm.ConfirmSaveChangesAsync = null;
            _vm.PrintDocumentAsync = null;
            _vm.AdvancedSearch.PickFolderAsync = null;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is null)
            return;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.PickPdfAsync = PickPdfAsync;
        _vm.RequestClose = Close;
        _vm.RequestPasswordAsync = (fileName, error) => new PasswordDialog(fileName, error).ShowDialog<string?>(this);
        _vm.ShowErrorAsync = (title, message) => new MessageDialog(title, message).ShowDialog(this);
        _vm.PickSavePathAsync = PickSavePathAsync;
        _vm.ConfirmSaveChangesAsync = async fileName =>
            await new SaveChangesDialog(fileName).ShowDialog<SaveChangesChoice?>(this) ?? SaveChangesChoice.Cancel;
        _vm.PrintDocumentAsync = PrintDocumentAsync;
        _vm.AdvancedSearch.PickFolderAsync = PickFolderAsync;
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
