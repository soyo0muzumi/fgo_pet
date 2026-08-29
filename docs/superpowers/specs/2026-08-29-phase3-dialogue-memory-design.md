# FGO Pet Phase 3：对话、Persona 与记忆设计

日期：2026-08-29

状态：设计讨论已确认，等待书面复核

基线：Phase 2 已接受的 WPF 桌宠、附着面板、SQLite 运行库和角色包运行时

## 1. 目标

Phase 3 建立用户主动触发的本地对话运行时，支持 OpenAI-compatible Provider、流式回复、版本化 Persona/Knowledge、短期摘要、可审核长期记忆和本地隐私管理。

Phase 3 的首要交付是可用聊天闭环，同时将 Persona、记忆和剧情知识拆成可以独立演进的边界。没有模型连接时，Phase 1/2 的桌宠、番茄钟、今日时间线、羁绊和本地事件台词必须继续可用。

## 2. 明确的阶段边界

### 2.1 Phase 3 包含

- 模型连接向导和 OpenAI-compatible Provider；
- API Key 的 Windows Credential Manager 存储；
- Provider、Base URL 和当前 Model 配置；
- 当前会话、消息、流式回复、取消和错误状态；
- servant core Persona 与 appearance overlay；
- approved-only Knowledge 按需检索；
- 当前状态和短期会话摘要；
- 长期记忆候选、用户审核、编辑、禁用和删除；
- 聊天、记忆和内容上下文的本地导出与删除；
- 现有附着面板的 `ExpandedDialogue` 真实数据流；
- Phase 2 面板导航回归项 `ExpandedDialogue + FocusClick → ExpandedFocus`。

### 2.2 Phase 3 不包含

- FGO Pet 云端账号、密码或服务器登录系统；
- TODO 任务模型；
- Codex Skill、MCP、Agent 事件桥、离线队列和任务跳转；
- 应用名称、窗口标题、屏幕内容、文档内容或键盘输入感知；
- 模型工具调用或本地命令执行；
- 语音输入、语音合成和远程同步；
- 跨从者全局记忆；
- 外观专属长期记忆；
- 自动人格成长或模型自行修改 Persona；
- 角色包可执行代码；
- Phase 5 的完整备份恢复、安装包和首次发行流程。

Phase 2 的画像锚点、窗口拖动、DPI 几何、面板状态机、番茄结算和羁绊归属不因 Phase 3 改变。需要修改这些合同时，必须单独评审。

## 3. 总体架构

采用端口/适配器结构：

- Core：会话、消息、内容上下文、模型输出、记忆候选和安全状态的数据合同；
- App：对话编排、Prompt 组装、流式取消、输出校验、错误降级和 ViewModel；
- Infrastructure：HTTP Provider、Credential Manager、SQLite、角色包内容读取和 FTS5 检索；
- WPF：只绑定状态和命令，不直接访问 HTTP、SQLite、Credential Manager 或 Prompt 文件。

数据流：

```text
ExpandedDialogue
  → ConversationOrchestrator
  → PromptComposer
  → IChatProvider
  → StructuredOutputValidator
  → SQLite 消息记录
  → 流式显示与角色表情反馈
```

Phase 3 只有用户主动发送消息时才调用模型。Phase 2 的专注开始、完成、暂停、休息和羁绊事件继续使用本地反馈，不自动触发 LLM 请求。

## 4. 身份、内容和外观关系

`servant_id` 是稳定的角色领域身份，关联聊天、记忆、Persona、Knowledge、Dialogue 和羁绊。`appearance_id` 只标识当前外观，但外观可以提供 Persona/Knowledge 覆盖层。

```text
servant_id
├─ servant core persona
├─ appearance overlay
├─ package/version binding
├─ conversations
├─ memories
└─ bond
```

一个聊天回合记录实际使用的内容上下文：

```text
servant_id
package_id
package_version
appearance_id
persona_version
knowledge_version
```

换服装或升级角色包时，后续回合可以使用新的内容上下文，历史消息不被重写。只要 `servant_id` 不变，记忆和羁绊不迁移、不丢失。

如果角色包升级后仍声明相同 `servant_id`，它是同一角色的新内容版本；如果声明不同 `servant_id`，则是新角色，不自动继承数据。

## 5. 角色包内容合同

建议的声明式结构：

```text
persona/
├─ core.json
└─ appearances/
   └─ <appearance_id>.json

knowledge/
├─ topics.jsonl
└─ appearance-overrides.jsonl
```

`core.json` 提供稳定身份、人格和语言风格；外观文件提供服装、灵基或阶段特有覆盖；Knowledge 条目声明适用的 `servant_id`、`appearance_id`、剧情阶段、关键词、摘要和来源定位。

运行时只接受 `approved` 内容。`pending` 和 `rejected` 内容不可见。内容包只能包含严格校验的 JSON/JSONL，不允许脚本、条件表达式、函数、工具调用或可执行模板。

Prompt 优先级：

```text
应用安全与隐私规则
  > 产品能力边界
  > servant core persona
  > appearance overlay
  > approved knowledge
  > 当前专注/系统状态
  > 已确认记忆
  > 用户消息
```

## 6. 模型连接与 Login 交互

“Login”定义为本地模型连接配置，不引入 FGO Pet 账号系统。它与角色包加载、从者选择和称呼配置完全分开。

首版 Provider 选项：

- OpenAI；
- DeepSeek；
- 自定义 OpenAI-compatible 服务。

连接向导只包含：

```text
模型连接

模型供应商   [ OpenAI ▼ ]
API Key      [••••••••••] [显示/隐藏]
Base URL     [https://api.openai.com/v1]
当前 Model   [ gpt-... ▼ ] [刷新模型列表]

[测试连接] [保存]
[跳过，保持离线使用]
```

Provider 预设只提供显示名和默认 Base URL，不声称提供官方账号登录。API Key 按不透明秘密处理，不依赖特定前缀。若 Provider 不支持 `/models`，允许手动填写 Model ID。

当前活动配置在设置页显示 Provider 和 Model，例如 `DeepSeek · deepseek-chat`。首版只有一个全局活动 Provider，不提供每个角色独立 Model 覆盖。

安全存储规则：

- API Key 只写入 Windows Credential Manager；
- `settings.json` 只保存 Provider ID、Base URL 和 Model ID；
- 设置页不回显 Credential Manager 中的明文 Key，只显示“已保存”；
- API Key 不进入 SQLite、日志、导出文件或角色包；
- 外部地址要求 HTTPS；本地模型允许回环地址；
- 模型连接失败只显示安全错误类别。

角色包未安装或未选择时也可以配置模型连接；没有模型连接时角色包和 Phase 2 功能仍可运行。

## 7. 按 servant_id 保存的角色称呼设置

称呼不放在 Login 页面。它属于当前角色的独立用户偏好，在“从者库与设置”中配置并按 `servant_id` 保存。

用户界面只提供两个选项：

```text
称呼方式：

○ 使用角色包默认称呼：前辈
○ 使用我的昵称或自定义称呼：[          ]
```

运行时字段：

```json
{
  "servant_preferences": {
    "800100": {
      "address_mode": "package_default",
      "address_text": ""
    }
  }
}
```

称呼解析优先级：

```text
用户为该 servant_id 设置的 address_text
  > 当前外观的角色包默认称呼
  > servant 级角色包默认称呼
  > 程序中性回退
```

选择角色包默认称呼时，角色包升级或更换外观可以提供新默认值；选择用户自定义时，用户设置保持不变。卸载角色包不删除该 `servant_id` 的设置，重新安装相同角色后恢复。

不自动读取 Windows 用户名或模型 Provider 账号名。若未来增加本地用户资料名，它只能作为 `address_text` 的可选填充值，不改变当前按角色保存的归属。

## 8. 对话与消息

Phase 3 使用现有 `ExpandedDialogue`，不创建第二个聊天窗口。首版提供当前会话、消息列表、多行输入、发送/停止、新建会话和内联错误状态。

首版不提供会话搜索、标签、归档和复杂管理。历史消息仍完整保存在本地，并在应用重启后恢复当前会话。

消息生命周期：

```text
写入用户消息
  → 读取 servant_id 与内容上下文
  → 流式显示回复
  → 成功：写入完整助手消息
  → 取消：写入 cancelled 状态，不保存半截正文
  → 失败：写入 failed 状态和安全错误类别
```

流式生成期间禁止重复发送。用户手动滚动后不强制跳回底部。输入框、按钮和滚动条继续遵守 Phase 2 的“不触发窗口拖动”合同。

## 9. SQLite 与消息数据

沿用同一个版本化 `runtime.db`，新增 Phase 3 迁移版本。建议数据职责如下：

| 数据 | 关键字段与规则 |
|---|---|
| `conversations` | 会话 ID、`servant_id`、创建/更新时间和状态 |
| `chat_messages` | 会话 ID、角色、序号、正文、完成/取消/失败状态、内容上下文版本 |
| `conversation_summaries` | 摘要正文、覆盖到的消息边界和摘要版本 |
| `memory_candidates` | `servant_id`、来源消息、候选内容和审核状态 |
| `memories` | `servant_id`、确认内容、启用状态和更新时间 |
| `content_bindings` | package、版本、appearance、Persona/Knowledge 版本与哈希 |

数据库不保存完整 system Prompt、Provider 原始响应、API Key、原始剧情正文、本地绝对路径或 Codex 工具过程。

`settings.json` 保存非敏感的全局 Provider 元数据、隐私选项和按 `servant_id` 索引的称呼偏好。API Key 单独存储。

## 10. 记忆生命周期

短期摘要和长期记忆分开：

```text
聊天消息
  → 当前会话上下文
  → 可选短期摘要
  → 模型提出 memory_candidate
  → 用户查看/编辑/确认
  → approved memory
```

规则：

- 短期摘要只服务当前会话，不自动变成长​​期记忆；
- 长期记忆默认按 `servant_id` 隔离；
- 长期记忆不绑定 `appearance_id`；
- 模型只能提出候选，不能直接写入确认记忆；
- 候选可接受、编辑、拒绝和删除；
- 已确认记忆可查看、编辑、禁用和删除；
- 只注入与当前问题相关的记忆，最多约 600 tokens；
- 关闭长期记忆后，不再生成或注入长期记忆；
- 不后台扫描全部历史聊天；
- 剧情检索结果不会自动进入长期记忆；
- 删除聊天不会悄悄删除已确认记忆；删除全部用户数据时一并删除。

## 11. Knowledge 检索与 Prompt 预算

普通对话不加载剧情 Knowledge。只有用户明确询问人物设定或剧情时，才从 approved-only 本地索引检索。

检索复用现有 FTS5 能力，最终只注入少量摘要和来源定位：

- 常驻人格、行为规则和当前状态约 2,500 tokens；
- 当前状态最多 250 tokens；
- 短期记忆最多 600 tokens；
- 剧情 Knowledge 最多约 900 tokens；
- 不注入完整脚本、连续长台词或整个 evidence 文件。

检索结果状态保留为 `answered_from_profile`、`answered_from_story` 或 `coverage_gap`，不向用户泄漏本地路径、完整 Prompt 或内部分类。

## 12. 模型输出与错误降级

模型输出目标结构：

```json
{
  "text": "回复正文",
  "emotion": "default",
  "feedback_type": "conversation",
  "memory_candidate": null
}
```

`emotion` 必须映射到当前外观支持的核心表情语义；`feedback_type` 只影响展示，不触发系统操作；`memory_candidate` 只能进入候选队列。

非法 JSON 时尝试提取正文，非法表情回退到 `Default`。超时、断网、Key 无效或模型不可用时显示中性错误，不伪造角色回复，不影响 Phase 2 计时和本地反馈。

## 13. 隐私、导出与删除

Phase 3 提供用户数据导出和删除，但不提供 Phase 5 的完整备份恢复。

导出包含：

- 会话与消息；
- 短期摘要；
- 记忆候选及其审核状态；
- 已确认记忆；
- 必要的 schema 和内容版本元数据。

导出不包含：

- API Key；
- 完整 Prompt；
- Provider 原始响应；
- 角色包原始资源；
- 完整剧情正文；
- 本地绝对路径。

删除会话时删除该会话及未确认候选；已确认记忆需要单独删除。删除全部用户数据时，聊天、摘要、候选和已确认记忆全部删除。

## 14. Phase 3 内部交付顺序

### 3A：对话基础设施

- Runtime DB 迁移；
- Provider 抽象和 OpenAI/DeepSeek/自定义预设；
- Credential Manager；
- Login/模型连接向导；
- 消息存储、流式取消和错误降级；
- `ExpandedDialogue` 接入；
- Provider/Model 当前状态展示。

### 3B：角色 Persona 与记忆

- servant core 与 appearance overlay；
- 按 `servant_id` 保存的称呼设置；
- Prompt Composer；
- 短期摘要；
- 记忆候选和审核 UI；
- 查看、编辑、禁用、删除和导出；
- 角色包升级后的内容上下文追溯。

### 3C：approved Knowledge

- Persona/Knowledge schema 与能力版本协商；
- 角色包严格读取与回退；
- FTS5 查询路由；
- approved-only 检查；
- coverage gap；
- 来源定位和隐私预算测试。

## 15. Phase 4/5 交接

Phase 4 可在不改变 Phase 3 基础数据含义的前提下增加可选外部任务上下文和已脱敏统一事件，并实现 Codex Skill、MCP、本地事件桥、离线队列、重连、去重和任务跳转。

Phase 3 不读取 Codex 私有数据库，不抓取终端日志，不保存完整 Agent 工具过程，也不因外部事件自动调用模型。

Phase 5 可在版本化导出基础上增加完整备份/恢复、导入迁移、应用感知、收藏、安装包、首次使用引导和正式发行。

## 16. 完成标准

- 未配置模型时 Phase 1/2 全部能力保持可用；
- OpenAI、DeepSeek 和自定义 OpenAI-compatible 配置均可测试并保存；
- API Key 不出现在 JSON、SQLite、日志或导出文件；
- Provider 和当前 Model 在设置及对话状态中清晰可见；
- 角色包加载与模型连接配置可以独立完成；
- 称呼设置只在从者设置中配置，并按 `servant_id` 恢复；
- 外观变化可以切换 Persona/Knowledge 覆盖，不影响记忆和羁绊；
- 流式回复、取消、错误回退和历史恢复通过自动化测试；
- `pending/rejected` Knowledge 永不进入 Prompt；
- 记忆只能经用户确认成为事实；
- 聊天、记忆、导出、删除和敏感信息脱敏测试通过；
- Phase 2 面板、拖动、DPI、番茄钟和羁绊回归测试通过；
- Phase 4/5 尚未实现的功能不会在 UI 中伪装成可用。
