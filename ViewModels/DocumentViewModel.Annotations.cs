using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenPdfStudio.Models;
using OpenPdfStudio.Services;
using OpenPdfStudio.Services.Pdf.Annotations;

namespace OpenPdfStudio.ViewModels;

public partial class DocumentViewModel
{
    public const double NoteSize = 20;
    public const double DefaultTextboxWidth = 160;
    public const double DefaultTextboxHeight = 48;
    private static readonly TimeSpan NewAnnotationMergeWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StyleMergeWindow = TimeSpan.FromMilliseconds(400);

    private readonly AnnotationUndoStack _undo = new();
    private readonly List<AnnotationViewModel> _selected = new();
    private bool _applyingHistory;

    /// <summary>Raised when an in-place text editor should open on a FreeText annotation.</summary>
    public event Action<AnnotationViewModel>? TextEditRequested;

    /// <summary>Raised when the sticky-note popup should open.</summary>
    public event Action<AnnotationViewModel>? NotePopupRequested;

    public FormatPanelViewModel Format { get; private set; } = null!;
    public IReadOnlyList<AnnotationViewModel> SelectedAnnotations => _selected;
    public AnnotationViewModel? PrimarySelection => _selected.Count > 0 ? _selected[^1] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNoteTool), nameof(IsTypewriterTool), nameof(IsTextboxTool),
        nameof(IsCalloutTool), nameof(IsHighlightTool), nameof(IsUnderlineTool), nameof(IsStrikeOutTool),
        nameof(IsSquigglyTool), nameof(HasAnnotationTool), nameof(IsMarkupTool))]
    private AnnotationTool _annotationTool = AnnotationTool.None;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedAnnotationsCommand))]
    private bool _hasSelectedAnnotation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCommentChrome), nameof(ShowFormatPanel))]
    private bool _isFormatPanelOpen;

    public bool IsCommentToolbarVisible => ViewerPreferences.ShowCommentToolbar;
    public bool KeepToolSelected => ViewerPreferences.KeepToolSelected;
    public bool ShowCommentChrome => IsCommentToolbarVisible && !IsPresenting;
    public bool ShowFormatPanel => IsFormatPanelOpen && !IsPresenting;

    public bool IsNoteTool => AnnotationTool == AnnotationTool.Note;
    public bool IsTypewriterTool => AnnotationTool == AnnotationTool.Typewriter;
    public bool IsTextboxTool => AnnotationTool == AnnotationTool.Textbox;
    public bool IsCalloutTool => AnnotationTool == AnnotationTool.Callout;
    public bool IsHighlightTool => AnnotationTool == AnnotationTool.Highlight;
    public bool IsUnderlineTool => AnnotationTool == AnnotationTool.Underline;
    public bool IsStrikeOutTool => AnnotationTool == AnnotationTool.StrikeOut;
    public bool IsSquigglyTool => AnnotationTool == AnnotationTool.Squiggly;
    public bool HasAnnotationTool => AnnotationTool != AnnotationTool.None;
    public bool IsMarkupTool => AnnotationTool.IsMarkup();
    public bool IsSelectAnnotationTool => Tool == ViewerTool.SelectAnnotation;
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    private void InitAnnotations()
    {
        Format = new FormatPanelViewModel(this);
        _undo.Changed += OnUndoStackChanged;
    }

    private void OnUndoStackChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleCommentToolbar()
    {
        ViewerPreferences.ShowCommentToolbar = !ViewerPreferences.ShowCommentToolbar;
        if (!IsCommentToolbarVisible)
        {
            AnnotationTool = AnnotationTool.None;
            IsFormatPanelOpen = false;
        }
    }

    [RelayCommand]
    private void ToggleKeepToolSelected() =>
        ViewerPreferences.KeepToolSelected = !ViewerPreferences.KeepToolSelected;

    [RelayCommand]
    private void OpenFormatPanel() => IsFormatPanelOpen = true;

    [RelayCommand]
    private void CloseFormatPanel() => IsFormatPanelOpen = false;

    [RelayCommand]
    private void UseSelectAnnotationTool()
    {
        AnnotationTool = AnnotationTool.None;
        Tool = ViewerTool.SelectAnnotation;
        ClearSelection();
    }

    [RelayCommand]
    private async Task UseAnnotationTool(AnnotationTool tool)
    {
        ViewerPreferences.ShowCommentToolbar = true;
        AnnotationTool = tool;
        IsFormatPanelOpen = true;
        ClearSelection();
        ClearAnnotationSelection();
        Format.Sync();

        if (tool.IsMarkup() && HasSelection)
            await MarkupSelection(tool.Kind()!.Value.ToString());
    }

    partial void OnAnnotationToolChanged(AnnotationTool value)
    {
        if (value != AnnotationTool.None)
            Tool = ViewerTool.Hand;
        Format.Sync();
    }

    public void FinishAnnotationTool()
    {
        if (!KeepToolSelected)
            AnnotationTool = AnnotationTool.None;
    }

    public AnnotationViewModel? HitTestAnnotation(int pageIndex, Point point)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            return null;

        foreach (var annotation in Pages[pageIndex].Annotations.Reverse())
        {
            if (annotation.IsEditing)
                continue;
            if (HitBounds(annotation.Model, point))
                return annotation;
        }

        return null;
    }

    public static bool HitBounds(PdfAnnotation annotation, Point point)
    {
        if (annotation.IsMarkup)
            return annotation.Quads.Any(q => q.Inflate(1).Contains(point));
        if (annotation.Kind == AnnotationKind.Callout && annotation.CalloutTarget is { } target)
        {
            if (new Rect(target.X - 5, target.Y - 5, 10, 10).Contains(point))
                return true;
            if (annotation.Bounds.Inflate(2).Contains(point))
                return true;
            var anchor = AppearanceBuilder.CalloutAnchor(annotation.Bounds, target);
            return DistanceToSegment(point, anchor, target) < 4;
        }

        return annotation.Bounds.Inflate(annotation.Kind == AnnotationKind.Note ? 2 : 1).Contains(point);
    }

    public static AnnotationHandle HitHandle(PdfAnnotation annotation, Point point, double handleSize)
    {
        if (annotation.Kind == AnnotationKind.Callout && annotation.CalloutTarget is { } target &&
            new Rect(target.X - handleSize, target.Y - handleSize, handleSize * 2, handleSize * 2).Contains(point))
            return AnnotationHandle.CalloutTarget;

        if (annotation.Kind is AnnotationKind.Note || annotation.IsMarkup)
            return AnnotationHandle.None;

        var b = annotation.Bounds;
        var h = handleSize;
        (AnnotationHandle Handle, Point At)[] handles =
        [
            (AnnotationHandle.TopLeft, b.TopLeft),
            (AnnotationHandle.Top, new Point(b.Center.X, b.Top)),
            (AnnotationHandle.TopRight, b.TopRight),
            (AnnotationHandle.Right, new Point(b.Right, b.Center.Y)),
            (AnnotationHandle.BottomRight, b.BottomRight),
            (AnnotationHandle.Bottom, new Point(b.Center.X, b.Bottom)),
            (AnnotationHandle.Left, new Point(b.Left, b.Center.Y)),
            (AnnotationHandle.BottomLeft, b.BottomLeft)
        ];

        foreach (var (handle, at) in handles)
        {
            if (new Rect(at.X - h, at.Y - h, h * 2, h * 2).Contains(point))
                return handle;
        }

        return AnnotationHandle.None;
    }

    public void SelectAnnotation(AnnotationViewModel annotation, bool add)
    {
        if (!add)
            ClearAnnotationSelection();

        if (!_selected.Contains(annotation))
        {
            annotation.IsSelected = true;
            _selected.Add(annotation);
        }

        HasSelectedAnnotation = _selected.Count > 0;
        IsFormatPanelOpen = true;
        Format.Sync();
    }

    public void ClearAnnotationSelection()
    {
        foreach (var annotation in _selected)
            annotation.IsSelected = false;
        _selected.Clear();
        HasSelectedAnnotation = false;
        Format.Sync();
    }

    public async Task EnsureAnnotationsAsync(PdfPageViewModel page)
    {
        if (page.AnnotationsLoaded || _disposed)
            return;

        page.AnnotationsLoaded = true;
        try
        {
            var list = await _session.GetAnnotationsAsync(page.Index);
            if (_disposed)
                return;

            page.Annotations.Clear();
            foreach (var model in list)
                page.Annotations.Add(new AnnotationViewModel(model));
        }
        catch (Exception)
        {
            page.AnnotationsLoaded = false;
        }
    }

    public async Task<AnnotationViewModel?> AddAnnotationAsync(PdfAnnotation annotation, bool beginEdit = false)
    {
        try
        {
            var saved = await _session.AddAnnotationAsync(annotation);
            var view = new AnnotationViewModel(saved) { IsNew = beginEdit };
            Pages[saved.PageIndex].Annotations.Add(view);
            PushHistory([new AnnotationChange(null, saved)], saved.Id);
            SelectAnnotation(view, add: false);
            RefreshPage(Pages[saved.PageIndex]);
            _setStatus($"{saved.Kind.DisplayName()} added");
            if (beginEdit)
                await BeginEditAsync(view);
            FinishAnnotationTool();
            return view;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (Exception)
        {
            _setStatus($"Could not add the {annotation.Kind.DisplayName().ToLowerInvariant()}");
            return null;
        }
    }

    public PdfAnnotation CreateNote(int pageIndex, Point topLeft) => new()
    {
        Id = PdfAnnotation.NewId(),
        PageIndex = pageIndex,
        Kind = AnnotationKind.Note,
        Bounds = new Rect(topLeft, new Size(NoteSize, NoteSize)),
        Author = Environment.UserName,
        Style = ViewerPreferences.GetAnnotationStyle(AnnotationKind.Note)
    };

    public PdfAnnotation CreateTypewriter(int pageIndex, Point topLeft) => new()
    {
        Id = PdfAnnotation.NewId(),
        PageIndex = pageIndex,
        Kind = AnnotationKind.Typewriter,
        Bounds = new Rect(topLeft, new Size(AppearanceBuilder.MinBoxWidth, AppearanceBuilder.LineHeight(ViewerPreferences.GetAnnotationStyle(AnnotationKind.Typewriter)) + AppearanceBuilder.TypewriterPadding * 2)),
        Author = Environment.UserName,
        Style = ViewerPreferences.GetAnnotationStyle(AnnotationKind.Typewriter)
    };

    public PdfAnnotation CreateTextbox(int pageIndex, Rect bounds, AnnotationKind kind, Point? target = null) => new()
    {
        Id = PdfAnnotation.NewId(),
        PageIndex = pageIndex,
        Kind = kind,
        Bounds = NormalizeBox(bounds),
        CalloutTarget = target,
        Author = Environment.UserName,
        Style = ViewerPreferences.GetAnnotationStyle(kind)
    };

    [RelayCommand]
    public async Task MarkupSelection(string? kindName)
    {
        if (!Enum.TryParse(kindName, out AnnotationKind kind) ||
            kind is not (AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut or AnnotationKind.Squiggly))
            return;

        if (SelectionPageIndex < 0 || Pages[SelectionPageIndex].SelectionRects is not { Count: > 0 } rects)
        {
            AnnotationTool = kind switch
            {
                AnnotationKind.Underline => AnnotationTool.Underline,
                AnnotationKind.StrikeOut => AnnotationTool.StrikeOut,
                AnnotationKind.Squiggly => AnnotationTool.Squiggly,
                _ => AnnotationTool.Highlight
            };
            ViewerPreferences.ShowCommentToolbar = true;
            IsFormatPanelOpen = true;
            Format.Sync();
            _setStatus("Drag across the text to mark it");
            return;
        }

        var union = rects[0];
        foreach (var rect in rects)
            union = union.Union(rect);

        await AddAnnotationAsync(new PdfAnnotation
        {
            Id = PdfAnnotation.NewId(),
            PageIndex = SelectionPageIndex,
            Kind = kind,
            Bounds = union,
            Quads = rects.ToList(),
            Style = ViewerPreferences.GetAnnotationStyle(kind)
        });
        ClearSelection();
    }

    public async Task BeginEditAsync(AnnotationViewModel annotation)
    {
        if (annotation.IsEditing)
            return;

        SelectAnnotation(annotation, add: false);
        annotation.IsEditing = true;
        try
        {
            await _session.SetAnnotationHiddenAsync(annotation.PageIndex, annotation.Id, true);
            RefreshPage(Pages[annotation.PageIndex]);
        }
        catch (Exception)
        {
            annotation.IsEditing = false;
            return;
        }

        if (annotation.Kind == AnnotationKind.Note)
            NotePopupRequested?.Invoke(annotation);
        else if (annotation.Model.IsFreeText)
            TextEditRequested?.Invoke(annotation);
    }

    public async Task CommitEditAsync(AnnotationViewModel annotation, string contents)
    {
        if (!annotation.IsEditing)
            return;

        if (annotation.IsNew && string.IsNullOrWhiteSpace(contents))
        {
            await CancelEditAsync(annotation, deleteIfNew: true);
            return;
        }

        var before = annotation.Model;
        var after = before with { Contents = contents, Modified = DateTimeOffset.Now };
        annotation.IsEditing = false;
        annotation.IsNew = false;
        await WriteUpdateAsync(annotation, before, after, mergeKey: before.Id);
        try
        {
            await _session.SetAnnotationHiddenAsync(annotation.PageIndex, annotation.Id, false);
        }
        catch (Exception) { }

        RefreshPage(Pages[annotation.PageIndex]);
    }

    public async Task CancelEditAsync(AnnotationViewModel annotation, bool deleteIfNew)
    {
        if (!annotation.IsEditing)
            return;

        annotation.IsEditing = false;
        if (deleteIfNew && annotation.IsNew)
        {
            await RemoveAnnotationCoreAsync(annotation, record: true);
            return;
        }

        try
        {
            await _session.SetAnnotationHiddenAsync(annotation.PageIndex, annotation.Id, false);
        }
        catch (Exception) { }

        RefreshPage(Pages[annotation.PageIndex]);
    }

    public async Task MoveOrResizeAsync(AnnotationViewModel annotation, PdfAnnotation after)
    {
        var before = annotation.Model;
        if (before == after)
            return;

        await WriteUpdateAsync(annotation, before, after);
    }

    public async Task MoveSelectedAsync(Vector delta)
    {
        if (_selected.Count == 0 || (delta.X == 0 && delta.Y == 0))
            return;

        var changes = new List<AnnotationChange>();
        foreach (var annotation in _selected.ToList())
        {
            var before = annotation.Model;
            var after = Offset(before, delta);
            changes.Add(new AnnotationChange(before, after));
            annotation.Model = after;
        }

        await CommitUpdatesAsync(changes);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAnnotation))]
    private async Task DeleteSelectedAnnotations()
    {
        var selected = _selected.ToList();
        ClearAnnotationSelection();
        var changes = new List<AnnotationChange>();
        foreach (var annotation in selected)
        {
            try
            {
                await _session.RemoveAnnotationAsync(annotation.PageIndex, annotation.Id);
                Pages[annotation.PageIndex].Annotations.Remove(annotation);
                changes.Add(new AnnotationChange(annotation.Model, null));
                RefreshPage(Pages[annotation.PageIndex]);
            }
            catch (Exception)
            {
                _setStatus("Could not delete the annotation");
            }
        }

        if (changes.Count > 0)
        {
            PushHistory(changes);
            _setStatus(changes.Count == 1 ? "Annotation deleted" : $"{changes.Count} annotations deleted");
        }
    }

    public async Task PatchStyleAsync(Func<AnnotationStyle, AnnotationStyle> patch, string? mergeKey = null)
    {
        if (_selected.Count == 0)
        {
            var kind = AnnotationTool.Kind();
            if (kind is { } k)
            {
                ViewerPreferences.SetAnnotationStyle(k, patch(ViewerPreferences.GetAnnotationStyle(k)));
                Format.Sync();
            }

            return;
        }

        var changes = new List<AnnotationChange>();
        foreach (var annotation in _selected.ToList())
        {
            var before = annotation.Model;
            var after = before with { Style = patch(before.Style), Modified = DateTimeOffset.Now };
            if (before.Kind == AnnotationKind.Note)
                after = after with { Bounds = new Rect(before.Bounds.TopLeft, new Size(NoteSize, NoteSize)) };
            changes.Add(new AnnotationChange(before, after));
            annotation.Model = after;
        }

        var key = mergeKey is null ? null : mergeKey + ":" + string.Join(',', _selected.Select(a => a.Id));
        await CommitUpdatesAsync(changes, key, StyleMergeWindow);
        Format.Sync();
    }

    public async Task ArrangeSelectedAsync(AnnotationArrange arrange)
    {
        if (_selected.Count == 0)
            return;

        var items = _selected.Select(a => a.Model).ToList();
        var page = Pages[items[0].PageIndex];
        var pageSize = new Size(page.WidthPoints, page.HeightPoints);
        IReadOnlyList<Rect> next = arrange switch
        {
            AnnotationArrange.PageCenter when items.Count == 1 =>
                [Centered(items[0].Bounds, pageSize)],
            AnnotationArrange.Left => AlignX(items, items.Min(i => i.Bounds.X)),
            AnnotationArrange.Right => AlignX(items, right: items.Max(i => i.Bounds.Right)),
            AnnotationArrange.Center => AlignX(items, center: items.Average(i => i.Bounds.Center.X)),
            AnnotationArrange.Top => AlignY(items, items.Min(i => i.Bounds.Y)),
            AnnotationArrange.Bottom => AlignY(items, bottom: items.Max(i => i.Bounds.Bottom)),
            AnnotationArrange.Middle => AlignY(items, middle: items.Average(i => i.Bounds.Center.Y)),
            AnnotationArrange.DistributeH => Distribute(items, horizontal: true),
            AnnotationArrange.DistributeV => Distribute(items, horizontal: false),
            _ => items.Select(i => i.Bounds).ToList()
        };

        var changes = new List<AnnotationChange>();
        for (int i = 0; i < items.Count; i++)
        {
            var before = items[i];
            var bounds = next[i];
            if (bounds == before.Bounds)
                continue;
            var delta = bounds.TopLeft - before.Bounds.TopLeft;
            var after = before with
            {
                Bounds = bounds,
                CalloutTarget = before.CalloutTarget is { } t ? t + delta : null,
                Modified = DateTimeOffset.Now
            };
            changes.Add(new AnnotationChange(before, after));
        }

        foreach (var change in changes)
        {
            if (FindAnnotation(change.Id) is { } view)
                view.Model = change.After!;
        }

        await CommitUpdatesAsync(changes);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private Task Undo() => ApplyHistoryAsync(_undo.PopUndo(), reverse: true);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private Task Redo() => ApplyHistoryAsync(_undo.PopRedo(), reverse: false);

    private async Task ApplyHistoryAsync(IReadOnlyList<AnnotationChange>? changes, bool reverse)
    {
        if (changes is null || changes.Count == 0)
            return;

        _applyingHistory = true;
        try
        {
            var ordered = reverse ? changes.Reverse() : changes;
            foreach (var change in ordered)
            {
                var to = reverse ? change.Before : change.After;
                var from = reverse ? change.After : change.Before;
                if (to is null && from is not null)
                    await RemoveByIdAsync(from);
                else if (from is null && to is not null)
                    await ReaddAsync(to);
                else if (to is not null)
                    await RewriteAsync(to);
            }
        }
        catch (Exception)
        {
            _setStatus("Could not undo that change");
        }
        finally
        {
            _applyingHistory = false;
            Format.Sync();
        }
    }

    private async Task ReaddAsync(PdfAnnotation annotation)
    {
        var saved = await _session.AddAnnotationAsync(annotation);
        Pages[saved.PageIndex].Annotations.Add(new AnnotationViewModel(saved));
        RefreshPage(Pages[saved.PageIndex]);
    }

    private async Task RemoveByIdAsync(PdfAnnotation annotation)
    {
        await _session.RemoveAnnotationAsync(annotation.PageIndex, annotation.Id);
        if (FindAnnotation(annotation.Id) is { } view)
        {
            view.IsSelected = false;
            _selected.Remove(view);
            Pages[annotation.PageIndex].Annotations.Remove(view);
        }

        HasSelectedAnnotation = _selected.Count > 0;
        RefreshPage(Pages[annotation.PageIndex]);
    }

    private async Task RewriteAsync(PdfAnnotation annotation)
    {
        var saved = await _session.UpdateAnnotationAsync(annotation);
        if (FindAnnotation(saved.Id) is { } view)
            view.Model = saved;
        RefreshPage(Pages[saved.PageIndex]);
    }

    private async Task WriteUpdateAsync(AnnotationViewModel annotation, PdfAnnotation before, PdfAnnotation after, string? mergeKey = null)
    {
        annotation.Model = after;
        await CommitUpdatesAsync([new AnnotationChange(before, after)], mergeKey, mergeKey is null ? null : NewAnnotationMergeWindow);
    }

    private async Task CommitUpdatesAsync(List<AnnotationChange> changes, string? mergeKey = null, TimeSpan? mergeWindow = null)
    {
        if (changes.Count == 0)
            return;

        try
        {
            foreach (var change in changes)
            {
                if (change.After is null)
                    continue;
                var saved = await _session.UpdateAnnotationAsync(change.After);
                if (FindAnnotation(saved.Id) is { } view)
                    view.Model = saved;
                RefreshPage(Pages[saved.PageIndex]);
            }

            PushHistory(changes, mergeKey, mergeWindow);
        }
        catch (Exception)
        {
            _setStatus("Could not update the annotation");
        }
    }

    private async Task RemoveAnnotationCoreAsync(AnnotationViewModel annotation, bool record)
    {
        try
        {
            await _session.RemoveAnnotationAsync(annotation.PageIndex, annotation.Id);
            annotation.IsSelected = false;
            _selected.Remove(annotation);
            HasSelectedAnnotation = _selected.Count > 0;
            Pages[annotation.PageIndex].Annotations.Remove(annotation);
            if (record)
                PushHistory([new AnnotationChange(annotation.Model, null)], annotation.Id);
            RefreshPage(Pages[annotation.PageIndex]);
        }
        catch (Exception)
        {
            _setStatus("Could not remove the annotation");
        }
    }

    private void PushHistory(IReadOnlyList<AnnotationChange> changes, string? mergeKey = null, TimeSpan? mergeWindow = null)
    {
        if (_applyingHistory)
            return;

        _undo.Push(changes, mergeKey, mergeWindow);
        IsModified = true;
    }

    private AnnotationViewModel? FindAnnotation(string id)
    {
        foreach (var page in Pages)
        {
            foreach (var annotation in page.Annotations)
            {
                if (annotation.Id == id)
                    return annotation;
            }
        }

        return null;
    }

    public static PdfAnnotation Offset(PdfAnnotation annotation, Vector delta)
    {
        var quads = annotation.Quads.Count == 0 ? annotation.Quads : annotation.Quads.Select(q => new Rect(q.TopLeft + delta, q.Size)).ToList();
        return annotation with
        {
            Bounds = new Rect(annotation.Bounds.TopLeft + delta, annotation.Bounds.Size),
            Quads = quads,
            CalloutTarget = annotation.CalloutTarget is { } t ? t + delta : null,
            Modified = DateTimeOffset.Now
        };
    }

    public static PdfAnnotation Resize(PdfAnnotation annotation, AnnotationHandle handle, Point start, Point current)
    {
        var delta = current - start;
        var b = annotation.Bounds;
        var left = b.Left;
        var top = b.Top;
        var right = b.Right;
        var bottom = b.Bottom;
        if (handle is AnnotationHandle.Left or AnnotationHandle.TopLeft or AnnotationHandle.BottomLeft)
            left += delta.X;
        if (handle is AnnotationHandle.Right or AnnotationHandle.TopRight or AnnotationHandle.BottomRight)
            right += delta.X;
        if (handle is AnnotationHandle.Top or AnnotationHandle.TopLeft or AnnotationHandle.TopRight)
            top += delta.Y;
        if (handle is AnnotationHandle.Bottom or AnnotationHandle.BottomLeft or AnnotationHandle.BottomRight)
            bottom += delta.Y;

        var bounds = NormalizeBox(new Rect(new Point(left, top), new Point(right, bottom)));
        if (handle == AnnotationHandle.CalloutTarget)
            return annotation with { CalloutTarget = current, Modified = DateTimeOffset.Now };

        return annotation with { Bounds = bounds, Modified = DateTimeOffset.Now };
    }

    private static Rect NormalizeBox(Rect bounds)
    {
        var width = Math.Max(AppearanceBuilder.MinBoxWidth, Math.Abs(bounds.Width));
        var height = Math.Max(16, Math.Abs(bounds.Height));
        return new Rect(Math.Min(bounds.X, bounds.X + bounds.Width), Math.Min(bounds.Y, bounds.Y + bounds.Height), width, height);
    }

    private static Rect Centered(Rect bounds, Size page)
    {
        var x = Math.Max(0, (page.Width - bounds.Width) / 2);
        var y = Math.Max(0, (page.Height - bounds.Height) / 2);
        return new Rect(x, y, bounds.Width, bounds.Height);
    }

    private static List<Rect> AlignX(List<PdfAnnotation> items, double? left = null, double? right = null, double? center = null)
    {
        var result = new List<Rect>(items.Count);
        foreach (var item in items)
        {
            var x = left ?? (right is { } r ? r - item.Bounds.Width : center!.Value - item.Bounds.Width / 2);
            result.Add(new Rect(x, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height));
        }

        return result;
    }

    private static List<Rect> AlignY(List<PdfAnnotation> items, double? top = null, double? bottom = null, double? middle = null)
    {
        var result = new List<Rect>(items.Count);
        foreach (var item in items)
        {
            var y = top ?? (bottom is { } b ? b - item.Bounds.Height : middle!.Value - item.Bounds.Height / 2);
            result.Add(new Rect(item.Bounds.X, y, item.Bounds.Width, item.Bounds.Height));
        }

        return result;
    }

    private static List<Rect> Distribute(List<PdfAnnotation> items, bool horizontal)
    {
        var indexed = items.Select((item, i) => (item, i)).ToList();
        indexed.Sort((a, b) => horizontal
            ? a.item.Bounds.X.CompareTo(b.item.Bounds.X)
            : a.item.Bounds.Y.CompareTo(b.item.Bounds.Y));

        var first = horizontal ? indexed[0].item.Bounds.X : indexed[0].item.Bounds.Y;
        var last = horizontal
            ? indexed[^1].item.Bounds.Right
            : indexed[^1].item.Bounds.Bottom;
        var used = indexed.Sum(t => horizontal ? t.item.Bounds.Width : t.item.Bounds.Height);
        var gap = indexed.Count > 1 ? (last - first - used) / (indexed.Count - 1) : 0;
        var pos = first;
        var placed = new Rect[items.Count];
        foreach (var (item, i) in indexed)
        {
            placed[i] = horizontal
                ? new Rect(pos, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height)
                : new Rect(item.Bounds.X, pos, item.Bounds.Width, item.Bounds.Height);
            pos += (horizontal ? item.Bounds.Width : item.Bounds.Height) + gap;
        }

        return placed.ToList();
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = new Vector(b.X - a.X, b.Y - a.Y);
        var length = ab.SquaredLength;
        if (length < 1e-6)
            return new Vector(p.X - a.X, p.Y - a.Y).Length;
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / length, 0, 1);
        var closest = a + ab * t;
        return new Vector(p.X - closest.X, p.Y - closest.Y).Length;
    }
}

public enum AnnotationHandle
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    CalloutTarget
}
