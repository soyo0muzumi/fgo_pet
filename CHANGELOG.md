# Changelog

## Unreleased

当前版本处于首版发布准备阶段。自动化测试、独立 Release acceptance gate、WiX 5.0.2 MSI 构建、Mash 正式角色包构建和用户人工生命周期验收已完成；最终发布授权仍待收口。

## 0.1.0（候选版本）

首版候选内容：

- Windows 11 WPF 桌宠与附着面板。
- 专注计时、今日时间线、羁绊、Todo、对话与记忆。
- 数据型 `.fgopetpack` 角色包安装与校验。
- Codex Agent Relay/Adapter 集成及默认拒绝的授权边界。
- 私有备份与恢复、配置引导和 Release candidate 验证工具。

已知限制：

- 尚未完成公开发布授权。
- 同时提供 Windows x64 ZIP 和 WiX per-user GUI MSI；MSI 已完成真实安装、启动、升级数据保留和卸载验收。
- 角色包与应用是分开的 Release artifact。
- 正式 Mash 角色包为 `official.mash-1.0.0.fgopetpack`，保留 28 个视觉表情，LLM 仅使用 Phase 3 八个核心语义键。
- Agent 和模型能力需要用户单独配置；离线桌宠和专注功能不依赖它们。

## 0.1.1 计划

- 历史对话查看：会话列表、打开会话、加载消息，以及重启后的最近会话恢复。
