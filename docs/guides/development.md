# 开发者指南

## 环境与入口

- Windows 11；.NET SDK 8.0.x；PowerShell 7。
- 解决方案入口：`FgoPet.sln`。
- 应用入口：`src/FgoPet.App/`；共享领域与持久化分别位于 `src/FgoPet.Core/`、`src/FgoPet.Infrastructure/`。
- 功能模块及依赖方向从 `modules/README.md` 开始查阅；共享 WPF 资源位于 `ui-foundation/`。

## 构建与测试

```powershell
dotnet build FgoPet.sln -c Release -warnaserror
dotnet test FgoPet.sln -c Release
pwsh -File scripts/test-phase1.ps1
pwsh -File scripts/test-phase2.ps1
pwsh -File scripts/test-phase3-settings.ps1
pwsh -File scripts/test-phase4.ps1
```

运行中的程序可能占用默认输出目录；此时使用仓库外的隔离 `--artifacts-path`，不要停止用户进程或清理用户数据。测试日志与生成物不得写入源码根目录。

## 模块与边界

- `modules/agent-integration/`：Provider-neutral Relay/协议/运行时与 Codex 参考适配器。
- `modules/speech/`：语音契约、合成适配、播放生命周期和设置界面；模型或设备不可用时不得影响离线桌宠功能。
- `modules/dialogue/`、`modules/todo/`、`modules/focus/`、`modules/memory/`：功能归属与迁移状态索引。
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
