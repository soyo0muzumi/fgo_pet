using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>Pure question validation. The coordinator owns persistence, provenance and consumption.</summary>
public sealed class UserInputBroker
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private static readonly JsonSerializerOptions PayloadJson = PayloadOptions();
    public UserInputBroker(TimeProvider? time = null, TimeSpan? ttl = null)
        => (_time, _ttl) = (time ?? TimeProvider.System, InteractionValidation.Ttl(ttl));

    public UserInputRequest Create(AgentRunIdentity identity, int stepNumber, string callId,
        long waitingRevision, JsonElement arguments)
    {
        var binding = InteractionValidation.CreateBinding(identity, stepNumber, callId, waitingRevision, _time, _ttl);
        try
        {
            ObjectKeys(arguments, ["questions"]);
            if (!arguments.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array
                || questions.GetArrayLength() is < 1 or > 3) throw InvalidQuestions();
            var result = ImmutableArray.CreateBuilder<UserQuestion>(questions.GetArrayLength());
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var question in questions.EnumerateArray())
            {
                ObjectKeys(question, ["id", "question", "options", "allowMultiple"]);
                var id = RequiredText(question, "id", 64);
                if (!InteractionValidation.QuestionId(id) || !ids.Add(id)) throw InvalidQuestions();
                var text = RequiredText(question, "question", 2000);
                var options = ImmutableArray.CreateBuilder<string>();
                if (question.TryGetProperty("options", out var rawOptions))
                {
                    if (rawOptions.ValueKind != JsonValueKind.Array || rawOptions.GetArrayLength() > 8) throw InvalidQuestions();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var option in rawOptions.EnumerateArray())
                    {
                        if (option.ValueKind != JsonValueKind.String) throw InvalidQuestions();
                        var label = option.GetString();
                        if (!InteractionValidation.Text(label, 200) || !seen.Add(label!)) throw InvalidQuestions();
                        options.Add(label!);
                    }
                }
                var multiple = false;
                if (question.TryGetProperty("allowMultiple", out var rawMultiple))
                {
                    if (rawMultiple.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidQuestions();
                    multiple = rawMultiple.GetBoolean();
                }
                if (multiple && options.Count == 0) throw InvalidQuestions();
                result.Add(new(id, text, options.ToImmutable(), multiple));
            }
            return new(binding, result.MoveToImmutable());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ObjectDisposedException)
        { throw InvalidQuestions(); }
    }

    public ToolResult Validate(UserInputRequest request, UserInputReply reply, ToolScope scope)
    {
        if (request is null || reply is null) throw new AgentStateException("RUN_INTERACTION_STALE");
        InteractionValidation.Match(request.Binding, reply.RunId, reply.RequestId, reply.ExpectedRevision, scope, _time);
        if (request.Questions.IsDefaultOrEmpty || request.Questions.Length > 3 || reply.Answers.IsDefault
            || reply.Answers.Length != request.Questions.Length) throw InvalidAnswers();
        var answers = new Dictionary<string, QuestionAnswer>(StringComparer.Ordinal);
        foreach (var answer in reply.Answers)
            if (answer is null || answer.QuestionId is null || !answers.TryAdd(answer.QuestionId, answer)) throw InvalidAnswers();
        long total = 0;
        var output = new List<object>(request.Questions.Length);
        foreach (var question in request.Questions)
        {
            if (question is null || question.Id is null || question.Options.IsDefault
                || !answers.TryGetValue(question.Id, out var answer) || answer.SelectedOptionIndices.IsDefault
                || !question.AllowMultiple && answer.SelectedOptionIndices.Length > 1
                || answer.SelectedOptionIndices.Length > question.Options.Length) throw InvalidAnswers();
            var selected = new List<string>(answer.SelectedOptionIndices.Length);
            var seen = new HashSet<int>();
            foreach (var index in answer.SelectedOptionIndices)
            {
                if (index < 0 || index >= question.Options.Length || !seen.Add(index)) throw InvalidAnswers();
                var label = question.Options[index];
                selected.Add(label);
                total += label.Length;
            }
            if (selected.Count == 0 && string.IsNullOrWhiteSpace(answer.Text)) throw InvalidAnswers();
            if (answer.Text is not null && !InteractionValidation.ValidUnicode(answer.Text)) throw InvalidAnswers();
            total += answer.Text?.Length ?? 0;
            if (total > 12000) throw InvalidAnswers();
            output.Add(new { questionId = question.Id, selectedOptions = selected, text = answer.Text });
        }
        var payload = JsonSerializer.SerializeToElement(new { answers = output }, PayloadJson);
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > ToolResultNormalizer.MaxPayloadBytes) throw InvalidAnswers();
        return new(true, payload)
            { ExecutionState = ToolExecutionState.NotExecuted };
    }

    private static void ObjectKeys(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidQuestions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw InvalidQuestions();
    }
    private static string RequiredText(JsonElement value, string name, int maximum)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String
            || !InteractionValidation.Text(property.GetString(), maximum)) throw InvalidQuestions();
        return property.GetString()!;
    }
    private static AgentStateException InvalidQuestions() => new("RUN_INVALID_QUESTIONS");
    private static AgentStateException InvalidAnswers() => new("RUN_INVALID_ANSWERS");

    private static JsonSerializerOptions PayloadOptions()
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        options.Converters.Add(new SupplementaryStringConverter());
        return options;
    }

    // Even the relaxed encoder escapes supplementary pairs. Tool data is JSON, never HTML;
    // write valid pairs literally so an allowed 12,000-unit answer remains within the byte cap.
    private sealed class SupplementaryStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (!value.Any(char.IsSurrogate)) { writer.WriteStringValue(value); return; }
            var literal = new StringBuilder(value.Length + 2).Append('"');
            foreach (var character in value)
            {
                if (character is '"' or '\\') literal.Append('\\').Append(character);
                else if (character < 0x20) literal.Append("\\u").Append(((int)character).ToString("X4"));
                else literal.Append(character);
            }
            writer.WriteRawValue(literal.Append('"').ToString());
        }
    }
}
