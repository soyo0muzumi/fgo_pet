# Modules

> **本文件已于 2026-09-20 按 v2 目标架构重写**（原 v1 措辞见 git 历史）。
> 目标架构 = **四层六模块一宿主**；权威边界文档在工作区 `modules-v2/`。

## Ownership map

**业务模块（6）**：`dialogue` `memory` `work` `focus` `character` `speech`

**宿主（2）**：`host/DesktopShell`、`host/DataManagement`（**只装配，不存业务状态**）

**适配器**：`agent-integration`（Q4：目录 / 程序集 / 命名空间**全部不改**，性质降为 adapters）

**跨切层**：`platform/{Sqlite.Primitives, Sqlite.Migrations, Runtime, Diagnostics, Settings.Foundation, Secrets}` + `ui-foundation/`

**验证支持**：`integration-tests/`

> ⚠️ **不得出现 `persistence/` 子层**——一旦有它，`SqliteTodoRepository` 就会被读成"persistence 分层里 todo 那部分"，退回 11 模块竖切的错误。
> 仓储住在**各业务模块内部**（`modules/*/Infrastructure/`），基建住在 `platform/`——两者不是同一层的两类东西。

27 张业务表归属：work 12 / focus 6 / dialogue 4 / memory 2 / character 1 / platform 1 / speech 0。

## Dependency direction

```
presentation   →  所属模块的 Application / 公开用例契约
application    →  Domain、自己声明的端口、**明确允许的其他模块 Contracts**
infrastructure →  它实现的端口、必要领域模型、平台抽象
host           →  模块注册入口（不授权使用模块仓储、状态实现或私有事件）
platform / ui-foundation  →  不依赖任何业务模块或 host
```

## Settings ownership

- character、dialogue、memory、speech、work/Execution 和 ui-foundation 各自发布本分区的设置值与存储端口。
- `host/Settings` 组合这些契约，实现唯一 schema-v2 codec，并串行化整文档的读-改-写。
- `platform/Settings.Foundation` 只持久化不透明文档，不依赖业务或表现类型。

### 跨模块契约边（8 条，全量）

| 发布方 | 消费方 | 契约内容 |
|---|---|---|
| `memory` | dialogue | 只读查询 + 候选提交 |
| `character` | dialogue | 内容契约（人格、知识、外观快照） |
| `speech` | dialogue | 命令 + 结果 |
| `work` | dialogue | ⚠️ **提案边界**：工具定义（name+schema+描述）+ 提案值对象 + 确认用例 + 只读 prompt 上下文投影 |
| `focus` | character | 公开事实（显示快照 + 已发生业务事实） |
| `adapters/agent-integration` | work | 窄派发契约 |
| 全部业务模块 | host/DesktopShell | 模块注册入口 |
| `platform` | host/DataManagement | 数据库快照适配器 + 维护门禁 |

**除这 8 条外，任何跨模块引用都是违规。**

> ⚠️ 工具**定义**归发布模块的 `Contracts/`（规则 `no-foreign-tool-schema`）；dialogue 只提供工具**机制**（`ChatToolDefinition` / `ChatToolCallDelta` / `Tools` 槽位 / 收集装配点）。

## 每个模块的内部布局（统一 8 目录）

```text
Contracts/             对外命令/查询/结果/通知；只暴露有消费者的内容
Application/           用例与端口
Domain/                规则、实体、内部事件
Infrastructure/        端口实现、存储、外部服务适配
Desktop/               模块自己的 View / ViewModel / EditorSession
Integrations/          明确依赖其他 Contracts 的具名适配组件
ModuleRegistration/    注册服务、处理器、运行生命周期
Tests/                 核心、存储、UI 和边界测试
```

> ⚠️ **不是机械地为每个文件夹建项目。** 设计 §7：「复杂模块可拆 Core/Infrastructure/Desktop 项目，但不机械地为每个文件夹建项目。」
> Gate 要求的是**编译/可见性约束**，不是 csproj 数量。**本骨架也适用于 Host。**

## 硬性禁止项（全模块适用）

1. 不引入全局事件总线
2. 不在 foundation 放业务事件全集
3. 不设 `persistence/` 层
4. 不让模块仓储碰别人的表
5. 不在跨切层放面向用户的文案
6. 不在 `ModuleRegistration` 里写业务 Lambda
7. 不创建万能 `IntegrationsManager`
8. 不把 B 的派生字段搬进 A 的领域模型
9. 不新增裸丢弃任务或 `async void` 内部处理器

## Migration status

源码已按模块树落位，`dialogue`、`memory`、`character`、`focus`、`work` 的 Todo / Execution / Archives 已有独立生产工程，并由 App 引用。文件归属应以当前 `.csproj` 的实际编译条目为准。

`src/FgoPet.{Core,Infrastructure,App}` 仍保留兼容职责和部分链接编译。App 承担应用组合入口，并以原 Link 编译部分共享 XAML，以保持 pack URI 兼容。独立工程已经存在，不应再次按“尚未拆项目”实施迁移；现有 legacy 引用和目标模块依赖规则仍需分别核对。

| 根 | 状态 |
|---|---|
| `modules/speech` | ✅ **已完全迁移**（v0.3）：6 个项目由自己的 csproj 编译，无回链 |
| `modules/agent-integration` | ✅ 4 个生产 + 4 个测试项目已迁移；Q4 决策路径与命名空间不改 |
| `dialogue` `memory` `work` `focus` `character` | 已有独立生产工程；部分契约和基础实现仍经 legacy 工程编译 |
| `host` | DesktopShell、HostContracts、DataManagement、SettingsHost 已有工程；应用组合入口仍由 App 编译 |
| `platform` `ui-foundation` | UiFoundation 已有工程；部分平台源码和共享 XAML 仍链接编译 |
| 集成测试 | 验证入口仍以 solution 中的 `tests/*` 和模块测试工程为准 |

> ⚠️ 设计 §9 末句：「**不得只凭目录移动、项目编译或单元测试通过宣布全部模块独立。**」

## 参考

- 权威边界文档（工作区）：`modules-v2/README.md` + 各模块 `.md`
- 完整文件落点表：`architecture/module-target-map-v2.md`
- 机器可读基线：`architecture/dependencies-v2.json` + 校验脚本 `architecture/validate-policy.py`
- 第 ④ 步执行日志：`step4-migration-log.md`
