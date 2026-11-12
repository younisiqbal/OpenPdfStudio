using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services;

namespace OpenPdfStudio.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void OpenDocumentation() => OpenLink(ExternalLinks.Website, "documentation");

    [RelayCommand]
    private void ReportIssue() => OpenLink(ExternalLinks.NewIssue, "issue tracker");

    [RelayCommand]
    private Task ShowAbout() => ShowAboutAsync?.Invoke() ?? Task.CompletedTask;

    private void OpenLink(string url, string name) =>
        Status = ExternalLinks.Open(url) ? $"Opened the {name} in your browser" : $"Could not open a browser. Visit {url}";
}
