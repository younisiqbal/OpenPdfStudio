using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Convert;

namespace OpenPdfStudio.ViewModels;

/// <summary>The kinds of file the save picker can ask for.</summary>
public enum SaveFileType
{
    Pdf,
    Word
}

/// <summary>Converting between PDF and Word.</summary>
public partial class MainViewModel
{
    private bool _isConverting;

    /// <summary>Asks for a Word document to convert. Returns null on cancel.</summary>
    public Func<Task<string?>>? PickWordAsync { get; set; }

    public static bool IsWordPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm" or ".doc";

    [RelayCommand]
    private async Task ConvertToWord()
    {
        if (_isConverting || PickSavePathAsync is null || await EnsureActiveDocumentAsync() is not { } document)
            return;

        var suggested = Path.ChangeExtension(document.FilePath, ".docx");
        if (await PickSavePathAsync(suggested, SaveFileType.Word) is not { Length: > 0 } picked)
        {
            Status = "Conversion cancelled";
            return;
        }

        var target = Path.GetFullPath(picked);
        var name = Path.GetFileName(target);
        _isConverting = true;
        try
        {
            var pageCount = document.Session.PageCount;
            var pages = new List<PdfFlowPage>(pageCount);
            for (var i = 0; i < pageCount; i++)
            {
                Status = $"Converting page {i + 1} of {pageCount}...";
                pages.Add(await document.Session.ExtractFlowPageAsync(i));
            }

            Status = $"Writing {name}...";
            await Task.Run(() => DocxWriter.Write(target, pages));
        }
        catch (IOException ex)
        {
            Status = "Conversion failed";
            await ShowError("Could not convert to Word",
                $"{name} could not be written. If it is open in Word, close it and try again.\n\n{ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            Status = "Conversion failed";
            await ShowError("Could not convert to Word", ex.Message);
            return;
        }
        finally
        {
            _isConverting = false;
        }

        Status = $"Converted {document.FileName} to {name}";
        if (ConfirmAsync is not null &&
            await ConfirmAsync("Conversion complete", $"Saved {name}. Open it now?", "Open") &&
            !ExternalLinks.Open(target))
        {
            Status = $"Saved {name}, but no app is installed to open Word documents";
        }
    }

    [RelayCommand]
    private async Task ConvertWordToPdf()
    {
        if (_isConverting || PickWordAsync is null)
            return;

        if (await PickWordAsync() is not { Length: > 0 } path)
        {
            Status = "Conversion cancelled";
            return;
        }

        await ConvertWordFileAsync(path);
    }

    /// <summary>Called when Word documents are dropped on the window: offers to convert each one.</summary>
    public async Task OfferWordConversionAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            if (ConfirmAsync is null ||
                !await ConfirmAsync("Convert Word document", $"Convert {name} to PDF?", "Convert"))
            {
                Status = "Conversion cancelled";
                continue;
            }

            await ConvertWordFileAsync(path);
        }
    }

    private async Task ConvertWordFileAsync(string path)
    {
        if (_isConverting || PickSavePathAsync is null)
            return;

        var source = Path.GetFullPath(path);
        var sourceName = Path.GetFileName(source);
        if (Path.GetExtension(source).Equals(".doc", StringComparison.OrdinalIgnoreCase))
        {
            await ShowError("Could not convert to PDF",
                $"{sourceName} is an older Word format. Open it in Word or LibreOffice, save it as .docx, and try again.");
            return;
        }

        if (await PickSavePathAsync(Path.ChangeExtension(source, ".pdf"), SaveFileType.Pdf) is not { Length: > 0 } picked)
        {
            Status = "Conversion cancelled";
            return;
        }

        var target = Path.GetFullPath(picked);
        var name = Path.GetFileName(target);
        if (Documents.Any(d => string.Equals(d.FilePath, target, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowError("Could not convert to PDF",
                $"\"{name}\" is open in a tab. Close it first, or pick a different name.");
            return;
        }

        _isConverting = true;
        try
        {
            Status = $"Converting {sourceName} to PDF...";
            await Task.Run(() => WordPdfRenderer.Convert(source, target));
        }
        catch (Exception ex)
        {
            Status = "Conversion failed";
            await ShowError("Could not convert to PDF", $"{sourceName}\n\n{ex.Message}");
            return;
        }
        finally
        {
            _isConverting = false;
        }

        await OpenPathAsync(target);
        Status = $"Converted {sourceName} to {name}";
    }
}
