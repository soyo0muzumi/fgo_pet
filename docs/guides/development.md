# 开发者指南

## 环境与入口

- Windows 11；.NET SDK 8.0.x；PowerShell 7。
- 解决方案入口：`FgoPet.sln`。
- 应用入口：`src/FgoPet.App/`（csproj 位于此，源码已按模块落位到 `modules/` 与 `host/`）；共享领域与持久化分别位于 `src/FgoPet.Core/`、`src/FgoPet.Infrastructure/`。
- 功能模块及依赖方向从 `modules/README.md` 开始查阅；共享 WPF 资源位于 `ui-foundation/`。

## 构建与测试

```powershell
dotnet build FgoPet.sln -c Release -warnaserror
dotnet test FgoPet.sln -c Release --no-build -m:1
pwsh -File scripts/test-architecture.ps1
pwsh -File scripts/test-phase1.ps1
pwsh -File scripts/test-phase2.ps1
pwsh -File scripts/test-phase3-settings.ps1
pwsh -File scripts/test-phase4.ps1
```

`-m:1` 隔离不同测试项目的进程与输出竞争，不关闭 xUnit 内部并行或业务并发测试。常规 CI 与 Phase 4 的完整测试使用相同调度方式。

架构验证的正式入口是 `scripts/test-architecture.ps1`：先构建 Release App 及其依赖，再执行项目 DAG、显式策略解析自测、MSBuild 求值和真实程序集引用检查；失败返回非零结果。历史名称 `verify-project-dag`、`validate-policy`、`self-test-validate-policy` 不是本仓库可执行命令，不应继续作为验收入口。CI 的 Architecture 工作流调用同一脚本，策略不能由当前依赖图自动生成。

Phase 4 默认执行完整 restore、warning-as-error build、全解决方案测试、独立 win-x64 publish、隔离安装、真实 MCP smoke 和卸载。每次使用独有临时根与结果目录，不修改用户 PATH 或注册用户插件；卸载之后、外层清理之前检查安装文件移除、非安装文件和合成状态保留。TRX 与 `phase4-summary.json` 位于 `artifacts/validation/phase4-*`；摘要记录阶段、结果、清理状态和实际插件校验模式，不记录凭据或真实配对数据。

`Phase 4 acceptance` 工作流对相关脚本改动连续运行三次完整入口，任意一次失败立即停止，不重试到绿；每轮证据均保留。手工使用 `-SkipBuild` 或 `-PublishedSource` 只能证明实际执行的子集，不能充当该完整验收。干净 runner 未安装 Codex 外部校验器时沿用明确标注的 manifest fallback；这不等于外部校验器、真实 Codex 注册、真实模型或用户安装环境验收。

运行中的程序可能占用默认输出目录；此时使用仓库外的隔离 `--artifacts-path`，不要停止用户进程或清理用户数据。测试日志与生成物不得写入源码根目录。

## 模块与边界

- `modules/agent-integration/`：Provider-neutral Relay/协议/运行时与 Codex 参考适配器。
- `modules/speech/`：语音契约、合成适配、播放生命周期和设置界面；模型或设备不可用时不得影响离线桌宠功能。
- `modules/dialogue/`：对话编排、提示词组装与对话界面。
- `modules/memory/`：对话摘要与长期记忆候选。
- `modules/work/`：待办、执行与归档（含 Agent 派发入口）。
- `modules/focus/`：专注会话状态机、时间线与羁绊进度。
- `modules/character/`：角色包、立绘与个性化偏好。
- `host/`：桌面外壳（`DesktopShell`）与数据管理（`DataManagement`）。
- `platform/`：横切机制——事件管理、LLM 管线、SQLite 数据层、诊断与脱敏。
- 各模块职责、依赖方向与迁移状态见 `modules/README.md`；宿主与横切层见 `host/README.md`、`platform/README.md`。
- API Key 使用 Windows Credential Manager；配对状态使用受保护本机存储。日志、协议和发布包不得包含凭据、完整 Prompt、对话、终端输出或敏感路径。
- `.fgopetpack` 是独立纯数据交付物，不得嵌入应用 ZIP。

## 制作 Windows x64 ZIP 候选包

输出目录必须位于仓库之外且尚不存在。以下示例中的日期目录可按实际候选调整：

```powershell
dotnet restore FgoPet.sln
pwsh -NoProfile -File scripts/publish-release.ps1 -OutputRoot D:\fgo_unpack\release-candidate-YYYYMMDD-vX.Y.Z
pwsh -NoProfile -File scripts/verify-release.ps1 -CandidateRoot D:\fgo_unpack\release-candidate-YYYYMMDD-vX.Y.Z
pwsh -NoProfile -File scripts/test-release-candidate.ps1 -CandidateRoot D:\fgo_unpack\release-candidate-YYYYMMDD-vX.Y.Z -TempRoot D:\fgo_unpack\_workspace\release-acceptance
```

成功候选包含 `manifest.json`、`SHA256SUMS` 和 `app/FgoPet-win-x64-X.Y.Z.zip`。候选验收覆盖归档校验、解压、必需可执行文件、MCP smoke、重复安装模拟、卸载及隔离状态保留；它不代表签名、安装器、真实模型/Agent/TTS 或多设备人工验收完成。

## 提交与发布检查

1. `git status --short` 与 `git diff --check` 无意外内容。
2. 应用版本、Changelog 和发布说明一致。
3. 构建及适用测试已在待发布提交上重新执行。
4. 校验 ZIP 哈希与 manifest，确认没有源码、调试符号、日志、数据库、密钥或角色包。
5. 上传、创建 Release、打标签和 push 必须另行获得发布授权。
