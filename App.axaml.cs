using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenPdfStudio.Services.Pdf;
using OpenPdfStudio.ViewModels;
using OpenPdfStudio.Views;

namespace OpenPdfStudio;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.Exit += (_, _) =>
            {
                viewModel.DisposeAll();
                PdfiumWorker.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}