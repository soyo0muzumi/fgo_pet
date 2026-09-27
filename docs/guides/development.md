# 开发者指南

## 环境与入口

- Windows 11；.NET SDK 8.0.x；PowerShell 7。
- 解决方案入口：`FgoPet.sln`。
- 应用组合根：`src/FgoPet.App/Composition/`。共享纯 .NET 角色、profile、presentation 与生命周期契约和对话执行机制位于 [`src/FgoPet.Kernel`](../../src/FgoPet.Kernel/README.md)；Windows 外壳与数据管理位于 [`src/FgoPet.Desktop`](../../src/FgoPet.Desktop/README.md)。
- 跨模块契约、存储与 Windows 实现位于 [`src/FgoPet.Platform`](../../src/FgoPet.Platform/README.md)；共享 WPF 内容与主题位于 [`src/FgoPet.UiSdk`](../../src/FgoPet.UiSdk/README.md)；静态能力目录与注册契约位于 [`src/FgoPet.Extensibility`](../../src/FgoPet.Extensibility/README.md)。
- 功能实现位于 `plugins/FgoPet.Plugin.*`，providers 位于 `plugins/providers/`。项目分类与允许的依赖方向以 [`tests/architecture/README.md`](../../tests/architecture/README.md) 和其中链接的 policy/baseline 为准。

## 构建与测试

```powershell
dotnet build FgoPet.sln -c Release -warnaserror
dotnet test FgoPet.sln -c Release --no-build -m:1
pwsh -File tools/scripts/test-architecture.ps1
pwsh -File tools/scripts/test-phase1.ps1
pwsh -File tools/scripts/test-phase2.ps1
pwsh -File tools/scripts/test-phase3-settings.ps1
pwsh -File tools/scripts/test-phase4.ps1
```

完整解决方案测试使用 `-m:1` 顺序调度测试项目，避免不同进程争用 WPF、SQLite 和构建输出；不关闭 xUnit 项目内部并行，也不删除业务并发测试。先构建再使用 `--no-build`，不要把旧产物当作当前源码的验证结果。

架构验证的正式入口是 `tools/scripts/test-architecture.ps1`，本地与 Architecture CI 使用同一脚本。它先构建 Release App，再执行架构测试（包括 MSBuild 实际求值的项目图、策略解析自测、编译程序集引用与呈现边界）；任一阶段失败均返回失败。历史名称 `verify-project-dag`、`validate-policy`、`self-test-validate-policy` 不是当前可执行命令，不应出现在已执行的验收清单中。

### Phase 4 完整验收

`test-phase4.ps1` 默认包含 restore、warning-as-error 构建、全解决方案测试、隔离发布、安装、MCP initialize/tools-list、卸载和清理。每次调用使用独立临时根和 TRX 目录，保留构建服务器隔离；安装固定跳过用户 PATH 与插件注册。配置、配对状态保留测试使用合成文件，不读取真实凭据或调用真实模型。

门禁检查安装的可执行文件、shim 与所有权哈希，卸载后检查这些自有文件及安装标记已移除，合成状态/配置及不属于安装器的文件保持不变。只有全部步骤及严格清理成功后，才在 `artifacts/validation/phase4-<id>/` 写入 `phase4-summary.json`；TRX 即使在后续打包失败时也保留。临时产物不加入版本库。

Phase 4 acceptance 工作流在同一 Windows runner 上顺序执行三次完整脚本，第一轮失败就停止，不将重复运行当作失败重试。每轮校验全部测试工程、用例结果和相同的测试清单，再验证打包与清理摘要。该门禁由安装/验收脚本或插件包相关 PR 触发，也可手动触发；普通 Tests 与 Architecture 门禁保持独立。

`-SkipBuild -PublishedSource <目录>` 仅适合单独检查已发布产物，摘要会明确标记未运行全量测试，不能据此宣称完整 Phase 4 通过。若没有安装外部 Codex 插件校验器，脚本会提示仅完成内置 manifest 校验；这不等价于外部校验器、真实 Codex 插件注册、受保护配对状态升级或正式安装包发行验收。

运行中的程序可能占用默认输出目录；此时使用仓库外的隔离 `--artifacts-path`，不要停止用户进程或清理用户数据。测试日志与生成物不得写入源码根目录。

## 模块与边界

- `src/FgoPet.App/Composition/`：应用 bootstrap 与组合根，装配外壳和各能力注册组件。
- `src/FgoPet.Kernel/`：纯 .NET 角色/profile、semantic presentation、process/context 生命周期，以及对话执行和中立契约；摘要与上下文相关执行机制依赖发布契约，不把长记忆状态纳入 Kernel。
- `src/FgoPet.Desktop/`：通用 WPF 外壳、窗口与数据管理流程；仍有少量到 Dialogue/Content/Memory 实现的显式引用，不能据此称模块边界已完全收敛。
- `src/FgoPet.Platform/`：中立契约、SQLite/JSON 存储基础、设置文档、Windows 凭据和屏幕布局实现；不拥有 LLM 管线或功能业务规则。
- `src/FgoPet.UiSdk/` 与 `src/FgoPet.Extensibility/`：共享 WPF/设置/紧凑内容契约与主题，以及静态能力目录和注册契约。
- [`Dialogue`](../../plugins/FgoPet.Plugin.Dialogue/README.md)：对话上下文、原消息、摘要/搜索投影、provider/storage 实现、模型设置和对话呈现。部分纯 .NET 对话执行机制编译于 Kernel。
- [`Memory`](../../plugins/FgoPet.Plugin.Memory/README.md)：长期记忆候选、复核、确认、查询与持久化；不拥有对话摘要。
- [`Todo`](../../plugins/FgoPet.Plugin.Todo/README.md)：Todo 状态、提案草稿、明确确认、持久化与归档；不消费 Agent 派发状态，也不提供已退役的提案/归档卡片 UI。
- [`Focus`](../../plugins/FgoPet.Plugin.Focus/README.md)、[`Speech`](../../plugins/FgoPet.Plugin.Speech/README.md) 与 [`Content/Desktop`](../../plugins/FgoPet.Plugin.Content/Desktop/README.md)：分别拥有专注与羁绊、语音播放与设置、角色包及个性化设置 UI。
- [`Agent Backend`](../../plugins/providers/FgoPet.Provider.Codex/README.md)：协议、授权、配对、受保护状态、运行期、协调和归档后端；桌面设置面向连接与配对，不拥有任务派发/归档前端。
- 完整工程分类、依赖方向和只允许缩减的临时例外见 [`tests/architecture/README.md`](../../tests/architecture/README.md)。各能力入口及验证要求见上列 owner README。
- API Key 使用 Windows Credential Manager；配对状态使用受保护本机存储。日志、协议和发布包不得包含凭据、完整 Prompt、对话、终端输出或敏感路径。
- `.fgopetpack` 是独立纯数据交付物，不得嵌入应用 ZIP。

## 制作 Windows x64 ZIP 候选包

输出目录必须位于仓库之外且尚不存在。以下示例中的日期目录可按实际候选调整：

```powershell
dotnet restore FgoPet.sln
pwsh -NoProfile -File tools/scripts/publish-release.ps1 -OutputRoot D:\fgo_unpack\release-candidate-YYYYMMDD-vX.Y.Z
pwsh -NoProfile -File tools/scripts/verify-release.ps1 -CandidateRoot D:\fgo_unpack\release-candidate-YYYYMMDD-vX.Y.Z
pwsh -NoProfile -File tools/scripts/test-release-candidate.ps1 -CandidateRoot D:\fgo_unpack\release-candidate-YYYYMMDD-vX.Y.Z -TempRoot D:\fgo_unpack\_workspace\release-acceptance
```

成功候选包含 `manifest.json`、`SHA256SUMS` 和 `app/FgoPet-win-x64-X.Y.Z.zip`。候选验收覆盖归档校验、解压、必需可执行文件、MCP smoke、重复安装模拟、卸载及隔离状态保留；它不代表签名、安装器、真实模型/Agent/TTS 或多设备人工验收完成。

## 提交与发布检查

1. `git status --short` 与 `git diff --check` 无意外内容。
2. 应用版本、Changelog 和发布说明一致。
3. 构建及适用测试已在待发布提交上重新执行。
4. 校验 ZIP 哈希与 manifest，确认没有源码、调试符号、日志、数据库、密钥或角色包。
5. 上传、创建 Release、打标签和 push 必须另行获得发布授权。
