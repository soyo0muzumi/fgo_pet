# Phase 4 closeout

日期：2026-09-01。

状态：Phase 4 本轮主要 Windows 环境验收通过（accepted）；代码、独立核心评审、机器门禁及用户桌面验收已关闭。改动仍在独立候选工作区，尚未合并 main 或发布。

## 候选与整合边界

- 候选分支：`phase4-closeout`，基于 main `c7dd87c`。
- 导入运行时修复分支已提交 `cff8250` 的有效代码、文档及插件定义，再叠加本轮修复。
- 原 main 的代码/测试修改已语义合并；原工作区未覆盖。
- 未继承修复分支中 `.superpowers/` 的 394 个生成/本机状态文件及 `.learnings/` 文件；构建物和配对状态不进入候选历史。
- 旧 Phase 4 计划的十任务流程不再作为重复执行要求；本轮保留实现、核心评审、回归和用户验收。

## 本轮已修复的问题

1. 极快完成事件被稍后收到的派发响应覆盖回 active。
2. 本地错误与远端开始事件竞争序号，持久化状态和当前任务 UI 不一致。
3. 重启恢复 helper 没有接入生产启动路径。
4. Relay 发出响应后删除队列，接收端尚未持久化时崩溃会丢任务/结果。
5. Relay 重启、重复派发、关闭/重启连接时回执与真实任务不一致。
6. Adapter journal 达到容量上限时不能丢单，也不能删除去重记录后重复执行任务；已有队列不得被新请求阻塞，后台 worker 失败应及时结束会话。
7. Relay ACK 历史不能随每条事件永久增长；结构化身份避免含 `/` 的标识碰撞或跨来源 ACK 误删。

网络超时或取消等待并不证明远端任务失败；修复不能通过伪造远端事件序号或盲目重执行来隐藏不确定状态。

## 验证记录

中间候选（尚不包含全部队列收尾）构建：

`dotnet build FgoPet.sln -c Release --no-restore -warnaserror -v quiet`

结果：0 warning / 0 error。插件清单验证通过。

完成投影第一轮的 17 项相关测试通过后，独立评审又发现生产恢复接线和错误事件序号问题，已退回修复；该 17 项结果不能作为最终门禁通过依据。

本轮补充的投影成功后 ACK 回归使用真实 SQLite；投影失败时队列保留，重试写入成功后才 ACK。定向测试 3/3 通过。Relay/Protocol 的定向回归分别 26/26 与 22/22 通过，包含超过 4096 条同任务事件、重启、重复 ACK 与身份碰撞。

候选 Python 全量回归：`D:/environments/anaconda/python.exe -m pytest -q`，105/105 通过（35.45 秒）。

首轮完整候选 Release 构建 0 warning / 0 error（10.46 秒）。全量测试发现并处理了测试门禁本身的问题：正常用户环境解决 DPAPI profile 限制；端到端用例补显式 ACK；导入 `.gitattributes` 后对既有 fixture checkout 应用 LF 规则，使文件字节与清单哈希一致（相关 14/14 已通过）；隔离 WPF 全局资源和 SQLite 测试池。

最终机器门禁通过：

- Release solution 构建（`--no-restore -warnaserror`）：0 warning / 0 error，最终复核 1.99 秒。
- .NET 9 个程序集合计 **697/697**，0 failed / 0 skipped。8 个程序集 628 项在全量运行中通过；Windows 测试宿主中止后，补齐 8 个 STA helper 的 Dispatcher shutdown，再单独完整重跑 Windows Release 69/69 通过。不是把中止运行视作通过。
- 对应 TRX 保存在 `artifacts/phase4-closeout-final-20260901/` 和 `artifacts/phase4-closeout-windows-final-20260901/`，早期失败与中止记录保留；构建物和测试结果不进入 Git。
- Python **105/105**，插件清单验证与最终 `git diff --check` 通过。
- 独立评审已复核 Relay 水印持久化与结构化键、Adapter journal→ACK→执行、最终事件重放、容量边界和 MCP fault 传播，未遗留本轮阻塞问题。

WPF 修复仅限测试生命周期与串行化，不改变产品窗口逻辑；SQLite 清池也限定到测试自己的数据库。主工作区原有未提交修改保持不动。

## 明确限制

- 派发结果未知时保留预留，不盲目重发；界面要求到 Codex 核实，尚无自动核对入口。
- Adapter journal 保留 512 条，Relay dispatch receipts 和 task watermarks 各保留最多 4096 条。容量耗尽安全拒绝，不自动删除去重信息；归档与用户恢复入口列入 Phase 5.1。此候选不承诺无限期无人维护运行。
- 旧版本已清除最终事件内容的 terminal journal 无法凭空恢复该事件；保持不重复执行。新版本会持久保存最终事件以便重放。
- 本轮未重新执行完整安装 smoke 或增加设备/DPI 支持证明；发布前仍须执行相应门禁。

## 用户验收

按用户要求，桌面操作由用户完成，本轮不继续自动操作窗口。
步骤与状态见 `../testing/phase4-windows-matrix.md`。

隔离任务名称为 `Phase4 completion acceptance`，仅要求回复固定文字，不操作文件。
测试根位于仓库外独立目录；不会修改正常用户数据、PATH 或正式插件配置。
早期测试来源首次批准来源未确认，不作为验收证据。最终候选使用全新的 `state-final` 和管道后缀，不复制旧 Relay 授权或 Adapter 凭据；首次批准由用户完成。

用户已通过截图完成 M1–M4：批准来源与目标核对、合成任务完成、退出重启后完成态保持且三端自动恢复在线、撤销后重启重新等待批准。最后一步没有重新批准或再次派发；未授权派发拒绝由自动测试验证。

只读 SQLite 核对确认测试 Todo 与唯一 execution 均为 `completed`，事件顺序为 started(1) → updated(2) → completed(3)，没有重复完成记录。完成时间为 2026-09-01 01:52:54（Asia/Shanghai），与界面一致。详见验收矩阵。本轮不读取或输出配对凭据。

## Phase 5 调整

见 `../superpowers/plans/2026-09-01-phase5-adjusted-direction.md`：
优先现有任务/配置体验与备份恢复，再完成角色包和安装候选，最后小范围试用及发行门禁。
应用感知、收藏及新 Agent 奖励不再作为首版必要条件。本轮未开始 Phase 5 功能实现。

本报告不构成公开发布授权；本机生成文件保留在各自测试目录，不进入发行物。
