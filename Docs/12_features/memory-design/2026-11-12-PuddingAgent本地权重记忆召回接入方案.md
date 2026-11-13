---
title: PuddingAgent 本地权重记忆召回接入方案
author: hyfree
date: 2026-11-12
last_reviewed: 2026-11-12
status: proposed
description: "以 save_memory 的已提交回执登记章节，在当前用户消息组装时按权重寻址，给出组件合同、发布步骤和验收用例。"
categories: [docs, features]
tags: [memory, recall, runtime, continual-learning]
related_docs: [Docs/12_features/2026-11-01-记忆存储与使用架构设计.md, Docs/10_conventions/组件化交付规程.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Memory/SaveMemoryTool.cs, Source/PuddingRuntime/Services/AgentLoop/IAgentLoopHook.cs, Source/PuddingRuntime/Services/ContextPipelineOrchestrator.cs, Source/PuddingRuntime/Services/AgentExecutionService.cs, Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Runtime.cs]
slug: features-puddingagent-weight-memory-integration
draft: false
---

# PuddingAgent 本地权重记忆召回接入方案

**首个接入版本建议先做影子运行。** 指定用户与 Agent 每次对话时，小模型都预测应想起的章节。收到可核对的用户反馈后，后台逐条更新候选权重，并和冻结权重配对计分。达到验收条件后，才把命中的章节内容放进主模型的本轮输入。这个顺序能尽早验证实时学习，同时量出一次错误联想会给主对话增加多少无关内容。

依据为独立仓库 `PuddingMemoryLab@3a86988` 的《PuddingAgent 权重记忆召回阶段总结》。925 条真实消息中筛出 31 条明确反馈，另留 19 条仅测试。更新后的首次不同说法共 17 次，冻结与更新都首选命中 8 次，更新新得 2 次、丢失 2 次，前三命中从 10 次变为 11 次。176 个章节共用 352 KiB 检索权重。

**2026-11-13 复核补记（同一产物 `.work/private/20261107-explicit-v1/explicit/aggregate.json`）**：上文那 17 次是子集 `first_next_per_update` 的结果。同文件的其余口径：

| 子集 | n | 冻结 top1/top3/exact | 更新 top1/top3/exact | gained / lost |
|---|---|---|---|---|
| `all` | 50 | 24 / 31 / 21 | 24 / 30 / 20 | 3 / 3 |
| `next_rewording` | 30 | 13 / 18 / 10 | 13 / 18 / 9 | 3 / 3 |
| `first_next_per_update` | 17 | 8 / 10 / — | 8 / 11 / — | 2 / — |

即**全集上更新并不优于冻结**（top3 反而 31→30，exact 21→20），增益只出现在窄子集。引用这个实验时应当同时给出全集数字，否则会把子集结论读成整体结论。本机 WSL/ROCm 热态查询 P95 约 12–13 ms，复用查询向量时更新含落盘 P95 约 17 ms，模型加载加预热约 46 秒。这些数字只覆盖独立实验中的模型与 SQLite，不涵盖产品身份核验、进程通信和主模型回答质量。本方案中的类型名标为“拟新增”时，表示待施工。

## 1. 接在哪一行调用链上

| 时机 | 当前代码事实 | 拟接入方式 |
|---|---|---|
| 用户入站 | `TurnExecutorAdapter.ExecuteAsync` 从受理的 Turn 构造 `RuntimeDispatchRequest`，带 `UserId`、`MessageId`、`Origin`、`ExecutionIdentity`。身份里有 Conversation、Turn、Command 和 Run。 | 在 Runtime 适配层从可信请求与命令构造本轮只读身份。首版只纳入已绑定用户的私有主会话，缺少身份、群聊或子代理跳过。[请求映射](../../../Source/PuddingRuntime/Services/TurnExecutorAdapter.cs) · [执行身份](../../../Source/PuddingCore/Runtime/RuntimeExecutionIdentity.cs) |
| 首次模型调用前 | Streaming 直接调用 `ContextPipeline.AssembleAsync`。Buffered 首轮可经过 `ContextAssemblyService`，亦有直接调用与后续按模板记忆开关重组装的路径。两者都把 `UserContextPrefix` 交给 `BuildCurrentUserChatMessage`，随后才触发 `OnLoopStartAsync`。 | 在上下文**组装时**征集有预算的补充材料。在已有 L6 召回附近汇合。Hook 的 LoopStart 留给通知，无法通过其返回值修改已组装的消息。[流式](../../../Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Streaming.cs) · [非流式](../../../Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs) · [组装门面](../../../Source/PuddingRuntime/Services/ContextAssemblyService.cs) |
| 记忆保存 | `SaveMemoryTool.ExecuteCoreAsync` 调用 `UpsertExperienceAsync` 后返回 `status/action/type/bookId/chapterId`。同章复用、追加新章、替代旧章都可成功。可选的 `IFactMemorySink` 是另一条写入路径。 | 用已提交的 `save_memory` 工具回执登记章节与来源消息。合并或更正以权威章的版本链为准。工具内部无需添加训练逻辑。[工具实现](../../../Source/PuddingRuntime/Tools/BuiltIns/Memory/SaveMemoryTool.cs) · [写入与替代语义](../../../Source/PuddingMemoryEngine/Data/MemoryLibraryConvenience.cs) |
| 工具完成，后台可学习 | 流式非终端工具经过 `OnToolResultTransformingAsync`，Buffered 的工具调用也经过此变换链。只读的 `OnToolResultAsync` 在两条路径上的覆盖不同。会话事件合同另有 `tool.call.requested/completed/failed`。 | 注册一个 Hook，遇到相应工具只提示后台检查会话并原样返回结果。正式登记以事件库中的请求、完成及实际章节为凭据。Hook 唤醒可以早到、重复或丢失。[Hook 签名](../../../Source/PuddingRuntime/Services/AgentLoop/IAgentLoopHook.cs) · [请求与完成事件字段](../../../Source/PuddingCore/Platform/ConversationTranscriptFold.cs) |
| 模型读回 | `BuildUserTailContent` 先写 Runtime tail，再写带哈希的当前用户消息围栏，文本和多模态消息共用该语义。 | 把已授权章节的短摘要、章节 ID 和来源作为历史资料放在围栏之前，维持原输入、附件及来源哈希。按最终出站消息核对实际注入。[用户消息构造](../../../Source/PuddingRuntime/Services/AgentExecutionService.cs) |

## 2. 独立组件与模型进程

| 拟建位置 | 职责与依赖 |
|---|---|
| `Source/PuddingWeightMemory/` | .NET 叶组件。定义下列合同，管理分区、章节向量、打分、增量更新、去重和快照。只依赖 BCL 与 SQLite 驱动，组件项目不引用 Host、Runtime、Platform 或 MemoryEngine。 |
| `Source/PuddingWeightMemoryTests/` | 只引用叶组件。确定性向量和临时 SQLite 可覆盖寻址、反馈、重放、并发与删除。另以真实本地编码器跑跨实现一致性用例。 |
| 独立仓库的编码器进程 | 第一版复用实验的 `scripts/memory_keys.py::Encoder` 算法，拟包装为常驻本地进程。输入文本、输出 512 维向量。训练和检索权重由 .NET 组件持有，Python 进程只编码。 |
| `Source/PuddingHost/Services/Memory/` | S5 时增加 Hook、身份与章节解析、后台事件消费者和编码器进程适配器。Host 引用叶组件和现有服务，负责资源释放、配置、服务装配。Runtime 只暴露通用的组装扩展口，Desktop 继续由 Core 提供此能力。 |

编码器通过 Core 子进程重定向的 stdin/stdout 交换带长度上限的 JSON 行，首包拟为 `hello(protocolVersion, encoderId, dimension)`，查询为 `encode(requestId, text)`，结果为 `encoded(requestId, encoderId, vector)`。核对请求号、固定模型版本、维度与有限数值，取消后丢弃迟到结果。以本地模型文件启动，异步预热，设有界队列，Core 退出时关闭子进程。实验编码定义为 `BAAI/bge-small-zh-v1.5@7999e1d3359715c523056ef9478215996d62a620`、首 token、L2 归一化、无指令前缀、最长 512 tokens。先在 Windows 产品启动方式验证同一组输入和排名，WSL/ROCm 作为对照环境。更换编码定义需重建同一分区的全部向量。

以下是拟新增的最小 C# 合同。训练可独立跑，模型输出可保存为地址或空结果。

```csharp
public sealed record MemoryScope(string WorkspaceId, string UserId, string AgentId);
public sealed record TurnKey(string ConversationId, string TurnId, string MessageId);
public sealed record RecallQuery(MemoryScope Scope, TurnKey Turn, string SnapshotId,
    string CurrentText, IReadOnlyList<string> PriorTurns, int Limit);
public sealed record MemoryHit(string ChapterId, float Score);
public sealed record RecallResult(string SnapshotId, IReadOnlyList<MemoryHit> Hits);
public sealed record SavedChapter(MemoryScope Scope, TurnKey Source, string EventId,
    string BookId, string ChapterId, string SourceCue);
public sealed record Feedback(MemoryScope Scope, TurnKey Source, string EventId,
    string PrimaryChapterId, IReadOnlyList<string> AcceptableChapterIds, string Cue);

public interface IWeightMemory
{
    Task<RecallResult> PredictAsync(RecallQuery query, CancellationToken ct);
    Task RegisterAsync(SavedChapter chapter, CancellationToken ct);
    Task ApplyFeedbackAsync(Feedback feedback, CancellationToken ct);
}

public interface IWeightMemoryMaintenance
{
    Task InvalidateChapterAsync(MemoryScope scope, string chapterId, string eventId,
        CancellationToken ct);
    Task<bool> TryPublishAsync(MemoryScope scope, string expectedActiveSnapshotId,
        string evaluatedCandidateSnapshotId, CancellationToken ct);
}
```

`PredictAsync` 在一次只读快照上对授权分区的章节打分。打分取归一化查询向量与章节向量的点积，输出按分数降序，相同分数按章节 ID 稳定排序。最高分或第一、二名分差低于各自阈值时返回空列表。阈值按模型与目录规模校准。读侧再核验地址后才取正文。冻结编码器，逐条训练章节参数。开发集比较单向量、保留初始向量再学第二向量、直接保存最近确认语句向量三种做法。困难负例排除所有可接受章节，正确章已领先到约定阈值时跳过梯度。阈值和旧线索回放量在开发集定好，再以留出的真实会话验收。

## 3. 写入、反馈和恢复

1. **登记章节。** `WeightMemoryReceiptHook`（拟新增）在 `OnToolResultTransformingAsync` 看到 `save_memory` 时用有界队列唤醒后台，忽略调用参数，返回原 `SkillResult`。后台按授权会话用 `IConversationEventStore.ReadForwardAsync(conversationId, afterExclusive, throughInclusive, limit, ct)` 顺序读。按 Conversation、Turn、Command、已登记的 Run 和 `toolCallId` 配对请求与完成事件。核对请求工具名、完成工具名、退出码为 0、无错误，且 `output` 可解析为 JSON，`status=ok`、`action=upsert`、两个 ID 非空。用命令的 `UserMessageId` 取原始消息，核验用户、工作区、Agent 和章的归属。只取登记所需字段。老数据缺证据时记跳过原因。[事件读取端口](../../../Source/PuddingCore/Platform/IConversationEventStore.cs) · [执行命令](../../../Source/PuddingCore/Platform/IExecutionCommandReader.cs) · [执行尝试](../../../Source/PuddingPlatform/Data/Entities/ExecutionRunEntity.cs)
2. **预测后揭示反馈。** 每条用户消息先持久化两组预测的快照号及排序，再接受此消息的标签。初次保存提供章节登记线索。单独的用户确认、明确引用或更正才作为监督训练。首版从已人工核定的反馈清单导入，线上可纳入能唯一绑定的显式选择，例如用户点名刚展示的章节 ID。“第二个”需要上一轮实际面向用户展示过的有序 ID，“继续”、只读过同一文件或模型自行选择的结果不产生训练样本。没有展示凭据或多个解释时只记待核对。更正正文由现有记忆写入流程提交，向量仅学习该说法指向哪个有效章节。
3. **去重与顺序。** 拟在 `<DataRoot>/databases/weight-memory.db` 保存派生数据。最少有每会话的 `cursor`，按分区存储的 `chapter_key`、`prediction`、`feedback` 及 `snapshot`。登记/反馈用 `(scope, eventId, kind)` 唯一约束，相同消息的同一目标反馈再以 `(scope, sourceMessageId, targetId, kind)` 防止执行重试导致二次训练。单分区串行应用写入，在同个 SQLite 事务提交权重、回执和游标，读请求持有自己的快照。分页跨过请求/完成边界时，在同一事务保存待配对请求。跨会话顺序用分区消费者分配的提交序，中断重放沿用原序。事件时间只用于排序核验与“隔几天”评测分组。
4. **漏事件与删除。** 启动和定时任务按授权会话目录恢复游标，使用 committed head 作为上界，补扫至追平。通知通道只负责唤醒。遇到事件保留期造成的缺口，标记分区需要从当前可信章节重建初始地址，丢失的历史用户反馈不可推造。成功删除按回执的 `deletedChapterIds/deletedBookIds` 使章节或整书立即失效，按 `deletedPointerIds` 清理对应资料线索。无删除命中、失败、`get_important/set_important` 各自跳过训练。章节别处删除、归档或版本变化同样由读前核验阻止过期内容输出。[提交通知合同](../../../Source/PuddingCore/Platform/ICommittedEventSignal.cs) · [可按主体核验的会话目录](../../../Source/PuddingPlatform/Data/Entities/ConversationCatalogEntity.cs)

线上影子预测以本轮开始时钉住的快照为准。离线回放必须同时限制章节版本和训练事件的可见上界，避免拿今天的更正回答昨天的问句。历史版本或运行证据缺失的样本单列。用户要求遗忘时，同时失效活动版本和候选版本中的地址，清除关联训练线索，旧快照受删除记录约束，每次发布或回滚先应用删除记录。跨章训练受影响时从剩余反馈重建候选。

建议把训练和发布分成两个可调用操作。单分区只允许一个写者。`ApplyFeedbackAsync` 按事件推进候选版本，后台评估后按预期旧版本号原子更新活动快照指针。读侧每个 Turn 固定一次活动快照。影子任务在开始时同时固定冻结组与候选组的版本，进入后台队列后仍使用这两个版本。组件未就绪或队列已满时记录漏测，不在事后以含该条反馈的新版本补算。关联同一问题的重试复用预测记录。

## 4. 读侧的代码改动

**在 S5 新增一个有返回值的通用扩展口。** 现有 `IAgentLoopHook.OnLoopStartAsync` 只返回 `Task`。`IContextLayerBuilder` 定义了固定顺序的层构建合同，当前 `AssembleAsync` 中 L6 是直接调用 `SubconsciousRecallPipeline.RunAsync`。单独登记一个新的 DI 服务不会让它进入首个模型请求。建议增加如下只读贡献接口，由具体适配器实现，组装器负责预算、故障隔离和是否采纳。[层合同](../../../Source/PuddingRuntime/Services/ContextLayerContracts.cs) · [L6 组装与去重位置](../../../Source/PuddingRuntime/Services/ContextPipelineOrchestrator.cs)

```csharp
// 拟新增，位于 PuddingRuntime。ContextRequest 增加可空的可信 Turn 身份。
public sealed record TurnContextItem(string Source, string ChapterId, string EvidenceId,
    string Title, string Excerpt);
public interface ITurnContextContributor
{
    Task<IReadOnlyList<TurnContextItem>> CollectAsync(
        ContextRequest request, int availableTokens, CancellationToken ct);
}
```

| 施工文件 | 具体变更与行为 |
|---|---|
| `PuddingCore/Runtime/ContextAssemblyContracts.cs`、`PuddingRuntime/Services/ContextPipeline.cs`、`ContextAssemblyService.cs`，以及两条 AgentExecution 分支 | 拟给 `ContextAssemblyRequest` 和 `ContextRequest` 透传同一可空的可信身份，含用户、私聊资格、Conversation、Turn、Command、Message、持久 Agent 和已受理事件上界。在调度侧解析一次，覆盖 Buffered 门面、直接组装及 Streaming。无证据的调用返回空材料。继续尊重模板记忆开关。[门面请求合同](../../../Source/PuddingCore/Runtime/ContextAssemblyContracts.cs) · [内部请求合同](../../../Source/PuddingRuntime/Services/ContextPipeline.cs) |
| `PuddingHost/Services/Memory/WeightMemoryContextContributor.cs`（拟新增） | 读取本轮用户原文和此前最多 N 条真实可见消息。按消息 ID 排除本轮重复项、模型私有思考、工具回执、历史注入块和将来消息。首个开关取 N=0 复现实验，再将 N=2 与之配对。限制长度，优先保留本轮文本。影子模式只记录排序，注入模式才解析获选地址。 |
| `PuddingHost/Services/Memory/ScopedChapterResolver.cs`（拟新增） | `GetChapterAsync` 按裸 ID 取章，个人权重的登记证据须匹配 `(WorkspaceId, UserId, AgentId)`。逐跳读取 `Chapter → GetBookReadOnlyAsync → GetLibraryAsync`，核对书和章状态、Library 工作区与章 Agent。首版只纳入有归属证据的私有章。按 `SupersededByChapterId` 找到 active 章时再核对权限、书和登记来源，限制跳数并检测环。证据断裂或找不到就返回空。文件名从可见的章或授权来源引用的标签取得，只传标识与摘要。[章、书及版本字段](../../../Source/PuddingCore/Abstractions/MemoryLibraryDtos.cs) · [记忆库读取接口](../../../Source/PuddingCore/Abstractions/IMemoryLibrary.cs) |
| `PuddingRuntime/Services/ContextPipelineOrchestrator.cs` | 在已有 L6 预算内调用注入的 `IEnumerable<ITurnContextContributor>`，默认可为空。提议先保留现有内容，再将有余量的权重命中作为带来源的历史资料加进 `userContextBuilder`。同批按有效章 ID 去重，和已有文本可精确确认重复时省略，并保留原可见上下文去重逻辑。按整个条目裁剪并转义边界标记。超时、空结果或预算用尽时立即继续。主请求取消向上传播。记录保留下来的章 ID 及 token 数，区分“预测过”“发给主模型”“展示给用户”。 |
| `PuddingHost/Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 通过 `IAgentLoopHook`、`ITurnContextContributor` 和 `AddHostedService` 分别装配回执提示、读侧、后台消费者。仅 Host 的适配器引用叶组件。注册前确认单例不持有 scoped DbContext，数据库适配器按调用取得上下文。配置关闭时不拉起编码器。验证现有记忆工具和 L6 的组合注册。[Core 装配位置](../../../Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Runtime.cs) |

例如用户说“这次还是让 Codex 先拆任务”，假设命中“Codex 规划，本地 Agent 实施”这条记录，模型返回的是章节地址与分数。读侧核验当前版本，附上短摘要和来源。命中时历史资料位于围栏外，用户本轮原话位于围栏内。模糊提问未达阈值时返回空列表，主模型直接处理这条用户消息。

## 5. 运行模式与验收

拟在 `<DataRoot>/config/system.json` 增加 `memory.weightRecall` 节，`mode` 取 `off | shadow | inject`，初始为 `off`。另设授权范围、`windowTurns`、`maxHits`、`maxTokens`、`queryTimeoutMs` 以及发布周期。建议先试 1 个命中、192 个新增 tokens、50 ms 的整段查询期限，均为待测预算。超时包括编码排队、打分和章读取。候选权重随反馈提交，活动快照按天或周经评测后原子切换，保留上一已验版本。切换失败仍可使用前一版，故障时切回 `shadow` 或 `off`。影子运行只记录必要的分区化标识、排名、版本、耗时和原因码，禁止把对话原文、工具参数或章节正文写入遥测。

| 交付步 | 可独立验收的结果 |
|---|---|
| S1–S2 建组件和独立测试 | 实现合同、SQLite 和编码器适配。锁定编码器的测试向量，校验归一化、打分、四步局部梯度与 Python 参考排序。用确定性用例覆盖重复反馈、事务中断、空地址、多正确章、删除、回滚，以及队列满时的降级。 |
| S3–S4 无宿主回放与依赖检查 | 在隔离快照复现已记录的 8/17 对照，再用未参与调参的后续会话评估冻结、按需更新和直接存确认向量，另列 FTS5 基线。加入人工标注的“无相关记忆”及旧说法保留集。独立运行组件测试、检查项目引用与依赖方向，完成组件代码地图。此时才准备宿主补丁。 |
| S5 接线为 shadow | 将组件与测试登记到 `PuddingAgentNetwork.slnx`，Host 引用组件，完成上表 DI 与两个入口的合同透传。在隔离 DataRoot 验证首轮、后续轮、文本、多模态和主请求取消。用可捕获请求的模型替身断言 shadow 下系统前缀、用户原文和附件完全一致。验证回执成功、失败、同一工具并行、事件迟到、重启追平及跨身份拒绝。[Host 组合测试入口](../../../Tests/PuddingHost.Tests/Hosting/PuddingApplicationHostCompositionTests.cs) · [现有工具合同测试](../../../Source/PuddingRuntimeTests/Tools/SaveMemoryToolContractTests.cs) |
| 小范围开启 inject | 预先固定判据。建议以同题配对“更新新增首选命中数大于损失数”为必要条件，同时在预留的空目标集上统计误注入率及分母，要求在预设阈值下误注入率和旧说法保留率均不劣于冻结组。有效章或身份读错、删除后仍注入均要求 0 次。测整轮新增 P50/P95、超时率、训练队列积压、写盘、并发时 CPU/RSS/显存与冷启动。用“保存 → 换说法 → 更正 → 再问 → 删除”验收最终出站请求和主模型实际复述。效果或误注入证据不足则继续 shadow。 |

实验推荐优先交付 **S1–S2 的回执配对、章节权重与冻结对照**。接下来才需要 Runtime 的读侧扩展。Engram 式寻址与强化学习管理存储、读取和遗忘列入 V2，复用同一批有来源的事件与评测合同。[既有记忆架构设计](../2026-11-01-记忆存储与使用架构设计.md) · [S1–S5 组件化交付规程](../../10_conventions/组件化交付规程.md)

### 追加（2026-11-13）：未来章节泄漏是本实验最大的口径问题

在来路可核的同一批 50 例上复算了冻结权重键（`PuddingMemoryLab/scripts/frozen_recheck.py`，
模型 `BAAI/bge-small-zh-v1.5`，冻结权重、不经任何反馈更新）：

| arm | 语料 | 候选集 | top1 | top3 |
|---|---|---|---|---|
| A | 本次重建 178 章 | 全库 | 0.22 | 0.58 |
| B | 原实验 176 章 | 全库 | 0.20 | 0.56 |
| C | 原实验 176 章 | 仅 cue 之前已存在的章节 | **0.48** | 0.62 |

两点：

1. **语料可比**。A≈B，且按遍历对齐后 gold 错位 0、gold 总数同为 77。此前「两把尺子」的
   质疑可以排除——差异不来自语料。
2. **B 与 C 语料完全相同**，唯一差别是 `temporal=True` 时把 cue 时刻尚不存在的章节从候选池删掉：

   ```python
   visible = {k for k, c in cards.items() if c['at'] < turn['at']}
   ```

   这不是时间加权，是**去泄漏**。它建模的是真实场景：用户说那句话时，系统只能看到当时已有的记忆。
   24/50 与 10/50 的差距，全部来自干扰项被移除。

**由此，本文档前述对照表的读数需要收紧**：表中冻结/更新两组数字取自 temporal 设置，
而字符 bigram BM25 基线（0.24 / 0.58）取自全库（含未来章节）。二者候选集口径不同，
不能相除得出「语义键是词法基线的 2 倍」。

在无泄漏口径下，冻结语义键（0.20）并不优于词法 BM25（0.24）。
语义键的真实优势必须在同一 temporal 设置下重跑词法基线后才能判定——该实验尚未完成。
在此之前，不应引用任何「几倍」的表述，也不应据此决定是否进入影子模式。

### 追加（2026-11-13）：同口径对照完成——小模型召回未显示出优于词法检索

把字符 bigram BM25 放进与 arm C 完全相同的泄漏控制下（`scripts/bigram_temporal.py`，
同一脚本内一次跑两个 arm，仅切换候选集）：

| 方法 | 候选集 | top1 | top3 |
|---|---|---|---|
| 字符 bigram BM25 | 全库 | 0.28 | 0.66 |
| 字符 bigram BM25 | 仅 cue 前已存在 | **0.50** | **0.74** |
| 冻结语义键（arm C） | 仅 cue 前已存在 | 0.48 | **0.62** |

`cases_with_gold_after_cue = 0`，即所有 gold 章节都在 cue 之前已存在，
arm C 的 0.48 不含「不可答」污染，两组数字可直接相减。

**结论：在本数据集上，零成本的字符 bigram BM25 在 top1 上不劣于、在 top3 上明显优于
本地小模型的冻结语义键（0.50/0.74 vs 0.48/0.62）。**

同时修正先前的词法基线：标准 BM25（含 `(K1+1)` 分子与 K1 项）在全库上得到 0.28/0.66，
而非旧实现（缺这两项）的 0.24/0.58。旧数字作废。

#### 这条结论的边界（不可越过）

- 样本 50 例 / 178 章，top1 的 95% 区间约 ±14% ⇒ 0.50 与 0.48 的差距**在噪声内**，
  只能说「未显示优势」，**不能说**「词法显著更好」。
- 语料是短文本（标题+正文若干句），词法在这种形态上天然占优；长文档下结论可能反转。
- gold 来自 11-07 显式反馈场景的人工核验，覆盖的是「用户点名/更正」这一类 cue，
  不覆盖「换个说法问同一件事」的语义泛化场景——那恰是语义检索理论上该赢的地方。
- 因此不能据此否定整个方案，但**足以否定「先上小模型」的优先级**：
  在拿到语义占优的证据之前，没有理由为此引入 46 秒模型加载与 GPU 依赖。

#### 对方案重心的建议

把第一阶段目标从「验证小模型召回」改为「验证语义泛化是否真实存在」：
构造一批 cue 与目标章节**几乎不共享字符 bigram** 的用例，只有在这种用例上语义模型的
优势才可能显现。若那批用例上语义键仍不领先，则本方案的召回部分应重新评估。

### 追加（2026-11-13）：LoRA 实验——「权重存记忆」被正面证伪，但 LoRA 提升了匹配器

按前述可证伪预测执行：在产品库 10,377 章候选池上，取最新 200 章训练、另抽 200 章对照
（从不参与训练），query 为章节标题，LoRA r=8 微调 `bge-small-zh-v1.5`，全程 GPU。

| | 冻结 | LoRA 后 | 变化 |
|---|---|---|---|
| 训练集 top1 / top3 | 146 / 183 | 178 / 191 | +32 / +8 |
| 对照集 top1 / top3 | 120 / 146 | 157 / 178 | **+37 / +32** |

逐例（top3）：训练集 gained 9 / lost 1；对照集 gained 35 / lost 3。

**预测的三条全部落空**——本以为对照集会退化、出现干扰对称，实际是它涨得比训练集还多。

原因清楚：LoRA 学到的不是那 200 条记忆的内容，而是「标题↔正文如何对应」的通用匹配函数，
因此泛化到未训练样本，且基线越低提升越大。反过来说，**若记忆真被写进权重，对照集本应退化**；
实测毫无退化，说明权重里只有一个更好的匹配器，没有那 200 条记忆。
这也印证 encoder 架构的限制：其输出是向量、不含内容，候选库一旦移除章节便无从「认出」。

#### 对前述建议的修正

- 「用权重存记忆」——**不成立**，本次实验提供了正面反证（而非仅仅「没测出增益」）。
- 「权重更新/持续学习」——本实验不为其翻案。这里训练的是匹配函数，与「按反馈更新章节键向量」
  是两件事，后者仍为 p = 1.0。
- 「先砍掉语义召回」——**应改为「先做一次 LoRA + 真实 gold 的对照再决定」**。
  此前的不占优结论测的是冻结模型；LoRA 后在「标题→该章」任务上 top1 从 0.60/0.73 升至 0.78/0.89。
  但该任务 query 与正文同源，比真实口语 cue 容易得多，**不可外推**。

下一步才是关键：用 50 例真实 gold 重测 LoRA 后的模型，与词法基线（0.50/0.74）同口径对照。

### 追加（2026-11-13）：真实 gold 上的三 arm 对照——结论出现拐点

LoRA 适配器在**另一批语料的「标题→正文」**上训练（产品库 200 章），从未见过真实用户 cue。
把它放到 50 例人工核验 gold、176 章语料、`temporal=True` 的同一口径下：

| 方法 | top1 | top3 |
|---|---|---|
| 字符 bigram BM25 | 25/50 = 0.50 | **37/50 = 0.74** |
| 冻结语义键 | 24/50 = 0.48 | 31/50 = 0.62 |
| **LoRA 语义键** | **31/50 = 0.62** | 35/50 = 0.70 |

LoRA 相对冻结语义键：top1 +7 例、top3 +4 例（逐例 gained 6 / lost 2，精确 McNemar p ≈ 0.29）。

**这推翻了先前「小模型召回不占优」的结论。** 该结论测的是冻结模型；LoRA 之后语义检索在 top1 上
已高于词法基线 6 例（0.62 vs 0.50），尽管 top3 仍落后 4 例（0.70 vs 0.74）。

#### 当前应采纳的判断

- 「用权重存记忆」——仍不成立。适配器在陌生语料上迁移有效，正说明它学的是通用匹配能力，
  而不是记住了某个语料里的条目。
- 「小模型召回不占优」——**撤回**。LoRA 后呈现 **top1 语义领先、top3 词法领先**的交叉状态。
- 「先砍掉语义召回」——改为：**做混合检索**。单路都没有全面优势，BM25 与向量互补是最自然的做法。

#### 未验证项（不可略过）

1. top1 的 31 vs 25（相对词法）**未做逐例配对检验**——两种方法的逐例命中向量不同源，未落盘同一文件。
   按 6 例差距的敏感度，若全单向 p = 0.031，若 4:2 则 p = 0.69。方向未测，故不下显著性结论。
2. LoRA 只训了 10 epoch / 200 样本，未做超参搜索；提升幅度受此限制，也可能有更大空间。
3. 50 例样本，top1 的 95% 区间约 ±14%。

### 追加（2026-11-13）：同口径混合检索——这是目前唯一有统计支持的方案

先前两次对照各用了不同的候选集（178 章 save-order 编号 vs 176 章 manifest 编号），
结论因此反复。本轮把四个 arm 全部压进**同一个逐例可见集**：

```python
visible(case) = {chapter : saved before the cue}      # 每例各自计算
```

BM25 的 idf 在每个可见集上重算；语义相似度与该集合做 z 标准化；融合为 `α·语义 + (1−α)·词法`。

| arm | top1 | top3 |
|---|---|---|
| L 字符 bigram BM25 | 28/50 = 0.56 | 34/50 = 0.68 |
| F 冻结语义 | 28/50 = 0.56 | 35/50 = 0.70 |
| S LoRA 语义 | 32/50 = 0.64 | 41/50 = 0.82 |
| **H0.7~0.9 融合** | **35/50 = 0.70** | 41/50 = 0.82 |

逐例配对（精确 McNemar，双侧）：

| 比较 | 新增 / 丢失 | p |
|---|---|---|
| S vs L，top1 | +8 / −4 | 0.388 |
| S vs L，top3 | +9 / −2 | 0.065 |
| S vs F，top3 | +7 / −1 | 0.070 |
| **H0.8 vs L，top1** | **+7 / −0** | **0.016** |
| **H0.8 vs L，top3** | **+8 / −1** | **0.039** |
| H0.8 vs S，top1 | +4 / −1 | 0.375 |
| H0.8 vs S，top3 | +2 / −2 | 1.000 |

**结论：混合检索（α≈0.8，偏向语义）相对纯词法在 top1 与 top3 上均显著更优。
纯语义相对纯词法只在 top3 上边缘（p=0.065），top1 不显著。**

α 在 0.7~0.9 上构成平台区（top1 恒为 35），不是尖峰，因此对 α 的选择不算过度拟合；
但 α 确实是在这 50 例上扫出来的，用更大样本时应重新标定。

#### 又一次需要撤回的判断

上一轮写下的「top3 词法领先 4 例（0.74 vs 0.70）」**作废**。那是跨候选集比较的产物：
语义臂在 176 章池内排名、词法臂在 178 章池内排名，两者的干扰项不同。
同一可见集下语义 top3 为 0.82，远高于词法的 0.68。

**本项目至今三次结论反转，全部源于同一个原因：比较两把刻度不同的尺子。**
今后任何方法对比，必须先证明两个 arm 的可见候选集逐例相同，再比数字。

#### 采纳建议

采用混合检索：BM25 负责字面命中与可解释性，LoRA 语义负责换说法命中，α 取 0.8。
不采用纯语义（top1 无显著优势、需 GPU），也不停留在纯词法（top1 显著落后混合方案）。

### 追加（2026-11-13）：对上述结论的两项自查

结论若只靠 50 例、且 α 在同一批 50 例上扫出，就有过拟合风险。两项检验都基于已落盘的逐例记录重算，不重跑模型。

**一、自助法（20,000 次重采样）**

| 指标 | H0.8 − 词法 | 95% 区间 | P(差 ≤ 0) |
|---|---|---|---|
| top1 | +0.140 | [0.06, 0.24] | 0.0004 |
| top3 | +0.141 | [0.04, 0.26] | 0.0079 |

区间不含 0，领先不是这 50 例的特例。

**二、嵌套 α 选择（5 折，α 在 4/5 上选、在留出 1/5 上评）**

各折选中的 α：`0.9 / 0.7 / 0.6 / 0.7 / 0.8`（中位数 0.7）。

| 方案 | top1 | top3 |
|---|---|---|
| 混合（α 每折重选） | **33/50 = 0.66** | **39/50 = 0.78** |
| 纯词法（同折） | 28/50 = 0.56 | 34/50 = 0.68 |

**α 调参的真实代价约 4 个百分点**（0.70→0.66、0.82→0.78）。先前报的 0.70/0.82 偏乐观，
应引用 0.66/0.78 这个诚实值。即便按诚实值，混合仍领先词法 5 例。

各折 α 落在 0.6~0.9 而非集中在单点，说明平台区宽、对 α 不敏感；这也意味着生产环境
不必精调 α，取 0.7 即可。

**最终建议（以此为准）**：BM25 + LoRA 语义混合，α = 0.7，预期 top1 ≈ 0.66 / top3 ≈ 0.78，
相对纯词法（0.56 / 0.68）有约 5 例的稳定优势。

### 追加（2026-11-13）：生产延迟实测

脚本 `PuddingMemoryLab/.work/exp-lora/latency.py`（`--save-adapter` 与 `--measure` 分两个进程，
使冷启动数字真实）。库规模 10,377 章。硬件 AMD RX 9070 XT / ROCm。

| 指标 | 实测 |
|---|---|
| `from_pretrained` 冷加载 | **3,413 ms** |
| 应用 LoRA 适配器 | 40 ms |
| 全库编码 10,377 章 | 8.6 s（0.83 ms/章） |
| BM25 索引构建（41,780 词） | 0.4 s |
| 单查询编码 p50 / p95 | 8.4 / 12.2 ms |
| 端到端检索（混合 top10）p50 / p95 | 81.2 / 95.8 ms |
| GPU 峰值显存 | 669 MB |

#### 需要更正一个先前引用的数字

本文档此前写「46 秒模型加载」。本次分进程实测，纯 `from_pretrained` 为 **3.4 秒**。
两个数字口径不同：46 秒很可能包含进程内 `import torch`、ROCm 初始化与首次 kernel 编译，
而上述 3.4 秒不含这些。**生产化评估必须重新测「应用进程冷启动到可服务」的全链路耗时**，
不可直接沿用任一个数字。

#### 端到端 81 ms 的主因不是模型

拆开看：查询编码仅 8.4 ms，其余约 70 ms 消耗在 BM25 打分的**纯 Python 循环**上
（逐 bigram 遍历倒排表、逐文档累加）。这是该探针的实现问题，不是设计上限；
改用 scipy 稀疏矩阵乘后，端到端应落在 15~25 ms 量级。该优化尚未实施，故不写入结论。

#### 可行性判断

- **常驻可行**：模型 + 适配器共 669 MB 显存，加载一次后查询成本 8.4 ms。
- **不可按需加载**：即便 3.4 秒，也无法塞进单轮对话的检索路径；必须常驻或懒加载一次后复用。
- 全库编码 8.6 秒属一次性成本，增量写入时只需编码新章（0.83 ms/章），可接受。

### 追加（2026-11-13）：BM25 向量化，端到端 81 ms → 13 ms

上一节的 81 ms 里约 70 ms 花在 BM25 的纯 Python 循环上。把打分改写成

```
score(d) = norm(d) · Σ_{t∈query} idf(t)·[t ∈ d]
```

`norm(d)` 只依赖文档长度，可从求和里提出；剩余部分退化为 CSR 倒排表上的 `np.bincount`
累加。不引入 scipy，CSR 用三个 numpy 数组自建。

| 指标 | 循环实现 | 向量化 |
|---|---|---|
| BM25 打分 p50 | ~70 ms（由端到端差值推算） | **1.34 ms** |
| BM25 打分 p95 | — | 2.32 ms |
| 端到端 p50 | 81.19 ms | **13.27 ms** |
| 端到端 p95 | 95.81 ms | **23.31 ms** |
| CSR 构建（一次性） | 0.4 s | 0.59 s |
| 显存峰值 | 669 MB | 669 MB |

#### 等价性校验（否则不叫优化）

`compare_bm25.py` 在同一 10,377 章语料、60 条 cue 上对比两种实现：
**排名 top-10 完全一致的 60/60，最大分数差 4.6e-5**（float32 舍入量级）。脚本以不一致数
作为退出码，可直接当门禁用。

#### 现在这套数字可以支撑结论了

查询编码 8.4 ms + BM25 1.3 ms + 余弦与融合约 3 ms ≈ **13 ms p50**。
常驻 669 MB 显存，全库编码一次性 8.7 s，增量 0.83 ms/章。
在生产路径上，混合检索的延迟成本已低于一次常规 LLM 调用的零头。
