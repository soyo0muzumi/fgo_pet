using FgoPet.Character.Settings;
using FgoPet.Core.Agents;
using FgoPet.Core.Settings;
using FgoPet.Core.Speech;
using FgoPet.Dialogue.Settings;
using FgoPet.Memory.Settings;
using FgoPet.Speech.Settings;
using FgoPet.UiFoundation.Theming;
using FgoPet.Work.Execution.Settings;
using System.Collections.Immutable;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class ModuleSettingsDefaultsTests
{
    [Fact]
    public void Character_section_defaults_are_literal_and_collections_are_ordinal()
    {
        var settings = CharacterSettings.Defaults;

        Assert.Null(settings.Selection);
        Assert.Equal(0.50, settings.Scale);
        Assert.True(settings.Topmost);
        Assert.True(settings.AutoCollapseExpandedPanel);
        Assert.Null(settings.UserProfile);
        AssertImmutableOrdinalEmptyDictionary(settings.ServantPreferences);
        AssertImmutableOrdinalEmptyDictionary(settings.PackageSettings);
    }

    [Fact]
    public void Dialogue_and_memory_section_defaults_are_literal()
    {
        Assert.Null(DialogueSettings.Defaults.ModelConnection);
        Assert.True(DialogueSettings.Defaults.ShowReasoning);
        Assert.True(MemorySettings.Defaults.Enabled);
    }

    [Fact]
    public void Work_execution_section_defaults_are_literal_and_collections_are_ordinal()
    {
        var connection = WorkExecutionSettings.Defaults.AgentConnection;

        Assert.False(connection.Enabled);
        Assert.Empty(connection.SourceEnabled);
        Assert.Empty(connection.ProjectAllowlist);
    }

    [Fact]
    public void Speech_section_defaults_are_literal()
    {
        var connection = SpeechSettings.Defaults.Connection;

        Assert.False(connection.Enabled);
        Assert.Equal(SpeechProviderKind.OpenAiCompatible, connection.Provider);
        Assert.Equal("https://api.openai.com/v1", connection.OpenAiBaseUrl);
        Assert.Equal("gpt-4o-mini-tts", connection.OpenAiModel);
        Assert.Equal("alloy", connection.OpenAiVoice);
        Assert.Equal("fgo-pet/speech/openai", connection.OpenAiCredentialTarget);
        Assert.Equal("http://127.0.0.1:9880", connection.GptSoVitsBaseUrl);
        Assert.Equal(string.Empty, connection.GptSoVitsReferenceAudioPath);
        Assert.Equal(string.Empty, connection.GptSoVitsPromptText);
        Assert.Equal("zh", connection.GptSoVitsLanguage);
        Assert.Equal("zh", connection.GptSoVitsPromptLanguage);
        Assert.Equal("http://127.0.0.1:7860", connection.IndexTtsBaseUrl);
        Assert.Equal(string.Empty, connection.IndexTtsVoiceId);
        Assert.Empty(connection.IndexTtsVoices);
        Assert.False(connection.AutoReadEnabled);
        Assert.False(connection.DoNotDisturb);
        Assert.Equal(300, connection.AutoReadLimit);
        Assert.Equal(1.0, connection.Rate);
        Assert.Equal(1.0, connection.Volume);
    }

    [Fact]
    public void Theme_section_default_is_literal()
    {
        Assert.Equal(AppTheme.FgoLight, ThemeSettings.Defaults.Theme);
    }

    [Fact]
    public void Each_section_exposes_its_owned_store_port()
    {
        AssertStorePort<ICharacterSettingsStore, CharacterSettings>();
        AssertStorePort<IDialogueSettingsStore, DialogueSettings>();
        AssertStorePort<IMemorySettingsStore, MemorySettings>();
        AssertStorePort<IWorkExecutionSettingsStore, WorkExecutionSettings>();
        AssertStorePort<ISpeechSettingsStore, SpeechSettings>();
        AssertStorePort<IThemeSettingsStore, ThemeSettings>();
    }

    private static void AssertImmutableOrdinalEmptyDictionary<TValue>(IReadOnlyDictionary<string, TValue> dictionary)
    {
        var mutationView = Assert.IsAssignableFrom<IDictionary<string, TValue>>(dictionary);
        Assert.True(mutationView.IsReadOnly);
        var immutable = Assert.IsType<ImmutableDictionary<string, TValue>>(dictionary);
        Assert.Empty(immutable);
        Assert.Same(StringComparer.Ordinal, immutable.KeyComparer);
    }

    private static void AssertStorePort<TStore, TSettings>()
    {
        var storeType = typeof(TStore);
        Assert.True(storeType.IsInterface);
        Assert.Equal(typeof(TSettings), storeType.GetMethod("Load")?.ReturnType);
        Assert.Equal(typeof(void), storeType.GetMethod("Save")?.ReturnType);
        Assert.Equal(typeof(TSettings), Assert.Single(storeType.GetMethod("Save")!.GetParameters()).ParameterType);
    }
}
