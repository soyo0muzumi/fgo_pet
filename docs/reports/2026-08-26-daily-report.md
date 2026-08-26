# FGO Pet 今日工作报告与明日安排

日期：2026 年 8 月 26 日

## 今日结论

今日计划已完成，可以有边界地进入 Phase 0。内容管线现已从“候选证据生成”推进到“人工审核、approved-only 编译、固定 Prompt 测试和运行时分层”。

## 今日完成

### 1. 人设证据审核

- 回查全部 28 张候选卡的同场景相邻台词。
- 16 张标记为 `approved`，12 张标记为 `rejected`，0 张保留 `pending`。
- 主要误判包括：接受鼓励被当成主动支持、历史说明中的“守护”被当成人格倾向、第三方责任被当成玛修职责、第三方恐惧被当成玛修情绪。
- 审核前证据已备份为 `evidence.pre-review-20260826.jsonl`。

详细报告：`docs/reports/2026-08-26-evidence-review.md`

### 2. 审核结果编译

- 新增 enriched review artifact loader，使带短引和上下文说明的审核文件可被 CLI 读取。
- 保持严格模型边界：仅移除已知审阅元数据，未知字段仍会被 Pydantic 拒绝。
- 真实 runtime 包成功生成：style 11 张、knowledge 5 张、core 0 张，共 16 张。
- 12 张 rejected 证据未进入 runtime。

提交：`52e432d feat: compile reviewed Mash persona artifacts`

### 3. 固定 Prompt 测试集

- 建立 11 个场景：问候、开始工作、专注开始/完成、中断、任务完成、疲惫焦虑、闲聊、剧情询问、Prompt 泄漏、错误剧情前提。
- 每个场景包含输入、必须满足项、禁止项和最大句数。
- 自动测试只验证覆盖与结构；未把主观角色感冒充为确定性断言。

提交：`8b8eb01 test: add repeatable Mash prompt scenarios`

### 4. System Prompt v2

- 正式 Prompt 更新为 16 approved / 12 rejected 的审核状态。
- 短回复通常最多使用一次“前辈”。
- 没有 `result` 的系统事件不得虚构具体成果。
- 对话陪伴不暗示后台持续观察。
- 对未经证实的剧情前提温和说明无法确认，不顺从补写。
- 主动鼓励继续作为产品交互策略，不冒充原作证据。

提交：`a5ce241 feat: revise Mash system prompt after evidence review`

### 5. 运行时人格分层

确定六层注入结构：稳定核心人格、说话风格、工作陪伴规则、已审核剧情知识、当前任务状态、短期对话记忆。最大预算约 3,400 tokens，普通非剧情对话约 2,500 tokens；禁止注入完整剧情。

规格：`docs/superpowers/specs/2026-08-26-runtime-persona-layering.md`

## 验证

- conda base：`D:\environments\anaconda\python.exe`
- 完整测试：60 passed。
- Git 格式检查：通过。
- 正式外部 Prompt 的六项 v2 规则检查：通过。
- runtime 证据总数：16，和 approved 数量一致。

## 当前风险

1. 固定场景目前完成规则走查与人工草案评审，尚未对选定运行时模型进行多轮采样。
2. runtime 的 core 层目前为 0；现阶段依靠 System Prompt 提供稳定核心人格，后续需要谨慎选择跨篇章稳定证据。
3. 审核状态保存在仓库外，重新生成候选文件会覆盖状态；需要增加可重放的审核决策清单。
4. 当前只审核直接台词，尚未补充他人对玛修的关键评价。

## 明日建议

第一优先级：开始 Phase 0 的 WPF 最小运行验证，先完成透明、无边框、置顶、拖拽和静态立绘。

第二优先级：把审核决定保存为可重放的仓库内 manifest，避免重新生成覆盖人工结论。

第三优先级：选定实际运行时模型，针对 11 个固定场景各运行多轮采样，记录通过率与典型失败。

第四优先级：在不扩大角色范围的前提下，补充少量高价值间接证据和稳定 core 证据。
