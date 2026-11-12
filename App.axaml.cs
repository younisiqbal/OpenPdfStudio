using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Pdf;
using OpenPdfStudio.ViewModels;
using OpenPdfStudio.Views;

namespace OpenPdfStudio;

public partial class App : Application
{
    internal static SingleInstance? SingleInstance { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
        QuestPDF.Settings.UseSystemFonts = true;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            if (SingleInstance is { } instance)
            {
                instance.Received += payload => Dispatcher.UIThread.Post(() =>
                {
                    viewModel.RequestActivate?.Invoke();
                    if (payload != SingleInstance.ActivateToken)
                        _ = viewModel.OpenPathAsync(payload);
                });
            }

            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) => OnActivated(viewModel, e);

            foreach (var path in PdfLaunch.PathsFromArgs(desktop.Args))
                Dispatcher.UIThread.Post(() => _ = OpenFromLaunchAsync(viewModel, path));

            desktop.Exit += (_, _) =>
            {
                viewModel.DisposeAll();
                PdfiumWorker.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnActivated(MainViewModel viewModel, ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs files)
            return;

        foreach (var file in files.Files)
        {
            var path = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path))
                Dispatcher.UIThread.Post(() => _ = OpenFromLaunchAsync(viewModel, path));
        }
    }

    private static async System.Threading.Tasks.Task OpenFromLaunchAsync(MainViewModel viewModel, string path)
    {
        viewModel.RequestActivate?.Invoke();
        await viewModel.OpenPathAsync(path);
    }
}
