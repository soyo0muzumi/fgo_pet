using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.App.Theming;
using FgoPet.Core.Todo;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Plugin.Todo;
using FgoPet.UiSdk;
using Microsoft.Extensions.DependencyInjection;

namespace FgoPet.Plugin.Todo.Desktop;

public static class TodoRegistration
{
    public static IServiceCollection AddTodoCapability(this IServiceCollection services, bool enabled = true,
        string? storageRoot = null)
    {
        // Storage remains available to privacy/backup compatibility adapters while the capability is disabled.
        services.AddSingleton<SqliteTodoRepository>();
        services.AddSingleton<ITodoRepository>(provider => provider.GetRequiredService<SqliteTodoRepository>());
        if (!enabled) return services;
        return services
            .AddSingleton(provider => new TodoApplicationService(provider.GetRequiredService<ITodoRepository>(),
                provider.GetRequiredService<TimeProvider>()))
            .AddSingleton<TodoProposalService>()
            .AddSingleton<ITodoDraftWorkflow>(provider => provider.GetRequiredService<TodoProposalService>().Drafts)
            .AddSingleton(provider => new TodoPlugin(provider.GetRequiredService<TodoProposalService>(),
                nativeTools: NativeTodoTools.Create(provider.GetRequiredService<TodoApplicationService>(),
                    provider.GetRequiredService<ITodoRepository>(), provider.GetRequiredService<TodoProposalService>(),
                    provider.GetRequiredService<TimeProvider>())))
            .AddSingleton<TodoChangeFeed>()
            .AddSingleton<TodoWebAdapter>()
            .AddSingleton(provider => new TodoPeekSurfaceFactory(
                provider.GetRequiredService<TodoWebAdapter>(), provider.GetRequiredService<TodoChangeFeed>(),
                provider.GetRequiredService<TimeProvider>(),
                workspaceId => provider.GetRequiredService<IWorkspaceLauncher>().Open(workspaceId),
                storageRoot ?? throw new InvalidOperationException("TODO_STORAGE_ROOT_REQUIRED"),
                provider.GetRequiredService<ThemeService>()))
            .AddSingleton<TodoDesktopPlugin>()
            .AddSingleton<IFgoPetPlugin>(provider => provider.GetRequiredService<TodoDesktopPlugin>())
            .AddSingleton<IWorkspaceViewFactory>(provider => provider.GetRequiredService<TodoDesktopPlugin>())
            .AddSingleton<ITransientSurfaceViewFactory>(provider => provider.GetRequiredService<TodoPeekSurfaceFactory>());
    }
}
