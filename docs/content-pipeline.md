# FGO Pet 内容管线使用说明

## 环境

本项目使用 Conda `base` 环境。Windows 命令直接调用以下解释器，避免 `conda run` 转发 Unicode CLI 输出时发生 GBK 编码错误：

```powershell
$FgoPython = 'D:\environments\anaconda\python.exe'
& $FgoPython -m pip install -e . --no-deps
& $FgoPython -m pytest -q
```

`pyproject.toml` 声明运行依赖和开发依赖。当前 base 环境需包含 Pydantic、HTTPX、Typer、pytest 和 respx。

## 数据边界

`--data-root` 必须位于 Git 仓库外。例如：

```text
D:\fgo_unpack\fgo_assets
```

管线在其中创建：

```text
story_cache/
├─ raw/       # 原始 CN/JP 剧情
├─ parsed/    # 无损结构化剧情
├─ catalog/   # 候选目录
└─ reports/   # 不含连续原文的审核报告
```

工具会拒绝把数据根目录设在仓库内部。不要将 `story_cache`、原始图片或发布受限资源复制进 Git。

## 剧情发现与抓取

搜索玛修候选剧情：

```powershell
& $FgoPython -m fgo_pet_content.cli story discover `
  --servant 800100 `
  --data-root D:\fgo_unpack\fgo_assets
```

抓取单个已审核脚本并生成脱敏报告：

```powershell
& $FgoPython -m fgo_pet_content.cli story fetch-script `
  --region CN `
  --script-id 0200040010 `
  --master-root D:\fgo_unpack\out\gamedata\unpack_master `
  --data-root D:\fgo_unpack\fgo_assets
```

默认策略是 CN 优先，CN 返回 404 时回退 JP。原始正文按 SHA-256 内容寻址保存；相同内容不会覆盖既有文件。

## 结构化剧情

解析器维护场景、角色槽位、当前发言槽位、形象 ID、表情 ID 和分支路径。未知演出命令保留名称、参数、原文和行号，但不会中断后续台词解析。

结构化文档仍含完整台词，因此继续保存在仓库外。`reports` 中只保存脚本 ID、来源、数量、未知命令统计和其他脱敏诊断。

## 证据提取

从一个结构化剧情文档提取候选证据：

```powershell
$env:FGO_LLM_API_KEY = '<用户自己的 API Key>'
& $FgoPython -m fgo_pet_content.cli evidence extract `
  --parsed-document D:\fgo_unpack\fgo_assets\story_cache\parsed\CN\0200040010\<hash>.json `
  --output D:\fgo_unpack\fgo_assets\story_cache\catalog\mash-evidence.jsonl `
  --base-url https://api.example.com/v1 `
  --model model-name
```

API 必须兼容 `/chat/completions` 和 JSON Schema structured output。每张候选卡必须引用当前窗口内的脚本、场景和台词序号；越界引用和直接复制台词的 claim 会被拒绝。

API Key 只从命令参数或 `FGO_LLM_API_KEY` 环境变量读取，不写入缓存、报告或 Git。

## 人工审核与人格编译

候选证据默认是 `pending`。批准一张证据：

```powershell
& $FgoPython -m fgo_pet_content.cli evidence review `
  --evidence-file D:\fgo_unpack\fgo_assets\story_cache\catalog\mash-evidence.jsonl `
  --evidence-id ev-example `
  --decision approved `
  --notes '已核对场景与上下文'
```

编译已审核证据：

```powershell
& $FgoPython -m fgo_pet_content.cli persona compile `
  --evidence-file D:\fgo_unpack\fgo_assets\story_cache\catalog\mash-evidence.jsonl `
  --output-dir D:\fgo_unpack\fgo_assets\packages\mash
```

只有 `approved` 状态会进入输出。Core、Style、Context/Flavor 分别写入常驻人格、语言风格和按需知识层；同一脚本的重复证据只计为一个独立来源。

## 清理与重建

- 删除单个哈希目录只会使该版本在下次执行时重新下载或解析。
- 删除 `parsed` 不会删除 `raw`，可以离线重新解析。
- 删除 LLM 候选卡不会影响原始正文和结构化剧情。
- 上游正文哈希变化时，新旧版本并存；审核人员根据差异报告决定是否重新生成证据。
- 不要直接修改 `raw` 文件；需要修正映射时修改配置或解析器并重建派生数据。
