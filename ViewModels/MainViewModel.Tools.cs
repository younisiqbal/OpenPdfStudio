using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Pdf;

namespace OpenPdfStudio.ViewModels;

/// <summary>Page tools: organize, split, and merge.</summary>
public partial class MainViewModel
{
    [RelayCommand]
    private async Task OrganizePages()
    {
        if (ShowOrganizeAsync is null || await EnsureActiveDocumentAsync() is not { } document)
            return;

        if (await ShowOrganizeAsync(document) is not { } pages)
        {
            Status = "Organize cancelled";
            return;
        }

        try
        {
            await document.Session.ArrangePagesAsync(pages);
        }
        catch (Exception ex)
        {
            Status = "Could not change the pages";
            await ShowError("Could not organize pages", ex.Message);
            return;
        }

        var oldCount = document.PageCount;
        var newIndexOf = pages.Select((p, i) => (p.SourceIndex, i)).ToDictionary(x => x.SourceIndex, x => x.i);
        UserBookmarks.Set(document.FilePath, UserBookmarks.For(document.FilePath)
            .Where(b => newIndexOf.ContainsKey(b.PageIndex))
            .Select(b => b with { PageIndex = newIndexOf[b.PageIndex] }));

        // Every page index the old view holds is stale now, so it is replaced by a fresh view of the same PDF.
        var replacement = CreateDocument(document.Session);
        replacement.IsModified = true;
        var index = Documents.IndexOf(document);
        if (index >= 0)
            Documents[index] = replacement;
        else
            Documents.Add(replacement);
        ActivateDocument(replacement);
        document.Detach();

        var deleted = oldCount - pages.Count;
        Status = deleted > 0
            ? $"Pages updated: {deleted} deleted, {pages.Count} left. Save to keep the changes."
            : "Pages updated. Save to keep the changes.";
    }

    [RelayCommand]
    private async Task SplitPdf()
    {
        if (ShowSplitAsync is null || await EnsureActiveDocumentAsync() is not { } document)
            return;

        if (await ShowSplitAsync(document) is not { Count: > 0 } outputs)
        {
            Status = "Split cancelled";
            return;
        }

        try
        {
            for (var i = 0; i < outputs.Count; i++)
            {
                Status = $"Splitting... file {i + 1} of {outputs.Count}";
                await document.Session.ExportPagesAsync(outputs[i].PageIndices, outputs[i].Path);
            }
        }
        catch (Exception ex)
        {
            Status = "Split failed";
            await ShowError("Could not split the PDF", ex.Message);
            return;
        }

        var folder = Path.GetDirectoryName(outputs[0].Path);
        Status = $"Split into {outputs.Count} file{(outputs.Count == 1 ? "" : "s")} in {folder}";
        if (outputs.Count == 1 && ConfirmAsync is not null &&
            await ConfirmAsync("Split complete", $"Saved {Path.GetFileName(outputs[0].Path)}. Open it now?", "Open"))
            await OpenPathAsync(outputs[0].Path);
    }

    [RelayCommand]
    private async Task MergePdfs()
    {
        if (ShowMergeAsync is null)
            return;

        IReadOnlyList<string> initial = ActiveDocument is { } active ? [active.FilePath] : [];
        if (await ShowMergeAsync(initial) is not { Count: >= 2 } files)
        {
            Status = "Merge cancelled";
            return;
        }

        var sessions = new List<PdfDocumentSession>();
        try
        {
            foreach (var file in files)
            {
                Status = $"Opening {Path.GetFileName(file)}...";
                if (await OpenSessionAsync(Path.GetFullPath(file)) is not { } session)
                {
                    Status = "Merge cancelled";
                    return;
                }

                sessions.Add(session);
            }

            var suggested = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(files[0]))!, "Merged.pdf");
            if (PickSavePathAsync is null || await PickSavePathAsync(suggested) is not { Length: > 0 } target)
            {
                Status = "Merge cancelled";
                return;
            }

            if (Documents.Any(d => string.Equals(d.FilePath, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)))
            {
                Status = "Merge cancelled";
                await ShowError("Could not merge", $"\"{Path.GetFileName(target)}\" is open in a tab. Close it first, or pick a different name.");
                return;
            }

            Status = $"Merging {files.Count} files...";
            await PdfDocumentSession.MergeAsync(sessions, target);

            var pageCount = sessions.Sum(s => s.PageCount);
            Status = $"Merged {files.Count} files ({pageCount} pages) into {Path.GetFileName(target)}";
            if (ConfirmAsync is not null &&
                await ConfirmAsync("Merge complete", $"Saved {Path.GetFileName(target)} with {pageCount} pages. Open it now?", "Open"))
                await OpenPathAsync(target);
        }
        catch (Exception ex)
        {
            Status = "Merge failed";
            await ShowError("Could not merge PDFs", ex.Message);
        }
        finally
        {
            foreach (var session in sessions)
                session.Dispose();
        }
    }
}
