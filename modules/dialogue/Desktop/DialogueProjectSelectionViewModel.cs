using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FgoPet.App.Dialogue;

public sealed class DialogueProjectSelectionViewModel : ObservableObject
{
    private readonly IDialogueProjectCatalog? _catalog;
    private IReadOnlyList<DialogueProjectOption> _projects = Array.Empty<DialogueProjectOption>();
    private bool _isLoading;
    private string _statusText = "点击刷新读取可用项目。";

    public DialogueProjectSelectionViewModel(IDialogueProjectCatalog? catalog)
    {
        _catalog = catalog;
        IsAvailable = catalog is not null;
        if (!IsAvailable)
        {
            _statusText = "当前没有可用的项目目录能力。";
        }

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => IsAvailable);
    }

    public bool IsAvailable { get; }

    public IReadOnlyList<DialogueProjectOption> Projects
    {
        get => _projects;
        private set
        {
            if (ReferenceEquals(_projects, value)) return;
            _projects = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProjects));
        }
    }

    public bool HasProjects => Projects.Count > 0;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is null)
        {
            return;
        }

        IsLoading = true;
        StatusText = "正在读取可用项目…";
        try
        {
            var result = await _catalog.ListAsync(cancellationToken).ConfigureAwait(true);
            Projects = result.Projects
                .Where(project => !string.IsNullOrWhiteSpace(project.Id)
                    && !string.IsNullOrWhiteSpace(project.Label))
                .GroupBy(project => project.Id.Trim(), StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            StatusText = !result.IsAvailable
                ? "项目目录暂不可用，请检查外部助手设置。"
                : Projects.Count == 0
                    ? "当前没有可用项目。"
                    : $"已找到 {Projects.Count} 个可用项目。";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Projects = Array.Empty<DialogueProjectOption>();
            StatusText = "项目目录读取超时，请重试。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "项目目录读取已取消。";
        }
        catch (Exception)
        {
            Projects = Array.Empty<DialogueProjectOption>();
            StatusText = "项目目录读取失败，请重试。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public bool TrySelect(DialogueProjectOption option, DialogueSessionContextViewModel context)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(context);
        if (!Projects.Any(project => project.Id == option.Id))
        {
            return false;
        }

        return context.TrySetProject(option.Id, option.Label);
    }
}
