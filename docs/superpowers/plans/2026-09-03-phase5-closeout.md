# Phase 5 收尾与首版发布准备

日期：2026-09-03
状态：开发完成，进入总体验收与发布准备

## 1. 完成范围

Phase 5 的已批准开发切片已合并到本地 `main`，合并提交为 `1d37405`：

- Agent 安全、归档边界与备份恢复链路；
- 引导式 Agent 配置、项目名称展示、配对与诊断边界；
- Art v3 布局确认、导出、预览和视觉 QA；
- 确定性、无可执行代码的角色包构建与验证；
- Python builder 与 .NET installer 的能力、文件清单和路径契约对齐；
- packaging gate、发布清单和官方 Mash 元数据候选。

## 2. 验证证据

在 `D:/fgo_unpack/fgo_pet/.worktrees/phase5-3-guided-config` 完成实现验证后合并：

| 检查 | 结果 |
|---|---|
| Python 全量测试 | 155 passed |
| Packaging gate Python | 71 passed |
| Packaging gate Core | 16 passed |
| Packaging gate Infrastructure | 76 passed |
| App Release build | 0 warnings / 0 errors |
| `git diff --check` | passed |

合并后 `main` 的原有未提交文件保持不变；本次没有覆盖或清理用户已有工作区内容。

## 3. 明确边界

本记录确认的是 Phase 5 开发交付，不等同于公开发行批准。正式角色 PNG、人工视觉确认、干净 Windows 安装矩阵和最终发行物仍属于发布准备证据。官方 Mash 目录当前是元数据候选，缺少真实素材时不得生成可发布包。

## 4. 后续门禁

1. 总体验收备份恢复、Agent 配置和角色包安装/失败回滚。
2. 完成正式素材人工确认并重跑资源 QA。
3. 执行干净环境安装、升级、恢复、卸载重装和隐私检查。
4. 生成 release notes、已知限制和最终候选构建；公开上传/发布另行授权。

## 5. Phase 5.5 Task 4 verification (2026-09-03)

This section records only evidence obtained in the isolated `phase5-5-release-prep` worktree. It does not authorize public release.

### Automated evidence

| Check | Result | Evidence |
|---|---|---|
| Python full suite | Passed | `D:\fgo_unpack\.venv-phase5-4a\Scripts\python.exe -m pytest -q`: `185 passed`. |
| PowerShell packaging gate | Passed | `scripts/test-packaging.ps1`: Python `71 passed`, Core `16 passed`, Infrastructure `76 passed`; `Packaging gate passed.` |
| PowerShell parser checks | Passed | `powershell.exe -NoProfile` AST parse completed successfully. |
| App RID Release build | Passed | `dotnet build src/FgoPet.App/FgoPet.App.csproj -c Release -r win-x64 --no-restore`: 0 warnings / 0 errors. |
| Publish / release verifier | Passed | Candidate `D:\fgo_unpack\release-candidate-20260903d`; `verify-release.ps1` returned `Release verification passed.` Manifest records version `0.1.0`, RID `win-x64`, TFM `net8.0-windows`, and `.NET 8 Desktop Runtime`. |
| Isolated acceptance | Passed in normal Windows user session | User reran with `powershell.exe -NoProfile -ExecutionPolicy Bypass`: release verification, extraction, MCP `initialize`/`tools/list` smoke, repeated install/upgrade, uninstall, and isolated-state preservation all passed. |

The candidate was produced and verified locally. Its archive SHA-256 and final public release authorization remain intentionally outside this task.

### Manual Windows evidence gaps

GUI behavior, sleep/resume, DPI, multi-monitor, long-running operation, and final Windows runtime evidence remain outstanding. The candidate is not publicly release-authorized until those manual checks and any release authorization are completed.
