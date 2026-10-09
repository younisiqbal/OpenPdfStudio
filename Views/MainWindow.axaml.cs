using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using OpenPdfStudio.ViewModels;
using OpenPdfStudio.Views.Dialogs;

namespace OpenPdfStudio.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PickPdfAsync = null;
            _vm.RequestClose = null;
            _vm.RequestPasswordAsync = null;
            _vm.ShowErrorAsync = null;
        }

        _vm = DataContext as MainViewModel;
        if (_vm is null)
            return;

        _vm.PickPdfAsync = PickPdfAsync;
        _vm.RequestClose = Close;
        _vm.RequestPasswordAsync = (fileName, error) => new PasswordDialog(fileName, error).ShowDialog<string?>(this);
        _vm.ShowErrorAsync = (title, message) => new MessageDialog(title, message).ShowDialog(this);
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
