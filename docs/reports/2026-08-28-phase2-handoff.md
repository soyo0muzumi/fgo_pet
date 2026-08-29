# FGO Pet Phase 2 完成与 Phase 3 交接

日期：2026-08-28

基线：`main` 工作区 Release candidate

状态：**Phase 2 accepted；Phase 3 engineering handoff ready。**

用户于 2026-08-28 完成主 Release 环境复核并确认界面“大体没有问题”，批准 Phase 2
完成。完整多 DPI 矩阵仍作为未来公开发行门禁，不阻塞 Phase 3 开发。

## 1. 本次完成内容

- 同一附着面板保留 `专注 | 今日 | TODO | 对话`，无独立收起入口；
- UI 按批准的 HTML 终端仪表方案重构：横向预设、步进输入、预计时长、计时轮次与进度；
- 自定义专注、休息和轮次支持键盘输入以及 `− / ＋` 调整；
- TextBox、按钮、选择器、滚动条及其子元素不会触发桌宠窗口拖动；
- 专注页采用计时刻度底纹，其他展开栏目采用原创极简终端底纹；
- TODO 仅保留空接口表面，没有提前增加任务模型、存储或 Agent 行为；
- Phase 2 已有 SQLite、番茄状态机、今日时间线、每角色独立羁绊和角色包事件台词合同保持不变。

## 2. 自动化证据

2026-08-28 执行：

```powershell
pwsh -NoProfile -File scripts/test-phase2.ps1
```

结果：Release 构建 0 warning / 0 error；Core 109、Infrastructure 84、App 120、Windows 28，共 **341 passed / 0 failed**。

Release candidate：

```text
artifacts/release/FgoPet-win-x64/FgoPet.App.exe
```

这是 framework-dependent `win-x64` 构建，需要本机 .NET 8 Desktop Runtime。

## 3. 用户验收重点

1. 空闲 Compact、专注展开和运行 Compact 是否与批准的 HTML 层级一致；
2. 自定义三个输入框能否选中、全选、键盘修改，并正确显示错误；
3. 六个步进按钮在上下界是否正确夹紧；
4. 点击输入框、按钮和滚动条时人物不移动，点击面板空白区域仍可拖动；
5. 计时开始后显示轮次、剩余时间、进度、本轮元信息、暂停/继续和退出；
6. 展开栏目、底纹和滚动内容不重叠，人物位置不随面板伸展改变。

上述候选已经用户确认，Phase 2 状态为 `accepted`。Windows 多 DPI 完整发行矩阵仍是
最终公开发行前的独立门禁。

## 4. 已知遗留问题

- 从“对话”展开状态单击“专注”不能可靠切换到专注展开状态；其他 Phase 2 主流程可用。
- 用户明确批准不在 Phase 2 继续修改，将其作为后续阶段的面板导航回归项处理。
- 后续修复应首先增加 `ExpandedDialogue + FocusClick → ExpandedFocus` 的真实 WPF 点击集成测试，
  同时复核窗口拖动预览事件是否吞掉栏目按钮事件；不得借此重做面板状态机。

## 5. Phase 3 可直接复用的合同

- 运行时业务数据继续进入同一个版本化 SQLite，按领域新增聊天/记忆表，不把业务记录移回 JSON；
- 简单偏好继续使用版本化 JSON；API Key 使用 Windows Credential Manager；
- 角色身份以稳定 `servant_id` 关联，外观 ID 不作为 Persona 或记忆归属；
- 角色化内容继续来自纯数据角色包，主程序提供中性回退；
- `dialogue/` 属于 Phase 2 本地事件台词，Phase 3 Persona/Prompt/knowledge 需要独立 schema 与能力版本；
- 普通对话默认不加载剧情知识，仅在用户明确询问剧情时检索 approved 摘要；
- 第三方 Prompt 不得覆盖应用安全、权限、隐私和工具边界。

## 6. Phase 3 建议首批任务

1. 设计版本化 Persona、Prompt 和 approved knowledge 合同；
2. 建立模型供应商抽象、流式取消与中性错误状态；
3. 建立 SQLite 对话、消息和摘要 schema；
4. 实现短期上下文与可控长期记忆，提供用户查看和删除入口；
5. 将“对话”展开区接入真实数据流，同时维持 TODO 为空；
6. 补齐隐私、凭据、日志脱敏、导出和删除测试，再进入 Codex/Agent 阶段。

Phase 3 不应修改画像锚点、窗口拖动、面板状态机、番茄结算或羁绊归属；如需改变这些合同，应单独评审。
