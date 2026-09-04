# FGO Pet 0.1.0 本地验收证据索引

记录日期：2026-09-04
环境：Windows x64，用户手工截图 + 本地自动化验证
范围：0.1.0 正式候选收尾；不包含语音、文件管理和 0.1.1 历史对话功能。

## 发布物

| Artifact | 状态 | SHA-256 |
| --- | --- | --- |
| `FgoPet-win-x64-0.1.0.zip` | 自动验证通过 | `0BD6647E75B76C695D09C461F14F2DA0E840DC7ECAAB48D8F5ABA3A5CD8476DD` |
| `official.mash-1.0.0.fgopetpack` | pack dry-run/build 通过 | `0602ED2D2EE1346609626F67E5C81EB87F5861E2CA9FD6C98788F70119651E5A` |
| `FgoPet-0.1.0-win-x64.msi` | WiX 5 构建、ICE 校验和用户生命周期验收通过 | `F696CB4294D03A21971565CD13847B64F7029A9B799F0D64E1CD14622494B7F1` |

## 证据映射

| Evidence | 内容 | 结果 |
| --- | --- | --- |
| `screenshots/Pasted image 20260904140000.png` | 首次启动/主界面 | 用户已确认正常 |
| `screenshots/Pasted image 20260904140059.png` | 角色包导入流程 | 用户已确认正常 |
| `screenshots/Pasted image 20260904141031.png` | 角色显示与交互 | 用户已确认正常 |
| `screenshots/Pasted image 20260904141434.png` | 专注面板 | 用户已确认正常 |
| `screenshots/Pasted image 20260904141554.png` | 当前版本综合状态 | 用户已确认正常 |
| `Person real test and feedback.md` | 用户实测反馈原文 | 已归档；包含早期问题记录及后续修正状态 |
| `installer-build-report.json` | MSI 构建信息与待验收步骤 | 已生成 |

## 已确认/不适用/待确认

- 已确认：首次启动、角色包导入、DPI 缩放、长时间运行、专注按钮修复后的主流程。
- 不适用：双显示器验收（当前环境没有多余显示器）。
- 不适用：角色包升级验收（当前没有可升级角色包）。
- 已确认：从 MSI 安装并启动、覆盖安装/升级后的数据保留、从 Windows Installed apps 卸载、卸载后的用户数据保留。
- 证据形式：用户现场验收确认；截图可作为补充材料继续归档。

## 角色包表达式冻结

LLM/Phase 3 核心语义只使用以下八个键：`neutral`、`happy`、`excited`、`shy`、`concerned`、`sad`、`surprised`、`angry`。Mash 仍保留 28 个表情图用于视觉轮换，核心映射见 `content/packs/official.mash/expression-semantics.json`。
