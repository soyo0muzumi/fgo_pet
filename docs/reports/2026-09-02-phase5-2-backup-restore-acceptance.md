# Phase 5.2 私有备份与事务恢复验收

日期：2026-09-02
工作区：`phase5-2-agent-visible`
分支：`phase5-2-agent-visible`

## 结论

Phase 5.2 的私有 `.fgopetbackup` 备份/恢复链路已在 S2 worktree 完成实现和自动化验收。主分支未修改；本 worktree 的 S2 既有未提交改动和本次改动均保留，尚未提交或合并。

## 实现边界

- 私有备份固定为四个成员：`manifest.json`、`runtime.sqlite`、`settings.json`、`packages.json`；manifest 对三个 payload 做长度和 SHA-256 校验。
- SQLite 使用 `VACUUM INTO` 生成一致性快照，不把活动 WAL/SHM 作为备份内容。
- 恢复先在隔离 staging 目录完成 ZIP 路径、重复成员、大小、哈希、设置、包引用、迁移和 SQLite integrity 校验，再触碰当前状态。
- 恢复前停止 Agent runtime；`dispatching`、`active`、`attention` 执行记录变为 `dispatch_outcome_unknown`，保留原身份与 `remote_task_id`，不发起网络请求或自动重新派发。
- 数据库/设置替换、包索引写入或启动自检失败时恢复旧数据库、设置、包索引及 sidecar。
- 设置快照只包含非秘密模型元数据和用户偏好；不包含 API Key、Agent 凭据、原始角色资源、Prompt、日志、截图或绝对路径。
- Privacy 设置页新增独立的私有备份/恢复卡片、`.fgopetbackup` 文件选择器、恢复确认和安全状态提示。

## 自动化验证

| 检查 | 结果 |
| --- | --- |
| `dotnet build FgoPet.sln -c Release --no-restore -warnaserror` | 通过，0 警告、0 错误 |
| 备份合同、策略、快照、reader、normalizer | 通过；Infrastructure 聚焦 14/14 |
| Private backup/restore App tests | 通过 7/7 |
| Windows Privacy 页面集成测试 | 通过 7/7 |
| Backup/restore EndToEnd tests | 通过 2/2 |
| Python `pytest` | 通过 105 passed |
| `git diff --check` | 通过，无 whitespace error |

完整 .NET 套件命令退出码为 1，但失败仅为既有 Windows DPAPI 用户配置文件环境限制，共 6 个测试：CodexAdapter 4 个、AgentRelay 1 个、relay E2E 1 个。失败栈均位于既有 `DpapiSecretProtector.Protect` 凭据持久化路径；本次新增备份/恢复测试没有失败。应在正常加载 Windows 用户配置文件的会话中重跑完整套件作为发行前门禁。

## 未完成的验收项

本次完成的是自动化 Windows STA/UI 集成验收，尚未进行人工启动桌面应用后的交互式恢复演练。正式整合前仍应在正常用户会话中确认：文件选择、恢复确认、缺失角色包提示、Agent 配对提示以及恢复后界面缓存刷新。

## 整合状态

未执行 commit、merge、reset、stash 或 push。待用户确认统一整合时，再以当前 S2 worktree 的完整 diff 为输入执行集成，并在集成前重新核对 S2 既有改动边界。
