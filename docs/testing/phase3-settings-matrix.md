# Phase 3 Settings — Windows Manual Matrix

Scope: manual verification of the settings shell, role package management,
runtime dialogue redesign, and configuration routing introduced in Phase 3.
Automated coverage: `scripts/test-phase3-settings.ps1` (four Release suites).
A required cell left "未观察" means the phase is not releasable.

Environment: clean Windows profile, 100% / 150% / 200% display scaling.

## Startup and shell

| # | Scenario | 100% | 150% | 200% |
|---|----------|------|------|------|
| S1 | Clean start without any installed pack opens 设置 > 角色包 (no legacy window) | 未观察 | 未观察 | 未观察 |
| S2 | Startup with a valid persisted selection shows the portrait and does not open any settings window | 未观察 | 未观察 | 未观察 |
| S3 | Tray menu shows exactly: 显示/隐藏, 设置, 打开角色包目录, 退出 (no 模型连接, no 从者库与设置) | 未观察 | 未观察 | 未观察 |
| S4 | Portrait menu shows exactly: 设置, 隐藏, 退出 (no 模型连接, no 从者库与设置) | 未观察 | 未观察 | 未观察 |
| S5 | Repeated 设置 navigation reuses the same settings window and keeps unsaved page state | 未观察 | 未观察 | 未观察 |

## Role packages

| # | Scenario | 100% | 150% | 200% |
|---|----------|------|------|------|
| P1 | Installing a `.fgopetpack` via double-click opens 设置 > 角色包 with the offered pack preselected | 未观察 | 未观察 | 未观察 |
| P2 | Package detail page shows previews, appearance list, and custom address editing persists | 未观察 | 未观察 | 未观察 |
| P3 | 打开角色包 (filesystem shortcut) opens the package root in Explorer | 未观察 | 未观察 | 未观察 |
| P4 | Appearance switch updates the portrait and address data | 未观察 | 未观察 | 未观察 |

## Model connection and offline behavior

| # | Scenario | 100% | 150% | 200% |
|---|----------|------|------|------|
| M1 | API key is saved to Windows Credential Manager; UI shows the masked/saved state | 未观察 | 未观察 | 未观察 |
| M2 | 拉取模型列表 populates the model list; Test connection reports status | 未观察 | 未观察 | 未观察 |
| M3 | With no model configured the pet starts and focus features run offline; no startup error dialogs | 未观察 | 未观察 | 未观察 |
| M4 | Dialogue panel with no model shows the configuration card; 去设置 · AI 模型与连接 opens 设置 > AI 模型与连接 | 未观察 | 未观察 | 未观察 |

## Runtime dialogue redesign

| # | Scenario | 100% | 150% | 200% |
|---|----------|------|------|------|
| D1 | Empty conversation shows 等待你的第一句话 placeholder; provider/model badges show correct values | 未观察 | 未观察 | 未观察 |
| D2 | Sending a message shows a right-aligned magenta user bubble and a left-edge cyan assistant reply | 未观察 | 未观察 | 未观察 |
| D3 | Streaming shows the stop control; 停止 cancels; 新对话 clears turns | 未观察 | 未观察 | 未观察 |
| D4 | The four header columns and hit-testing/drag behavior are unchanged | 未观察 | 未观察 | 未观察 |
| D5 | Turn list stays bounded (oldest turns drop) and does not overflow the panel | 未观察 | 未观察 | 未观察 |

## Memory and privacy

| # | Scenario | 100% | 150% | 200% |
|---|----------|------|------|------|
| R1 | 对话与记忆 page lists candidates and stored memories; editing/deleting works | 未观察 | 未观察 | 未观察 |
| R2 | Memory enable/disable switch persists | 未观察 | 未观察 | 未观察 |
| R3 | 导出 writes a redacted archive to the chosen path | 未观察 | 未观察 | 未观察 |
| R4 | 删除全部 shows the irreversible-confirmation dialog and clears conversations, memories, address, and model connection metadata | 未观察 | 未观察 | 未观察 |

## Icons and themes

| # | Scenario | 100% | 150% | 200% |
|---|----------|------|------|------|
| T1 | Theme switch (现代灰 / FGO 浅色) applies immediately and persists across restart | 未观察 | 未观察 | 未观察 |
| T2 | Header icons show focus and disabled states on all pages | 未观察 | 未观察 | 未观察 |

## Security redaction

| # | Scenario | 100% | 150% | 200% |
|---|----------|------|------|------|
| X1 | Export archive contains no API keys or absolute storage paths | 未观察 | 未观察 | 未观察 |
| X2 | Logs and error surfaces show no absolute paths or secrets | 未观察 | 未观察 | 未观察 |
