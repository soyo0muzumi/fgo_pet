using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Archives;
using FgoPet.Core.Archives;

namespace FgoPet.App.ViewModels;

public sealed partial class ArchiveDraftViewModel : ObservableObject
{
    private readonly ArchiveDraftService _service;

    public ArchiveDraftViewModel(ArchiveDraft draft, ArchiveDraftService service)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Title = draft.Title;
        Summary = draft.Summary;
    }

    public ArchiveDraft Draft { get; }
    public int CoveredTodoCount => Draft.CoveredTodoCount;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _summary;

    public void Confirm()
    {
        _service.Confirm(Draft with { Title = Title, Summary = Summary });
    }
}
