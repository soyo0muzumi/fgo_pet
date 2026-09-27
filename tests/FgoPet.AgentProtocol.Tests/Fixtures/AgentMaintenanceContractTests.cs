using FgoPet.AgentProtocol;
using FgoPet.AgentProtocol.Messages;
using FgoPet.AgentProtocol.Validation;
using Xunit;

namespace FgoPet.AgentProtocol.Tests.Fixtures;

public sealed class AgentMaintenanceContractTests
{
    private const string Sha256 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-08-30T08:00:00Z");

    [Fact]
    public void Maintenance_status_request_and_response_use_opposite_routes()
    {
        var request = ProtocolEnvelope.Create("status-request", "maintenance_status", new { });
        AgentProtocolValidator.Validate(request);

        var status = new AgentMaintenanceStatusResponse(
            new[] { new AgentCapacityCounter("adapter_journal", 7, 128, 5) },
            At,
            "batch-1",
            "safe maintenance status");
        var response = ProtocolEnvelope.Create("status-response", "maintenance_status", new
        {
            result = "status",
            counters = status.Counters,
            oldest_archivable_at = status.OldestArchivableAt,
            active_batch_id = status.ActiveBatchId,
            safe_error = status.SafeError,
        });

        AgentProtocolValidator.ValidateResponse(response);
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(response));
        var copy = ProtocolEnvelope.Parse(response.ToJson()).DeserializePayload<AgentMaintenanceStatusResponse>();

        Assert.Equal(status.Counters, copy.Counters);
        Assert.Equal(status.OldestArchivableAt, copy.OldestArchivableAt);
        Assert.Equal(status.ActiveBatchId, copy.ActiveBatchId);
        Assert.Equal(status.SafeError, copy.SafeError);
        Assert.Contains("\"oldest_archivable_at\"", response.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"active_batch_id\"", response.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Archive_commit_round_trips_and_rejects_response_payload_as_request()
    {
        var request = new AgentArchiveCommitRequest("batch-1", Sha256);
        var envelope = ProtocolEnvelope.Create("commit-request", "archive_commit", request);

        AgentProtocolValidator.Validate(envelope);
        var copy = ProtocolEnvelope.Parse(envelope.ToJson()).DeserializePayload<AgentArchiveCommitRequest>();
        Assert.Equal(request, copy);
        Assert.Contains("\"batch_id\"", envelope.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"batch_sha256\"", envelope.ToJson(), StringComparison.Ordinal);

        var response = ProtocolEnvelope.Create("commit-response", "archive_commit", new { result = "committed", batch_id = "batch-1", batch_sha256 = Sha256 });
        AgentProtocolValidator.ValidateResponse(response);
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(response));
    }

    [Fact]
    public void Archive_operation_rejection_requires_a_safe_error()
    {
        var missingError = ProtocolEnvelope.Create("rejected", "archive_commit", new { result = "rejected", batch_id = "batch-1", batch_sha256 = Sha256 });
        var successfulWithError = ProtocolEnvelope.Create("accepted", "archive_commit", new { result = "accepted", batch_id = "batch-1", batch_sha256 = Sha256, safe_error = "unexpected" });

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(missingError));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(successfulWithError));
    }

    [Fact]
    public void Maintenance_sync_accepts_prepare_commit_and_noop_commands_only_as_responses()
    {
        var prepare = ProtocolEnvelope.Create("prepare-command", "maintenance_sync", new
        {
            result = "prepare",
            batch_id = "batch-1",
            items = new[] { Item(endedAt: At, executionId: "execution-1") },
            batch_sha256 = Sha256,
        });
        var commit = ProtocolEnvelope.Create("commit-command", "maintenance_sync", new
        {
            result = "commit",
            batch_id = "batch-1",
            batch_sha256 = Sha256,
        });
        var none = ProtocolEnvelope.Create("none-command", "maintenance_sync", new { result = "none" });

        foreach (var response in new[] { prepare, commit, none })
        {
            AgentProtocolValidator.ValidateResponse(response);
            Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(response));
        }
    }

    [Fact]
    public void Maintenance_sync_rejects_a_command_with_a_different_source_instance()
    {
        var response = ProtocolEnvelope.Create("mismatched-source", "maintenance_sync", new
        {
            result = "prepare",
            source_type = "codex",
            source_instance = "source-2",
            batch_id = "batch-1",
            items = new[] { Item(sourceInstance: "source-1", endedAt: At, executionId: "execution-1") },
            batch_sha256 = Sha256,
        });

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(response));
    }

    [Fact]
    public void Maintenance_sync_acknowledgements_round_trip_each_result_and_phase()
    {
        var sync = new AdapterMaintenanceSyncRequest(
            "codex", "source-1", "batch-1", "commit", null,
            new AgentCapacityCounter("adapter_journal", 1, 10, 1));
        var request = ProtocolEnvelope.Create("sync-request", "maintenance_sync", sync);
        AgentProtocolValidator.Validate(request);
        var copy = ProtocolEnvelope.Parse(request.ToJson()).DeserializePayload<AdapterMaintenanceSyncRequest>();
        Assert.Equal(sync.SourceType, copy.SourceType);
        Assert.Equal(sync.SourceInstance, copy.SourceInstance);
        Assert.Equal(sync.AcknowledgedBatchId, copy.AcknowledgedBatchId);
        Assert.Equal(sync.AcknowledgedPhase, copy.AcknowledgedPhase);
        Assert.Equal(sync.AdapterJournal, copy.AdapterJournal);
        Assert.Contains("\"acknowledged_batch_id\"", request.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"acknowledged_phase\"", request.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"adapter_journal\"", request.ToJson(), StringComparison.Ordinal);

        var responses = new[]
        {
            ProtocolEnvelope.Create("prepared", "maintenance_sync", new { result = "prepared", acknowledged_batch_id = "batch-1", acknowledged_phase = "prepare", safe_error = (string?)null }),
            ProtocolEnvelope.Create("committed", "maintenance_sync", new { result = "committed", acknowledged_batch_id = "batch-1", acknowledged_phase = "commit", safe_error = (string?)null }),
            ProtocolEnvelope.Create("rejected", "maintenance_sync", new { result = "rejected", acknowledged_batch_id = "batch-1", acknowledged_phase = "commit", safe_error = "adapter rejected safely" }),
        };

        foreach (var response in responses)
        {
            AgentProtocolValidator.ValidateResponse(response);
            Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(response));
        }
    }

    [Fact]
    public void Maintenance_payloads_expose_only_opaque_archive_fields()
    {
        var item = Item(endedAt: At, executionId: "execution-1");
        var json = ProtocolEnvelope.Create(
            "privacy", "archive_prepare", new AgentArchivePrepareRequest("batch-1", new[] { item }, Sha256)).ToJson();

        Assert.Contains("\"execution_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ended_at\"", json, StringComparison.Ordinal);
        Assert.Contains("\"summary_sha256\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"summary\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"prompt\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"path\"", json, StringComparison.OrdinalIgnoreCase);
        AgentProtocolValidator.Validate(ProtocolEnvelope.Parse(json));
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("user_text")]
    public void Maintenance_payloads_reject_unknown_content_fields_recursively(string forbiddenField)
    {
        var nested = new Dictionary<string, object?> { [forbiddenField] = "must not cross the protocol" };
        var request = ProtocolEnvelope.Create("content-request", "archive_prepare", new Dictionary<string, object?>
        {
            ["batch_id"] = "batch-1",
            ["items"] = new[] { Item(endedAt: At, executionId: "execution-1") },
            ["batch_sha256"] = Sha256,
            ["metadata"] = nested,
        });
        var response = ProtocolEnvelope.Create("content-response", "maintenance_status", new Dictionary<string, object?>
        {
            ["result"] = "status",
            ["counters"] = Array.Empty<AgentCapacityCounter>(),
            ["metadata"] = nested,
        });

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(request));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(response));
    }

    [Fact]
    public void Maintenance_sync_none_rejects_an_unknown_operation_field()
    {
        var noneWithUnknownOperation = ProtocolEnvelope.Create("none-invalid", "maintenance_sync", new
        {
            result = "none",
            operation = "purge_everything",
        });

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(noneWithUnknownOperation));
    }

    [Fact]
    public void Archive_protocol_item_requires_an_explicit_ended_at_timestamp()
    {
        Assert.DoesNotContain(
            typeof(AgentArchiveProtocolItem).GetConstructors(),
            constructor => constructor.GetParameters().Length == 7);

        var itemWithoutTimestamp = new Dictionary<string, object?>
        {
            ["source_type"] = "codex",
            ["source_instance"] = "source-1",
            ["task_id"] = "task-1",
            ["dispatch_request_id"] = "dispatch-1",
            ["final_sequence"] = 1,
            ["final_status"] = "completed",
            ["execution_id"] = "execution-1",
            ["summary_sha256"] = Sha256,
        };
        var envelope = ProtocolEnvelope.Create("missing-ended-at", "archive_prepare", new Dictionary<string, object?>
        {
            ["batch_id"] = "batch-1",
            ["items"] = new[] { itemWithoutTimestamp },
            ["batch_sha256"] = Sha256,
        });

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(ProtocolEnvelope.Parse(envelope.ToJson())));
    }

    [Fact]
    public void Archive_prepare_round_trips_a_bounded_protocol_item()
    {
        var item = Item();
        var request = new AgentArchivePrepareRequest("batch-1", new[] { item }, Sha256);
        var envelope = ProtocolEnvelope.Create("prepare-1", "archive_prepare", request);

        AgentProtocolValidator.Validate(envelope);
        var copy = ProtocolEnvelope.Parse(envelope.ToJson()).DeserializePayload<AgentArchivePrepareRequest>();

        var copiedItem = Assert.Single(copy.Items);
        Assert.Equal(item.SourceType, copiedItem.SourceType);
        Assert.Equal(item.SourceInstance, copiedItem.SourceInstance);
        Assert.Equal(item.TaskId, copiedItem.TaskId);
        Assert.Equal(item.DispatchRequestId, copiedItem.DispatchRequestId);
        Assert.Equal(item.FinalSequence, copiedItem.FinalSequence);
        Assert.Equal(item.FinalStatus, copiedItem.FinalStatus);
        Assert.Equal(item.EndedAt, copiedItem.EndedAt);
        Assert.Equal(item.ExecutionId, copiedItem.ExecutionId);
        Assert.Equal(item.SummarySha256, copiedItem.SummarySha256);
        Assert.Equal(request.BatchId, copy.BatchId);
        Assert.Equal(request.BatchSha256, copy.BatchSha256);
        Assert.Equal(request.Items, copy.Items);
        Assert.Equal(item, copiedItem);
    }

    [Fact]
    public void Archive_prepare_rejects_empty_or_oversized_item_batches()
    {
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("empty", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", Array.Empty<AgentArchiveProtocolItem>(), Sha256))));

        var items = Enumerable.Range(0, 129)
            .Select(index => Item(taskId: $"task-{index}", dispatchRequestId: $"dispatch-{index}"))
            .ToArray();

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("large", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", items, Sha256))));
    }

    [Fact]
    public void Archive_prepare_accepts_exactly_128_items_after_json_parse()
    {
        var items = Enumerable.Range(0, 128)
            .Select(index => Item(taskId: $"task-{index}", dispatchRequestId: $"dispatch-{index}", executionId: $"execution-{index}"))
            .ToArray();
        var envelope = ProtocolEnvelope.Create("maximum", "archive_prepare", new AgentArchivePrepareRequest("batch-1", items, Sha256));

        AgentProtocolValidator.Validate(ProtocolEnvelope.Parse(envelope.ToJson()));
    }

    [Fact]
    public void Archive_prepare_rejects_duplicate_identity_even_when_outcome_differs()
    {
        var duplicate = Item(finalSequence: 1, finalStatus: "completed");
        var changed = duplicate with { FinalSequence = 2, FinalStatus = "failed" };

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("duplicate", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", new[] { duplicate, changed }, Sha256))));
    }

    [Fact]
    public void Archive_item_rejects_negative_sequence_invalid_status_and_noncanonical_hash()
    {
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("negative", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", new[] { Item(finalSequence: -1) }, Sha256))));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("status", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", new[] { Item(finalStatus: "running") }, Sha256))));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("hash", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", new[] { Item(summarySha256: Sha256.ToLowerInvariant()) }, Sha256))));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("batch-hash", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", new[] { Item() }, Sha256.ToLowerInvariant()))));
    }

    [Theory]
    [InlineData("dispatching")]
    [InlineData("active")]
    [InlineData("attention")]
    [InlineData("dispatch_outcome_unknown")]
    [InlineData("abandoned")]
    [InlineData("Completed")]
    [InlineData("")]
    public void Archive_item_accepts_only_lowercase_terminal_statuses(string status)
    {
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("status", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", new[] { Item(finalStatus: status) }, Sha256))));
    }

    [Theory]
    [InlineData("C:\\Users\\alice\\archive")]
    [InlineData("\\\\server\\share\\archive")]
    [InlineData("/home/alice/archive")]
    [InlineData("sk-proj-1234567890")]
    [InlineData("AKIA123456789012")]
    [InlineData("password:secret")]
    public void Maintenance_identity_and_hash_fields_reject_sensitive_text(string unsafeValue)
    {
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("unsafe", "archive_prepare", new AgentArchivePrepareRequest(
                "batch-1", new[] { Item(taskId: unsafeValue) }, Sha256))));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("unsafe-batch", "archive_commit", new AgentArchiveCommitRequest(unsafeValue, Sha256))));
    }

    [Fact]
    public void Maintenance_counters_reject_negative_or_over_limit_values()
    {
        var valid = new AgentMaintenanceStatusResponse(
            new[] { new AgentCapacityCounter("adapter_journal", 1, 10, 1) }, null, null, null);
        AgentProtocolValidator.ValidateResponse(ProtocolEnvelope.Create(
            "status", "maintenance_status", new { result = "status", counters = valid.Counters }));

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("negative", "maintenance_status", new
            {
                result = "status",
                counters = new[] { new AgentCapacityCounter("journal", -1, 10, 0) },
            })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("used", "maintenance_status", new
            {
                result = "status",
                counters = new[] { new AgentCapacityCounter("journal", 11, 10, 0) },
            })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("archivable", "maintenance_status", new
            {
                result = "status",
                counters = new[] { new AgentCapacityCounter("journal", 1, 10, 11) },
            })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("limit", "maintenance_status", new
            {
                result = "status",
                counters = new[] { new AgentCapacityCounter("journal", 0, 0, 0) },
            })));
    }

    [Fact]
    public void Safe_errors_are_bounded_and_sanitized_at_the_protocol_boundary()
    {
        var oversized = new string('x', 513);
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("oversized", "maintenance_status", new
            {
                result = "status",
                counters = Array.Empty<AgentCapacityCounter>(),
                safe_error = oversized,
            })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("secret", "maintenance_status", new
            {
                result = "status",
                counters = Array.Empty<AgentCapacityCounter>(),
                safe_error = "token=secret-value",
            })));
    }

    [Fact]
    public void Adapter_sync_requires_coherent_acknowledgement_fields()
    {
        var valid = new AdapterMaintenanceSyncRequest(
            "codex", "source-1", "batch-1", "prepare", null,
            new AgentCapacityCounter("adapter_journal", 1, 10, 1));
        AgentProtocolValidator.Validate(ProtocolEnvelope.Create("sync", "maintenance_sync", valid));

        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("missing-phase", "maintenance_sync", valid with { AcknowledgedPhase = null })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("missing-batch", "maintenance_sync", valid with { AcknowledgedBatchId = null })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("bad-phase", "maintenance_sync", valid with { AcknowledgedPhase = "archive" })));
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.Validate(
            ProtocolEnvelope.Create("safe-error", "maintenance_sync", valid with { SafeError = new string('x', 513) })));
    }

    [Fact]
    public void Maintenance_sync_rejects_unknown_relay_operation()
    {
        Assert.Throws<AgentProtocolValidationException>(() => AgentProtocolValidator.ValidateResponse(
            ProtocolEnvelope.Create("unknown", "maintenance_sync", new
            {
                result = "status",
                operation = "purge_everything",
            })));
    }

    private static AgentArchiveProtocolItem Item(
        string sourceType = "codex",
        string sourceInstance = "source-1",
        string taskId = "task-1",
        string dispatchRequestId = "dispatch-1",
        long finalSequence = 1,
        string finalStatus = "completed",
        string summarySha256 = Sha256,
        DateTimeOffset? endedAt = null,
        string? executionId = null) =>
        new(sourceType, sourceInstance, taskId, dispatchRequestId, finalSequence, finalStatus,
            endedAt ?? At, executionId ?? "execution-1", summarySha256);
}
