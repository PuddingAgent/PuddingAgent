# Pudding Diagnostics Tools

本目录存放本地诊断工具，用于把前端性能采集、SQLite 结构化事件、JSONL 会话事件和反代日志对齐到同一条时间线。

可观测数据分三类：

- 故障还原 Trace：面向单次请求现场还原，回答“这一次为什么慢、为什么错”。
- 长期统计 Metrics：面向 SQL 聚合，回答“工具、缓存、LLM、会话整体有什么行为模式”。
- 用户感知 Progress：从同一套阶段模型派生，回答“用户等待时系统正在做什么”。

架构上，Trace 是原始证据，Metrics 是结构化事实，Insights 是基于 Metrics 的派生诊断。脚本和前端面板应优先读取结构化事实进行统计，只在需要还原现场时回溯 JSONL 或普通日志。

可观测处理分三阶段：

1. 数据采集：运行时在业务事件发生点写入稳定字段，例如耗时、计数、状态、分类、trace/session/workspace/tool/case 标识。
2. 数据清洗和处理：脚本或服务对原始事件做脱敏、截断、hash、失败分类、阈值判断和聚合。
3. 数据展示：管理后台、`Tools/Diagnostics` 和 `TestScripts` 输出可复现的摘要、趋势和现场证据链接。

新增埋点时优先补齐这些量化维度：

- 工具：耗时、输出行数、输出字符数、错误行数、错误字符数、参数长度、参数 hash、输出过长等级、失败分类。
- 审批：白名单命中、工单命中、隐式审批覆盖、隐式审批耗时、拒绝原因、兜底路径。
- Agent 过程：轮次、每轮耗时、工具调用数、失败恢复、重复调用、用户介入。
- 上下文和记忆：context token、召回次数、召回命中、召回内容使用情况、截断情况。
- 子代理：创建数、成功率、父子消息往返、贡献度。
- 基准测试：case id、难度、标签、耗时、轮数、token、工具调用、失败次数、审批路径、完成质量和摩擦点。

时间线统一使用 `stage` / `stage_order` 对齐阶段：`request`、`routing`、`dispatch`、`context`、`tool`、`llm_prepare`、`llm_provider`、`stream_persist`、`stream_deliver`、`ui_render`、`complete`、`error`。

工具默认通过 `pudding_paths.py` 路径 helper 解析数据目录，保持与后端 `PuddingDataPaths` 的目录语义一致：

- DataRoot: 优先使用 `PUDDING_DATA_ROOT`，否则使用仓库根目录下的 `data/` 作为开发兜底。
- SQLite: 优先从环境变量或本地开发启动器的 `ConnectionStrings:Default` / `ConnectionStrings:PlatformDb` 解析；未配置时读取 `Source/PuddingAgent/appsettings*.json`，再回退到 `PuddingDataPaths.DatabasesRoot/pudding_platform.db`，并兼容已有的 `DataRoot/pudding_platform.db`。
- 会话事件 JSONL: `PuddingDataPaths.DataRoot/jsonl/{sessionId}.jsonl`
- 后端诊断 JSONL: `PuddingDataPaths.DiagnosticsLogsRoot/session-timeline/YYYYMMDD/{sessionId}.jsonl`
- 反代诊断 JSONL: `PuddingDataPaths.DiagnosticsLogsRoot/proxy/YYYYMMDD.jsonl`
- 潜意识调试 JSONL: `PuddingDataPaths.DiagnosticsLogsRoot/subconscious/YYYY-MM-DD*.jsonl`

## 运行环境

使用项目根目录的 `.venv`：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_timeline.py --help
```

当前工具只使用 Python 标准库，`requirements.txt` 为空。

## 潜意识调试

潜意识运行时调试 API 独立归类在 `/api/debug/subconscious/*`，不挂在正常业务或 admin API 下。可通过配置 `Subconscious:DebugApiEnabled=false` 整体关闭。

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\subconscious_debug.py status
.\.venv\Scripts\python.exe Tools\Diagnostics\subconscious_debug.py stop --reason "manual debug"
.\.venv\Scripts\python.exe Tools\Diagnostics\subconscious_debug.py start --reason "resume"
.\.venv\Scripts\python.exe Tools\Diagnostics\subconscious_debug.py trigger --session-id <sessionId> --last-user-message "..." --last-assistant-reply "..." --wait
.\.venv\Scripts\python.exe Tools\Diagnostics\subconscious_debug.py result --job-id <jobId>
```

`trigger` 通过 `/api/debug/subconscious/trigger` 投递一条 `memory.consolidate_session` durable job，仍走真实 worker lease、F4 plan generation、validator 和 F5 dry-run 管道；`--wait` 会轮询 `/api/debug/subconscious/jobs/{jobId}/result` 直到拿到结构化 `SubconsciousJobResultEnvelope` 或超时。脚本默认登录本地 `admin` 测试账号，也可以用 `--token` 传入已有 Bearer token。调试 API 写入独立 JSONL 日志目录 `logs/diagnostics/subconscious/`，文件按日期和大小分片，不写入原始记忆内容或完整 LLM 输出。

## 查询 SQLite 时间线

按会话查询：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_timeline.py --session-id <sessionId>
```

按 trace 查询并输出 JSON：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_timeline.py --trace-id <traceId> --format json
```

## 查询统计指标

`query_metrics.py` 读取现有 SQLite 结构化表，面向后期遥测和统计分析。当前数据源包括：

- `telemetry_metric_events`: 会话、上下文、LLM、工具、Token、缓存等可聚合遥测事实，优先用于统计。
- `runtime_activity`: LLM 调用、工具调用、事件队列、Agent 执行等运行时活动。
- `session_event_log`: 会话流事件、delta、thinking、done 等事件事实。
- `TokenUsageEvents`: token、成本、prefix cache 命中与 churn 诊断。
- `context_layer_metric_events`: 每次 LLM 调用的上下文分层 token、层 hash、变化原因、估算缓存命中 token 和命中率。

工具使用只读查询，不会修改数据库。

工具调用统计：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py tool-usage --days 7
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py tool-usage --session-id <sessionId> --format json
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py tool-output --days 7 --min-chars 8192
```

LLM 延迟与 token 使用：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py llm-latency --days 1
```

Prefix cache 命中率：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py cache-hit-rate --days 30
```

上下文分层 token 与缓存命中率：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py context-layers --days 30
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py context-layers --session-id <sessionId> --format json
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py context-layers --provider-id deepseek --model-id deepseek-v4-flash
```

该命令用于分析缓存命中率改进空间，重点看：

- `tokenShare`: 某层在一段时间内占全部上下文 token 的比例。
- `medianCacheHitRate` / `avgCacheHitRate`: 该层估算缓存命中率的中位数和平均值。
- `changeRate` / `distinctHashes` / `changeReasons`: 该层内容易变性和主要变化原因。
- `p95Tokens`: 该层在高分位调用中的膨胀程度。

会话效率：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py session-efficiency --limit 20
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py session-efficiency --session-id <sessionId>
```

会话事件类型统计：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py event-stats --days 7
```

遥测事实汇总：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py telemetry-summary --days 7
.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py telemetry-summary --session-id <sessionId> --format json
```

新增后端埋点会写入 `telemetry_metric_events`，包括：

- `session.message.received` / `session.message.returned` / `session.message.failed`
- `session.background.completed` / `session.background.failed`
- `context.assembly`
- `llm.chat` / `llm.chat_stream`
- `tool.call` / `tool.execution`
- `token.usage`

`/api/stats/tokens/context-layers` 暴露与 `context-layers` 脚本同口径的后端聚合数据，可用于 `/admin/stats/tokens` 展示每层 token 占比、命中率中位数、变化率和变化原因分布。

默认不会保存上下文最终产物、工具原始参数和返回值。需要临时排查时，可设置统一环境变量 `PUDDING_DEBUG=1`（兼容 `PUDDING_TELEMETRY_DEBUG=1`），系统会在 `debug_json` 中保存脱敏/截断后的上下文与工具预览。`dev-up.py` 在本地开发环境会默认注入这些开关。

## 检查 SQLite 表结构

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\inspect_schema.py
```

## 导出诊断包

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\export_session_bundle.py --session-id <sessionId>
```

附加前端采集文件：

```powershell
.\.venv\Scripts\python.exe Tools\Diagnostics\export_session_bundle.py --session-id <sessionId> --frontend-perf temp\pudding-perf-xxx.json
```

导出结果默认写入 `temp/diagnostics/session-<sessionId>-<timestamp>.zip`。
