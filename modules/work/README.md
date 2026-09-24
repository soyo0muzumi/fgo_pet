# work

> Todo + Execution + Archives 同一一致性边界。**不建 WorkManager**。

## Responsibilities

- Todo：条目/步骤/优先级、日期规则、编辑规则、待确认提案、**幂等创建**
- Execution：派发、执行记录、授权检查、回执归一、停止/未知结果/重连、项目快照
- Execution：`WorkExecutionSettings` / `IWorkExecutionSettingsStore` 拥有 Agent 连接设置分区
- Archives：工作归档草稿、确认、长期归档

## Non-responsibilities

- Agent 传输协议与 Relay/Runtime/Adapter（归 `adapters/agent-integration`，本模块只发布**窄派发契约**）
- 对话运行过程（归 dialogue）

## Public interfaces

仅 `Contracts/` 下的类型可被其他模块引用。跨模块调用必须走 `modules/README.md` 登记的契约边，**除登记的 8 条外任何跨模块引用都是违规**。

`Todo/Contracts/TodoDraftContracts.cs` 发布 `ITodoDraftWorkflow` 及草稿/结果值对象。Work 拥有会话草稿、版本、不可变提案快照与确认幂等性；对话只经该契约读取完整草稿投影、替换、取消或确认。旧类型命名空间保留用于兼容，不代表 Dialogue 拥有这些状态。

`Todo/Contracts/TodoConversationContracts.cs` 发布模型提案读取与会话草稿装配契约。`LegacyTodoProposalContracts.cs` 单独发布旧卡片的 `ILegacyTodoProposalPort`（解析）及 `ILegacyTodoProposalConfirmation`（用户确认）；它们不继承模型端口，模型端口也不继承这些兼容接口。解析、构造和显示卡片不写入；确认方法只能由明确的用户操作调用。旧卡片保留成功后的本实例重复点击去重，不由此承诺跨实例或未知写入结果的幂等性；新对话仍使用带作用域、版本与幂等键的 `ITodoDraftWorkflow`。

`Archives/Contracts/ArchiveDraftContracts.cs` 发布原有 `ArchiveDraft` 值对象与仅含 `Confirm` 的 `IArchiveDraftConfirmation`。界面编辑标题、摘要后提交原草稿的副本；归档标识、来源、覆盖条目、日期、成果与模型输入字段保持原有传递语义。草稿创建、归档持久化与覆盖 Todo 清理由 Work 实现负责，不经界面契约暴露仓储。

这两组界面契约和归档值对象由 Core 编译一次，保留历史命名空间。宿主显式工厂继续传入既有服务实例；接口不是新的服务生命周期或授权机制。归档 DTO 的程序集归属及界面构造函数签名已变化，需要完整重建、部署，不支持只替换单个 DLL。

## Dependencies

- **允许**：`platform/*`、`ui-foundation/*`、`adapters/agent-integration`（仅窄派发契约）
- **禁止**：dialogue（方向相反）；`host`

## 表所有权（Q2=b：状态拥有者 = SQL 执行者）

`todo_items` `todo_steps` `agent_executions` `agent_event_receipts` `agent_connections` `agent_project_targets` `agent_project_snapshots` `work_archives` `work_archive_items` `long_work_archives` `agent_archive_batches` `agent_archive_items`（12 张）

> 本模块的仓储**只能碰上面这些表**；碰别人的表是 Q2=b 违规。

## Data and security boundaries

派发前必须过授权检查；执行记录与归档不得写入未脱敏的异常全文；**幂等键必须真正幂等**（重复确认不得产生第二条记录）。

## Development and validation

Core / App / Infrastructure / Windows 的 todo / execution / archive 测试；work↔agent-integration 派发边测试；幂等与恢复语义需专项回归。

`WorkCardContractTests` 在只引用 Core 的项目中检查契约归属和模型/旧卡片接口隔离；`WorkCardBoundaryTests` 检查界面参数、显示零写入、编辑、失败重试、重复点击及真实服务到仓储的调用。既有 Todo/Archive 服务与 SQLite 测试仍负责生产校验、事务及清理语义；接口替身不能代替这些验证。

## 要点 / 易错处

1. **`TodoContinuationState` 归本模块**（`work/Todo/Application`）。草稿的状态键含 `conversationId` 但它是**不透明字符串**——「草稿按会话隔离」是 work 的状态形状，不是 dialogue 的生命周期所有权。
2. **本模块是提案边界的发布侧**（§6.9）：工具定义 + 提案值对象在 `work/Todo/Contracts`，确认用例在 `work/Todo/Application`（写库唯一入口）。
3. 全部提案及版本的提示词数据投影由 `TodoContinuationState.GetPromptState` 提供；提示词预算与注入防护仍归 Dialogue。

## Migration status

**过渡态（2026-09-20）**：源码**已物理迁移**到本目录的 8 目录骨架（`Contracts` / `Application` / `Domain` / `Infrastructure` / `Desktop` / `Integrations`）。

Todo、Execution、Archives 已各有现存工程编译应用和桌面条目；部分共享值对象、仓储仍由 legacy Core / Infrastructure 编译。工程拆分不等于全部跨模块依赖已收口。

- 文件定位依据：工作区 `architecture/module-target-map-v2.md` §4
- 收口以实际调用链与契约为准，不重复创建已有工程。

## Migration debt

- Dialogue 已通过契约使用提案解析与归档确认，但仍组合 Work 的卡片 ViewModel 和 XAML 控件；桌面呈现与程序集引用边界仍需收口。
- 部分文件按落点表**主列**归位，与文档中同时列举它的另一处存在归属差异；逐条记在工作区 `step4-migration-log.md` §3.5

## 决策出处

Q1 **Q2** **Q4** Q5 Q7 **Q10** **草稿归属** **提案边界（发布侧）** —— 完整论证见工作区 `modules-v2/work.md`
