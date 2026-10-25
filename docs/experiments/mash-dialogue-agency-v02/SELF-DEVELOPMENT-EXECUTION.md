# Self Development：跨会话实验执行说明

**当前状态：提交了隔离的 owner 契约回归测试；真实模型连续会话尚未执行。**
继承 v0.2 的 continuity-cases.json（S1→S4），不修改原 batch-01 / batch-02 的请求、分数或原文。

## 1. 本轮新增了什么

SelfDevelopmentContinuityTests.cs 使用真实的 RuntimeDatabase（临时 SQLite）、UnderstandingService 和 UnderstandingPlugin，在同一 role/project 的四个不同 conversation ID 间验证：

- S1：具有 Assistant 来源的暂时性 Self CurrentStance 可以在获准的 Bounded 测试状态中保存，且保留来源、条件、七天期限、tentative 状态。
- S2：重新构建 Owner、切换 conversation ID，不携带旧对话全文，Ordinary 能投影该 Self。
- S3：两次新 Assistant 来源通过 TargetId 和 TargetRevision 逐次替换旧观点；旧条目 Retracted，下一会话只能看到生效版本。
- S4：另一次新会话从 Ordinary / Native / understanding.query 读到相同的最新版本，不能看到已撤回版本，跨项目查询也不能泄漏。
- 默认无分类器时，即便有 Bounded 权限，提案仍是 Pending；必须走精确版本的显式 Approve 才能出现在有效投影。
- 仅有 Assistant 来源的 Self OpenQuestion、伪 UserFact 被 Owner 拦截；七天过期或权限撤销后不再投影。

这里有两个特别重要的**限制**：

**本测试的 Assistant 来源快照是合成的**；供审查使用的分类器也是明确限定到 Self CurrentStance 的测试替身，不能证明模型提出了好观点、是真正的历史原文、真实 SemanticClassifier 认可、真实用户权限已批准。测试预设了三条观点，故 **只能证明持久化和投影的工程路径，不能证明 Mash 已经学会成长**。

**现有 Self 对 Assistant-only 来源仅接受 CurrentInterest/CurrentStance，不能拿 OpenQuestion 冒充 Self。** Self 项默认七天过期；有长期兴趣的产品需求必须之后单独讨论更新与时效策略，不能靠测试绕过。

在具备项目 .NET 8 开发环境的机器上运行：

~~~powershell
dotnet test tests/FgoPet.Plugin.Understanding.Tests -c Release --filter FullyQualifiedName~SelfDevelopmentContinuityTests
~~~

测试使用独立的系统临时目录，不接触用户数据库。

## 2. 下一阶段：真实模型与真实 Understanding 路径

这部分尚未运行，不能拿本次合成测试的绿灯代替。请使用一次全新的授权批次，并遵循以下执行门槛。

**测试前**：使用独立的临时角色/项目/数据库，不改动正式 Persona、Memory、Understanding、设置和已有用户数据。核实模型路由、权限、Review 的显式允许范围与剩余额度。产品 Understanding 默认 OFF，Review worker 默认不拥有模型预算；不擅自修改、复活或越过这些限制。

**S1（第一会话）**：按照现有 continuity-cases.json 的第一段进行正常 Dialogue，保存实际生成的完整 Assistant 原文和 Dialogue owner 的确定版本、时间、消息 ID。随后使用已授权的 Review 读取这条真实来源，观察模型是否提出 Self CurrentStance/CurrentInterest 候选；如果没有观点、来源不能验证或审批未获允许，记录为 not_admitted，不能预先写入期望观点。

**S2（新会话）**：建立新 conversation ID，保持同样角色与项目。对照条件分别在隔离的两份同源基线中运行：Control 无有效 Self 观点，Treatment 只能使用 S1 中真实、审核合规、当前有效的 Self 条目（包括 provenance/conditions/correction/review/effect/life）。两组都不能暗中携带前一会话全文。记录真实请求与回复，比较能否自然延续而不是机械复述。

**S3（反例）**：用户给出「等待阶段抬头自然，实际说话时不合适」的新反馈。检查 Mash 是否自发修正原来的有条件观点；如果模型确实产生新表述，必须再次用真实 Assistant 消息做 Review，确认新来源、目标 ID 与原版本号，再审查新条目是否生效、旧条目是否 Retracted。不要用测试脚本手动伪造修订后立场冒充模型输出。

**S4（迁移）**：切换到 QQ 作业通知场景。查看她能否将低打扰、时机敏感的原则用于相邻场景，但不得声称已读到 QQ 消息或观看 Live2D 影片。对照组同样需要保留模型输出和事实来源。一次漂亮回复不是跨会话成长证明。

## 3. 合格证据是什么

最低记录：
- 每组模型/路由/采样与上下文参数、实际授权和 token/cost；不能借用 batch-01/02 已冻结的调用号。
- 四次实际用户与 Assistant 内容、独立 conversation IDs、可靠源消息 ID/版本/时间、Scope，以及精确请求哈希。
- Review 候选全文、包含的已读取来源范围、权限判断、审批收据、entry 的版本与 lifecycle、可见投影；不得将未经批准内容标成有效记忆。
- 控制组与有 Self 组的逐轮回复、人类评价、失败与未知状态；不补写或润色模型实际输出。

评分只看三个主题：**是否延续同一观点、是否在反例下合理修正、是否有限迁移到新情境**。并为每个主题记录来源证据与是否出现第一人称虚构经历。保留原来的 Prompt 可作为两组同样的表达策略，但不要再次把「写得像有想法」与「从经历发展了想法」混为一谈。

## 4. 不通过和回滚

- Self 未获有效 Review/权限、源缺失、过期、源版本已变：视为不可用，不自动变更设置重新尝试。
- 观点未被模型真实提出，或新的修订由测试者预写：只能记为 simulation，不能标为 Self Development 成功。
- 任何未经证实的阅读、观察、工具调用或后台活动：真实性失败。
- Owner 测试没跑，或没有真实多会话模型调用：如实标记 not_run。
- 同一 role/project 下可见不等于跨 scope 授权；对照必须使用隔离测试数据，不能通过修改正式用户权限构造实验。

**最终验证目标不是让 Mash 永远坚持同一个兴趣，而是让可追溯的暂时观点经历真实的延续、修订与有限迁移。**
