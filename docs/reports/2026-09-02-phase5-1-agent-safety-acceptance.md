# Phase 5.1 Agent 任务运行安全验收

日期：2026-09-02
工作区：`phase5-2-agent-visible`
分支：`phase5-2-agent-visible`

## 结论

Phase 5.1 的任务运行安全闭环已在 S2 worktree 完成实现和自动化验收。主
分支未修改；S2 既有改动、Phase 5.1 改动和已完成的 Phase 5.2 备份恢复
改动均保留在当前 worktree，尚未提交或合并。

## 实现边界

- Relay 状态从 schema v1 迁移到 v2，增加跨进程可恢复的归档批次、Adapter
  容量报告和重放保护墓碑。
- App、Relay、Adapter 通过 prepare/commit/ack 两阶段归档协议协调；只有
  Adapter 确认本地 journal 已写入墓碑后，Relay 才删除回执/水位并完成批次。
- Adapter journal 归档校验来源身份、任务/派发标识、最终序列、终态、时间
  和不包含业务文本的哈希；重启后可恢复 prepared/committed 批次。
- App 只选择超过 30 天、已结束且有精确最终回执的候选；执行中或“待核对”
  任务、维护状态未知或墓碑已满时，归档入口禁用。
- 传输超时、离线或异常响应会保留原始批次/执行，不自动重试或盲目删除。
  派发结果未知时显示受限的核对标识，人工确认只写本地投影，不重新派发。
- 人工确认后的下一次派发生成新的 request/execution ID，并通过
  `PreviousExecutionId` 关联旧执行；Relay 去重不会把它误判为旧请求重放。
- Agent 连接设置新增容量卡和不可恢复归档确认；当前任务条和 Todo 详情
  提供人工核对入口与受限诊断信息，不展示 Prompt、工具参数、终端输出、
  凭据或绝对路径。

## 自动化验证

| 检查 | 结果 |
| --- | --- |
| `dotnet build FgoPet.sln -c Release --no-restore -warnaserror`（隔离输出） | 通过，0 警告、0 错误 |
| AgentProtocol tests | 58/58 通过 |
| AgentRelay tests | 33/34 通过；1 项为 DPAPI 环境失败 |
| AgentRuntime tests | 31/31 通过 |
| App tests | 241/241 通过 |
| CodexAdapter tests | 41/45 通过；4 项为 DPAPI 环境失败 |
| Core tests | 152/152 通过 |
| EndToEnd tests | 6/7 通过；1 项为 DPAPI 环境失败 |
| Infrastructure tests | 179/179 通过 |
| Windows tests | 70/70 通过 |
| Phase 5.1 Relay 聚焦测试 | 11/11 通过 |
| Phase 5.1 Adapter 聚焦测试 | 13/13 通过 |
| Phase 5.1 App 归档聚焦测试 | 6/6 通过 |
| Phase 5.1 WPF 设置/任务入口聚焦测试 | 15/15 通过 |
| Python `pytest` | 105 passed |
| `git diff --check` | 通过，无 whitespace error |

完整 .NET 项目测试合计 817 项，其中 811 项通过、6 项失败。6 项失败均
来自当前会话的 Windows DPAPI 用户配置文件未加载：CodexAdapter 4 项、
AgentRelay 1 项、relay E2E 1 项；失败栈位于既有
`DpapiSecretProtector.Protect` 凭据持久化路径，不是本次 Phase 5.1 测试
失败。应在正常加载用户配置文件的 Windows 会话中重跑完整套件，作为发行
前门禁。

## 未完成的验收项

本次完成了协议、持久化、跨进程协调、服务和 WPF 自动化验收；尚未在正常
用户会话中做新的人工断网/重启演练，也未在真实长期 journal 上做容量压测。
统一整合前应补做一次真实 Adapter/Relay 重启后继续归档，以及 transport
timeout 后人工核对和新尝试的交互演练。

## 整合状态

未执行 commit、merge、reset、stash 或 push。待用户确认统一整合时，再以
当前 S2 worktree 的完整 diff 为输入执行集成，并在集成前重新核对 S2 既有
改动边界。
