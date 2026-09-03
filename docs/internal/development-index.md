# 内部开发文档索引

更新时间：2026-09-03

本文把历史计划中的未完成项与当前路线对应起来。不要通过旧计划中的未勾选框判断当前状态；旧计划记录的是当时的实施步骤，实际完成情况以代码、测试和最新发布记录为准。

## 仍值得重新评估的事项

| 来源 | 事项 | 当前归属 | 处理方式 |
|---|---|---|---|
| `docs/internal/superpowers/plans/2026-09-01-project-progress-roadmap.md` | 发布前人工 Windows 验收 | Now | 转入 `docs/roadmap.md`，完成后补入 Release readiness |
| `docs/internal/superpowers/plans/2026-09-01-project-progress-roadmap.md` | Phase 1 packaging SDK 后续整合 | Later / 待评估 | 不视为当前承诺，需重新确认正式角色包需求 |
| `docs/internal/superpowers/plans/2026-09-01-project-progress-roadmap.md` | Agent 断线、退出和 outbox/ack 语义 | Next / 待评估 | 先明确真实用户场景，再决定是否补持久化设计 |
| `docs/internal/superpowers/plans/2026-09-03-phase5-5-release-preparation.md` | 候选版本人工证据 | Now | 以 `docs/release/README.md` 的验收要求为准 |
| `docs/internal/superpowers/plans/2026-08-25-fgo-art-pipeline.md` | 更多角色和素材扩展 | Later | 首版先冻结 Mash 角色包，不扩展素材范围 |

## 历史计划的处理规则

- 已实现的计划保留，作为实现来源和决策追溯材料。
- 仍有未勾选步骤但目标已被替代的计划，标记为 `Historical` 或 `Superseded`。
- 仍然有效的未完成事项必须复制到 `docs/roadmap.md`，并写明完成标准。
- 一次性验收输出、截图、日志和本机状态不作为远程用户文档入口。

## 当前可信入口

- 用户使用：根目录 README 和 `docs/guides/`
- 发布流程：`docs/release/`
- 当前路线：`docs/roadmap.md`
- 版本变化：`CHANGELOG.md`
- 历史实现过程：`docs/internal/superpowers/` 和 `docs/internal/reports/`
