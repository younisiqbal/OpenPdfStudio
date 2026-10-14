using System;
using System.Collections.Generic;
using System.Linq;
using OpenPdfStudio.Models;

namespace OpenPdfStudio.ViewModels;

/// <summary>One annotation before and after a change. Before is null for an add, After is null for a delete.</summary>
public sealed record AnnotationChange(PdfAnnotation? Before, PdfAnnotation? After)
{
    public string Id => After?.Id ?? Before!.Id;
}

/// <summary>Undo and redo history for annotation edits in one document.</summary>
public sealed class AnnotationUndoStack
{
    private const int MaxEntries = 200;

    private sealed record Entry(IReadOnlyList<AnnotationChange> Changes, string? MergeKey, DateTime Time);

    private readonly List<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Records a change. When <paramref name="mergeKey"/> matches the last entry and it is newer than
    /// <paramref name="mergeWindow"/>, the two become one step (a slider drag, or typing into a note just added).
    /// </summary>
    public void Push(IReadOnlyList<AnnotationChange> changes, string? mergeKey = null, TimeSpan? mergeWindow = null)
    {
        if (changes.Count == 0)
            return;

        var now = DateTime.UtcNow;
        _redo.Clear();

        if (mergeKey is not null && _undo.Count > 0 && _undo[^1] is { } last && last.MergeKey == mergeKey &&
            (mergeWindow is null || now - last.Time <= mergeWindow))
        {
            _undo[^1] = new Entry(Merge(last.Changes, changes), mergeKey, now);
            if (_undo[^1].Changes.Count == 0)
                _undo.RemoveAt(_undo.Count - 1);
        }
        else
        {
            _undo.Add(new Entry(changes, mergeKey, now));
            if (_undo.Count > MaxEntries)
                _undo.RemoveAt(0);
        }

        Changed?.Invoke();
    }

    public IReadOnlyList<AnnotationChange>? PopUndo()
    {
        if (_undo.Count == 0)
            return null;

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(entry with { MergeKey = null });
        Changed?.Invoke();
        return entry.Changes;
    }

    public IReadOnlyList<AnnotationChange>? PopRedo()
    {
        if (_redo.Count == 0)
            return null;

        var entry = _redo.Pop();
        _undo.Add(entry);
        Changed?.Invoke();
        return entry.Changes;
    }

    private static List<AnnotationChange> Merge(IReadOnlyList<AnnotationChange> first, IReadOnlyList<AnnotationChange> second)
    {
        var merged = first.ToList();
        foreach (var change in second)
        {
            var index = merged.FindIndex(c => c.Id == change.Id);
            if (index < 0)
                merged.Add(change);
            else
                merged[index] = merged[index] with { After = change.After };
        }

        merged.RemoveAll(c => c.Before is null && c.After is null);
        return merged;
    }
}
