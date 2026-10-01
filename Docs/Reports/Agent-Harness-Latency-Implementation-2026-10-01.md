# Pudding 耗时诊断的代码实施与验证记录

日期：2026-10-01。上游诊断：[Agent-Harness-Latency-Diagnosis-2026-10-01.md](Agent-Harness-Latency-Diagnosis-2026-10-01.md)（提交 `2f1e7f0`）。本文只记录**已落地的代码/配置变更、可复现证据与明确边界**；未做真实模型 A/B，未做部署后 smoke。

## 1. 落地顺序与提交

按诊断报告的建议顺序「准确计时 → 提示词与搜索 → 按测量优化上下文 → 补齐 UI 统计」执行，每步单独提交：

| 提交 | 范围 | 内容 |
|---|---|---|
| `823fef9` | P0 计时 | 真实供应商 TTFT 口径、上下文就绪与首正文时点、`done` 帧 `timings` |
| `3fa0800` | P1 搜索 | `code_symbol_search` 陈旧路径/未登记门禁；`search_grep` 结构化覆盖范围 |
| `2ac98ed` | P1 提示词 | 预设 `general-assistant.json`：按需恢复/检视/联网 + 「证据足够即停」 |
| `7601ac2` | P2 上下文 | 上下文阶段计时随结果与 `done` 帧下发 |
| `14e35fd` | P0 补测 | 落库耗时按轮汇总 + `STREAM_PERSIST` 埋点 |
| `2bf71ca` | 文档 | `How-Debuge.md` / `code_map.md` |
| `9cfd131` | UI 统计 | 真实服务态、缓存口径修复、本轮时间分解面板 |

## 2. P0：计时口径

**问题**：`FIRST_TOKEN` 在模型调用前记录（实为上下文就绪）；`stream_ttft_ms` 取首个 provider 读延迟，既不含响应头等待、也会被"有 chunk 但无模型内容"（如 role-only）提前触发。

**改动**
- `StreamDelta` 新增 `ProviderDispatchElapsedMs`（HTTP 派发起算）与 `ProviderHeadersMs`（派发→响应头）；新增 `ProviderStreamTiming` 承载两个时点。
- 三个网关（OpenAI Chat Completions / Responses / Anthropic Messages）在**真实 `SendAsync` 处**打点；Responses 的文件重建重发会重置派发时点。
- `DirectLlmClient.LlmStreamDiagnosticsAccumulator`：TTFT 改为**首个非空模型增量**的派发口径，新增 `stream_ttft_reasoning_ms` / `stream_ttft_content_ms` / `stream_ttft_tool_ms` / `stream_provider_headers_ms` / `stream_ttft_source`；**未采集输出空串而非 0**。
- `AgentExecutionService.Streaming.cs`：`FIRST_TOKEN` 正名 `CONTEXT_READY`；新增 `PROVIDER_TTFT`、`TURN_COMPLETE`、`STREAM_PERSIST`；`done` 帧新增 `timings`（历史加载/上下文装配/配置解析/工具构建/上下文就绪/模型派发/供应商 TTFT 及拆分/首个正文帧/模型耗时/工具耗时/调用次数/完成时点/上下文分阶段耗时）。
- 两套时钟**不混用**：本地时点相对**提交**，供应商 TTFT 相对**HTTP 派发**；`providerTtftSource` 显式标注是 `provider_dispatch` 还是本地回退 `local_model_call`。网页渲染仍用浏览器 `performance.now()`，与两侧时钟不得相减。

## 3. P1：提示词与搜索

**提示词**（仓库内预设 + 运行中实例，两边同口径）
- 预设 `Source/PuddingHost/default-data/agent-template-presets/general-assistant.json`：`personaPrompt` / `memoryPrompt` 不再要求"会话开场必检索历史/必读 INDEX.md"；`agentsPrompt` 的上下文恢复由「第零步必做」改为按需，新增「范围纪律（证据足够即停）」与按需检视工具/Skill、按需联网；**6 条仓库卫生子句逐字保留**（`AgentTemplateFileServiceTests` 断言）。
- 运行中实例（仓库外，改动前各留 `.bak-<ts>` 快照）：`D:\data\agents\default.global_general-assistant.6a8\manifest.json` 的 `systemPrompt`（去掉"先回忆/先检视工具/先检视 SKILL/代码任务前必须联网"，改为按需 + 证据足够即停）；同目录 `AGENTS.md`（`## 上下文恢复` 由"第零步必做"改为按需，新增 `## 范围纪律（证据足够即停）`）；`TOOLS.md`（cmd 说明限定到 `terminal_*`，`shell` 明确支持 `powershell`/`wsl`）。
- 未改动预算：`maxRounds=200` / `maxElapsedSeconds=86400` / `maxToolCallsTotal=400` 保持；报告建议的"定位任务软预算"依赖意图判定，仓库内**无意图分类器**，故不引入猜测式启发（只在提示词层用"证据足够即停"约束）。

**搜索**
> 口径对齐：并行线（`bf1c9e5`、`Docs/Features/Index-Retrieval-Known-Defects-2026-10-01.md`）已实测认定 **D2 的"陈旧 E: 命中"现象在当前 live 索引上已随四个陈旧项目注销而消失**（覆盖完整、路径正确、新鲜），但该文档同时登记"搜索入口仍未过滤"这一**代码洞仍开放**。本轮的 `code_symbol_search` 改动正是针对后者——**防御性加固**，不主张"当前 live 上复现过陈旧命中"。
- `code_symbol_search`：接入 `ICodeProjectRegistry`。显式项目未登记 ⇒ **fail-closed**（`not_registered`，与 `code_index_status` 同一句文案，单点定义）；命中校验「文件存在 + 落在其登记项目根目录内」，失效命中计入 `stale_skipped` 并从 `results` 剔除；新增 `include_stale`（带 `stale_reason`）供排查；全陈旧时返回可行动原因，**不得读成"符号不存在"**；输出新增 `searched_scope` / `registered_project_count` / `complete`。
- `search_grep`：非完整覆盖声明补齐 `searched_scope` / `complete=false` / `limit_reason`（稳定 token，如 `scan_budget`、`max_results`）；索引后端 summary 增加 `complete` / `limit_reason`。
- `ToolLoopInstructionBuilder`：由"优先索引"改为"**索引健康才走加速路径**；出现陈旧/未登记信号时回落实时搜索"，并说明 partial 空结果不是"不存在"的证据。
- 未做：索引重建/注册的 HTTP 入口（仓库内不存在，只有 agent 工具与 Host 侧托管服务）；报告中"自动检测索引不健康并改用实时扫描"仅落到提示词与工具返回值层，未做运行时自动路由。

## 4. P2：按测量优化上下文

- `ContextAssemblyResult` 新增 `StageDurationsMs`，由 `ContextPipelineOrchestrator` 既有 `MeasureAsync` 计时填充；随 `done` 帧 `timings.contextStagesMs` 下发，`CONTEXT_READY` 日志附最慢 5 层。**先让每层开销可见，再按测量决定是否裁剪。**
- 未引入启发式跳过召回：`SubconsciousRecallPipeline` 对首条消息本就跳过深召回，`memory_crop` 阶段在 `ContextPipelineOrchestrator` 中已是死代码（`cropBundles` 恒空）；因此"简单定位跳过召回"在本场景基本是空操作，盲目添加启发式只会带来"漏召回"风险。
- 落库成本已可测：`STREAM_PERSIST session=… appendCount=… appendTotalMs=…`（`Append` 在每个 `yield` 之前 await，若该值随轮次显著增长，才值得做有界批量写入）。

## 5. UI 统计

- **缓存率单位契约**：后端一律 0–1 比例（`CacheDiagnosticsService` / `TokenUsageNormalizer`）。前端不再用 `rate <= 1` 猜单位，改为无条件 `×100`，并标注口径（`本请求窗口(最近N条)` / `全会话`）与样本条数；窗口值不再静默覆盖全会话值。
- **真实服务态**（`serviceStatus.ts` 纯映射，全部由已有端点推导，未新增后端端点）：`contextService`←`context-health.state`；`index`←`GET /api/admin/index/status` + `deriveIndexHealth`；`backgroundMemory`←`GET /api/debug/subconscious/debug`（仅 `running`/`paused`）；`modelService`←`GET /api/llm/providers`（`isEnabled && hasApiKey`）。取值域补 `warning`/`unknown`；**采不到一律 `unknown`**，`disabled` 只在确证关闭时出现（先前是硬编码 `index:'disabled'`）。
- **本轮时间分解**：新增 `TurnTimingPanel`（独立文件，避开 chat 包预算），渲染 `turn.completed` 帧 `timings`：本轮耗时、模型耗时、工具耗时、Provider 首包（含计时基准）与推理/内容拆分、模型与工具调用次数、输入/输出 token；**未采集显示「未采集」，真实 0 才显示 0**。

## 6. 验证证据（本轮实测）

后端（均用 `--artifacts-path temp/build/latencyfix` 隔离构建）：
- `PuddingCoreTests`：**1032/1032 通过**（exit 0）。
- `PuddingRuntimeTests` 全量：**1904 通过 / 3 失败 / 1913 总计**。3 条失败均可归因，且与本次改动无关：
  1. `ArchitectureGuardTests.PuddingRuntime_MustNot_Reference_PuddingPlatform`：测试用 `AppContext.BaseDirectory` 上溯 5 级找 `Source/PuddingRuntime/PuddingRuntime.csproj`（`ArchitectureGuardTests.cs:15-18`），在 `--artifacts-path` 布局下上溯到 `temp/`，故失败；默认输出布局下成立。
  2. `ArchitectureGuardTests.PuddingRuntime_MustNot_Contain_UsingPuddingPlatform`：同上。
  3. `HostShellExecutor_WslMode_UsesWindowsWorkingDirectoryMapping`：本机 `wsl.exe` 无可用发行版（`wsl --status` exit 1），环境限制。
- `PuddingMemoryEngineTests`：286 通过 / 3 失败。3 条为**既有登记红**（`Docs/Reports/Regression-Baseline-2026-09-22.md` §三：`ContextPipeline_ShouldAssembleAll7Layers`、`EnvironmentLayer_ShouldBePresentInAssemblyResult`、`ContextPipeline_ShouldTriggerGentleCompaction`，层显示名与测试期望不一致），本轮计数与文档一致。
- 定向：搜索工具族（`SearchGrepToolTests` / `CodeSymbolSearch*` / `CodeIndexStatusRegistryGate`）79/79、`PuddingToolInfrastructureTests`+`CodeQueryServiceTests` 151/151、上下文与计时族 35/35、`AgentTemplateFileServiceTests` 7/7。

前端（`Source/PuddingPlatformAdmin`，pnpm）：
- `pnpm exec jest ...`（新增/改动套件）：44 通过 / 1 失败（45）。唯一失败是**既有语音族红**：HEAD 源码里 `IntentConsole.tsx` 只有 `开始语音输入`、无 `开始语音对话` 按钮（`git show HEAD:` 实测计数 0），而 HEAD 测试期望 `开始语音会话`——与 `TestScripts/known-red-dispositions.md` #6/#7/#9 同一归属问题。
- `pnpm exec tsc --noEmit`：6 条错误，**均在本次未改逻辑的既有代码**上：`chat/index.tsx:808` 的 `onEditAndRerun`（HEAD 同样存在该调用，已用 `git show HEAD:` 证实）、`executionFlowProjector.ts:682/685/686/688`（该文件本次零改动）。
- `node scripts/check-chat-bundle-budget.cjs`：`ok sync=… chat=348792 common=417160`（exit 0，上限 507,904 B；本次 +5,510 B，未削弱门禁）。
- 前端全量 `pnpm exec jest --runInBand`：**Test Suites 192 通过 / 2 失败；Tests 1591 通过 / 3 失败 / 1594 总计**。3 条失败与 `TestScripts/known-red-dispositions.md` 登记的语音族 #6/#7/#9 完全一致（`InputArea.test.tsx` ×2「voice mode … transcript / unavailable state」+ `IntentConsole.test.tsx` ×1 同族用例），无新增红。

## 7. 明确未做 / 不可主张

- **无真实模型 A/B**：报告 §7 要求的"同 repo commit、同 endpoint/模型/参数、每类 ≥10 次、冷/热分组、TTFT 分位数"未执行；完成时间是否接近 Harness **不可主张**。
- **未做部署后验证**：本轮改动需由外部控制器重启到新构建后，再在新会话执行功能 smoke（`ready-for-external-deploy` → `in-product-functional-complete` 两段式），本次只到"源码 + 自动测试"。
- **未改缓存机制**：DeepSeek 缓存默认开启且依赖已持久化前缀，现有 `PrefixCacheSnapshotBuilder` / Tail 追加 / 压缩策略保持不变；未观察到可证伪的前缀失效变化，不做投机性改动。
- **未做索引重建/注册入口**：仓库内没有对应 HTTP 路由；旧 `E:` 路由的清理已由运行期 `code_index_unregister_project(remove_index_data=true)` 完成（见 `Docs/Features/Index-Retrieval-Known-Defects-2026-10-01.md`），本轮只堵"陈旧命中被当权威"的代码洞（该文档 §"收窄后仍然成立的两条"已同步更新为已修）。
- **首包/渲染延迟**：`firstContentFrameMs` 是服务端首个正文帧；浏览器渲染延迟仍由既有 `chat.output.commit/paint` 本地埋点度量，本次未改动。

## 8. 回滚

- 代码：`git revert <sha>`（每个提交单一主题，可逐项回退）。
- 运行中 Agent 配置：`D:\data\agents\default.global_general-assistant.6a8\` 下的 `manifest.json.bak-<ts>`、`AGENTS.md.bak-<ts>`、`TOOLS.md.bak-<ts>`（本轮时间戳 `20261001220947`）可直接覆盖还原。
