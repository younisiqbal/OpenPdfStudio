using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.ViewModels;

/// <summary>Side-by-side PDF compare.</summary>
public partial class MainViewModel
{
    private CompareViewModel? _compare;

    /// <summary>Asks for two PDFs to compare. Argument: optional left path. Returns (left, right) or null.</summary>
    public Func<string?, Task<(string Left, string Right)?>>? ShowComparePickAsync { get; set; }

    public bool IsCompareActive => _compare is not null;

    [RelayCommand]
    private async Task ComparePdfs()
    {
        if (ShowComparePickAsync is null)
            return;

        var initial = ActiveDocument?.FilePath;
        if (await ShowComparePickAsync(initial) is not { } pair)
        {
            Status = "Compare cancelled";
            return;
        }

        await StartCompareAsync(pair.Left, pair.Right);
    }

    public Task StartCompareFromPathsAsync(string leftPath, string rightPath) =>
        StartCompareAsync(leftPath, rightPath);

    private async Task StartCompareAsync(string leftPath, string rightPath)
    {
        leftPath = Path.GetFullPath(leftPath);
        rightPath = Path.GetFullPath(rightPath);
        if (string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase))
        {
            await ShowError("Could not compare", "Choose two different PDF files.");
            return;
        }

        Status = "Opening files to compare...";
        if (await OpenSessionAsync(leftPath) is not { } leftSession)
            return;
        if (await OpenSessionAsync(rightPath) is not { } rightSession)
        {
            leftSession.Dispose();
            return;
        }

        CloseCompare();

        var left = CreateComparePane(leftSession);
        var right = CreateComparePane(rightSession);
        _compare = new CompareViewModel(left, right, CloseCompare, path => _ = OpenPathAsync(path));

        if (ActiveDocument is not null)
            ActiveDocument = null;
        else
            CurrentPage = _compare;

        OnPropertyChanged(nameof(IsCompareActive));
        Status = $"Comparing {left.FileName} and {right.FileName}";
    }

    private DocumentViewModel CreateComparePane(PdfDocumentSession session) =>
        new(session, s => Status = s, _ => { }, _ => { });

    private void CloseCompare()
    {
        if (_compare is null)
            return;

        var closing = _compare;
        _compare = null;
        OnPropertyChanged(nameof(IsCompareActive));

        if (ReferenceEquals(CurrentPage, closing))
            CurrentPage = ActiveDocument as ViewModelBase ?? Home;

        closing.Dispose();
        Status = "Compare closed";
    }
}
