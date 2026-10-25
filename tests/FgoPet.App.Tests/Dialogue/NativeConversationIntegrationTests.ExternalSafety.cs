using System.Collections.Immutable;
using System.IO;
using System.Windows.Threading;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Contracts;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task External_cannot_read_or_clear_a_desktop_image_before_desktop_send(bool asksForImage) => RunInputChatTest(async () =>
    {
        var turns = new List<ProviderTurn>();
        if (asksForImage) turns.Add(ProviderTurn.Call("image.read", "remote-image", "{}"));
        turns.Add(ProviderTurn.Final("Remote text answer"));
        turns.Add(ProviderTurn.Call("image.read", "desktop-image", "{}"));
        turns.Add(ProviderTurn.Final("Desktop image answer"));
        var provider = new ScriptedProvider(turns.ToArray());
        var connection = new ModelConnectionSettings("openai", "https://api.openai.com/v1", "gpt-4.1-mini",
            contextWindowOverride: 32768, maxOutputTokens: 512);
        var settings = new TestSettingsStore(DialogueSettings.Defaults with { ModelConnection = connection });
        var route = ModelRouteKey.From(connection);
        var images = new SelectedImageStore(_ => true);
        await using var imageTool = new ImageToolsPlugin(images, () => route);
        await imageTool.StartAsync(default);
        await using var host = await TestHost.CreateAsync(new ImagePreparingProvider(provider, images, route), imageTool,
            new RecordingObserver(), settings, context: imageTool);
        using var model = new ConversationViewModel(host.Orchestrator, settings);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        using var selection = new ChatImageSelection(model, images, settings, TimeProvider.System);
        var path = Path.Combine(Path.GetTempPath(), "qq-image-synthetic-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        try
        {
            Assert.True(await selection.ChooseAsync(() => path, default));
            model.InputText = "Unsent desktop image question";
            var result = await gateway.ExchangeTextAsync(binding, asksForImage ? "Inspect the selected image" : "Ordinary remote text");
            Assert.Equal(ExternalConversationStatus.Completed, result.Status);
            Assert.DoesNotContain(provider.Requests, request => request.ModelMessages.Any(message => message.Image is not null));
            Assert.DoesNotContain(provider.Requests, request => request.ModelMessages.Any(message =>
                message.Content.Contains("The user explicitly selected an image", StringComparison.Ordinal)));
            Assert.NotNull(selection.Label);
            Assert.Equal("Unsent desktop image question", model.InputText);

            var desktopReceivedPixels = false;
            provider.RequestReceived = request =>
            {
                foreach (var message in request.ModelMessages.Where(message => message.Image is not null))
                    desktopReceivedPixels |= images.Resolve(message.Image!, RoleId, binding.ConversationId, route, default).ByteLength > 0;
            };
            await model.SendCommand.ExecuteAsync(null);
            Assert.True(desktopReceivedPixels);
            Assert.Null(selection.Label);
            Assert.Contains(model.Turns, turn => turn.IsAssistant && turn.Text == "Desktop image answer");
        }
        finally { File.Delete(path); }
    });

    [Theory]
    [InlineData(false, "answer")]
    [InlineData(true, "answer")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "expiry")]
    [InlineData(true, "expiry")]
    public Task External_next_text_is_admitted_after_desktop_finishes_a_remote_question(bool multiple, string finish) => RunInputChatTest(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var turns = new List<ProviderTurn> { multiple ? QuestionTurn() : ExternalQuestionTurn() };
        if (finish == "answer") turns.Add(ProviderTurn.Final("Desktop clarification answer"));
        turns.Add(ProviderTurn.Final("Next remote answer"));
        var provider = new ScriptedProvider(turns.ToArray());
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            TestSettingsStore.Create(32768), questions: true, time: clock);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings, time: clock);
        var binding = BindExternalConversation(host, model);
        var gateway = new ExternalConversationGateway(model, Dispatcher.CurrentDispatcher);
        var waiting = await gateway.ExchangeTextAsync(binding, "Ask a synthetic question");
        Assert.Equal(ExternalConversationStatus.WaitingInput, waiting.Status);
        var reference = waiting.Input!.Reference;
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        if (finish == "answer")
        {
            object[] answers = multiple ? InputAnswers() :
                [new { questionId = "choice", selectedOptionIndices = new[] { 0 }, text = (string?)null }];
            Assert.True((await session.HandleCommandAsync(InputCommand(session, "chat.input.answer", card, answers), default)).Success);
        }
        else if (finish == "cancel")
            Assert.True((await session.HandleCommandAsync(InputCommand(session, "chat.input.cancel", card), default)).Success);
        else clock.Advance(TimeSpan.FromMinutes(15));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (model.HasPendingRun || model.IsStreaming) await Task.Delay(10, deadline.Token);

        var beforeLateReply = provider.Requests.Count;
        Assert.Equal(ExternalConversationStatus.InvalidInput,
            (await gateway.AnswerAsync(binding, new(reference, [new("choice", [0])]))).Status);
        Assert.Equal(ExternalConversationStatus.InvalidInput, (await gateway.CancelAsync(binding, reference)).Status);
        Assert.Equal(beforeLateReply, provider.Requests.Count);
        var next = await gateway.ExchangeTextAsync(binding, "A new ordinary message");
        Assert.Equal(ExternalConversationStatus.Completed, next.Status);
        Assert.Equal("Next remote answer", next.Text);
    });

    private sealed class ImagePreparingProvider(IChatProvider inner, SelectedImageStore images, ModelRouteKey route)
        : IChatProvider, IChatImageInputProvider
    {
        public string ProviderId => inner.ProviderId;
        public string ModelId => inner.ModelId;
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => inner.ListModelsAsync(token);
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken token) => inner.StreamAsync(request, token);
        public void PrepareImages(ChatRequest request, CancellationToken token) => ChatImageInputs.Prepare(request, route, images, true, token);
    }
}
