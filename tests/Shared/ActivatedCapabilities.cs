using FgoPet.Extensibility;
using FgoPet.Kernel.Conversation;

namespace FgoPet.Testing;

/// <summary>Static in-memory test capabilities have synchronous activation and no external resources.</summary>
internal static class ActivatedCapabilities
{
    public static IFgoPetPlugin Todo(FgoPet.Infrastructure.Persistence.RuntimeDatabase database,
        FgoPet.App.Dialogue.ITodoDraftWorkflow? drafts = null) => new FgoPet.Plugin.Todo.TodoPlugin(
            new FgoPet.App.Dialogue.TodoProposalService(new FgoPet.App.Services.TodoApplicationService(
                new FgoPet.Infrastructure.Persistence.SqliteTodoRepository(database), TimeProvider.System)), drafts);

    public static ConversationCapabilityRouter Create(params IFgoPetPlugin[] plugins)
    {
        var catalog = PluginCatalog.Create(plugins);
        var runtime = new PluginRuntime(catalog);
        var activation = runtime.StartAsync(default).GetAwaiter().GetResult();
        if (!activation.Succeeded) throw new InvalidOperationException(activation.ErrorCode);
        return new(catalog, runtime);
    }
}
