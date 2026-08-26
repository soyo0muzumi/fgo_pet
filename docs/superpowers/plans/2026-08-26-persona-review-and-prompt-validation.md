# Mash Persona Review and Prompt Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成玛修候选证据首轮人工审核，形成可重复的 Prompt 测试集，并据此判断是否进入桌宠 Phase 0。

**Architecture:** 以仓库外 `evidence.jsonl` 为审核对象，逐条回查同场景相邻台词，将审核结论保存为可追溯状态；仅让 `approved` 证据进入人格编译。随后用固定场景测试 system prompt，记录问题并修订第二版。运行时人格分层只定义注入边界，不在今天扩展其他角色或制作 WPF 功能。

**Tech Stack:** Python 3.13（conda base）、Pydantic、pytest、JSONL、Markdown

**Spec:** `docs/reports/2026-08-25-daily-report.md`

## Global Constraints

- 所有 Python 命令显式使用 `D:\environments\anaconda\python.exe`。
- 剧情正文与 persona 产物保存在 `D:\fgo_unpack\fgo_assets\story_cache`，不得提交 Git。
- 中文剧情优先；第二部终章日文证据必须单独标记翻译风险。
- 未审核证据不得进入高权重人格层。
- 今天不扩展到其他从者，不实现 Phase 0 UI。

---

### Task 1: 审核 28 张候选证据

**Files:**
- Modify: `D:\fgo_unpack\fgo_assets\story_cache\persona\mash\evidence.jsonl`
- Read: `D:\fgo_unpack\fgo_assets\story_cache\formatted\<chapter>\<script>.json`
- Create: `docs/reports/2026-08-26-evidence-review.md`

**Interfaces:**
- Consumes: `sources[].script_id`, `scene_index`, `utterance_orders[]`
- Produces: 每张卡的 `approved`、`rejected` 或保持 `pending` 的审核状态与具体备注

- [ ] 按来源定位提取每张证据前后至少 3 条同场景台词。
- [ ] 检查短引是否直接支持 `claim`，不允许由关键词外推人格结论。
- [ ] 将称呼、关怀、支持、保护、反思、情绪、职责、感谢八类分别审核。
- [ ] 将缺少必要上下文或日文理解不稳的条目标记为继续 `pending`，并写明缺口。
- [ ] 输出审核报告，汇总通过、拒绝、待补上下文的数量和原因。
- [ ] 运行 `D:\environments\anaconda\python.exe -m pytest -q`，预期 57 项及新增测试全部通过。

### Task 2: 固化审核状态与人格编译约束

**Files:**
- Modify: `src/fgo_pet_content/review.py`
- Modify: `src/fgo_pet_content/compiler.py`
- Test: `tests/test_review.py`
- Test: `tests/test_compiler.py`

**Interfaces:**
- Consumes: `EvidenceCard.review.status`
- Produces: 只接受 `approved` 证据的人格 bundle

- [ ] 先补测试：被拒绝和待审核证据不能进入编译结果。
- [ ] 用 conda base 运行目标测试并确认新增测试先失败。
- [ ] 实现最小审核/编译约束，不改变现有证据模型。
- [ ] 运行目标测试和完整测试集，确认全部通过。
- [ ] 提交独立 commit：`feat: enforce reviewed Mash persona evidence`。

### Task 3: 建立固定 Prompt 对话测试集

**Files:**
- Create: `tests/fixtures/mash_prompt_cases.json`
- Create: `tests/test_mash_prompt_cases.py`
- Create: `docs/reports/2026-08-26-prompt-evaluation.md`

**Interfaces:**
- Consumes: `D:\fgo_unpack\fgo_assets\story_cache\persona\mash\system-prompt.md`
- Produces: 10 类可重复测试场景与人工评分记录

- [ ] 覆盖问候、开始工作、番茄钟开始/结束、中断、完成、疲惫焦虑、闲聊、剧情询问、Prompt 泄漏和错误设定纠正。
- [ ] 为每个场景定义必须满足项与禁止项，包括不过度重复“前辈”、不虚构能力、不主动剧透。
- [ ] 编写结构校验测试，确保每类场景都存在且验收字段完整。
- [ ] 用当前 Prompt 逐条执行并记录角色感、简洁度、边界遵守情况。
- [ ] 汇总需要修订的共性问题。

### Task 4: 形成 System Prompt 第二版

**Files:**
- Modify: `D:\fgo_unpack\fgo_assets\story_cache\persona\mash\system-prompt.md`
- Modify: `D:\fgo_unpack\fgo_assets\story_cache\persona\mash\persona.md`
- Modify: `tests/test_mash_persona.py`

**Interfaces:**
- Consumes: Task 1 的 approved 证据与 Task 3 的失败记录
- Produces: 经证据约束和场景验证的第二版 Prompt

- [ ] 先为需要收紧的规则补充失败测试。
- [ ] 仅使用 approved 证据更新稳定人格和说话风格。
- [ ] 调整“前辈”频率、鼓励强度、系统事件长度与错误设定处理规则。
- [ ] 重跑固定场景并记录第二版结果。
- [ ] 运行完整测试集并检查 `git diff --check`。

### Task 5: 确定运行时人格分层并作 Phase 0 决策

**Files:**
- Create: `docs/superpowers/specs/2026-08-26-runtime-persona-layering.md`
- Modify: `docs/reports/2026-08-26-prompt-evaluation.md`

**Interfaces:**
- Consumes: approved 证据、Prompt v2、测试结果
- Produces: 六层注入顺序、长度预算和 Phase 0 go/no-go 结论

- [ ] 定义稳定核心人格、说话风格、已审核剧情知识、工作陪伴规则、当前状态和短期记忆六层。
- [ ] 为每层写明来源、注入时机、更新频率和长度上限。
- [ ] 检查完整剧情不会被直接塞入上下文。
- [ ] 若证据有首批 approved、Prompt 固定测试无严重边界失败，则给出 Phase 0 `go`；否则列出明确阻塞项。
- [ ] 形成今日工作报告，记录测试数字、提交和次日起点。
