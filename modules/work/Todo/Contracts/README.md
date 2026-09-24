# Todo 对话契约边界

## 所有权与装配

Todo 提案、待确认草稿、确认状态和幂等创建属于 Work。Dialogue 只负责模型调用与对话编排，不取得 Todo 的直接创建接口。

`ITodoConversationPort` 是装配入口，包含只读 `ITodoProposalReader` 和共享的 `ITodoDraftWorkflow`。编排器将它们分别保存在只读解析字段与草稿工作流字段中，不保留 `TodoProposalService` 实现引用。只读接口只能解析文本提案、解析工具参数、提供脱敏的运行状态；不能创建 Todo 或派发 Agent。

`Drafts` 必须在端口的生命周期内返回同一个工作流实例。当前宿主仍使用显式工厂，把同一个 `TodoProposalService` 及其 `Drafts` 交给对话与确认界面。构造时显式传入的 `todoDrafts` 仍优先于端口自带工作流，保留原有装配语义；自定义宿主必须让确认界面使用同一个实际工作流。

## 不变的行为

解析成功只代表提案有效，不代表用户授权。工具调用和文本兜底都先生成草稿；写入必须经过现有确认流程，携带从者/会话作用域、草稿 ID、版本和幂等键。模糊同意、引用、否定、疑问和修改要求不等于明确确认。旧版本确认不得写入，重复确认不得创建第二条记录。

解析器仍负责拒绝执行字段、未知字段和不安全内容。状态构造仍使用既有脱敏和字符预算。`TodoContinuationState`、真实 SQLite 写入、异常恢复、模型重试和取消逻辑不因本次契约抽取而改变。

## 编译边界与兼容性

`TodoProposal.cs`、`TodoDraftContracts.cs` 和 `TodoConversationContracts.cs` 的物理位置仍在 Work 的 `Contracts` 下，由 `FgoPet.Core` 编译一次；`FgoPet.Work.Todo` 不再重复编译这些类型。采用现有 Core 契约程序集作为迁移阶段载体，没有新增 WorkManager，也没有反向引用 Dialogue 实现程序集。

暂保留 `FgoPet.App.Dialogue` 命名空间，以兼容当前源代码。类型所属程序集已变化，必须完整重建与部署解决方案，不能仅替换一个 DLL 并宣称二进制兼容。

这不是整个 Dialogue 模块的编译隔离：Desktop 仍引用 Todo 提案卡片、归档视图模型等实现，相关项目引用暂保留。后续应单独处理界面组合契约、其他跨模块实现依赖与命名空间归属，而不是在本次改动中同时重写 UI。

## 回归验证

`TodoConversationContractTests` 在只引用 Core 的测试工程中编译，验证契约与 DTO 不要求加载 Work.Todo、Dialogue 或 WPF，并检查只读接口不暴露写入入口。

`TodoConversationPortTests` 用替代解析端口连接真实草稿工作流和 SQLite，验证工具/文本提案、明确确认、模糊及否定输入、草稿替换、旧版本、重复确认、取消、新会话、工作流覆盖及解析拒绝。另直接通过契约测试生产解析器的危险字段拒绝与零写入行为。替代解析器只用于验证编排，不代替生产解析器的安全测试。

完整验证命令：

```powershell
dotnet build FgoPet.sln -c Release -warnaserror
dotnet test FgoPet.sln -c Release --no-build --logger trx --results-directory artifacts/test-results
```

Core 契约测试通过只能证明契约可独立使用，不代表整个 Dialogue 已消除其他模块实现依赖。合并依据仍是当前提交的完整 CI；不得跳过失败用例或放宽门槛来掩盖基线问题。
