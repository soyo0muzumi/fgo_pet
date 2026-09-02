# Phase 5.3 Agent 配置引导 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让普通用户在设置页通过项目名称完成 Agent 来源批准、项目 allowlist 保存、连接测试、撤销、重新配对和脱敏诊断复制，无需编辑 JSON、复制目标 ID 或运行命令。

**Architecture:** Adapter 继续通过现有 target list CLI 拥有目标目录权威状态；App/Infrastructure 新增只读、受控的 companion 查询服务，只把 TargetId、DisplayName 和 ReadOnly 投影给 App，不把目录送入 Relay 或持久化。App ViewModel 将目标 ID 映射为项目名称多选项，权限写入继续复用现有的来源实例级 update_permissions 边界；WPF 页面只负责用户确认、展示和剪贴板操作。

**Tech Stack:** .NET 8 / C# nullable reference types, WPF/XAML, CommunityToolkit.Mvvm, System.Diagnostics.Process, existing Relay/Adapter JSON contracts, xUnit.

**Spec:** docs/superpowers/specs/2026-09-02-phase5-3-guided-agent-configuration-design.md

## Global Constraints

- UI 不要求用户复制或输入不透明 TargetId，也不展示本地绝对路径。
- TargetId 只作为内部授权键保存和传输。
- Directory 不得进入 App 持久化模型、Relay message、业务数据库、备份清单、诊断摘要或普通日志。
- 默认仍为拒绝：未批准来源、未选择项目或已撤销权限均不能派发。
- 不得提供“默认允许全部项目”快捷方式。
- 诊断摘要不得包含凭据、Prompt、对话正文、任务正文、目标 ID 原文、本地绝对路径、用户名、环境变量、命令行参数或日志原文。
- 本切片不安装、升级、修复、卸载 Relay/Adapter 或 Codex 插件，不修改 PATH，不写入用户项目配置。
- Adapter 查询必须使用固定的 target list 参数，不经过 shell，不接受用户拼接参数，并限制超时、输出大小和目标数量。
- 查询失败时必须保留旧的 AllowedTargetIds，禁止“空列表保存即清空”已有授权。
- 每个任务先写失败测试，再实现最小代码，测试通过后独立提交；不得在测试通过前声称完成。

## File Map

- Create src/FgoPet.Core/Agents/AgentTargetCatalog.cs: 跨层目标目录状态、无路径目标摘要和查询接口。
- Create src/FgoPet.Core/Agents/AgentDiagnosticSummary.cs: 只输出安全诊断字段的纯函数。
- Create src/FgoPet.Infrastructure/Agents/CodexTargetCatalogClient.cs: 定位并调用随应用发布的 Adapter companion，解析并裁剪 target list 输出。
- Modify src/FgoPet.App/Bootstrap/ServiceRegistration.cs: 注册目标目录查询服务，并把它注入设置 ViewModel。
- Create src/FgoPet.App/ViewModels/AgentTargetOptionViewModel.cs: 项目名称、只读标记、选择状态和不可解析授权的 UI 模型。
- Modify src/FgoPet.App/ViewModels/AgentConnectionSettingsViewModel.cs: 加载目标、映射 allowlist、重新配对、生成诊断文本和更新引导文案。
- Modify src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml: 用项目名称多选列表替换原始 ID 文本框，并增加刷新目标、重新配对和诊断复制入口。
- Modify src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml.cs: 处理确认对话框、目标刷新、重新配对和剪贴板复制。
- Create tests/FgoPet.Core.Tests/Agents/AgentDiagnosticSummaryTests.cs: 诊断脱敏合同测试。
- Create tests/FgoPet.Infrastructure.Tests/Agents/CodexTargetCatalogClientTests.cs: Adapter 输出解析、进程错误和边界测试。
- Modify tests/FgoPet.App.Tests/Settings/AgentConnectionSettingsViewModelTests.cs: 多选映射、旧授权保留、重新配对和诊断测试。
- Modify tests/FgoPet.Windows.Tests/Settings/AgentConnectionPageTests.cs: 页面控件、项目名称展示、无 ID 输入框和操作入口测试。

---

### Task 1: 定义目标目录结果合同和安全诊断生成器

**Files:**
- Create: src/FgoPet.Core/Agents/AgentTargetCatalog.cs
- Create: src/FgoPet.Core/Agents/AgentDiagnosticSummary.cs
- Test: tests/FgoPet.Core.Tests/Agents/AgentDiagnosticSummaryTests.cs

**Interfaces:**
- Produces AgentTargetDescriptor(string TargetId, string DisplayName, bool IsReadOnly)，不含 Directory。
- Produces AgentTargetCatalogStatus values Available、AdapterNotInstalled、AdapterUnavailable、TimedOut、InvalidResponse。
- Produces AgentTargetCatalogResult(AgentTargetCatalogStatus Status, IReadOnlyList<AgentTargetDescriptor> Targets, string? SafeError = null) and IAgentTargetCatalog.ListAsync(CancellationToken)。
- Produces AgentDiagnosticSummary.Build(AgentRelaySnapshot snapshot, AgentTargetCatalogResult catalog, DateTimeOffset observedAtUtc) returning a safe multiline string.

- [ ] **Step 1: Write the failing diagnostic tests**

测试必须构造包含凭据形状实例名和路径形状目标 ID 的 snapshot，然后只允许断言状态、数量和协议版本出现在文本中：

~~~csharp
[Fact]
public void Diagnostic_contains_counts_but_no_paths_or_target_ids()
{
    var snapshot = new AgentRelaySnapshot(
        AgentRelayConnectionState.Connected, true, true, true,
        DateTimeOffset.Parse("2026-09-02T00:00:00Z"), [],
        [new AgentApprovedSource("codex", "instance-secret", "Codex", "1", true,
            ["target-secret", @"C:\private\project"], true)],
        "relay_offline");
    var catalog = new AgentTargetCatalogResult(
        AgentTargetCatalogStatus.Available,
        [new AgentTargetDescriptor("target-secret", "Project", false)],
        "relay_offline");

    var text = AgentDiagnosticSummary.Build(snapshot, catalog,
        DateTimeOffset.Parse("2026-09-02T01:02:03Z"));

    Assert.Contains("Connected", text, StringComparison.Ordinal);
    Assert.Contains("target_count=1", text, StringComparison.Ordinal);
    Assert.DoesNotContain("target-secret", text, StringComparison.Ordinal);
    Assert.DoesNotContain(@"C:\private\project", text, StringComparison.Ordinal);
    Assert.DoesNotContain("instance-secret", text, StringComparison.Ordinal);
    Assert.DoesNotContain("relay_offline", text, StringComparison.Ordinal);
}

[Fact]
public void Empty_catalog_produces_safe_zero_counts()
{
    var text = AgentDiagnosticSummary.Build(
        AgentRelaySnapshot.Disabled,
        new AgentTargetCatalogResult(AgentTargetCatalogStatus.AdapterUnavailable, [], "adapter_query_failed"),
        DateTimeOffset.Parse("2026-09-02T01:02:03Z"));

    Assert.Contains("target_count=0", text, StringComparison.Ordinal);
    Assert.DoesNotContain("adapter_query_failed", text, StringComparison.Ordinal);
}
~~~

- [ ] **Step 2: Run the focused tests and verify they fail**

Run: dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --filter "FullyQualifiedName~AgentDiagnosticSummary"

Expected: FAIL because the target contract and diagnostic builder do not exist.

- [ ] **Step 3: Implement the minimal contracts and builder**

Keep the shared target descriptor path-free. The builder emits stable key/value lines for protocol version, connection state, online flags, source count, target count, selected count, read-only count, catalog status, an allowlisted safe error category, and UTC observation time. Hash source instance identifiers with SHA-256 and emit only a short fixed-length digest; never iterate or format AllowedTargetIds.

~~~csharp
public sealed record AgentTargetDescriptor(string TargetId, string DisplayName, bool IsReadOnly);

public enum AgentTargetCatalogStatus
{
    Available, AdapterNotInstalled, AdapterUnavailable, TimedOut, InvalidResponse,
}

public sealed record AgentTargetCatalogResult(
    AgentTargetCatalogStatus Status,
    IReadOnlyList<AgentTargetDescriptor> Targets,
    string? SafeError = null)
{
    public bool IsAvailable => Status == AgentTargetCatalogStatus.Available;
}

public interface IAgentTargetCatalog
{
    Task<AgentTargetCatalogResult> ListAsync(CancellationToken cancellationToken = default);
}
~~~

Use only these diagnostic error categories: none, adapter_not_installed, adapter_unavailable, adapter_timeout, adapter_invalid_response, relay_offline, version_mismatch, and unknown_error.

- [ ] **Step 4: Run the focused tests and verify they pass**

Run: dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --filter "FullyQualifiedName~AgentDiagnosticSummary"

Expected: PASS with no path-like or identifier-like values in the generated output.

- [ ] **Step 5: Commit the contract and diagnostic unit**

~~~powershell
git add src/FgoPet.Core/Agents/AgentTargetCatalog.cs src/FgoPet.Core/Agents/AgentDiagnosticSummary.cs tests/FgoPet.Core.Tests/Agents/AgentDiagnosticSummaryTests.cs
git commit -m "feat(phase5): add safe agent target contracts"
~~~

### Task 2: Implement the controlled Adapter target-list client

**Files:**
- Create: src/FgoPet.Infrastructure/Agents/CodexTargetCatalogClient.cs
- Test: tests/FgoPet.Infrastructure.Tests/Agents/CodexTargetCatalogClientTests.cs

**Interfaces:**
- Consumes RelayRuntimeOptions and the Task 1 target contract.
- Produces CodexTargetCatalogClient : IAgentTargetCatalog with ListAsync(CancellationToken).
- Exposes a testable Parse(string json) returning only AgentTargetDescriptor values; the private wire record may read Directory for validation but never returns it.
- The constructor accepts an optional test runner with exact type Func<CancellationToken, Task<(int ExitCode, string Stdout)>>?.

- [ ] **Step 1: Write failing parser and process-boundary tests**

Cover the current Adapter JSON shape and required rejection cases:

~~~csharp
[Fact]
public void Parse_projects_adapter_targets_without_exposing_directory()
{
    var json = "[{\"TargetId\":\"target-1\",\"DisplayName\":\"Project\",\"Directory\":\"C:\\\\work\\\\project\",\"ReadOnly\":true}]";

    var result = CodexTargetCatalogClient.Parse(json);

    var target = Assert.Single(result);
    Assert.Equal("target-1", target.TargetId);
    Assert.Equal("Project", target.DisplayName);
    Assert.True(target.IsReadOnly);
    Assert.DoesNotContain("Directory", target.ToString(), StringComparison.OrdinalIgnoreCase);
}

[Theory]
[InlineData("[{\"TargetId\":\"C:\\\\path\",\"DisplayName\":\"Project\",\"Directory\":\"C:\\\\work\",\"ReadOnly\":false}]")]
[InlineData("[{\"TargetId\":\"target-1\",\"DisplayName\":\"bad\\nname\",\"Directory\":\"C:\\\\work\",\"ReadOnly\":false}]")]
public void Parse_rejects_unsafe_catalog_entries(string json)
{
    Assert.Throws<InvalidDataException>(() => CodexTargetCatalogClient.Parse(json));
}
~~~

Also inject fake runners and assert non-zero exit, internal timeout, oversized stdout, malformed JSON, duplicate IDs, missing fields, and missing companion executable map to safe statuses without returning stderr, exception text, or paths.

- [ ] **Step 2: Run the focused Infrastructure tests and verify they fail**

Run: dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CodexTargetCatalogClient"

Expected: FAIL because the client and parser do not exist.

- [ ] **Step 3: Implement the minimal client**

Use the injected runner in tests. The production runner must derive the sibling Adapter path from RelayRuntimeOptions.RelayExecutablePath, return AdapterNotInstalled before process start if absent, and create ProcessStartInfo with UseShellExecute=false, CreateNoWindow=true, redirected stdout/stderr, fixed arguments target and list, and only FGO_PET_STATE_ROOT/FGO_PET_PIPE_SUFFIX environment variables. Enforce a five-second linked timeout and one-megabyte stdout cap. Suppress stderr and map failures to the safe enum values. Parse case-insensitively, require at most 256 entries, bounded non-empty display names, opaque IDs using AgentPayloadSanitizer.ContainsForbiddenText, and distinct IDs. The shared result must contain only TargetId, DisplayName, and ReadOnly.

~~~csharp
public sealed class CodexTargetCatalogClient : IAgentTargetCatalog
{
    public CodexTargetCatalogClient(
        RelayRuntimeOptions options,
        Func<CancellationToken, Task<(int ExitCode, string Stdout)>>? runner = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? RunAdapterListAsync;
    }

    public async Task<AgentTargetCatalogResult> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetAdapterPath();
            if (!File.Exists(path))
                return new(AgentTargetCatalogStatus.AdapterNotInstalled, [], "adapter_not_installed");
            var result = await _runner(cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return new(AgentTargetCatalogStatus.AdapterUnavailable, [], "adapter_unavailable");
            return new(AgentTargetCatalogStatus.Available, Parse(result.Stdout));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(AgentTargetCatalogStatus.TimedOut, [], "adapter_timeout"); }
        catch (InvalidDataException)
        { return new(AgentTargetCatalogStatus.InvalidResponse, [], "adapter_invalid_response"); }
        catch (IOException)
        { return new(AgentTargetCatalogStatus.AdapterUnavailable, [], "adapter_unavailable"); }
    }
}
~~~

- [ ] **Step 4: Run the focused Infrastructure tests and verify they pass**

Run: dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CodexTargetCatalogClient"

Expected: PASS for valid output, malformed output, bounds, timeout, exit-code, and missing-executable cases.

- [ ] **Step 5: Commit the Adapter query client**

~~~powershell
git add src/FgoPet.Infrastructure/Agents/CodexTargetCatalogClient.cs tests/FgoPet.Infrastructure.Tests/Agents/CodexTargetCatalogClientTests.cs
git commit -m "feat(phase5): query adapter targets safely"
~~~

### Task 3: Replace the raw ID editor with a project-name target editor

**Files:**
- Create: src/FgoPet.App/ViewModels/AgentTargetOptionViewModel.cs
- Modify: src/FgoPet.App/ViewModels/AgentConnectionSettingsViewModel.cs
- Test: tests/FgoPet.App.Tests/Settings/AgentConnectionSettingsViewModelTests.cs

**Interfaces:**
- Consumes AgentTargetDescriptor from Task 1.
- Produces AgentTargetOptionViewModel with internal TargetId, DisplayName, IsReadOnly, IsSelected, and IsResolved.
- AgentApprovedSourceViewModel produces ObservableCollection<AgentTargetOptionViewModel> Targets, HasTargets, HasUnresolvedTargets, AllowedTargetIds, ApplyCatalog(IReadOnlyList<AgentTargetDescriptor>), and RemoveUnresolvedTargets().

- [ ] **Step 1: Write failing App ViewModel tests**

Add tests for exact ID-to-name mapping, preserving unknown IDs, unchecking resolved projects, and explicit removal of unresolved authorizations:

~~~csharp
[Fact]
public void Applying_catalog_maps_saved_ids_to_names_and_preserves_unknown_ids()
{
    var editor = new AgentApprovedSourceViewModel(
        new AgentApprovedSource("codex", "instance-1", "Codex", "1", true,
            ["known-id", "missing-id"], true));

    editor.ApplyCatalog([new AgentTargetDescriptor("known-id", "Project A", false)]);

    Assert.Equal("Project A", Assert.Single(editor.Targets).DisplayName);
    Assert.True(Assert.Single(editor.Targets).IsSelected);
    Assert.True(editor.HasUnresolvedTargets);
    Assert.Equal(new[] { "known-id", "missing-id" }, editor.AllowedTargetIds);
}

[Fact]
public void Unchecking_a_project_does_not_clear_unresolved_authorization()
{
    var editor = new AgentApprovedSourceViewModel(
        new AgentApprovedSource("codex", "instance-1", "Codex", "1", true,
            ["known-id", "missing-id"], true));
    editor.ApplyCatalog([new AgentTargetDescriptor("known-id", "Project A", false)]);
    Assert.Single(editor.Targets).IsSelected = false;

    Assert.Equal(new[] { "missing-id" }, editor.AllowedTargetIds);
}

[Fact]
public void Removing_unresolved_authorizations_is_explicit()
{
    var editor = new AgentApprovedSourceViewModel(
        new AgentApprovedSource("codex", "instance-1", "Codex", "1", true,
            ["missing-id"], true));
    editor.ApplyCatalog([]);

    Assert.True(editor.RemoveUnresolvedTargets());
    Assert.Empty(editor.AllowedTargetIds);
}
~~~

Migrate the current administration-action test from TargetIdsText assignment to ApplyCatalog plus checkbox selection. Preserve the existing offline-runtime dirty-editor regression test.

- [ ] **Step 2: Run the focused App tests and verify they fail**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "FullyQualifiedName~AgentConnectionSettingsViewModel"

Expected: FAIL because the target option model and mapping methods do not exist.

- [ ] **Step 3: Implement the target option and editor mapping**

Store unresolved IDs in a private HashSet<string> and never expose them through a UI property. AllowedTargetIds returns selected resolved IDs followed by unresolved IDs, distinct with ordinal comparison. ApplyCatalog creates options, selects only exact persisted IDs, records persisted IDs not returned by the catalog, and preserves a dirty editor during runtime status refresh. Checkbox changes and explicit unresolved removal mark the editor dirty.

~~~csharp
public sealed partial class AgentTargetOptionViewModel : ObservableObject
{
    internal AgentTargetOptionViewModel(AgentTargetDescriptor target, bool isSelected)
    {
        TargetId = target.TargetId;
        DisplayName = target.DisplayName;
        IsReadOnly = target.IsReadOnly;
        _isSelected = isSelected;
    }

    internal string TargetId { get; }
    public string DisplayName { get; }
    public bool IsReadOnly { get; }
    public bool IsResolved => true;

    [ObservableProperty]
    private bool _isSelected;
}
~~~

Keep AgentApprovedSourceViewModel.AllowedTargetIds as the only property consumed by SaveSourceAsync; do not alter the Relay permission request shape.

- [ ] **Step 4: Run the focused App tests and verify they pass**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "FullyQualifiedName~AgentConnectionSettingsViewModel"

Expected: PASS, including global-save, source-instance, and offline-editor regressions after their assertions are migrated from TargetIdsText.

- [ ] **Step 5: Commit the target editor model**

~~~powershell
git add src/FgoPet.App/ViewModels/AgentTargetOptionViewModel.cs src/FgoPet.App/ViewModels/AgentConnectionSettingsViewModel.cs tests/FgoPet.App.Tests/Settings/AgentConnectionSettingsViewModelTests.cs
git commit -m "feat(phase5): replace agent target ids with project selection"
~~~

### Task 4: Load the catalog, add re-pair/diagnostic actions, and wire dependency injection

**Files:**
- Modify: src/FgoPet.App/ViewModels/AgentConnectionSettingsViewModel.cs
- Modify: src/FgoPet.App/Bootstrap/ServiceRegistration.cs
- Modify: tests/FgoPet.App.Tests/Settings/AgentConnectionSettingsViewModelTests.cs

**Interfaces:**
- Consumes IAgentTargetCatalog, AgentDiagnosticSummary, existing IAgentRelayAdministration, and existing IAgentRelayRuntime.
- Produces RefreshTargetsAsync(CancellationToken), RePairSourceAsync(AgentApprovedSourceViewModel, CancellationToken), and BuildDiagnosticText() on the settings ViewModel.
- SaveSourceAsync continues calling UpdatePermissionsAsync(source.SourceType, source.SourceInstanceId, source.AllowedTargetIds, source.IsEnabled, cancellationToken).

- [ ] **Step 1: Write failing orchestration tests**

Add tests for catalog success, catalog failure retaining authorization, re-pair ordering, and diagnostic redaction:

~~~csharp
[Fact]
public async Task Refresh_loads_catalog_and_applies_project_names()
{
    var source = new AgentApprovedSource("codex", "instance-1", "Codex", "1", true, ["target-1"], true);
    var catalog = new FakeTargetCatalog(new AgentTargetCatalogResult(
        AgentTargetCatalogStatus.Available,
        [new AgentTargetDescriptor("target-1", "Project A", false)]));
    using var viewModel = CreateViewModel(Snapshot(approved: source), catalog);

    await viewModel.RefreshAsync();

    var target = Assert.Single(Assert.Single(viewModel.ApprovedSources).Targets);
    Assert.Equal("Project A", target.DisplayName);
    Assert.True(target.IsSelected);
}

[Fact]
public async Task Catalog_failure_does_not_clear_existing_authorization()
{
    var source = new AgentApprovedSource("codex", "instance-1", "Codex", "1", true, ["target-1"], true);
    using var viewModel = CreateViewModel(
        Snapshot(approved: source),
        new FakeTargetCatalog(new AgentTargetCatalogResult(
            AgentTargetCatalogStatus.AdapterUnavailable, [])));

    await viewModel.RefreshTargetsAsync();

    Assert.Equal(new[] { "target-1" },
        Assert.Single(viewModel.ApprovedSources).AllowedTargetIds);
}

[Fact]
public async Task RePair_revokes_and_restarts_enabled_runtime()
{
    var source = new AgentApprovedSource("codex", "instance-1", "Codex", "1", true, [], true);
    var runtime = new FakeRuntime(Snapshot(approved: source));
    var administration = new FakeAdministration(Snapshot(approved: source));
    using var viewModel = CreateViewModel(
        Snapshot(approved: source), new FakeTargetCatalog(null), administration, runtime);
    viewModel.Enabled = true;

    await viewModel.RePairSourceAsync(Assert.Single(viewModel.ApprovedSources));

    Assert.Equal(("codex", "instance-1"), Assert.Single(administration.Revocations));
    Assert.Equal(new[] { false, true }, runtime.EnabledCalls);
}
~~~

FakeTargetCatalog returns a supplied result, or AdapterNotInstalled with an empty list when null. Update the test factory instead of breaking existing constructor calls.

- [ ] **Step 2: Run the focused App tests and verify they fail**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "FullyQualifiedName~AgentConnectionSettingsViewModel"

Expected: FAIL because the ViewModel has no catalog dependency or target/re-pair methods.

- [ ] **Step 3: Implement orchestration and DI**

Add optional IAgentTargetCatalog? targetCatalog = null to preserve legacy/unit-test construction. Register the production client after RelayRuntimeOptions:

~~~csharp
.AddSingleton<IAgentTargetCatalog>(provider =>
    new CodexTargetCatalogClient(provider.GetRequiredService<RelayRuntimeOptions>()))
~~~

Implement private ApplyTargetCatalog(AgentTargetCatalogResult result) to store the latest result, call ApplyCatalog(result.Targets) on every approved source, and raise target/status properties. RefreshAsync and TestConnectionAsync refresh the Relay snapshot and then query the catalog once; RefreshTargetsAsync only queries the catalog. A failed query updates status with the safe category but does not mutate existing allowlists.

Implement RePairSourceAsync as one busy-gated operation: first call existing RevokeSourceAsync and wait for its Relay ACK; if global Enabled and _runtime exist, call SetEnabledAsync(false) then SetEnabledAsync(true) so the existing Adapter identity recovery loop observes the revocation; refresh the Relay snapshot once; report “旧授权已失效，等待适配器重新发起配对”，without claiming approval already happened. BuildDiagnosticText passes CurrentSnapshot, the last catalog result, and DateTimeOffset.UtcNow to AgentDiagnosticSummary.Build. Change InstallationGuidanceText to the approved project-name wording. Make all new operations obey the existing busy gate.

- [ ] **Step 4: Run the focused App tests and verify they pass**

Run: dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --filter "FullyQualifiedName~AgentConnectionSettingsViewModel"

Expected: PASS, including catalog success/failure, old authorization retention, source-instance permission updates, re-pair sequencing, and diagnostic redaction.

- [ ] **Step 5: Commit catalog orchestration and DI**

~~~powershell
git add src/FgoPet.App/ViewModels/AgentConnectionSettingsViewModel.cs src/FgoPet.App/Bootstrap/ServiceRegistration.cs tests/FgoPet.App.Tests/Settings/AgentConnectionSettingsViewModelTests.cs
git commit -m "feat(phase5): guide agent pairing and target permissions"
~~~

### Task 5: Update the WPF settings page and user actions

**Files:**
- Modify: src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml
- Modify: src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml.cs
- Test: tests/FgoPet.Windows.Tests/Settings/AgentConnectionPageTests.cs

**Interfaces:**
- Consumes AgentApprovedSourceViewModel.Targets, HasTargets, HasUnresolvedTargets, CanInteract, and Task 4 methods.
- Produces visible controls named 刷新项目, 复制诊断信息, and 重新配对 while retaining 保存权限, 撤销授权, 测试连接, and 批准.

- [ ] **Step 1: Write failing WPF rendering tests**

Extend the existing STA page test with a fake target catalog and an approved source containing one selected target. Assert that the visual tree contains the project name and new buttons, while the old ID text box and ID guidance are absent:

~~~csharp
Assert.Contains("Project A", Descendants(page).OfType<TextBlock>().Select(item => item.Text));
var buttons = Descendants(page).OfType<Button>().Select(button => button.Content?.ToString()).ToArray();
Assert.Contains("刷新项目", buttons);
Assert.Contains("复制诊断信息", buttons);
Assert.Contains("重新配对", buttons);
Assert.DoesNotContain("允许的项目 ID", Descendants(page).OfType<TextBlock>().Select(item => item.Text));
Assert.Empty(Descendants(page).OfType<TextBox>());
~~~

Also test that a read-only target renders a 只读 marker and unresolved authority does not render the raw ID.

- [ ] **Step 2: Run the focused Windows tests and verify they fail**

Run: dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --filter "FullyQualifiedName~AgentConnectionPageTests"

Expected: FAIL because the page still contains the multiline ID editor and no new controls.

- [ ] **Step 3: Implement the XAML target list and action handlers**

In the approved-source template, replace the ID TextBox with an ItemsControl bound to Targets. Each checkbox binds IsSelected and shows DisplayName plus a 只读 marker when IsReadOnly is true. Show safe empty/unresolved states without the ID values. Add a 刷新项目 button in the Agent section, 复制诊断信息 to the top card, and 重新配对 next to 撤销授权. Set AutomationProperties.Name for each new control and disable them while busy.

~~~csharp
private async void OnRefreshTargetsClick(object sender, RoutedEventArgs e) =>
    await RunUiOperationAsync(() => ViewModel?.RefreshTargetsAsync());

private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
{
    if (ViewModel is null) return;
    try
    {
        Clipboard.SetText(ViewModel.BuildDiagnosticText());
        ViewModel.ReportDiagnosticCopied();
    }
    catch (ExternalException)
    {
        ViewModel.ReportUiError();
    }
}
~~~

The re-pair handler must require a warning confirmation naming only the source display name, stating that old authorization is invalidated while business data remains, then call ViewModel.RePairSourceAsync(source). Do not show target IDs or paths in confirmation, error, or clipboard text.

- [ ] **Step 4: Run the focused Windows tests and verify they pass**

Run: dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --filter "FullyQualifiedName~AgentConnectionPageTests"

Expected: PASS with project names and action buttons visible, no raw ID editor, no path display, and no regression in the Phase 5.1 archive card.

- [ ] **Step 5: Commit the settings UI**

~~~powershell
git add src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml src/FgoPet.App/Views/Settings/AgentConnectionSettingsView.xaml.cs tests/FgoPet.Windows.Tests/Settings/AgentConnectionPageTests.cs
git commit -m "feat(phase5): add guided agent configuration UI"
~~~

### Task 6: Run the Phase 5.3 verification gate and privacy review

**Files:**
- Modify only if required by verification: files from Tasks 1–5.
- Test: all Phase 5.3 test files and existing affected Agent tests.

**Interfaces:**
- Consumes the complete implementation from Tasks 1–5.
- Produces verified build/test evidence; no new behavior unless a failed check requires a focused fix.

- [ ] **Step 1: Run focused project tests**

Run:

~~~powershell
dotnet test tests/FgoPet.Core.Tests/FgoPet.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentDiagnosticSummary"
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~CodexTargetCatalogClient|FullyQualifiedName~AgentRelayAdministration"
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentConnectionSettingsViewModel"
dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentConnectionPageTests"
~~~

Expected: all commands exit 0. If a check fails, add a regression test, fix only the scoped behavior, rerun the failed command, and do not proceed until it passes.

- [ ] **Step 2: Run the affected full test projects**

Run:

~~~powershell
dotnet test tests/FgoPet.Infrastructure.Tests/FgoPet.Infrastructure.Tests.csproj --no-restore
dotnet test tests/FgoPet.App.Tests/FgoPet.App.Tests.csproj --no-restore
dotnet test tests/FgoPet.Windows.Tests/FgoPet.Windows.Tests.csproj --no-restore
~~~

Expected: PASS without changing unrelated user-owned files.

- [ ] **Step 3: Build the Release application**

Run: dotnet build src/FgoPet.App/FgoPet.App.csproj -c Release --no-restore

Expected: exit 0, including the registered Adapter companion.

- [ ] **Step 4: Run source-level privacy checks**

Run:

~~~powershell
rg -n "Directory|TargetIdsText|允许的项目 ID|target add|local_path|working_directory" src/FgoPet.App src/FgoPet.Infrastructure tests/FgoPet.App.Tests tests/FgoPet.Windows.Tests
git diff --check HEAD~6..HEAD
~~~

Expected: no Directory binding or target-path copy in new UI/diagnostic code; the only allowed Directory reference is Adapter wire parsing and existing execution internals. Confirm the diagnostic tests assert absence of paths and raw target IDs.

- [ ] **Step 5: Commit any verification-only fix and record evidence**

If a focused fix is required, add the regression test and implementation together:

Stage only the exact implementation and regression-test files changed by the failed check; never use `git add .` or `git add -A` because the worktree may contain unrelated user files. Then commit:

~~~powershell
git commit -m "fix(phase5): close guided configuration verification gap"
~~~

Otherwise leave implementation commits unchanged and record the exact passing commands in the final handoff.

## Self-review checklist

- Spec sections 1–3 are covered by Tasks 1–5 and the explicit non-goals in Global Constraints.
- User flows are covered by Task 4 orchestration and Task 5 confirmations/buttons.
- Adapter path ownership and no-Relay-path boundary are covered by Task 2 path-free projection.
- Legacy allowlist compatibility and default-deny behavior are covered by Tasks 3–4.
- Re-pair authority ordering is covered by Task 4 and never synthesizes successful approval locally.
- Diagnostic privacy is covered by Task 1 and Task 6 checks.
- The plan contains no production implementation before a failing test and no installer work outside the approved Phase 5.3 slice.
