# PuddingAgent 架构评审（2026-09-23）

> **来源与验证状态（provenance）—— 请先读这段再引用本文结论**
>
> - 生成者：子代理 `sub-151756b3`，模型 `fastrouter/gpt-6-astra`；任务书见同目录 `ArchReview-GPT6Astra-2026-09-23.taskbook.md`。
> - 成本：33 次请求 / **325 万 token**（gpt-6-astra 按 FastRouter 实测费率约 $5.8/百万 token ⇒ 本次约 **$15~19**）。平台自身 `费用` 字段显示 $0.0000，系计价未登记，不可信。
> - 过程：前 3 次同步派发失败（① 任务书写了不存在的 `Source/code_map.md`；② ③ `search_grep` 的 `query` 含未转义 `(` 正则报错且重试重放同一计划），第 4 次改异步 + 收紧工具规则后成功；运行期 1 次工具报错自行恢复。
> - **独立抽验（父代理复核，不采信自述）—— 4 处锚点逐字命中**：
>   1. `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs:196` = `Archive degraded — event dropped`
>   2. `Source/PuddingRuntime/Services/AgentExecution/ToolResultContextPolicy.cs:92` = `Failed to spill oversized tool result; fail open`
>   3. `Source/PuddingRuntime/Services/Skills/SkillEnforcerService.cs:144` = `... && !map.ContainsKey(kw)`
>   4. `AgentExecutionService.Buffered.cs` 与 `AgentExecutionService.Streaming.cs` 两个主循环文件确实存在
> - **未验证**：第 4 条（`PuddingApplicationHost.cs` 两套配置构建路径）未抽验；5 条均未用构建/测试/线上数据量化发生率（见文末「我没能验证的事项」）。
> - 处置：**第 2 条已修复并提交**（落盘失败不再放弃 8 KiB 边界）；第 1、3、4、5 条待裁决。`Source/PuddingRuntime/Services/AgentExecution/ToolResultContextPolicy.cs` 的注释、`Source/PuddingRuntime/code_map.md` 与 `Docs/Features/上下文自动压缩与Compact命令设计方案.md` §3.0.1 已同步更正（它们此前都把 fail-open 写成设计要求）。

范围：`E:\\github\\AgentNetworkPlan\\PuddingAgent`。本评审只读完成，未构建、未测试、未重启、未执行 Git 操作。排序按修复价值（故障半径、数据/状态不可恢复性、跨层返工成本、验收可判定性）排列。

## 1. 子代理运行归档把文件系统和数据库同时当作事实链，缺少可证明的一致性提交协议

**证据**

- `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs:14-18`：注释明确写着“运行归档以文件为主存储（run.json / events.jsonl / tools.jsonl / output.md），数据库仅做索引”。
- `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs:25-30`：同一个 Store 同时持有 `IDbContextFactory<PlatformDbContext>`、`IConversationEventStore` 和可选 `IExecutionJournal`，文件归档、数据库索引、对话事件投影由同一服务协调。
- `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs:184-197`：事件写入失败后记录 `Archive degraded — event dropped`，并把观测事件丢弃以保持运行继续。
- `Source/PuddingPlatform/Services/FileSubAgentRunStore.cs:1178-1183`、`:1245-1250`：数据库索引写入/更新失败被记录为 Warning；`:1278-1282` 还明确把数据库索引删除失败标成“non-fatal”。

**为什么是架构级**

这是 §2 的 1、3、4、6：同一运行事实分布在文件、数据库索引、Conversation Event 三条链上；部分链失败可以继续，因而系统没有单一权威状态，也没有统一的提交位点和一致性证明。

**实际影响**

文件已经写入而索引未写入、索引存在而事件投影缺失、或 JSONL 事件丢失时，详情 API、恢复扫描、会话投影可能给出不同的运行历史。故障后只能看到“degraded”告警，却不能由一个版本化游标判定哪些事实已提交、哪些事实可重放。这个问题会直接扩大子代理恢复、父 Turn 接续和诊断取证的返工面。

**建议方向**

需要 ADR，先定义一个权威运行事件日志和明确的提交顺序；数据库索引与会话投影改为可重建投影，带事件序号、幂等键、重放水位和一致性检查。文件归档可以保留为冷归档，但不得继续承担另一份可查询事实源。验收应注入每个写入阶段的崩溃/IO 故障，要求恢复后能由权威事件重建相同终态与水位，并能测出丢失事件数。

**代价与风险**

P0/P1 级持久化边界调整，涉及 Store、诊断 API、投影 worker、恢复逻辑和历史归档；需要迁移或一次性重建索引，回归风险高，可能需要停机窗口。

**优先级：P0**。它排第一，因为它同时影响运行状态、父子接续和事故取证，且当前设计允许“部分成功后继续”，导致错误无法由单一事实源收敛；其它候选通常只影响一条输入或一类执行路径。

**可判定的验收**

故障注入覆盖 file write、DB index write、conversation projection 三个阶段；恢复后权威事件重放产生唯一终态，索引/投影水位等于权威水位；同一 run 重放两次不产生重复事件；诊断 API 能明确列出未投影而非静默缺失。

## 2. 工具结果溢出持久化失败时 fail-open，绕过了上下文大小边界

**证据**

- `Source/PuddingRuntime/Services/AgentExecution/ToolResultContextPolicy.cs:14-18`：类的职责是“把 oversized tool results 排除出 model history”，并声明模型只收到 bounded preview。
- `Source/PuddingRuntime/Services/AgentExecution/ToolResultContextPolicy.cs:27-37`：超过 `8 * 1024` 字符时才进入 spill；路径解析、目录存在性和落盘都是边界成立的前提。
- `Source/PuddingRuntime/Services/AgentExecution/ToolResultContextPolicy.cs:87-94`：任意非取消异常都会记录 `Failed to spill oversized tool result; fail open`，随后 `return content`。
- `Source/PuddingRuntime/Services/AgentExecution/ToolResultContextPolicy.cs:101-106`：只有成功 spill 才构造 bounded preview；因此落盘失败路径直接把完整结果返回给模型。

**为什么是架构级**

这是 §2 的 3、6、8：上下文预算是执行稳定性的结构性边界，但边界存储失败没有失败出口，而是改变为无界输入；观测仅有日志，调用方无法区分“已受限”与“未受限”。

**实际影响**

工作区不可写、路径不一致、磁盘满或权限异常时，大工具输出会重新进入模型历史，触发 provider 截断、预算耗尽、缓存前缀漂移或隐藏失败。日志虽有 Warning，但上层仍以正常字符串继续，无法保证模型请求满足同一预算合同。

**建议方向**

需要 ADR 或至少冻结一个跨工具的 ResultContextContract：spill 成功才允许引用 artifact；失败应返回结构化、可观测的 `context_materialization_failed`，并提供有限错误摘要/重试或终止当前回合的选择。不要让异常路径改变预算语义。验收应对目录不可写、超时、磁盘满模拟，证明模型输入永远不超过合同上限且错误码可检索。

**代价与风险**

P0/P1，涉及 Runtime 调度、工具结果协议、前端/诊断对错误的呈现和可能的重试策略；改动中等，现有依赖“返回字符串”的调用点需要收敛。

**优先级：P0**。它是确定性的资源边界，却在最需要保护的异常路径上被主动取消，容易把单个工具输出升级成整轮或整会话失败。

**可判定的验收**

对 `WriteAllTextAsync`、manifest 写入和路径解析分别注入异常；每次模型可见内容都满足最大字符/Token 合同；调用结果包含稳定错误码、tool/session/call 身份和一次重试或终止决策；不得出现完整原文回流。

## 3. 技能关键词映射采用“先到先得”，同关键词冲突会静默丢失技能

**证据**

- `Source/PuddingRuntime/Services/Skills/SkillEnforcerService.cs:41-45`：服务以元数据关键词扫描用户消息，并把命中的技能正文注入上下文。
- `Source/PuddingRuntime/Services/Skills/SkillEnforcerService.cs:55-72`：命中判定使用 `messageLower.Contains(keyword.ToLowerInvariant())`，命中后只加入一个 `skillId` 集合。
- `Source/PuddingRuntime/Services/Skills/SkillEnforcerService.cs:135-146`：构建映射时使用 `if (... && !map.ContainsKey(kw)) { map[kw] = entry.SkillId; }`，同一关键词的后续技能被静默跳过。
- `Source/PuddingRuntime/Services/Skills/SkillEnforcerService.cs:151-166`：正文读取和遥测只遍历已经选中的 `matchedSkillIds`，被覆盖的技能没有“冲突未注入”终态记录。

**为什么是架构级**

这是 §2 的 1、3、7、8：技能发现规则和技能归属没有单一可审计的冲突合同；元数据注册顺序改变行为，失败表现为缺少上下文而不是明确错误。

**实际影响**

新增或重排技能索引后，同一用户请求可能注入不同技能。名称、标签或自动生成技能增多时，正确技能可能长期不被调用，遥测也无法区分“未命中”和“被冲突淘汰”，使 RSI 价值评估产生偏差。

**建议方向**

建立版本化的关键词冲突策略：唯一关键词启动期拒绝；或保留全部候选并按明确优先级、能力范围、技能版本裁决；无论哪种策略，都把冲突作为结构化诊断和遥测事实。需要 ADR，且策略必须与索引生成、注入和打分共用同一契约。

**代价与风险**

P1，中等改动，可能改变既有技能注入顺序和上下文长度；需要重新评估缓存、技能预算和历史指标，不需要数据库迁移。

**优先级：P1**。问题通常不直接使进程失败，但会长期造成静默能力缺失和错误的技能价值数据。

**可判定的验收**

构造两个技能共享每类元数据关键词，启动/刷新索引时产生可检索冲突记录；策略输出稳定且与注册顺序无关；每个候选都有 injected、rejected 或 conflict 终态；重复运行同一输入得到相同技能集合。

## 4. Host 启动配置存在两套构建路径，Bootstrap 与运行时配置的事实源边界不够明确

**证据**

- `Source/PuddingHost/Hosting/PuddingApplicationHost.cs:40-51`：先单独创建 `bootstrapConfiguration`，读取 `appsettings.json`、环境配置和环境变量，并将其传给 `PuddingLoggingBootstrapper.Configure`。
- `Source/PuddingHost/Hosting/PuddingApplicationHost.cs:54-66`：随后重新创建 `WebApplicationBuilder`，另行加入 DataRoot 下 `system.json`、环境变量和命令行参数。
- `Source/PuddingHost/Hosting/PuddingApplicationHost.cs:76-82`：URL 又直接读取 `ASPNETCORE_URLS`，与 builder 配置链并行存在。
- `Source/PuddingHost/Hosting/PuddingApplicationHost.cs:103-109`：CORS 直接从第二套 `builder.Configuration` 读取并自行提供默认值；同一启动过程存在按用途分裂的解析口径。

**为什么是架构级**

这是 §2 的 1、2、4、7：同一进程启动行为由 bootstrap 配置、builder 配置和直接环境变量读取共同决定；优先级与热加载边界依赖调用者记忆，且不同组件可能看到不同值。

**实际影响**

日志可能按第一套配置启动，而服务/URL/CORS 按第二套配置运行；配置变更或诊断时难以回答“哪个值最终生效”。当环境变量、system.json、命令行同时存在时，局部直接读取还可能绕开统一验证和来源记录。

**建议方向**

需要 ADR 或配置边界重构：定义 bootstrap-only 字段与 runtime 字段，统一构建一份带来源、优先级和校验结果的 effective configuration；URL、日志、CORS 和服务注册只消费投影，不再各自直接读取环境变量。验收要能输出脱敏的来源/优先级审计，并验证 hot reload 不改变启动期不可变字段。

**代价与风险**

P1，涉及 Host 初始化、日志早期启动、测试夹具和配置文档；需要谨慎处理启动顺序，可能需要重启才能验证，通常无需数据迁移。

**优先级：P1**。它会制造跨环境、低频但难定位的配置漂移，且启动期错误可能先于普通诊断链建立。

**可判定的验收**

为每个配置键定义唯一来源和优先级；同一输入组合下 bootstrap、builder、日志、URL、CORS 得到一致 effective 值；冲突配置在启动时明确失败或记录来源，不允许静默采用不同值。

## 5. Buffered 与 Streaming 各自维护一套 Agent Loop，跨模式语义容易漂移

**证据**

- `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs:1-8` 与 `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Streaming.cs:1-8` 分别承载非流式和 SSE 主循环；两者都在同一部分类中实现执行语义。
- `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs:248`、`:361`、`:862` 等位置存在独立异常边界；`Streaming.cs:179`、`:258`、`:340`、`:441`、`:912`、`:1186`、`:1767` 也存在另一套对应边界。
- `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs` 的入口注释（约 `:34-51`）和 `Streaming.cs` 的入口注释（约 `:38`）分别处理冻结视觉上下文、工具轮次和回复边界；共享的是部分辅助服务，不是一个统一的状态机。
- `Source/PuddingRuntime/code_map.md` 的 Agent Loop 条目分别将 `AgentExecutionService.Buffered.cs` 和 `AgentExecutionService.Streaming.cs` 描述为各自的“主循环”，说明模式差异位于编排层而非仅位于输出适配层。

**为什么是架构级**

这是 §2 的 2、4、7、8：同一执行协议在两个编排实现中重复维护，终止、预算、截断恢复、Steering、工具发现和异常终态需要两边同步；测试若只覆盖一种模式无法证明另一种模式满足同一合同。

**实际影响**

修复一个模式的终态、取消、预算或 late Steering 行为时，另一模式可能继续旧语义，导致用户看到“流式成功、缓冲失败”或相反。重复异常边界也会造成错误码、日志和事件顺序不一致，增加跨模式回归排查成本。

**建议方向**

需要 ADR：抽取 provider/transport 无关的单一 ExecutionStateMachine 或 round executor，Buffered 与 Streaming 只实现事件输出适配；统一状态转移、预算裁决、工具调用、取消和终态合同。验收用同一组 canonical 输入跑双模式，比较事件序列、终态、预算和错误码，仅允许传输层差异。

**代价与风险**

P1/P2，大范围重构，容易触及当前稳定执行链；需要先建立双模式黄金合同和迁移期对照测试，通常需要重启部署验证。

**优先级：P2**。它的风险很大但已有大量共享组件和测试基础，短期故障半径低于前四项；适合在执行合同进一步稳定后治理。

**可判定的验收**

同一 canonical turn 在 Buffered/Streaming 下产生等价的状态转移、工具调用顺序、预算扣减、取消结果和 terminal status；所有执行规则只在一个状态机/round executor 中定义，模式文件不再各自决定业务终态。

## 被排除项

1. **AddHostedService 具体类型注册缺陷**：任务书 §3 已明确修复并有生产组合根守护测试；没有发现新的变体证据，不重复报告。
2. **存储管理页容量口径**：这是已立卡的产品投影问题，任务书已给出完整事实和修复方向；本次没有新的跨层证据推翻既有判断。
3. **聊天正文水合/晚一条**：已知修复在真实数据上 0/99，根因仍未定位；把它写成架构结论会把未验证的诊断假设当成事实。
4. **system 日志删除**：已知运行数据事故，当前材料不能证明是代码架构中的单一责任边界缺陷；暂列诊断卡而非本次新发现。
5. **`.pudding` spill 目录本身**：任务书已明确它是运行期必需目录；问题在 spill 失败语义（第 2 条），不是目录存在。

## 我没能验证的事项

- 未运行构建、测试、重启或真实数据探针，无法量化五项问题的当前发生率、性能成本或线上频率。
- `code_symbol_search` 在当前会话未返回 `ToolResultContextPolicy` 符号，故相关证据采用分页源码和纯文本搜索；不影响已读到的行级事实。
- 全仓 `search_grep` 多次触及工具的枚举/结果上限，因此没有据此断言“其它位置不存在同类实现”；第 5 条的重复范围仅基于已读的两个主循环文件和代码地图。
- 未检查全部 ADR 的历史迁移状态，建议实施前先核对子代理归档 ADR 与当前 schema/恢复脚本是否已有部分收敛。
