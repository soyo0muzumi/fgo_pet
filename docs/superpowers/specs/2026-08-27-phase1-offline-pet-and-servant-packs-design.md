# FGO Pet Phase 1 离线桌宠与从者扩展包设计

日期：2026-08-27  
状态：待用户审阅  
依据：`docs/reports/2026-08-27-phase1-handoff.md`、`docs/decisions/0001-windows-portrait-renderer.md`

## 1. 目标

Phase 1 交付一个不依赖 Python、LLM 或 Codex 插件即可独立运行的 Windows 11 桌宠宿主。程序发行包不包含任何从者图片、Prompt 或人格资源；玛修作为首个独立 `.fgopetpack` 单独版本化和发布。后续新增从者、外观、表情和人格资源时不需要修改或重新发布应用程序。

Phase 1 主计划覆盖正式 WPF 壳、从者扩展包运行时、窗口生命周期、从者库、表情语义和可收起附着 UI。素材整理与发行工具作为独立的 P1.4 计划，共享同一扩展包协议。

## 2. 固定技术基线

- Windows 11、.NET 8、WPF。
- WPF 双层 `Image` 合成；不引用 SkiaSharp。
- `AllowsTransparency=True`；不保留 DWM 或 renderer 运行时选择。
- 图片使用 `BitmapCacheOption.OnLoad` 解码并 `Freeze()`，加载后释放文件句柄。
- 默认人物比例为 `0.50`，仅支持 `0.50`、`0.60`、`0.75`。
- 身体、表情、底部锚点和面板锚点使用同一个源像素到设备像素变换。
- 使用 `CommunityToolkit.Mvvm` 和 `Microsoft.Extensions.DependencyInjection`、`Configuration`、`Logging`；不引入第三方 UI 控件库。
- 正式应用是模块化单体，资源扩展包永远不加载第三方代码。

## 3. 范围

### 3.1 包含

- 正式 WPF solution 与应用生命周期。
- 玛修首发包与其他本地从者扩展包的安装、校验、卸载、重扫、版本共存和回退。
- 未安装任何角色包时仍可启动托盘和从者库，并引导用户安装本地 `.fgopetpack`。
- 从者、外观、比例和置顶设置。
- 独立从者库与设置窗口。
- 透明无边框窗口、托盘、单实例、拖动、像素级命中和透明区域穿透。
- 位置持久化、混合 DPI、多显示器拓扑变化和不可见窗口恢复。
- 八类核心表情语义及包级映射和回退。
- `Collapsed`、`Compact`、`Expanded.Dialogue`、`Expanded.Todo` 附着 UI 状态。
- 对话/Todo 数据模板、有界增长、滚动和极端内容 fixture。
- 本地脱敏诊断与可操作的包错误。

### 3.2 不包含

- 在线从者商店、GitHub API、应用内下载、账号、付费或自动更新。
- 资源包数字签名；仅预留未来兼容空间。
- 第三方 DLL、脚本、XAML、HTML、着色器或其他可执行扩展。
- 正式 Todo 编辑、持久化与业务来源。
- LLM 对话、Prompt 执行、记忆、剧情检索、番茄钟、事件中心和 Codex 插件。
- 安装包、开机启动和完整首次使用引导。

## 4. 工程边界

```text
FgoPet.sln
src/
  FgoPet.App/                    WPF 启动、窗口、视图、ViewModel、位图与画像控件
  FgoPet.Core/                   包契约、画像/表情状态、设置模型、服务接口
  FgoPet.Infrastructure/         文件包仓库、安装器、JSON 设置、屏幕、日志
tests/
  FgoPet.Core.Tests/             纯逻辑、状态与几何测试
  FgoPet.Infrastructure.Tests/   包、文件、设置与恢复测试
  FgoPet.App.Tests/              STA/WPF 组件和 ViewModel 测试
  FgoPet.Windows.Tests/          Windows、DPI、托盘和窗口集成测试
tools/
  FgoPet.Packaging/              P1.4 扩展包 SDK/CLI；不属于主计划
```

依赖方向固定为 `App -> Core`、`App -> Infrastructure`、`Infrastructure -> Core`。`Core` 不引用 WPF。WPF 位图解码和视图留在 `App`；文件、归档、manifest 和安装事务留在 `Infrastructure`。

## 5. 从者扩展包

### 5.1 安全边界

`.fgopetpack` 是使用专属扩展名的 ZIP 归档，只允许图片、JSON、Markdown、纯文本本地化和来源说明。安装器拒绝程序集、可执行文件、脚本、宏、XAML、HTML、着色器、绝对路径、越界路径、符号链接和未声明文件类型。资源包不能执行命令、修改应用目录或写注册表。

人格与 Prompt 属于声明式资源。Phase 1 可以安装和保存这些资源，但不解释或执行它们。未来 Phase 3 必须把第三方 Prompt 当作不可信输入，且不得允许其覆盖应用安全规则、工具权限或隐私策略。

### 5.2 包结构

```text
official.mash-1.0.0.fgopetpack
├─ package.json
├─ previews/
│  └─ library.png
├─ appearances/
│  └─ casual/
│     ├─ manifest.json
│     └─ runtime/
│        ├─ full_body.png
│        └─ expressions/*.png
└─ persona/                       可选；Phase 1 不执行
   ├─ profile.json
   ├─ system_prompt.md
   ├─ style_rules.json
   └─ localizations/zh-CN.json
```

包级 `package.json` 使用 pack schema v1，负责包、从者、发行者、兼容版本和外观列表。外观 `manifest.json` 使用 art schema v3，负责图像、合成几何、表情语义和哈希。二者分离，避免破坏已验证的素材管线职责。

现有 art schema v2 保持原义并作为只读迁移输入；它仍代表玛修固定的 `full_body + 7x4` 产物。P1.4 工具把 v2 确定性转换为 v3，正式 `.fgopetpack` 只发布 v3 外观 manifest。应用运行时不把两种结构解释为同一个 schema 版本。

稳定身份分为：

```text
servant_id       mash_kyrielight
package_id       official.mash
package_version  1.0.0
appearance_id    casual
```

显示名称和路径不能充当身份。包版本使用 SemVer；同一包的多个版本可以并存。

### 5.3 最低发行内容

每个包必须包含 `package.json`、一张从者库预览图和至少一个外观。每个外观必须包含：

- 一张稳定身体底图 `full_body`。
- 至少一张具有可见 Alpha 的独立表情覆盖图。
- 身体、表情、偏移、尺寸、面板锚点、默认比例和文件哈希。
- 八类核心语义的完整映射；多个语义可以映射到同一表情。

玛修首发包包含现有 303x603 `full_body`、28 张 256x240 表情、`(13, 0)` overlay 偏移、`(151, 360)` 面板锚点、从者库预览图和可选人格资源。

### 5.4 表情语义

核心应用只请求以下稳定语义，不直接请求包内图片 ID：

- `neutral`
- `happy`
- `excited`
- `shy`
- `concerned`
- `sad`
- `surprised`
- `angry`

资源包可以定义额外语义，但核心应用不依赖它们。每个外观必须将八类核心语义解析到有效图片；回退链最终必须到达 `neutral`。`neutral` 不可用时整个外观无效。

art schema v3 不再包含 v2 的固定 `7x4` 与 `r01c01` ID 限制，改为显式 `asset_type` 和 expression 列表。玛修转换到 v3 后仍保持现有稳定 ID。

### 5.5 安装事务与信任

安装流程为：

```text
选择 .fgopetpack
  -> 归档结构、条目数、单文件和总解压大小预检
  -> 解压到随机 staging 目录
  -> 严格解析 manifest
  -> 校验路径、哈希、类型、图片、几何、语义和应用兼容版本
  -> 生成脱敏诊断
  -> 原子移动到 Packages/<package-id>/<version>
  -> 更新本地索引
  -> 用户明确选择后才切换当前从者
```

安装失败时删除 staging 并保留当前包。更新不会覆盖旧版本。卸载当前包前必须先切换到其他有效包；最后一个角色包允许卸载，卸载后应用进入“未安装角色”状态。Phase 1 没有数字签名，因此包括玛修在内的所有外部包都显示“未验证来源”；包内自称 `verified` 不影响应用信任判断。

GitHub Releases 是首期分发渠道。程序安装包与角色包使用独立发行物和独立版本：程序 Release 不附带角色资源，角色包 Release 提供 `.fgopetpack`、外部 SHA-256 和变更说明。Phase 1 应用不调用 GitHub API。用户下载后可双击文件或从从者库选择文件安装。

### 5.6 加载和缓存

从者或外观切换采用两阶段提交：后台完成验证、解码、冻结和几何计算，成功后在 UI 线程一次性替换画像快照。失败时原画像保持不变。

缓存采用“当前外观全量预载 + 最近一个外观快照”。加载第三个外观时释放最旧快照引用。正常表情切换只替换 overlay `Image.Source`，不重建身体，不改变窗口外部尺寸、底部锚点或面板锚点。

恢复顺序为当前有效版本、同包上一有效版本、最后验证成功的已安装包。没有任何有效包时应用不创建画像窗口，保留托盘并打开从者库安装引导；这不是启动错误。

## 6. 桌宠窗口与生命周期

桌宠窗口透明、无边框、默认置顶且不显示任务栏按钮。托盘图标始终存在，提供显示/隐藏、从者库与设置、打开资源包目录和退出。

应用是单实例。第二实例普通启动时激活现有进程；携带 `.fgopetpack` 路径时把安装请求转交给现有进程。

### 6.1 点击、拖动和透明穿透

指针状态机为：

```text
Pressed -> 未超过系统拖动阈值 -> Released = 单击
Pressed -> 超过系统拖动阈值 -> Dragging
Dragging -> Released = 保存位置
```

身体和表情保存源像素 Alpha 掩码。运行时将指针从当前设备坐标映射回源像素：身体或当前表情 Alpha 超过阈值时命中人物；面板矩形交给 WPF 控件命中；其余区域通过 `WM_NCHITTEST` 穿透。右键菜单只在人物或面板命中区域打开。命中查询不得在每次鼠标移动时分配位图或大数组。

### 6.2 DPI、位置和屏幕恢复

设置保存当前从者、包版本、外观、比例、置顶和自动收起选项。窗口位置另存显示器稳定标识、相对工作区的 DIP 坐标、保存时 DPI 和最后可见时间。

恢复时依次匹配原显示器、与旧区域重叠最多的显示器、主显示器；随后按当前 DPI 重建整套画像几何，并把窗口约束到工作区，至少保证人物主体和拖动区域可见。`WM_DPICHANGED`、工作区变化和显示器拓扑变化均触发统一重算，不能只改窗口宽高。

状态写入 `%LocalAppData%/FgoPet/` 下的版本化 JSON，使用临时文件和原子替换。设置损坏时保留坏文件用于诊断并使用安全默认值。

## 7. 用户界面分工

### 7.1 桌宠附着 UI

附着 UI 只承载高频、临时操作，不承担包安装或完整角色管理。状态为：

```text
Collapsed
  -> Compact
  -> Expanded.Dialogue
  -> Expanded.Todo
```

- `Collapsed`：默认及每次启动状态，只显示人物，不预留面板空白。
- `Compact`：点击人物后显示小型控制条，包括对话、Todo、当前从者头像/快速切换、设置和收起。
- `Expanded.Dialogue`：显示有界对话历史。
- `Expanded.Todo`：显示有界 Todo 列表。

点击当前入口、收起按钮或按 `Esc` 逐级收起。Expanded 在鼠标不位于人物或面板、且连续 30 秒无交互时默认回到 Compact；该自动收起可在设置中关闭。重启不恢复临时展开状态。

对话内存历史最多 20 条，面板约显示 6 条并滚动到最新项。Todo 最多显示 8 行，其余滚动。Phase 1 只使用本地 fixture 验证模板和状态，不提供 Todo 编辑或持久化。

面板最大高度为当前工作区的 60%，宽度限制在受控 DIP 区间。它优先从 `panel_anchor` 向左展开，空间不足时翻转到右侧，上下也受工作区约束。展开、收起、文本增长、DPI 或移动均不改变人物位置。

### 7.2 快速切换

Compact 控制条显示当前从者头像。点击后出现轻量快速切换器，只列出最近使用的 3 至 5 名从者；选择项仍使用两阶段画像切换。“管理从者库”进入独立窗口。

### 7.3 独立从者库与设置窗口

从者库是普通独立 WPF 窗口，不受附着面板尺寸限制，负责：

- 搜索和浏览全部已安装从者。
- 查看缩略图、名称、包版本、来源状态和可用外观。
- 设置当前从者与外观。
- 安装 `.fgopetpack`、卸载第三方包和重新扫描。
- 打开资源包目录和查看脱敏诊断。
- 设置人物比例、置顶和附着 UI 自动收起。

窗口采用左侧从者列表、右侧选中从者详情以及“从者库、常规、外观与窗口、诊断”导航。安装或切换错误在该窗口显示完整可操作摘要，桌宠继续保持原角色。

## 8. 错误与诊断

资源错误必须提供稳定错误码，至少包括：

- `PackageArchiveInvalid`
- `PackagePathEscapesRoot`
- `PackageTooLarge`
- `ManifestMalformed`
- `SchemaUnsupported`
- `AppVersionIncompatible`
- `AssetMissing`
- `AssetHashMismatch`
- `ImageDecodeFailed`
- `ImageHasNoVisibleAlpha`
- `CompositionOutOfBounds`
- `ExpressionMappingInvalid`

日志记录包 ID、版本、错误码和脱敏相对路径，不记录 Prompt 正文、未来聊天正文、凭据或仓库外原始素材路径。截图与压力诊断放在测试或开发构建中，不进入日常画像接口。

## 9. 测试策略

### 9.1 Core

- 八类语义映射、共享图片、回退链循环、缺失 neutral。
- `Collapsed/Compact/Expanded` 全部合法和非法状态转换。
- 统一画像几何、不同 X/Y DPI、负屏幕坐标和工作区约束。
- 设置默认值、版本迁移和损坏恢复。

### 9.2 Infrastructure

- 非法 ZIP、Zip Slip、符号链接、条目数/大小限制和 staging 清理。
- 严格 JSON、未知 schema、应用版本不兼容和未声明文件类型。
- 缺失文件、哈希不符、无可见 Alpha、图片解码失败和合成越界。
- 原子安装、版本共存、卸载约束、重扫、旧版本与内置包回退。

### 9.3 App 与 STA

- 身体对象只加载一次，28 个表情切换只替换 overlay。
- 比例和 DPI 变化后所有边界来自同一变换。
- Alpha 命中、透明穿透、点击/拖动阈值和右键区域。
- 快速切换两阶段提交和失败保留原画像。
- 长中文、英文长词、空列表、超长 Todo、字体回退和较大字号。
- 面板左右翻转、60% 高度限制、自动收起和不改变人物锚点。

### 9.4 Windows 集成和人工验证

- 单实例、安装文件转交、托盘恢复、隐藏和退出。
- Windows 11 下 200% 完整验证与 150% 最终组合验证。
- 混合 DPI 双显示器实际跨屏、负坐标、显示器断开与恢复。
- 透明像素穿透且人物/面板可操作。
- 多包反复切换、28 表情循环、面板反复展开/收起和长时 soak。

## 10. 实施里程碑

### P1-A 正式壳与画像基线

建立 solution、依赖注入、配置和日志；迁移画像 schema、loader、统一几何与 WPF 双层画像；无角色包时可进入从者库，安装本地玛修包后可离线显示；移植 Phase 0 测试。

### P1-B 从者扩展包系统

实现 pack schema v1、art schema v3、v2 到 v3 迁移 fixture、安装事务、版本和回退、从者库与设置窗口、从者/外观切换；把玛修转换为独立发布、可卸载的首发角色包。

### P1-C 窗口与生命周期

实现单实例、托盘、隐藏/恢复/退出、安装文件转交、点击/拖动、Alpha 命中、透明穿透、位置持久化、DPI 重排和屏幕恢复。

### P1-D 表情与可收起附着 UI

实现八类表情语义和回退、Compact 快速切换、对话/Todo 模板、有界增长、滚动、翻转布局、自动收起和极端 fixture。

### P1.4 独立计划：扩展包 SDK 与发行工具

参数化素材整理，生成人工确认预览、语义映射模板、预览图、`.fgopetpack`、外部 SHA-256 和 QA 报告；支持 GitHub Release 发行产物。未识别版式必须停止并要求人工确认。

## 11. 验收标准

- 无 Python、LLM、Codex 插件或角色包时，应用仍可启动托盘和从者库；安装本地玛修包后可完全离线显示桌宠。
- 程序发行包与角色包分别构建、分别版本化、分别发布；程序包不含角色图片、Prompt 或人格资源。
- 正式应用不引用 SkiaSharp，不包含 renderer/DWM 开关，不加载资源包代码。
- 有效 `.fgopetpack` 可安装、重扫、切换、共存和卸载；损坏包不影响当前画像。
- 身体保持稳定，表情切换不改变窗口外部尺寸、底部锚点和面板锚点。
- 默认 50%，可选 60%/75%；200% 与 150% 下无可见接缝。
- 混合 DPI、多显示器断开和重启后窗口保持可见。
- 透明区域可穿透，人物和面板可点击、拖动和右键操作。
- 附着 UI 默认收起，展开不超过工作区 60%，收起后不保留工作区占位。
- 从者库与对话/Todo 完全分离，Compact 可快速切换最近从者。
- 动态 fixture 不会导致面板无限增高或破坏画像锚点。
- 日志和诊断不记录 Prompt、聊天正文、凭据或敏感绝对路径。

## 12. 风险与缓解

- **透明窗口像素命中复杂。** 使用源 Alpha 掩码和纯坐标映射；把 `WM_NCHITTEST`、DPI 和拖动状态纳入 Windows 集成测试。
- **第三方归档可能造成路径穿越或资源耗尽。** 安装前限制条目数、单文件大小、总解压大小和允许类型；只在 staging 内解压并拒绝链接。
- **schema 同时服务 Python 工具和 .NET 运行时。** 保留包级与外观级分层；v2 不改语义，v3 承载泛化协议，并建立共享 fixture 和跨语言契约测试。
- **多显示器恢复依赖真实 Windows 行为。** 纯逻辑屏幕模型配合真实混合 DPI 人工门槛，不再豁免双显示器。
- **多包全量预载可能提高内存。** 缓存限定为当前与最近外观，并增加反复切换和 soak test。
- **未来 Prompt 带来信任问题。** Phase 1 仅保存不执行；Phase 3 必须采用独立 schema、安全分层和权限隔离。

## 13. 已拒绝方案

- 不把渲染探针直接演变为产品工程。
- 不采用双项目大应用，以免窗口、资源包和未来事件系统耦合。
- 不采用程序集/MEF 插件，以免第三方资源获得代码执行能力。
- 不把从者库、安装管理与对话/Todo 放在同一个附着面板。
- 不在 Phase 1 接入 GitHub API、在线商店或自动更新。
- 不让包自报“官方已验证”；信任状态只能由安装来源或未来签名产生。
