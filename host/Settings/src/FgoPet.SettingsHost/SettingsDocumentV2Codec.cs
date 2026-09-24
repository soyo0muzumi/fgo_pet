using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Character.Settings;
using FgoPet.Core.Agents;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Core.Speech;
using FgoPet.Dialogue.Settings;
using FgoPet.Memory.Settings;
using FgoPet.Speech.Settings;
using FgoPet.UiFoundation.Theming;
using FgoPet.Work.Execution.Settings;

namespace FgoPet.SettingsHost;

internal sealed class SettingsDocumentV2Codec
{
    private const int SchemaVersion = 2;

    public string Serialize(SettingsDocumentSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(SettingsDto.FromModel(settings));
    }

    public string SerializeForBackup(SettingsDocumentSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(SettingsDto.FromModel(settings, sanitizeLocalPaths: true));
    }

    public SettingsDocumentSnapshot Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new JsonException("Settings snapshot is empty.");
        }

        var dto = JsonSerializer.Deserialize<SettingsDto>(json)
            ?? throw new JsonException("Settings snapshot is empty.");
        if (dto.SchemaVersion is < 1 or > SchemaVersion)
        {
            throw new JsonException("Settings snapshot schema is not supported.");
        }

        try
        {
            return new SettingsDocumentSnapshot(
                CharacterSettings.Defaults with
                {
                    Selection = dto.Selection?.ToModel(),
                    Scale = dto.Scale,
                    Topmost = dto.Topmost,
                    AutoCollapseExpandedPanel = dto.AutoCollapseExpandedPanel,
                    ServantPreferences = ParseServantPreferences(dto.ServantPreferences),
                    UserProfile = dto.UserProfile?.ToModel(),
                    PackageSettings = ParsePackageSettings(dto.PackageSettings),
                },
                new DialogueSettings(dto.ModelConnection?.ToModel(), dto.ShowReasoning ?? true),
                new MemorySettings(dto.MemoryEnabled ?? true),
                new WorkExecutionSettings(dto.AgentConnection?.ToModel() ?? AgentConnectionSettings.Defaults),
                new SpeechSettings(dto.SpeechConnection?.ToModel() ?? SpeechConnectionSettings.Defaults),
                new ThemeSettings(ParseTheme(dto.Theme)));
        }
        catch (JsonException)
        {
            throw;
        }
        catch (ArgumentException error)
        {
            throw new JsonException("Settings snapshot contains invalid values.", error);
        }
    }

    private sealed record SettingsDto
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("selection")]
        public SelectionDto? Selection { get; init; }

        [JsonPropertyName("scale")]
        public double Scale { get; init; }

        [JsonPropertyName("topmost")]
        public bool Topmost { get; init; }

        [JsonPropertyName("auto_collapse")]
        public bool AutoCollapseExpandedPanel { get; init; }

        [JsonPropertyName("model_connection")]
        public ModelConnectionDto? ModelConnection { get; init; }

        [JsonPropertyName("speech_connection")]
        public SpeechConnectionDto? SpeechConnection { get; init; }

        [JsonPropertyName("agent_connection")]
        public AgentConnectionDto? AgentConnection { get; init; }

        [JsonPropertyName("memory_enabled")]
        public bool? MemoryEnabled { get; init; }

        [JsonPropertyName("show_reasoning")]
        public bool? ShowReasoning { get; init; }

        [JsonPropertyName("servant_preferences")]
        public Dictionary<string, ServantPreferenceDto>? ServantPreferences { get; init; }

        [JsonPropertyName("theme")]
        public string? Theme { get; init; }

        [JsonPropertyName("user_profile")]
        public UserProfileDto? UserProfile { get; init; }

        [JsonPropertyName("package_settings")]
        public Dictionary<string, Dictionary<string, string?>?>? PackageSettings { get; init; }

        public static SettingsDto FromModel(
            SettingsDocumentSnapshot settings,
            bool sanitizeLocalPaths = false) => new()
        {
            SchemaVersion = SettingsDocumentV2Codec.SchemaVersion,
            Selection = SelectionDto.FromModel(settings.Character.Selection),
            Scale = settings.Character.Scale,
            Topmost = settings.Character.Topmost,
            AutoCollapseExpandedPanel = settings.Character.AutoCollapseExpandedPanel,
            ModelConnection = ModelConnectionDto.FromModel(settings.Dialogue.ModelConnection),
            SpeechConnection = SpeechConnectionDto.FromModel(settings.Speech.Connection, sanitizeLocalPaths),
            MemoryEnabled = settings.Memory.Enabled,
            ShowReasoning = settings.Dialogue.ShowReasoning,
            ServantPreferences = settings.Character.ServantPreferences.ToDictionary(
                pair => pair.Key,
                pair => ServantPreferenceDto.FromModel(pair.Value),
                StringComparer.Ordinal),
            Theme = FormatTheme(settings.Theme.Theme),
            UserProfile = UserProfileDto.FromModel(settings.Character.UserProfile),
            PackageSettings = settings.Character.PackageSettings.ToDictionary(
                package => package.Key,
                package => (Dictionary<string, string?>?)package.Value.ToDictionary(
                    setting => setting.Key,
                    setting => (string?)setting.Value,
                    StringComparer.Ordinal),
                StringComparer.Ordinal),
            AgentConnection = AgentConnectionDto.FromModel(settings.WorkExecution.AgentConnection),
        };
    }

    private sealed record UserProfileDto(
        [property: JsonPropertyName("display_name")] string? DisplayName)
    {
        public UserProfile ToModel() => new(DisplayName);

        public static UserProfileDto? FromModel(UserProfile? profile) =>
            profile is null ? null : new UserProfileDto(profile.DisplayName);
    }

    private sealed record ModelConnectionDto(
        [property: JsonPropertyName("provider_id")] string? ProviderId,
        [property: JsonPropertyName("base_url")] string? BaseUrl,
        [property: JsonPropertyName("model_id")] string? ModelId,
        [property: JsonPropertyName("tools_supported")] bool? ToolsSupported = null,
        [property: JsonPropertyName("context_window_override")] int? ContextWindowOverride = null,
        [property: JsonPropertyName("max_output_tokens")] int? MaxOutputTokens = null)
    {
        public ModelConnectionSettings? ToModel() =>
            string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ModelId)
                ? null
                : new ModelConnectionSettings(ProviderId, BaseUrl, ModelId, ToolsSupported ?? true,
                    ContextWindowOverride, MaxOutputTokens ?? 2048);

        public static ModelConnectionDto? FromModel(ModelConnectionSettings? settings) =>
            settings is null ? null : new ModelConnectionDto(settings.ProviderId, settings.BaseUrl, settings.ModelId,
                settings.ToolsSupported, settings.ContextWindowOverride, settings.MaxOutputTokens);
    }

    private sealed record SpeechConnectionDto(
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("provider")] string? Provider,
        [property: JsonPropertyName("openai_base_url")] string? OpenAiBaseUrl,
        [property: JsonPropertyName("openai_model")] string? OpenAiModel,
        [property: JsonPropertyName("openai_voice")] string? OpenAiVoice,
        [property: JsonPropertyName("openai_credential_target")] string? OpenAiCredentialTarget,
        [property: JsonPropertyName("gpt_sovits_base_url")] string? GptSoVitsBaseUrl,
        [property: JsonPropertyName("gpt_sovits_reference_audio_path")] string? GptSoVitsReferenceAudioPath,
        [property: JsonPropertyName("gpt_sovits_prompt_text")] string? GptSoVitsPromptText,
        [property: JsonPropertyName("gpt_sovits_language")] string? GptSoVitsLanguage,
        [property: JsonPropertyName("gpt_sovits_prompt_language")] string? GptSoVitsPromptLanguage,
        [property: JsonPropertyName("auto_read_enabled")] bool? AutoReadEnabled,
        [property: JsonPropertyName("auto_read_limit")] int? AutoReadLimit,
        [property: JsonPropertyName("rate")] double? Rate,
        [property: JsonPropertyName("volume")] double? Volume)
    {
        [JsonPropertyName("index_tts_base_url")] public string? IndexTtsBaseUrl { get; init; }
        [JsonPropertyName("index_tts_voice_id")] public string? IndexTtsVoiceId { get; init; }
        [JsonPropertyName("index_tts_voices")] public ReferenceVoice[]? IndexTtsVoices { get; init; }
        [JsonPropertyName("do_not_disturb")] public bool DoNotDisturb { get; init; }

        public SpeechConnectionSettings ToModel()
        {
            var provider = string.IsNullOrWhiteSpace(Provider)
                ? SpeechProviderKind.OpenAiCompatible
                : Enum.TryParse<SpeechProviderKind>(Provider, ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new JsonException("Unknown speech provider.");
            return new SpeechConnectionSettings
            {
                Enabled = Enabled,
                IndexTtsBaseUrl = IndexTtsBaseUrl ?? SpeechConnectionSettings.Defaults.IndexTtsBaseUrl,
                IndexTtsVoiceId = IndexTtsVoiceId ?? string.Empty,
                IndexTtsVoices = IndexTtsVoices ?? Array.Empty<ReferenceVoice>(),
                DoNotDisturb = DoNotDisturb,
                Provider = provider,
                OpenAiBaseUrl = OpenAiBaseUrl ?? string.Empty,
                OpenAiModel = OpenAiModel ?? string.Empty,
                OpenAiVoice = OpenAiVoice ?? string.Empty,
                OpenAiCredentialTarget = OpenAiCredentialTarget ?? string.Empty,
                GptSoVitsBaseUrl = GptSoVitsBaseUrl ?? string.Empty,
                GptSoVitsReferenceAudioPath = GptSoVitsReferenceAudioPath ?? string.Empty,
                GptSoVitsPromptText = GptSoVitsPromptText ?? string.Empty,
                GptSoVitsLanguage = GptSoVitsLanguage ?? string.Empty,
                GptSoVitsPromptLanguage = GptSoVitsPromptLanguage ?? string.Empty,
                AutoReadEnabled = AutoReadEnabled ?? false,
                AutoReadLimit = AutoReadLimit ?? 300,
                Rate = Rate ?? 1.0,
                Volume = Volume ?? 1.0,
            }.Normalize();
        }

        public static SpeechConnectionDto FromModel(
            SpeechConnectionSettings settings,
            bool sanitizeLocalPaths = false)
        {
            var normalized = (settings ?? SpeechConnectionSettings.Defaults).Normalize();
            return new(
                normalized.Enabled,
                normalized.Provider.ToString(),
                normalized.OpenAiBaseUrl,
                normalized.OpenAiModel,
                normalized.OpenAiVoice,
                normalized.OpenAiCredentialTarget,
                normalized.GptSoVitsBaseUrl,
                sanitizeLocalPaths ? string.Empty : normalized.GptSoVitsReferenceAudioPath,
                normalized.GptSoVitsPromptText,
                normalized.GptSoVitsLanguage,
                normalized.GptSoVitsPromptLanguage,
                normalized.AutoReadEnabled,
                normalized.AutoReadLimit,
                normalized.Rate,
                normalized.Volume)
            {
                IndexTtsBaseUrl = normalized.IndexTtsBaseUrl,
                IndexTtsVoiceId = normalized.IndexTtsVoiceId,
                IndexTtsVoices = normalized.IndexTtsVoices
                    .Select(voice => sanitizeLocalPaths
                        ? voice with { AudioPath = string.Empty }
                        : voice)
                    .ToArray(),
                DoNotDisturb = normalized.DoNotDisturb,
            };
        }
    }

    private sealed record AgentConnectionDto(
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("source_enabled")] Dictionary<string, bool>? SourceEnabled,
        [property: JsonPropertyName("project_allowlist")] Dictionary<string, AgentProjectTargetDto[]>? ProjectAllowlist)
    {
        public AgentConnectionSettings ToModel()
        {
            if (!Enabled && (SourceEnabled is null || SourceEnabled.Count == 0)
                && (ProjectAllowlist is null || ProjectAllowlist.Count == 0))
            {
                return AgentConnectionSettings.Defaults;
            }

            var allowlist = ProjectAllowlist?.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<AgentProjectTarget>)((pair.Value ?? throw new JsonException(
                    "Agent project allowlist must be an array."))
                    .Select(target => target.ToModel())
                    .ToArray()),
                StringComparer.Ordinal);
            return new AgentConnectionSettings(Enabled, SourceEnabled, allowlist);
        }

        public static AgentConnectionDto FromModel(AgentConnectionSettings settings) => new(
            settings.Enabled,
            settings.SourceEnabled.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            settings.ProjectAllowlist.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Select(AgentProjectTargetDto.FromModel).ToArray(),
                StringComparer.Ordinal));
    }

    private sealed record AgentProjectTargetDto(
        [property: JsonPropertyName("target_id")] string? TargetId,
        [property: JsonPropertyName("display_name")] string? DisplayName)
    {
        public AgentProjectTarget ToModel()
        {
            try
            {
                return new AgentProjectTarget(TargetId ?? string.Empty, DisplayName ?? string.Empty);
            }
            catch (ArgumentException error)
            {
                throw new JsonException("Invalid Agent project target.", error);
            }
        }

        public static AgentProjectTargetDto FromModel(AgentProjectTarget target) =>
            new(target.TargetId, target.DisplayName);
    }

    private sealed record ServantPreferenceDto(
        [property: JsonPropertyName("address_mode")] string? AddressMode,
        [property: JsonPropertyName("address_text")] string? AddressText)
    {
        public ServantPreference ToModel() =>
            AddressMode switch
            {
                "package_default" => new ServantPreference(FgoPet.Core.Settings.AddressMode.PackageDefault),
                "user_defined" => new ServantPreference(FgoPet.Core.Settings.AddressMode.UserDefined, AddressText),
                _ => throw new JsonException("Unknown servant address mode."),
            };

        public static ServantPreferenceDto FromModel(ServantPreference preference) =>
            preference.AddressMode switch
            {
                FgoPet.Core.Settings.AddressMode.PackageDefault => new("package_default", null),
                FgoPet.Core.Settings.AddressMode.UserDefined => new("user_defined", preference.AddressText),
                _ => throw new ArgumentOutOfRangeException(nameof(preference)),
            };
    }

    private static IReadOnlyDictionary<string, ServantPreference> ParseServantPreferences(
        IReadOnlyDictionary<string, ServantPreferenceDto>? preferences)
    {
        if (preferences is null || preferences.Count == 0)
        {
            return CharacterSettings.Defaults.ServantPreferences;
        }

        var parsed = new Dictionary<string, ServantPreference>(StringComparer.Ordinal);
        foreach (var pair in preferences)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value is null)
            {
                throw new JsonException("Invalid servant preference.");
            }

            parsed[pair.Key] = pair.Value.ToModel();
        }

        return parsed;
    }

    private static AppTheme ParseTheme(string? theme) => theme switch
    {
        "fgo_light" => AppTheme.FgoLight,
        "modern_gray" => AppTheme.ModernGray,
        _ => AppTheme.FgoLight,
    };

    private static string FormatTheme(AppTheme theme) => theme switch
    {
        AppTheme.ModernGray => "modern_gray",
        AppTheme.FgoLight => "fgo_light",
        _ => "modern_gray",
    };

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParsePackageSettings(
        IReadOnlyDictionary<string, Dictionary<string, string?>?>? packageSettings)
    {
        if (packageSettings is null || packageSettings.Count == 0)
        {
            return CharacterSettings.Defaults.PackageSettings;
        }

        var parsed = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var package in packageSettings)
        {
            if (!IsSafeServantId(package.Key) || package.Value is null)
            {
                throw new JsonException("Invalid package settings.");
            }

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var setting in package.Value)
            {
                if (!IsValidPackageSettingKey(setting.Key)
                    || setting.Value is null
                    || setting.Value.Length > 256)
                {
                    throw new JsonException("Invalid package setting.");
                }

                settings[setting.Key] = setting.Value;
            }

            parsed[package.Key] = settings;
        }

        return parsed;
    }

    private static bool IsSafeServantId(string? value) =>
        value is { Length: > 0 and <= 128 }
        && IsAsciiAlphaNumeric(value[0])
        && value.All(character => IsAsciiAlphaNumeric(character) || character is '.' or '_' or '-');

    private static bool IsValidPackageSettingKey(string? value) =>
        value is { Length: > 0 and <= 64 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
                                  or >= '0' and <= '9'
                                  or '.' or '_' or '-');

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private sealed record SelectionDto(
        [property: JsonPropertyName("package_id")] string? PackageId,
        [property: JsonPropertyName("appearance_id")] string? AppearanceId,
        [property: JsonPropertyName("package_version")] string? PackageVersion)
    {
        public PortraitSelection? ToModel() =>
            string.IsNullOrWhiteSpace(PackageId) || string.IsNullOrWhiteSpace(AppearanceId)
                ? null
                : new PortraitSelection(PackageId, AppearanceId, PackageVersion);

        public static SelectionDto? FromModel(PortraitSelection? selection) =>
            selection is null ? null : new SelectionDto(selection.PackageId, selection.AppearanceId, selection.PackageVersion);
    }
}
