# 隔夜缓存命中评估与 Harness / Reasonix 优化方案

日期：2026-09-17。状态：诊断和设计完成，优化项待实施。此次只读分析运行数据，没有修改产品代码、运行配置或数据库，没有发起模型测试请求。沿用 ADR-084 和现有 Composition 所有者。

## 1. 结论

昨晚至取样时，本地记录总体 **96.0359%**；DeepSeek Flash **96.5512%**，GLM-5.3-Flash **93.6809%**。主要集中项是工具新增、压缩后的历史替换、后台短调用。现有排序修复有效，但不能阻止新增工具改变请求前缀。

后半夜16次摘要生成已达到 **99.2946%**，证明现有 warm-prefix 路径在这些请求上有效；20次压缩后首轮只有 **18.4035%**。下一步重点应从重复实现“摘要复用”转向压缩后的重建成本与触发决策。

不能宣布达到99%，不能承诺相关 miss 全部可消除，也不能将参考项目的结构设计当作其真实账单命中率证据。

## 2. 窗口、公式和台账边界

- 北京时间 **2026-09-16 18:00:00 至 2026-09-17 08:29:19**；UTC `[2026-09-16 10:00:00, 2026-09-17 00:29:19)`。这是调查开始时冻结的请求时间窗口，不是报告写完时刻。
- `命中率 = sum(cache_hit_tokens) / sum(cache_hit_tokens + cache_miss_tokens)`；不平均请求百分比，不含输出token。
- `pudding_platform.db / llm_gateway_usage_events`：本地记录的 Provider usage 总量，2550条，全部满足 input=hit+miss；不等同于已经完全对账的服务商账单。
- `TokenUsageEvents`：2551条，仅补充目的、角色和前缀归因。2550条 Gateway 均能按 session/provider/model/input/output/hit/miss 七元组唯一关联。此为内容关联，不是共享requestId的严格审计连接；额外归因行不计入总量。
- `context_layer_metric_events`：分层估算；`pudding_memory.db / CompositionSnapshots`：工具序列/hash变化证据。二者不与usage相加。
- SQLite用`mode=ro`、`query_only=on`读取。两个台账分开提取，不是跨库原子快照，迟到usage仍可能影响以后重算。
- 中间脚本和原始提取位于忽略目录`temp/cache-0917-*`；随报告仅保留不含密钥、正文和会话身份的聚合JSON。

### 2.1 模型与角色

|模型|调用数|输入token|命中token|未命中token|命中率|
|---|---:|---:|---:|---:|---:|
|deepseek-flash|1,917|264,653,258|255,526,016|9,127,242|96.5512%|
|glm-5.3-flash|633|57,918,532|54,258,624|3,659,908|93.6809%|
|合计|2,550|322,571,790|309,784,640|12,787,150|96.0359%|

|模型 / 调用角色|调用数|输入token|未命中token|命中率|
|---|---:|---:|---:|---:|
|deepseek-flash / main|1,023|185,242,786|6,524,834|96.4777%|
|glm-5.3-flash / subagent|633|57,918,532|3,659,908|93.6809%|
|deepseek-flash / subagent|790|78,470,368|1,674,848|97.8656%|
|deepseek-flash / background|104|940,104|927,560|1.3343%|

main包含摘要调用；GLM在此窗口全部为子代理。DeepSeek普通`agent_llm`1793次，97.0944%；摘要20次，86.4974%；后台104次，1.3343%。后台输入只占DeepSeek的0.355%，却贡献其10.163%的miss，适合单独治理，但不是整体最大的损失源。

### 2.2 小时走势（北京时间；按token加权）

|小时开始|DeepSeek命中率 / 调用数|GLM命中率 / 调用数|
|---|---:|---:|
|2026-09-16 18:00|95.77% / 120|95.30% / 206|
|2026-09-16 19:00|96.50% / 190|94.72% / 166|
|2026-09-16 20:00|90.47% / 89|92.37% / 137|
|2026-09-16 21:00|98.05% / 182|无调用|
|2026-09-16 22:00|97.10% / 145|86.05% / 32|
|2026-09-16 23:00|97.07% / 534|91.53% / 64|
|2026-09-17 00:00|96.22% / 66|93.36% / 28|
|2026-09-17 01:00|96.53% / 78|无调用|
|2026-09-17 02:00|97.18% / 74|无调用|
|2026-09-17 03:00|98.27% / 82|无调用|
|2026-09-17 04:00|97.12% / 94|无调用|
|2026-09-17 05:00|95.28% / 110|无调用|
|2026-09-17 06:00|99.34% / 20|无调用|
|2026-09-17 07:00|93.97% / 35|无调用|
|2026-09-17 08:00|95.48% / 98|无调用|

08时仅到08:29:19。06时DeepSeek超过99%但只有20次请求；不能用这一个小时替代全窗口或7日验收。与9月16日旧报告的00:00–19:22窗口有重叠且任务构成不同，不构成优化前后的受控对比。

### 2.3 官方账单核对

来自本机`E:/github/deepseek/usage_data_2026-09-11_2026-09-17/amount-2026-09-11_2026-09-17.csv`（08:14:34导出）；全部为DeepSeek Flash。日聚合不能截取昨晚18时以后的部分。

|账单日期|请求数|命中token|未命中token|命中率|
|---|---:|---:|---:|---:|
|2026-09-11|292|27,767,424|1,396,172|95.2126%|
|2026-09-12|2,908|235,534,336|10,950,286|95.5574%|
|2026-09-13|1,181|80,394,112|6,032,578|93.0200%|
|2026-09-14|545|45,313,408|1,871,210|96.0343%|
|2026-09-15|1,651|198,220,160|8,687,181|95.8014%|
|2026-09-16|1,813|212,122,368|9,232,435|95.8291%|
|2026-09-17（未完整）|595|101,787,520|3,561,350|96.6195%|

9月16日本地DS为1808次、hit212,228,224、miss8,593,852；官方1813次、hit212,122,368、miss9,232,435。差异为官方多5次、miss多638,583，但hit反而少105,856，不能解释为简单漏记5次。

9月17日本地截止08:14:34为605次、hit103,603,968、miss3,517,836（96.7160%）；官方导出为595次、hit101,787,520、miss3,561,350（96.6195%）。导出时间不是服务商已结算请求的准确水位，账户范围、计时边界、失败/重试usage和迟到入账尚未逐请求核齐。正式费用以官方账单为准；本地数据用于定位。

## 3. 归因与证据强度

### 3.1 变化标签对应的请求

这些标签分组互斥，但不是所有miss的因果证明。未标记不表示最终请求字节不变；一个标签也不保证没有其他同时变化。

|模型 / 标签|请求数|相关miss|这些请求命中率|
|---|---:|---:|---:|
|deepseek-flash / (none)|1842|3,789,376|98.5074%|
|deepseek-flash / compaction_checkpoint|20|2,387,564|18.4034%|
|glm-5.3-flash / tool_spec_changed|31|2,258,786|3.0944%|
|glm-5.3-flash / (none)|602|1,401,122|97.4794%|
|deepseek-flash / tool_spec_changed|23|1,232,482|24.6326%|
|deepseek-flash / compaction_replay|20|684,483|86.4973%|
|deepseek-flash / system_prompt_changed|3|572,260|2.0592%|
|deepseek-flash / session_rehydrated|9|461,077|16.7739%|

### 3.2 工具新增：当前最明确的改进项

54次`tool_spec_changed`在请求开始±1秒内匹配相邻Composition：全部是追加工具，未观察到旧工具删除或相对顺序变化。相关miss共 **3,491,268**（全部miss的27.30%）；其中GLM2,258,786，占其miss的61.72%。

高频新增：file_write23次、file_read22、file_search21、file_patch19、search_grep17、image_reader16、terminal_read12、terminal_start11、terminal_wait11。说明常用文件/终端能力仍在任务过程中分批曝光。工具名称序列不能证明每个schema字段不变，但“只是重排导致”已不符合本窗口证据。

- 9月16日20:18:14，GLM子会话`…sub-bc03662c`在round索引96（第97次）新增工具，input151,163、hit6,016、miss145,147。
- 18:52:48，GLM子会话`…sub-20a2a204`第79次请求新增工具，input143,575全部未命中。
- 9月17日00:38:34，dsh主会话新增工具，input231,509、hit22,400、miss209,109。

`ToolExposurePlanner`与`BuildFrozenToolManifestCore`应在首轮选择受权限约束的必要能力包，避免将常用工具拆成多次发现。代价是初始输入变大，必须比较整任务成本，不能全量预加载全部工具。

### 3.3 压缩：摘要复用已改善，历史替换仍昂贵

|阶段|次数|输入token|miss|命中率|
|---|---:|---:|---:|---:|
|18:02–20:27前4次摘要|4|1,166,262|656,950|43.6705%|
|21:56–07:24后16次摘要|16|3,902,989|27,533|99.2946%|
|20次checkpoint后首轮|20|2,926,060|2,387,564|18.4035%|

Pudding已在`WarmPrefixCompaction.TryCreatePlan`重放原出站messages/tools，并追加固定摘要指令；符合Harness主要机制。前4次低命中仍需要最终编码差异证据，不能仅凭源码宣称这些调用也精确复用了实际wire。

历史替换会生成新前缀，后续首轮冷缓存有合理成本。优化方向是减少无收益或过密的checkpoint、保留工具调用/结果完整单元、一次回收足够空间，并对比“生成摘要+重建首轮+后续工作”的总成本。不能为了命中率取消必要压缩、保留重复历史，或不断重写旧工具结果。

当前软压缩使用`ResolveContextSoftCompactionRatios`和`LlmRequestBudgetGuard`的最终输入估算；配置来源是runtime.execution的SubAgents段，缺省trigger0.8/target0.5。此次未取得每次触发时的有效配置、估算/Provider输入差及触发原因完整记录，因此**不直接建议调高阈值**。独立`ContextCompactionService`的旧diagnostics JSONL最后记录在9月14日，不能拿它解释这些warm-prefix调用。

另有独立摘要作业`min(资源池MaxOutputTokens,8192)`预算，见AgentExecutionService；这是实际存在的辅助任务预算，不能误报为所有请求已无代码级输出预算。本轮不更改它；后续应与资源池配置权威来源单独核定，不能靠缩减主模型输出上限改善缓存。

### 3.4 system标签的观测缺口

3次system变化相关miss **572,260**：20:02:53为273,318；05:09:01为188,177；05:28:29为110,765。三次均在随后一轮恢复高命中。

逐一比较相邻layer hash，只观察到`L9-CURRENT`变化；已记录的静态/偏好/运行层hash相同。Composition的system hash涵盖全部System消息，分层流水并未覆盖最终消息序列所有加工，所以不能把这3次归因于L9正文、用户偏好或模板的具体变更。昨日09:34偏好变化的结论不能套用到今日。

最终请求manifest应记录每条有序消息的role、来源、hash、长度、工具有序hash及首个差异位置；动态数据更新与真正系统策略更新要分开。仅hash规范化后的schema也不等于最终发送序列。

### 3.5 后台与重启

后台104次，输入940,104，miss927,560：memory-write51次miss540,335；periodic53次miss387,225。`periodic:skill.extract_patterns`43次377,412输入全部miss。这里应检查是否反复扫描已处理内容、是否有不必要的不同首部，而非要求短请求天然达到99%。优先按版本增量提取、幂等去重、有界批处理；关键记忆写回仍及时完成。

重启前07:50:59主会话input149,230、hit148,992；08:07:38恢复首轮input79,265、hit19,968；08:07:48工具变化input85,533、hit31,872；08:08:04已input87,063、hit86,400（99.24%）。恢复请求内容规模发生变化，不能把miss全归因于服务商TTL或Core进程重启。重启后至取样98次合计95.4846%，也不足以证明长期恢复质量。

本窗口9次session_rehydrated相关miss461,077。需持久化Composition版本/工具曝光顺序/动态快照，确保恢复得到同一可见历史；必要的配置更新仍应明确形成新版本。

### 3.6 分层开销：工具定义仍然很重

层遥测覆盖2447个请求口径，非完整2550条Gateway，token是本地估算，不是服务商逐层计费或逐层命中结果。

|层|观测次数|累计估算token|平均每次token|
|---|---:|---:|---:|
|TOOL-DEFINITIONS|2447|62,480,057|25,533|
|SKILL-CATALOG|1606|9,406,959|5,857|
|L0-STATIC|2447|4,927,969|2,014|
|HISTORICAL-CONTEXT|1010|4,578,330|4,533|
|USER-PREFERENCES|2447|3,787,956|1,548|
|L2-SKILLS|2447|2,469,023|1,009|

这些行不覆盖全部重放历史，不能相加当作完整输入。工具schema已很大，能力包需要限定任务范围；技能目录应保持稳定紧凑、正文按需读取。除缓存外，也应按每个成功任务的总输入衡量改进，避免预加载更多工具后命中率上升但账单反而变差。

## 4. 两个参考项目：实际代码与采用边界

本机clone只读核对：Harness `0d1f50007f9bca3f52b06e1c3074fa14d5fb0720`；Reasonix `1f598bc9d616248feb6f916e936a9ff6dd61ec82`。以下为代码机制比较，不声称其生产命中率优于Pudding。

|参考机制|源码证据|对Pudding的取舍|
|---|---|---|
|Harness确定性section和tool顺序|`packages/core/system-prompt/src/index.ts`；`tests/tool-order.spec.ts`|保留现有Composition权威与追加序；排序稳定已不是当前主要缺口。不能照搬字母排序后在中间插入新工具。|
|Harness动态上下文只在变化时追加持久User快照，支持清空标记|`packages/core/agent-loop/src/runtime-context.ts`|借鉴快照版本、去重、明确撤销；偏好/名册等数据不重写消息0，真正安全策略不得降级为普通用户数据。|
|Harness SystemPromptProjection能力门控|同上；`tests/system-prompt-projection.spec.ts`|只有路由支持历史system更新时才追加，否则显式换epoch；不能假设OpenAI、Anthropic、Responses行为相同。|
|Harness摘要重放messages/tools并追加指令，拒绝max-tokens摘要|`packages/compaction/compaction-basic/src/summarizer.ts`|Pudding已具备主要机制；补最终wire断言和截断/回滚验收，优先解决checkpoint后成本。|
|Reasonix provider可见工具集与执行注册表分离，use_capability分发|`internal/tool/tool.go`；`internal/agent/finalization.go`；`usecapability.go`；`internal/boot/use_capability_surface_test.go`|可作为长尾工具入口实验；首选稳定常用能力包。代理分发必须通过既有权限、参数校验、取消和审计，不能绕过ToolRunner。它会改变模型调用协议，需独立评估，不直接替换全部工具。|
|Reasonix有版本的Pinned Context差量、撤销和可见文本hash|`internal/agent/pinned_context_revision.go`|借鉴会话持久revision/完整manifest/变更正文；明确正文为用户上下文而非系统指令。|
|Reasonix shape变化记录多原因、只记模型可见改写|`internal/agent/cache_shape.go`|扩展现有manifest，分开permissionEpoch与exposureHash；当前DirectLlmClient用toolIds算permissionFingerprint不能代表实际权限版本。|
|Reasonix摘要输入复用、投影与canonical分离、有界工具内容与原文按需读取|`internal/agent/compact_fold_input.go`；`docs/research/cache-aware-compaction-design.md`|Pudding已有有界工具结果策略；审计首次写入后的可见文本是否稳定和原文是否可检索，不再搭一套旁路持久化。|

固定版本源码链接：[Harness动态上下文投影](https://github.com/deepseek-ai/deepseek-harness/blob/0d1f50007f9bca3f52b06e1c3074fa14d5fb0720/packages/core/agent-loop/src/runtime-context.ts)、[Harness摘要重放](https://github.com/deepseek-ai/deepseek-harness/blob/0d1f50007f9bca3f52b06e1c3074fa14d5fb0720/packages/compaction/compaction-basic/src/summarizer.ts)、[Reasonix工具注册表](https://github.com/esengine/deepseek-reasonix/blob/1f598bc9d616248feb6f916e936a9ff6dd61ec82/internal/tool/tool.go)、[Reasonix版本上下文](https://github.com/esengine/deepseek-reasonix/blob/1f598bc9d616248feb6f916e936a9ff6dd61ec82/internal/agent/pinned_context_revision.go)、[Reasonix请求形状](https://github.com/esengine/deepseek-reasonix/blob/1f598bc9d616248feb6f916e936a9ff6dd61ec82/internal/agent/cache_shape.go)。

不照搬参考项目的默认上下文长度、max_output_tokens、省略参数语义、压缩阈值或TTL。能力上限以当前资源池与服务商契约为准。

DeepSeek当前官方文档说明：缓存是best-effort，SWA下缓存前缀按完整单元持久化；两次请求有共同前缀不保证第二次立即命中，首次检测后的后续请求才可能复用。缓存构建需要时间并会淘汰，不能把“稳定客户端hash”等同于命中保证，也不能假设固定TTL。[官方KV Cache说明](https://api-docs.deepseek.com/guides/kv_cache/)

## 5. 文件级实施方案与验收

### P0-A：先补最终请求身份与差异证据

- 所有者：Runtime的Composition/DirectLlmClient，加Core网关实际编码边界；不新建PromptRegistry。
- 文件：`CompositionSnapshot.cs`、`DirectLlmClient.cs`、`OpenAiLlmGateway.cs`、`AnthropicMessagesLlmGateway.cs`、`ResponsesLlmGateway.cs`及usage recorder。
- 将invocationId/attemptId/Provider requestId（若有）、route/model/build/config版本贯穿Gateway与归因；每attempt分别记成功、失败、重试usage。未知usage显式unknown，不填0。
- 编码后、发送前生成有序消息/工具manifest及首差位置；仅存hash、计数、引用，避免落盘完整prompt/秘密。真正权限版本与曝光定义身份分离。
- 验收：同请求两个台账严格对账；模型可见内容不变时最终manifest相同；改消息role、工具schema、选项或顺序均能定位；重试不漏记、不重复。解释三次system变化与前四次低命中摘要。

### P0-B：常用工具一次性选包，工作段内稳定

- 文件：`Services/Tools/ToolExposurePlanner.cs`、`AgentExecutionService.BuildFrozenToolManifestCore`、现有Composition持久化与恢复路径。
- 首轮按任务类型与授权选必要文件/代码/终端/Git包，终端start/read/wait/input/cancel按实际任务需要整组选取；长尾仍按需发现。不允许为了固定包暴露未授权能力。
- 在模型轮边界提交包版本，发现更新合并一次；需要新能力时仍立即可用，撤权立即生效。恢复沿用有序manifest，不再次经历多段冷启动。
- 可选实验：Reasonix式稳定长尾分发入口，复用ToolRunner/权限/校验/审计。因为改变调用协议，不计入“原工具调用不变”基线的收益，单独设置语义与成功率验收。
- 验收：重放本次54个工具变化场景，任务和实际执行能力不变；比较总输入、miss、调用数、输出、耗时和成功率。收益不能用3,491,268直接当承诺；首轮包开销必须扣除。

### P1-C：版本化动态上下文与精确恢复

- 文件：`ContextPipelineOrchestrator.cs`、`ContextPipelineLayers.cs`、`UserPreferenceService`、AgentExecution streaming/buffered及canonical历史投影。
- 工作段固定稳定system；偏好/名册/环境状态等普通数据更新以带revision、替代关系和删除标记的尾部快照承载。相同可见内容不重复注入。
- 新会话/压缩后只补一次最新完整快照；重启恢复读取持久revision。真正策略改变由provider能力决定system更新或新epoch，不能延迟权限撤销。
- 验收：更新/删除能被模型正确使用，重启/压缩不遗失、不过期、不重复；未变化请求的前缀字节保持一致。

### P1-D：按完整工作段成本做压缩决策

- 文件：`WarmPrefixCompaction.cs`、`AgentExecutionService.TryWarmPrefixCompactionAsync`、`LlmRequestBudgetGuard.cs`、`ContextUsageSnapshotStore`及canonical coverage路径。
- 先记录trigger/target、资源池context/maxInput/maxOutput、估算输入与真实usage、summary成功/截断、回收量、距上次checkpoint新增量，区分manual/soft/hard overflow。
- 保留同messages/tools的摘要复用；采用有收益判定和滞回，保护最近完整tool-call/result单元。预算安全边界优先；canonical完整历史与可重建的模型可见投影分开。
- 大工具结果首次产生时定型有界预览和原文引用；之后不得随轮次重写旧预览。只有明确的新checkpoint可重建投影，原文可按需精确回查。
- 验收包含：空/截断摘要不得提交；覆盖清单完整；任务恢复正确；比较摘要+重建+后续完整工作段成本。不固定照搬Harness的0.8/0.16或Reasonix默认值，不缩小主模型输出能力追求比率。

### P1-E：后台增量与幂等

- 文件：后台memory-write和skill.extract_patterns调用链、现有ToolResultContextPolicy与任务版本记录。
- 稳定任务指令置前，变化数据置后；按已处理版本增量读取，相同输入与版本去重，确定性判断无变化时不调用LLM。必要写回不拖延、不删减任务能力。
- 验收：同版本不重复提取，更新/删除正确反映；比较每个有效产物的输入、miss和成本，而非仅看短请求命中百分比。

## 6. 目标是否可达，以及如何验收

在输入总量不变的假设下，DeepSeek要到99%还需减少约 **6,480,709 miss（当前miss的71.00%）**，GLM需减少 **3,080,723（84.17%）**。这是数学差距，不是已证明可回收的收益；若输入变少，分母也会变化。

当前54次工具变化、20次checkpoint后请求、3次system变化、9次恢复及后台请求值得优先处理，但还存在必要新输入、冷启动、其他历史加工和服务商淘汰。不能把所有“无标签miss”叫浪费。

1. 先用固定任务/工具执行轨迹做离线请求形状和语义回归；真实模型小样本同时比较成功率、总输入/miss/输出/金额/P95延迟。不同方案不添加无价值预热或padding。
2. 部署后确认loadedBuildId与配置版本，正式指标总体及每个主力计量模型、每个完整自然日及7日加权均>99%，冷启动/后台/恢复全部纳入；并且任务成本和质量没有退化。
3. 核对官方账单和本地attempt台账。仅source测试通过、某小时99%或warm-only99%均不构成验收。
4. 若不可避免的新输入已超过输入总量1%，如实报告目标约束，优先降低每个任务的实际成本；不得靠增长历史分母抬高命中率。

关联：[ADR-084](../07架构/98ADR-084稳定请求前缀与缓存99验收ADR.md)、[9月16日诊断](缓存命中诊断与修复方案-2026-09-16.md)。
