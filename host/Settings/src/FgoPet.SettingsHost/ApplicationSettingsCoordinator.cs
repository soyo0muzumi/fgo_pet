using System.Text.Json;
using FgoPet.Character.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Memory.Settings;
using FgoPet.Platform.Settings;
using FgoPet.Speech.Settings;
using FgoPet.UiFoundation.Theming;
using FgoPet.Work.Execution.Settings;

namespace FgoPet.SettingsHost;

public sealed class ApplicationSettingsCoordinator :
    IApplicationSettingsDocument,
    ICharacterSettingsStore,
    IDialogueSettingsStore,
    IMemorySettingsStore,
    IWorkExecutionSettingsStore,
    ISpeechSettingsStore,
    IThemeSettingsStore
{
    private readonly object _gate = new();
    private readonly ISettingsDocumentStore _documents;
    private readonly SettingsDocumentV2Codec _codec = new();

    public ApplicationSettingsCoordinator(ISettingsDocumentStore documents) =>
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));

    public string Location => _documents.Location;

    CharacterSettings ICharacterSettingsStore.Load() => ReadLive().Character;

    void ICharacterSettingsStore.Save(CharacterSettings settings) =>
        Update(snapshot => snapshot with
        {
            Character = settings ?? throw new ArgumentNullException(nameof(settings)),
        });

    DialogueSettings IDialogueSettingsStore.Load() => ReadLive().Dialogue;

    void IDialogueSettingsStore.Save(DialogueSettings settings) =>
        Update(snapshot => snapshot with
        {
            Dialogue = settings ?? throw new ArgumentNullException(nameof(settings)),
        });

    MemorySettings IMemorySettingsStore.Load() => ReadLive().Memory;

    void IMemorySettingsStore.Save(MemorySettings settings) =>
        Update(snapshot => snapshot with
        {
            Memory = settings ?? throw new ArgumentNullException(nameof(settings)),
        });

    WorkExecutionSettings IWorkExecutionSettingsStore.Load() => ReadLive().WorkExecution;

    void IWorkExecutionSettingsStore.Save(WorkExecutionSettings settings) =>
        Update(snapshot => snapshot with
        {
            WorkExecution = settings ?? throw new ArgumentNullException(nameof(settings)),
        });

    SpeechSettings ISpeechSettingsStore.Load() => ReadLive().Speech;

    void ISpeechSettingsStore.Save(SpeechSettings settings) =>
        Update(snapshot => snapshot with
        {
            Speech = settings ?? throw new ArgumentNullException(nameof(settings)),
        });

    ThemeSettings IThemeSettingsStore.Load() => ReadLive().Theme;

    void IThemeSettingsStore.Save(ThemeSettings settings) =>
        Update(snapshot => snapshot with
        {
            Theme = settings ?? throw new ArgumentNullException(nameof(settings)),
        });

    public string Export()
    {
        lock (_gate)
        {
            return _codec.Serialize(ReadLiveUnsafe());
        }
    }

    public SettingsRestoreMetadata ValidateForRestore(string document)
    {
        var snapshot = _codec.Deserialize(document);
        return new SettingsRestoreMetadata(snapshot.WorkExecution.AgentConnection.Enabled);
    }

    private SettingsDocumentSnapshot ReadLive()
    {
        lock (_gate)
        {
            return ReadLiveUnsafe();
        }
    }

    private SettingsDocumentSnapshot ReadLiveUnsafe()
    {
        var document = _documents.Read();
        if (document is null)
        {
            return SettingsDocumentSnapshot.Defaults;
        }

        try
        {
            return _codec.Deserialize(document);
        }
        catch (JsonException)
        {
            _documents.Quarantine();
            return SettingsDocumentSnapshot.Defaults;
        }
    }

    private void Update(Func<SettingsDocumentSnapshot, SettingsDocumentSnapshot> replace)
    {
        lock (_gate)
        {
            var next = replace(ReadLiveUnsafe());
            _documents.Write(_codec.Serialize(next));
        }
    }
}
