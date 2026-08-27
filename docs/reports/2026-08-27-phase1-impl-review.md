# FGO Pet Phase 1 实施审核任务

日期：2026-08-27（2026-08-28 内审补充）
状态：代码内审与生产组合补充已完成；实机审核和附着面板生产接线仍阻塞发布

> 本文件把「代码接口审核」与「实机审核」写成可勾选的审核任务清单，并记录当前已完成的
> 实施与测试证据。审核通过前不把 Phase 1 标记为可发布。

---

## 1. 审核范围

Phase 1 主计划（13 项任务）已在 `src/FgoPet.Core`、`src/FgoPet.Infrastructure`、
`src/FgoPet.App` 全部提交（`0a4cdc8`→`f8d4c71`）。P1.4（Python 扩展包 SDK）是独立计划，
不在本次范围内。本次审核回答三个问题：

- 代码与接口是否自洽、是否与计划接口一致？
- 测试证据是否真实、覆盖是否充分？
- 真机上是否按验收标准可运行？

---

## 2. A 代码 / 接口审核清单（内审）

### A0 提交与工作树

- [x] 以下 3 个遗留修改已复判并纳入本轮待提交变更：
  - `src/FgoPet.App/Bootstrap/ServiceRegistration.cs`（IAppLifetime 改 AppLifetimeService 工厂）
  - `tests/FgoPet.Infrastructure.Tests/Packs/PackArchiveBuilder.cs`（PackManifestJson 增加 servantId/displayName）
  - `tests/FgoPet.Infrastructure.Tests/Packs/FgoPetPackInstallerTests.cs`（4 个测试改 async/await，消除 xUnit1031）
- [x] `ServiceRegistration.cs` 末尾换行已补齐。

### A1 公共接口一致性（Core 层）

| 接口 | 位置 | 核对点 | 待核对 |
|---|---|---|---|
| `IPackInstaller.InstallAsync(path, ct) -> PackInstallResult` | Core/Packs | 与计划签名一致 | 已通过 |
| `IArtPackageRepository`（Scan/List/Get/Resolve/Remove/MarkLkg） | Core/Packs | 六个方法齐备，四段恢复已测 | 已通过 |
| `IPortraitController`（Activate/SetExpression/SetScale/ApplyDpi） | Core/Portraits | 保留 `ApplyDpi`：窗口 `WM_DPICHANGED` 必需；已补直接测试 | 已通过 |
| `IExpressionResolver.Resolve(requested, manifest)` | Core/Portraits | 与计划一致 | 已通过 |
| `IAppSettingsStore` / `IWindowPlacementStore` / `IScreenLayoutService` | Core/Settings,Windowing | 与计划一致；屏幕服务已补真实枚举测试 | 已通过 |
| `IResourceDiagnostics` | Core/Diagnostics | 只收白名单字段 | 已通过 |
| 契约记录 `PackManifestV1` / `AppearanceManifestV3` / `CompositionV3` | Core/Packs | 严格 JSON + ExtensionData 拒绝未知字段 | 已通过 |

### A2 已知缺口 / 风险点（内审需逐条复判）

- [x] **生产组合已接线**：新增 `DesktopAppShell` / `DesktopAppUi`，`ServiceRegistration`
  现可解析 repository、installer、controller、tray、library、portrait window 和 coordinator。
  无包时显示从者库；有效选择会激活并显示画像；`.fgopetpack` 参数进入从者库但不自动激活。
  角色激活后的 `StateChanged` 会显示画像窗口。
- [x] 透明命中和拖动状态已有纯逻辑测试；本轮新增 coordinator 位置恢复集成测试。
  `WM_NCHITTEST` 与 `WM_DPICHANGED` 的真实消息投递仍需实机矩阵提供最终证据。
- [x] `WindowsScreenLayoutService` 已补真实 Win32 枚举、主显示器工作区和正 DPI 测试。
- [x] `PortraitController.ApplyDpi` 已补非等比 X/Y DPI 直接测试，验证选择/比例保持且整套几何重算。
- [x] 肖像右键菜单已接入“从者库与设置 / 隐藏 / 退出”；透明区由 `WM_NCHITTEST`
      返回 `HTTRANSPARENT`，不会打开菜单。
- [ ] `PortraitController` 的 `_scale`/`_dpi` 在 `Task.Run` 后台线程被读取，UI 可同时改
      `SetScale`——存在轻微竞态，冲突时以最后一次发布为准；当前可接受但需备注。
- [ ] `PointerGestureRecognizer` 的拖动用 WPF `DragMove()`，拖拽期间阻塞消息循环；Phase 1
      可接受，实机需留意拖拽流畅度。
- [x] `TrayService` 通过 WPF `OnStartup` 的生产 DI 图首次解析，在 UI 线程创建。
- [ ] 附件：`UseWindowsForms` 已通过 `<Using Remove>` 移除全局 Imports（避免 Application/Point
      歧义），`TrayService` 局部引入；需确认无其他 WPF/WinForms 命名冲突残留。
- [ ] **新增发现（重要）**：`AttachedPanelViewModel` / `AttachedPanelView` 虽有组件测试，仍未挂入
      `PortraitWindow`，Compact / Dialogue / Todo 也没有生产入口。因此面板相关 Phase 1 验收不能
      仅凭现有测试转绿，必须在发布前新增一项生产接线任务并进行工作区占用实测。

### A3 安全核对点（内审已完成，结论：可接受）

- `FgoPetPackInstaller`：ZipSlip/绝对路径/盘符/重复路径/越权后缀/大小限额/截断 均已测；
  链接条目一律落盘为普通文件（不创建 reparse point）。
- `AppearanceValidator` / `BitmapAssetLoader`：路径越出根、缺失、哈希、解码失败、
  不可见 Alpha、图层越界均已测。
- `ResourceDiagnostics`：只输出 id/版本/错误码/相对路径；凭据与绝对路径会被 REDACTED 化。
- 包内自报 `verified` 不影响信任判断（未实现来源签名）。

---

## 3. B 实机审核清单（真机，当前全部未执行）

所需环境：Windows 11 @ 200%（设备默认），另备 150%。每一项需 PASS/FAIL + 证据
（截图文件名或日志引用）。填 `docs/testing/phase1-windows-matrix.md`。

- [ ] 无包启动：托盘 + 从者库安装引导，无画像窗口、进程不报错
- [ ] 安装本地 Mash `.fgopetpack` → 完全离线显示桌宠（前提：A2 生产组合已接线）
- [ ] 透明像素穿透 + 人物/面板可点、可拖、可右键
- [ ] 拖拽后重启恢复窗口位置（同显示器 / 混合 DPI）
- [ ] 三种比例 0.50 / 0.60 / 0.75 无线缝
- [ ] 28 表情循环：身体稳定、外尺寸/锚点不变
- [ ] 200% 下表情接缝不可见；150% 最终组合复核
- [ ] 托盘：隐藏/恢复、打开角色包目录、退出
- [ ] 单实例：第二实例激活第一实例，`.fgopetpack` 路径转发
- [ ] 面板展开 ≤ 60% 工作区；收起后不拦截空窗口
- [ ] 包失败恢复：损坏包不影响当前画像
- [ ] 诊断：稳定错误码 + 相对路径，不含绝对路径
- [ ] 压力：28 表情 ×10、3 外观反复切换、面板反复展开/收起（可选 soak）
- [ ] 混合 DPI 双显示器（如可得）：跨屏拖动、显示器断开后窗口仍可见

---

## 4. 已完成证据（供审核对照）

- 测试：Core 56 / Infra 64 / App 72 / Windows 8 = **200 通过、0 失败**。
- Release 构建 `-warnaserror`：0 警告 / 0 错误。
- `scripts/test-phase1.ps1` 门禁已跑：Build→单元→Windows 集成→无 SkiaSharp→手动矩阵提示。
- `--smoke-test`：从正式应用入口执行，退出码 0，输出 no-pack / 不建窗口 / tray+库确认；
  应用壳使用延迟解析，smoke 不创建托盘或 WPF 窗口。
- 单实例管道转发、窗口呈现、托盘创建销毁：Windows 集成测试已过。
- 新增：生产容器解析、启动三路径、非等比 DPI、Win32 主屏枚举、coordinator 位置恢复测试已过。

---

## 5. 下一步决策候选

1. **先接线附着 UI**：把 Compact / Dialogue / Todo 挂入 `PortraitWindow`，统一画像与面板
   窗口边界、命中区域和锚点，再补生产集成测试；
2. 进入 P1.4（Python `.fgopetpack` SDK），产出真实玛修包作为端到端和实机审核输入；
3. 使用真实包执行 B 矩阵，补齐 200% / 150% / 混合 DPI 证据后再判定 Phase 1 可发布。

当前建议顺序为 1 → 2 → 3。生产组合缺口已关闭，但 Phase 1 仍不可标记为可发布。
