using CommunityToolkit.Mvvm.ComponentModel;
using OpenPdfStudio.Models;

namespace OpenPdfStudio.ViewModels;

/// <summary>One annotation shown on a page: its current PDF state plus selection and editing flags.</summary>
public partial class AnnotationViewModel(PdfAnnotation model) : ObservableObject
{
    [ObservableProperty] private PdfAnnotation _model = model;
    [ObservableProperty] private bool _isSelected;

    /// <summary>True while an in-place editor or note popup is open; the PDF copy is hidden meanwhile.</summary>
    [ObservableProperty] private bool _isEditing;

    /// <summary>True until the first commit of a newly placed annotation, so Esc can cancel it.</summary>
    [ObservableProperty] private bool _isNew;

    public string Id => Model.Id;
    public int PageIndex => Model.PageIndex;
    public AnnotationKind Kind => Model.Kind;

    partial void OnModelChanged(PdfAnnotation value)
    {
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(PageIndex));
        OnPropertyChanged(nameof(Kind));
    }
}
