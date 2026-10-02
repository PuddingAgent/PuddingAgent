---
title: PuddingAgent 高磁盘读取诊断
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: 发送消息后的零游标 SSE 回放和边界扫描，以及后台代码索引逐符号关系清理，共同造成读取放大。
categories: [docs, reports]
tags: [diagnostics, sqlite, code-index, disk-io]
related_docs: [Docs/08_how_debuge/05-常见症状.md, Docs/12_features/Agent统一检索与渐进展开工具链设计-2026-09-13.md]
related_files: [Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs, Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs, Source/PuddingCodeIndex/Services/CompositeCodeIndexer.cs, Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs, Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs, Source/PuddingCodeIntelligence/CSharp/RoslynCSharpIndexer.cs, Source/PuddingFullTextIndex/Contracts/FullTextChangeSet.cs, Source/PuddingFullTextIndex/Infrastructure/Maintenance/MTimeComparison.cs, Source/PuddingPlatform/Services/ConversationEventStore.cs, Source/PuddingPlatform/Services/SessionEventStreamService.cs, Source/PuddingPlatformAdmin/src/pages/chat/hooks/useMessageSend.ts, Source/PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventConnection.ts]
slug: reports-puddingagent-high-disk-read-2026-10-02
draft: false
---

# PuddingAgent 高磁盘读取诊断

## 结论与范围

发现两条同时存在、应分别治理的读取放大链路：

1. **发送消息后平台数据库读取近 1 GB**：已有约 164 万条事件的长会话以 `cursor=0` 开始 SSE 全历史回放，每回放 256 条又调用线性扫描的 MIN/MAX 边界查询。消息后的两次托管栈实际观察到 `FollowAsync → GetBoundsAsync → SQLite`。
2. **消息发送前后台持续高读**：Markdown 文档变更进入 watcher → 没有语言索引器认领 → 返回 Failed → 增量维护升级为全仓索引 → Roslyn 逐文件清理旧符号 → 对每个符号扫描整个项目的引用/关系分区。前两次托管栈观察到这一链路，消息后的栈中它仍同时存在。

不能把两条链路混为一谈，或把发送一条消息的读取成本解释为模型必须读取全部历史。结论依据是用户两张截图、实际运行日志、四次托管调用栈和真实数据库上的只读查询计划/读取探针。没有取得完整文件级跟踪，不能精确分摊每条链路的全部 I/O。

本任务仅诊断：没有修改业务代码、配置、索引或数据库，没有重启/停止产品。实例由用户启动。探针以 SQLite `mode=ro` 与 `query_only=on` 访问数据库；DELETE 仅执行 `EXPLAIN QUERY PLAN`，读取成本用相同 WHERE 条件的 SELECT 测量。

## 运行现场

- 日期/时间均为北京时间 2026-10-02。
- 用户启动后的 Core PID：23984，路径 `Source/PuddingAgent/bin/Debug/net10.0/PuddingAgent.exe`。
- `PuddingCodeIndex.dll` 和 `PuddingCodeIntelligence.dll` 时间：2026-10-01 11:06:04；`PuddingPlatform.dll`：11:06:10。因此证据针对这份运行中制品，不能认为工作树后续代码已加载。
- `code_index.db` 约 2.84 GB；`pudding_platform.db` 9,995,956,224 bytes，page_size=4096，page_count=2,440,419，freelist_count=0。
- 仓库 scope：`default / 8a48458b30150fdbed4baaced35d24cf`。一次只读采样：CodeFiles=3,383、CodeSymbols=101,001、CodeReferences=379,958、CodeRelations=251,562。在线数据会变化，数字不是冻结快照。

### 进程读取增长

通过 `Win32_Process` 连续六次采样（5 秒间隔），首末采样如下：

| 时间 | 读次数 | 写次数 | 读取 bytes | 写入 bytes |
|---|---:|---:|---:|---:|
| 11:40:37.838919 | 5,764,510 | 3,772 | 23,713,325,352 | 6,274,815 |
| 11:41:03.799652 | 12,285,222 | 8,780 | 50,419,743,335 | 20,950,245 |

25.960733 秒增加 26,706,417,983 bytes / 6,520,712 次读取，约 **981.07 MiB/s**；写入增加 14,675,430 bytes，约 0.54 MiB/s。增量平均每次读约 4096 bytes，与 SQLite 页大小吻合。

这是进程 I/O 口径，包含缓存服务的读取，不能直接解释为 SSD 实际读取速率。另一次非同步性能计数器采样：D: 总物理读取约 97.4 MB/s，磁盘忙碌率计数器 92%，同时 PuddingAgent 进程读取约 1.16 GB/s。D: 数字包含其他进程，不能全部归因给 PuddingAgent。

截图的 AppReadWriteCounter 按应用聚合多个实例，退出再启动仍累计同一应用条目。因此截图的 4.88 GB 是监视期间的累计量，不是单个 PID 的生命周期量。参见 [NirSoft 官方说明](https://www.nirsoft.net/utils/app_read_write_counter.html)。

### 触发日志与活动任务

系统日志 `pudding-20261002_007.log`：

1. 11:39:24.842：Markdown 变更的逐文件索引失败，消息为 `No language code indexer owns the file`。
2. 11:39:24.843：`CodeIndexMaintenance` 升级为 scope-level indexing run。
3. 11:39:25.005：`CodeIndexScheduler` 开始索引当前仓库。
4. `SessionChunkBackfill` 11:40:16.060 已完成，但之后 11:40:37～11:41:03 仍有极高读取；不能把持续高读取全部解释为此回填。
5. 全文 `IndexPrebuild` 11:39:31.792 判断索引新鲜并跳过，不能把这次高读取归因于该全文重建。

### 两次调用栈

使用临时安装的 `dotnet-stack 10.0.745401`，两次 `report -p 23984` 均观察到：

```text
Microsoft.Data.Sqlite.SqliteDataReader.NextResult
DbCommand.ExecuteNonQueryAsync
SqliteCodeIndexStore.ExecuteNonQueryCountAsync
SqliteCodeIndexStore.ExecuteNonQueryAsync
SqliteCodeIndexStore.RemoveSymbolGraphForFileAsync
SqliteCodeIndexStore.ClearSymbolsForFileAsync
RoslynCSharpIndexer.IndexWorkspaceCoreAsync
```

这是两个时点的栈采样，不是完整时间占比分析；与持续高读及启动日志共同支持当前主因判断。

## 核心放大点：逐符号 OR 删除

`SqliteCodeIndexStore.RemoveSymbolGraphForFileAsync` 获取文件所有符号后，对每个 symbol 执行：

```sql
DELETE FROM CodeReferences
WHERE WorkspaceId=$workspaceId AND ProjectId=$projectId
  AND (SourceSymbolId=$symbolId OR TargetSymbolId=$symbolId);
-- CodeRelations 使用相同 WHERE
```

两个表虽然都有 Source/Target 索引，探针中的 DELETE 查询计划却只使用主键前缀：

```text
SEARCH CodeReferences USING INDEX sqlite_autoindex_CodeReferences_1
  (WorkspaceId=? AND ProjectId=?)
SEARCH CodeRelations USING INDEX sqlite_autoindex_CodeRelations_1
  (WorkspaceId=? AND ProjectId=?)
```

计划没有约束 SymbolId，意味着对每个符号重新遍历项目分区。只读 SELECT 同谓词得到相同的前缀扫描；测试选择 scope 中一个仍存在的符号：

| 查询 | 命中 | 耗时 ms | 读取 bytes | 读次数 |
|---|---:|---:|---:|---:|
| References 的 Source OR Target | 1 | 520.15 | 195,863,223 | 48,794 |
| Relations 的 Source OR Target | 1 | 272.04 | 139,863,604 | 37,171 |
| References 的 Source 精确查询 | 0 | 1.04 | 20,480 | 5 |
| References 的 Target 精确查询 | 1 | 0.64 | 20,480 | 5 |
| Relations 的 Source 精确查询 | 0 | 0.86 | 20,480 | 5 |
| Relations 的 Target 精确查询 | 1 | 0.51 | 20,480 | 5 |

原查询合计约 **335.7 MB**，四个精确查询合计 **81,920 bytes**，且实际使用 Source/Target 覆盖索引。以上为查询成本诊断，不是修改后的 DELETE 验收：未在生产库执行删除。探针 SQLite 版本 3.50.4，运行 Core 使用 Microsoft.Data.Sqlite；必须在修复时用产品 provider 再验证查询计划与事务语义。

`CreateConnection` 的 `Pooling=false`，全量重建又逐文件开连接/事务，跨文件难以复用 SQLite 页缓存，是附加放大因素；不建议仅凭此直接开启 pooling，需评估组件生命周期和锁行为。

## 发送消息近 1 GB：零游标回放叠加 SSE 边界扫描

### 发送消息后的现场补证

用户第二张截图明确展示 PID 23984 的 `pudding_platform.db` 累计读取 **961.09 MB**，`pudding_memory.db` 35.93 MB，`code_index.db` 332.41 MB。截图中平台库这一行应独立归因，不能用代码索引解释。截图采样区间未提供，不能声称精确 961.09 MB 全部已分解。

同实例系统日志：

- 11:43:28.574：取得新聊天命令 lease。
- 11:43:30.010～11:43:30.194：HistoryHydration 完成；active_messages 查询 rows=100，最终 history count=62，总耗时 450 ms；canonical 增量同步仅 rowsRead=2。
- 11:43:30.600：上下文组装完成，promptLen=17406。
- **11:43:31.672：`SSE subscribed session=206a9b48ec904ebb93e7541131fbb835 cursor=0 phase=replay-from-zero head=1644341`**。
- 11:43:34.571：ChatWorker 已完成本轮，terminal sequence=1644370；后续栈仍能观察到事件回放边界查询。

后两次 `dotnet-stack report` 同时含代码索引栈和平台栈：

```text
Microsoft.Data.Sqlite.SqliteDataReader.NextResult
SqliteCommand.ExecuteDbDataReaderAsync
ConversationEventStore.GetBoundsAsync
SessionEventStreamService.FollowAsync
SessionEventsController.EventsStream
```

`FollowAsync` 的 replay 循环每次读取 256 条、输出给 UI 后，只要该批满 256 条，就再次调用 `GetBoundsAsync` 并更新 head；live 阶段还每秒轮询。因此零游标回放不仅在读取历史 payload，**每一小批都再次扫描整个长会话索引**。按下表单次 81,702,912 bytes 估算，12 次边界扫描即 980,434,944 bytes（约 0.98 GB，尚不包含事件正文读取）。这解释了接近 1 GB 的量级；属于基于实测单查询成本的推算，不是该截图的逐操作核算。

当前前端源码的候选入口是 `useMessageSend.ts`：发送前和收到 POST 回复后均调用 `startSessionEventStream`；`useSessionEventConnection.ts` 默认使用 `lastSequenceNumRef`，初值为 0。`useChatState.ts` 的持久 SSE effect 会因 projection-owned 会话而跳过，但 send 路径直接开流。需在当前部署前端核对页角版本、调用参数和 bootstrap 游标，确认究竟哪个入口把旧会话当作零游标；本任务不把工作树源码当作已部署前端证据。

### SQL 与只读对照

`ConversationEventStore.GetBoundsAsync` 同时执行：

```sql
SELECT MIN(sequence), MAX(sequence)
FROM conversation_events WHERE conversation_id=@cid;
```

当前索引存在，但同时 MIN/MAX 的计划遍历该 conversation 的索引分区。`SessionEventStreamService.FollowAsync` 在 live notification 和每秒 poll 时都调用此方法；bootstrap 和 SSE 订阅也调用。

只读对照：将查询改为同一 SELECT 内两个 `ORDER BY sequence ASC/DESC LIMIT 1` 标量子查询，保持同一语句的读取视图和空会话 NULL 语义。每组通过 `PRAGMA shrink_memory` 释放探针连接缓存后测量：

| 会话 head | 原查询读取 bytes / 次数 | 首尾查询读取 bytes / 次数 | 结果 |
|---:|---:|---:|---|
| 1,644,340 | 81,702,912 / 19,947 | 32,768 / 8 | 均为 (1, 1644340) |
| 328,568 | 16,474,112 / 4,022 | 32,768 / 8 | 均为 (1, 328568) |
| 161,452 | 8,269,824 / 2,019 | 28,672 / 7 | 均为 (1, 161452) |

无需新增索引即可显著减少读取。不能用 conversation_heads 替代实际事件最小值，因为保留期裁剪后需要真实 min 判断 snapshot_required。第二张截图后的实际栈已经证实此路径正在运行。

## 修复优先级与验收建议

1. **发送消息的 SSE 游标与边界查询**：已有会话在发送前使用权威 bootstrap/checkpoint 游标，不能因投影路径未初始化 cursor 而从 0 重放全部历史；确认 send 与 projection-owned 状态的职责及重连逻辑。bounds 改用索引首尾查询，replay 固定有界快照 head，后续事件由 live 阶段连续衔接，避免每 256 条重新扫全部索引。覆盖新会话、已有长会话、发送前后开流、跨会话切换、重连、bootstrap 落后、空会话、非连续序号、裁剪缺口及并发追加。
2. **代码索引清理 SQL**：Source/Target 删除拆成同一事务内两次索引精确删除，或设计等价的有界批量删除；覆盖入边、出边、自引用、跨文件符号、事务回滚、零符号文件与 RemoveFiles。先在独立 PuddingCodeIndex 组件测试，再接入。生产数据上的删除验证只能用 `temp/test-out` SQLite 在线备份副本。
3. **精确索引维护**：按既定 C# FileSystemWatcher + 文件修改时间扫描方案，合并变化提示、比较持久文件指纹、形成真实变更集，再更新相应消费者及必要的语义依赖范围。Markdown 更新其全文内容，代码修改更新对应文件及受影响绑定，配置变更重新计算项目输入与覆盖集合的差异。遗漏通知、目录变更和失败通过校准与局部重试恢复，不再以“没有语言 owner/配置变化/增量失败”自动升级全仓重建。详见末尾 D。
4. **性能收敛**：待上述修复后评估批量清理/连接缓存，避免只减少日志或放慢 worker 掩盖查询放大。

交付后必须由外部控制器明确部署新 Core，再测静置、文档变更、单个代码文件变更、长会话 SSE 四种场景。对比相同时间窗口的进程读次数/字节、物理磁盘活动、索引水位与功能正确性。本任务没有执行修复或部署，不能宣称高读取已消除。

## 证据限制与收集方式

- `wpr -start FileIO -start DiskIO -filemode` 返回 Access denied / 0x80070005，未启动跟踪。因此没有文件级 ETW、物理 I/O 栈或各文件读取占比，也未终止任何已有录制。
- 进程样本与托管栈临时输出位于 `temp/test-out/io-diagnosis`，关键数值与栈已汇总在本文；按仓库卫生规则，临时输出收尾清理，不提交完整栈或数据库内容。
- 只读查询探针自己的 I/O 通过 `GetProcessIoCounters(GetCurrentProcess())` 前后差测量，未计入 PuddingAgent PID 的采样窗口。缓存、其他并发进程与在线库变化影响耗时；数值代表该次观测，不是稳定性能基准。
- 原截图监视窗口及其是否经历多次重启未知，无法把本次采样精确还原为截图当时的完整调用分布。

## 代码级修复方案（待实施）

本节是施工方案，示例用于明确算法与合同，**本报告任务尚未修改产品代码、运行修复测试或部署**。建议分为 A 边界 SQL、B SSE 回放/游标、C 索引关系删除、D 精确索引维护；D 再按其组件边界分步交付。A/C 可先各自在现有组件内独立验证；宿主接入与部署放在组件门禁之后。以下路径均相对仓库根。

### A. 将事件边界查询改为两次索引端点查找

修改 `Source/PuddingPlatform/Services/ConversationEventStore.cs` 的 `GetBoundsAsync`，保留方法签名、参数化、`EnsureTableAsync` 和返回 `EventBounds(null, null)` 的空会话行为，只替换 SQL：

```sql
SELECT
  (SELECT sequence FROM conversation_events
   WHERE conversation_id = @cid
   ORDER BY sequence ASC LIMIT 1),
  (SELECT sequence FROM conversation_events
   WHERE conversation_id = @cid
   ORDER BY sequence DESC LIMIT 1);
```

两端在**同一条语句**内读取，避免拆成两个 command 后遇到并发写入/裁剪而得到不一致的边界。不新增索引；利用已有 `(conversation_id, sequence)`。不改用 `conversation_heads` 作为 min，不缓存真实 min，不增加迁移或兼容层。既有两套同构索引的清理不与本修复混做。

在 `Source/PuddingPlatformTests/Services/` 新增边界查询测试，用产品 `Microsoft.Data.Sqlite` 创建隔离库：空会话、单事件、两个会话隔离、稀疏序号、前缀裁剪后 min 改变、并发追加/裁剪的合法快照。百万事件性能夹具只保留小 payload，比较相同缓存条件下的读次数/字节与执行工作量；不以机器相关的毫秒数作单元测试门禁。查询计划出现 `SEARCH` 本身不能证明优化，因为原扫描也是 SEARCH；需确认端点 LIMIT 提前结束，工作量不会随整段会话事件数线性增加。

### B. SSE 先确定可靠游标，再按固定上界追赶

#### B1. 前端消除“未初始化等于零”的含义冲突

主要修改：

- `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventConnection.ts`：统一开流入口与会话身份校验。
- `.../hooks/useSessionEventReplay.ts`：bootstrap 游标初始化的成功/失败合同。
- `.../hooks/useMessageSend.ts`：发送前后不直接无条件重建 SSE。
- `.../hooks/useChatState.ts`、`.../hooks/useSessionSelection.ts`、`.../hooks/sessionRuntimeCleanup.ts`、`.../hooks/useSessionEventProjection.ts`：所有游标初始化、清理、递增与跨会话切换一起调整。

游标保存为带会话身份的状态，替换仅凭数值判断的 `lastSequenceNumRef` 用法。建议形状：

```ts
type SessionEventCursorState = {
  sessionId: string | null;
  phase: 'unknown' | 'hydrating' | 'ready';
  sequence: number;
};
```

`sequence=0, phase='unknown'` 不能订阅；只有服务端 bootstrap 成功、其快照已经应用到当前会话后才能设置 ready。bootstrap 返回的真实 0 可以是合法空会话游标，不能以“值为 0”直接判失败或跳到 head。正常事件仅推进同一 session 的 sequence；410 恢复先应用替代快照，再按其 cursor 重置。切换会话时清空 readiness，禁止把 A 的较大 sequence 带给 B。

当前 `syncCompletedHistoryEventCursor` 捕获异常后返回 `[]`，调用方无法区别“成功但没有 turns”与“初始化失败”。拆出返回判别联合的游标准备方法，由现有历史同步与连接入口共同使用；失败时不能标 ready。不要继续用 turns 数量推断初始化成功。

统一新增 `ensureSessionEventStream(sessionId)` 异步入口；它负责 ready 检查、同会话 bootstrap 请求合并与连接复用。算法骨架：

```ts
// 伪代码：generation、AbortSignal 与会话身份必须贯穿所有 await。
if (hasHealthyConnection(sessionId)) return;
const request = beginConnectionRequest(sessionId);
if (!isReadyFor(sessionId)) {
  const prepared = await prepareAndApplyBootstrap(sessionId, request.signal);
  if (!prepared.ok || !request.isCurrent()) return;
}
if (!request.isCurrent() || !isReadyFor(sessionId)) return;
openSessionEventStream(sessionId, cursorStateRef.current.sequence);
```

具体约束：

1. `useMessageSend` 发送已有会话前 await ensure；游标准备失败时保持输入/待发项，不用 0 兜底。bootstrap 正在进行时不冻结整个页面。
2. POST 返回同一个 session 且连接健康时复用连接；返回另一个 session 时为该 session 单独准备游标。返回时用户已切走，则不切换页面、不替其他会话开流。
3. `useChatState` 的 effect、新建/后继/分支会话、重连、send 全部走统一入口。删除绕过准备的 `cursor: 0` 调用，或将其限定为服务端已确认并已应用的空快照；不能只凭调用点名称断言会话是新的。
4. projection-owned 页面也必须经过统一的开流决策。若该路径的消息/活动投影已完整承担实时更新，则 send 不能偷偷启用另一条原始 SSE；若仍需 SSE 承载工具、压缩、子代理事件，应明确保留这些职责并完成 cursor 准备，不能简单全部禁用。
5. 开流使用的是**已应用快照的 snapshotCursor**，不能在 POST 后直接取最新 head 跳过本轮开始事件。projection checkpoint 落后时允许补读真正的缺口，不能静默跳过。
6. 请求用 generation/session/AbortSignal 三项复检，防止 A 的迟到 bootstrap 修改 B 的游标。同 session 的准备请求只进行一次；取消不等于准备成功。

扩展已有 `useSessionEventConnection.test.tsx`、`useSessionEventReplay.test.tsx`，并补消息发送集成测试：旧会话初值 0、bootstrap 失败、A/B 交错返回、同会话 POST 前后复用、返回不同 session、投影负责主会话、新会话真实 cursor=0、410 替代快照、丢通知补偿。必须断言旧会话实际订阅的 `afterSequence`，不能只测试 ref 被赋值。

#### B2. 后端每次追赶只读到固定 head

修改 `Source/PuddingPlatform/Services/SessionEventStreamService.cs` 的 `FollowAsync`：入口读取一次 bounds，冻结 `replayThrough`；删除 replay 循环中每满 256 条重新查询 bounds/更新 head 的两行：

```csharp
var replayThrough = (await _eventStore.GetBoundsAsync(sessionId, ct))
    .MaxSequence ?? 0;
while (nextAfter < replayThrough)
{
    var batch = await _eventStore.ReadForwardAsync(
        sessionId, nextAfter, replayThrough, 256, ct);
    // 按现有逻辑逐条输出 IsReplay=true，推进 nextAfter。
    // 空批/不足一批按实际读取结果结束；不再刷新 replayThrough。
}
```

live 通知分支同样冻结本次 `drainThrough`，逐批读完该区间，不在批内刷新全局 head；本次之后追加的事件留给下一次通知/轮询追赶。保留 1 秒 durable poll 和 15 秒 heartbeat，首轮先订阅再查 head，避免在 replay→live 衔接时仅依赖一次易丢的通知。

当前 `CommittedEventSignal` 使用多个 reader 共享 Channel；不能假定每个 SSE 都收到每次广播。继续保留持久化轮询兜底。替换通知 waiter 时取消并等待旧 waiter，使用每个等待周期的关联 CTS；不要让旧的未完成 waiter继续消费新通知。取消或客户端断开时释放 CTS、停止轮询。

在 `Source/PuddingPlatformTests/Services/` 新增 `SessionEventStreamService` 测试，使用可控制 event store/signal：

- 读取 1,024 条历史需要四个主要批次，但 replay 不发生四次边界重查；所有输出都有正确 IsReplay 标志。
- 回放途中追加事件不延长初始 replay；进入 live 后仍完整输出，顺序正确、无重复。
- 只通过数据库追加且故意丢通知，轮询最终读到；两个 subscriber 都不会因共享 Channel 而永久漏事件。
- 空会话等待、稀疏序号、查询空批、取消、heartbeat、旧 waiter 清理。

不在第一批补丁里粗暴拒绝所有 cursor=0，也不拿 `head-cursor` 当事件数量，因为新会话与稀疏序号合法。若增加大规模回放保护，需用有界 LIMIT 探针判断真实缺口，并与可用快照及投影落后恢复一起设计；只返回 410 而 bootstrap cursor 始终落后会形成重连循环。

### C. 将索引图删除的 OR 拆成四个精确删除

修改 `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` 的 `RemoveSymbolGraphForFileAsync`。保留已有 symbolIds 查询和调用者传入的事务，对每个 symbol 执行：

```sql
DELETE FROM CodeReferences
WHERE WorkspaceId=$workspaceId AND ProjectId=$projectId
  AND SourceSymbolId=$symbolId;
DELETE FROM CodeReferences
WHERE WorkspaceId=$workspaceId AND ProjectId=$projectId
  AND TargetSymbolId=$symbolId;
DELETE FROM CodeRelations
WHERE WorkspaceId=$workspaceId AND ProjectId=$projectId
  AND SourceSymbolId=$symbolId;
DELETE FROM CodeRelations
WHERE WorkspaceId=$workspaceId AND ProjectId=$projectId
  AND TargetSymbolId=$symbolId;
```

四次删除和最后的文件符号删除仍在**同一事务**，取消/异常时整体回滚。自引用在 source 删除时已消失，target 再删是安全的 no-op。跨文件入边与出边按原合同都清除；其他 workspace/project 不受影响。不只按 SourceFilePath 删除，因为那会漏掉来自别的文件的入边。

先采用这个行为等价的改动，不同时更改 pooling、PRAGMA、事务粒度或全量重建算法；未来若引入批量 symbolIds/临时表，另做独立任务。保留 Source/Target 现有索引，用产品 provider 的 DELETE 查询计划确认约束进入完整 `(WorkspaceId, ProjectId, SymbolId)`，而不是仅 scope 前缀。

扩展现有 `Source/PuddingCodeIndexTests/Storage/SqliteCodeIndexStoreTests.cs` 和 `SqliteCodeIndexStoreRemoveFilesTests.cs`：多符号文件、入边/出边/自引用、同 ID 不同 scope、无关图保留、重复删除幂等、零符号文件、批内异常/取消回滚。大量无关关系夹具应证明删除代价由目标符号关联规模决定，不随整个 project 图规模重复线性增长。真实数据删除对照只在 SQLite 在线备份副本执行。

### D. 优化 Watcher + 修改时间扫描，实现精确索引维护

#### D1. 沿用既定设计，补齐已有功能

以 [统一检索设计 §10](../12_features/Agent统一检索与渐进展开工具链设计-2026-09-13.md) 为依据：C# `FileSystemWatcher` 提供及时提示，修改时间/stat 扫描补漏，持久 manifest 与内容 hash 判定实际变化，既有 worker 执行局部提交。**撤销上一版“扩展名分类 → Ignore / Incremental / 全量 Reconcile”的施工建议**；配置变化和失败不再直接映射为全仓索引任务。`Reconcile` 的含义应是核对磁盘与 manifest 后生成差异，不能等同 `IndexWorkspaceAsync`。

已检查的基础与缺口：

- `CodeIndexWatcher` 已有轻量回调、噪声过滤和有界队列；`CodeIndexMaintenanceService` 已有合并/防抖。优化这条维护链路，不另起并行 worker，也不在回调里读文件或数据库。
- `CodeIndexCalibrationService` 当前主要枚举**已索引路径**并清理消失记录，不能发现漏通知的新增文件，也没有完整的修改识别。扩展为磁盘清单与 manifest 的差异扫描，保留其删除保护和预算。
- `CodeFileRecord` 当前只有路径、语言、`LastIndexedAtUtc` 等信息；“索引时间”不是源文件修改时间，不能作为源版本。需补源指纹和各消费者提交版本。
- 全文组件已有 `FullTextChangeSet`、`MTimeComparison`、`MaintenanceCorpusScan` 和局部 `ApplyChangesAsync`：三源合流、重叠窗口、完整扫描才清理、扫描开始时间水位等机制应沿用。代码侧补齐等价合同；不让 `PuddingCodeIndex` 反向依赖 `PuddingCodeIntelligence`，也不为复用几行逻辑直接引用整个全文引擎。

Git 的可借鉴点是先比较缓存 stat，再对必要候选比较内容，而不是每次读取全部文件。Git 自身也处理同时间粒度、同大小但内容改变的 racy 情况；Pudding 使用自身 manifest，不调用 `git status` 作为真源，未提交、未跟踪和非 Git 工作区同样必须维护。参见 [Git stat 与 racy 处理](https://git-scm.com/docs/racy-git)、[git update-index 的 refresh](https://git-scm.com/docs/git-update-index)。

#### D2. 三源生成同一种变更集，指纹决定是否执行

```text
FileSystemWatcher → 合并路径提示 ─────┐
启动/周期 mtime + stat 扫描 ──────────┼→ 候选路径 → 稳定读取/hash
溢出/目录变化/深度分片校验 ───────────┘           → 内容/配置差异
                                                → 消费者与语义影响计划
                                                → 局部原子提交 + manifest
```

在代码索引合同/存储中增加持久 manifest 与维护待办。下面是**拟新增模型示意**，实际可拆为文件表和 provider 状态表，不能用一个成功标记替代所有消费者的提交状态：

```csharp
// 源状态：按 scope + 规范化路径登记；首次没有基线时必须处理。
record SourceFingerprint(DateTimeOffset LastWriteTimeUtc, long Length,
    string ContentHash); // hash 来自实际参与提取/解析的同一份内容
// 消费者状态：全文与各语言 provider 分别推进。
record AppliedFileVersion(string ProviderId, string ParserPolicyFingerprint,
    string SemanticInputFingerprint, long AppliedVersion);
// 持久维护账本另存 epoch、desiredVersion、待重试路径和成功扫描水位。
```

判定顺序与不变量：

1. **Watcher 是提示**：同路径重复 Changed 合并，处理时核对最终状态。rename 核对旧路径与新路径；目录变化校准相应子树/最近可读父目录。队列满、溢出和失去监听标记待校准，再由同一 worker 处理。
2. **扫描优先读取元数据**：枚举实际文件与持久 manifest；新路径即使 mtime 很旧也必须处理，已知路径比较 LastWriteTimeUtc/Length 等 stat。mtime 使用 `>= scanStartWatermark - overlap` 保留边界候选，逐文件 stat 差异也独立产生候选，不能仅按“大于上次索引时间”跳过。无基线/时钟回拨触发范围核对，已有指纹仍可避免重复解析。
3. **内容校验有预算**：stat 未变、没有通知、配置/解析器版本未变且不在深度核验/racy 窗口时，直接复用已提交结果；不重新读取文件正文或打开语言 workspace。Watcher 提示、stat 差异、可疑时间窗口及深度核验的路径稳定读取并计算 hash。hash 相同且语义输入相同，只刷新必要的源元数据，不改索引文档/图。
4. **mtime 不是绝对证明**：保留时间戳的同大小编辑可绕过纯 stat；收到通知仍核验内容，漏通知由有预算的周期深度核验补偿。按既定设计保留查询时有界 current 验证和 stale/partial 状态，不能声称只靠 mtime 实时发现所有更改，也不能为此每轮 hash 全仓。
5. **稳定读取再解析**：读取前后 stat、观察版本和 epoch 校验；内容在读取/解析期间改变则放弃结果、重新排队。支持语言绑定时，还校验参与解析的项目输入/依赖版本；不能 hash 磁盘新版本却提交 Roslyn 旧快照。时间戳相同的竞争由观察版本及必要内容复核补足。
6. **删除需要可证实**：完整成功扫描才将“manifest 有、磁盘未见”转成删除；失败子树/离线根保留记录并标 incomplete。扫描期间变化的路径暂缓清理、重新核对，不能把 `File.Exists=false` 或权限错误当删除。目录校准不能调用全仓语言重建。
7. **水位不掩盖失败**：成功补偿扫描使用扫描开始时刻作下一水位；扫描不完整或候选未成功提交，保留失败待办并按既有策略不推进该消费者扫描水位。Watcher 批次不推进扫描水位，文件提交版本与全范围水位分开。部分路径成功后可凭指纹跳过重复工作，失败路径退避重试。

#### D3. 从真实差异计算消费者和语义影响范围

`CompositeCodeIndexer` 使用已注册语言能力与项目归属规划更新，维护服务不另建扩展名白名单。消费者可以返回 `NotApplicable`，这是能力路由结果，不是索引成功、失败或“忽略整个文件”的同义词。语言解析失败返回有路径/项目原因的待重试结果，不自动 enqueue 全仓。

| 实际变化 | 精确更新计划 |
|---|---|
| Markdown 内容变更 | 向适用的全文消费者提交该文档更新；语言消费者无能力时 NotApplicable。若某语言工具链实际把该文档作为生成输入，沿其声明的依赖更新受影响输出 |
| 代码内容变更 | 更新所属语言文件；根据符号/签名/导入等差异及反向依赖扩大到受影响文件/编译单元。正文 hash 不变但绑定输入变了，也需重新绑定 |
| 项目/依赖/解析配置变更 | 语言侧重新评估受影响项目的源集合、引用与解析选项，比较配置/语义输入指纹，生成增删文件及重新绑定范围；全文只处理实际内容/覆盖差异 |
| 排除规则变更 | 重新核对规则作用子树，按旧/新覆盖集合差异新增或移除各消费者记录；仍覆盖且内容/语义未变的文件不重写 |
| 文件/目录 rename、删除、原子替换 | 根据最终磁盘事实和旧 manifest 清理旧路径、更新新路径及必要的路径/导入依赖，不要求每个子文件都有通知 |
| 丢通知、启动恢复或 Watcher 溢出 | 执行 metadata 清单核对，派发新增/修改/可证实删除及尚未提交的语义待办；不重新解析所有未变文件 |

语言工具链声明项目配置输入与依赖关系，不能以“某类配置扩展名”直接选全 scope。C# 的 `Directory.Build.props` 等确实可能影响多个项目，需由 MSBuild 评估给出影响范围；配置内容变化但有效输入未变可不重新绑定。类型签名变化可能让源文件未改的引用方失效，不能把“精确”误解为只处理发生 Changed 的路径。

若某个语言 provider 暂时只能保证**受影响项目**的语义正确，明确返回项目级计划、原因和实际成本；该项目内文件可能全部受影响。这是能力边界，不能静默当成精确单文件或扩大到无关项目/全文。初次建立索引、不可兼容 schema/损坏索引重建仍走明确的初始化/重建操作，与日常补漏分开；普通失败不能借此退回全仓。

#### D4. 同一解析批次与原子替换，避免“增量”重复打开全工程

`RoslynCSharpIndexer.IndexFileAsync` 当前每次打开 workspace，且先 `ClearSymbolsForFileAsync`，之后分别 upsert 文件/符号/关系/引用。即使只调用逐文件 API，仍可能反复加载工程，并在失败时丢失旧结果；需要优化这条功能链路：

- 新增语言侧批量更新接缝：按项目/配置版本复用一个有界 Roslyn workspace/编译快照，同批候选统一装载变更；无候选的 metadata 扫描不打开 MSBuild。项目评估变化时刷新相应快照，资源由既有维护所有者管理和释放。
- 在内存中完成稳定内容提取和影响规划，再通过 `ICodeIndexStore` 的拟新增 `ReplaceFilesAsync` 事务接缝提交文件记录、符号、引用/关系及该消费者已应用指纹。事务失败保留旧完整产物，标 stale 并重试；不能提前 clear 后再提取。实现只依赖组件合同，不将 Roslyn 类型引入 CodeIndex。
- 精确替换要区分引用/关系的**所有权**与目标依赖：重建该文件拥有的出边；稳定符号仍存在时保留其他文件拥有的有效入边，移除/改变目标符号则同步使依赖方失效并安排重新绑定。C 节拆 OR 可优化现有删除合同，但“删掉所有入边后只写回本文件出边”不能作为新精确替换合同。实际文件删除仍清除关联图并修复依赖，所有图查询都有 scope+索引约束。
- 全文继续使用已有局部提取 → update/delete → commit → reader 刷新链路；SQLite 与 Lucene 分别提交，各自推进 provider 水位，不声称跨存储原子事务。失败侧幂等重试，已成功侧不反复重建。
- 执行期间新变化推进 desiredVersion/dirtyAgain；提交只确认捕获的版本，较新变化继续补跑。多批语义更新完成前保持受影响范围 stale，必要时使用既定 generation 发布机制，不能把单个批次完成当整个项目 current。

建议施工顺序：**源 manifest/变更判定纯逻辑 → 完整清单校准与持久待办 → store 原子替换 → 语言批量/依赖计划 → 既有维护链路接入**。各阶段先在 `PuddingCodeIndexTests`、`PuddingFullTextIndexTests`、`PuddingCodeIntelligenceTests` 对应边界独立验证，接入前维护相关子项目 code_map；不抽取反向依赖组件、不保留旧分类补丁作为新方案的终态。

#### D5. 验收针对真实索引结果和工作量

| 场景 | 必须观察到的后置条件 |
|---|---|
| 稳定基线、无修改的普通扫描 | 无内容解析、无索引文档/图改写、不开语言 workspace；允许有预算的 stat 枚举与账本写入，深度核验单独计量 |
| 一条 Markdown 修改 | 正文搜索结果更新；只处理该文档及确有声明的依赖，不能以“没有语言 owner”失败或全仓重建 |
| 一个代码正文/签名修改 | 符号与引用结果正确；受影响绑定完整更新，无关项目不重建；重复保存相同内容不重复解析 |
| 项目配置修改 | 有效输入/覆盖差异决定更新范围，验证无效配置变动不重写、共享配置影响多个项目时不漏更新 |
| 漏掉所有 Watcher 通知 | 扫描仍发现新增、修改、删除；新文件旧 mtime、mtime 相等边界、回拨均覆盖 |
| 同大小同 mtime 内容修改 | 有通知时内容核验发现；无通知时深度预算扫描最终发现，发现前 current 验证不误报精确新鲜 |
| rename/目录移动/删除重建 | 旧路径命中消失、新路径可检索，跨文件引用无悬挂；最终重新出现的文件不被 sweep |
| 读取/解析失败、扫描不全、提交取消 | 旧完整结果保留且可见 stale，失败范围重试、不错误删除、不自动全仓重建 |
| 解析期间再次修改、崩溃重启 | 旧任务不确认新版本；持久待办/启动校准恢复，索引与已应用指纹一致 |

用可计数的文件访问/语言 updater/store 接缝断言读正文、解析文件、工程装载、图删除范围和 provider 提交次数；再在隔离真实索引验证新增/修改/删除后的搜索与引用结果。不能只断言 scheduler 没被调用或把事件队列清空当精确更新完成。

### 交付、部署与验收

| 原子任务 | 独立门禁 | 产品验证 |
|---|---|---|
| A 边界 SQL | Platform 隔离 SQLite 功能与读取成本测试 | 长会话 bounds 查询不再读完整事件索引 |
| B 游标与 SSE | Platform 事件流测试；前端 pnpm/Jest 连接、回放和发送测试 | 旧会话首次发送、第二次发送、刷新、断线重连无零游标全回放，且事件不丢失 |
| C 图删除 | PuddingCodeIndex 独立 store/事务测试，真实数据备份副本对照 | 单文件代码变更及必要全量索引无逐符号全 scope 扫描 |
| D 精确维护 | manifest/stat/hash 判定、三源变更集、原子替换、语言依赖计划与故障恢复的组件门禁 | 文档、代码、配置均按真实差异和受影响范围更新；漏通知扫描补足，无关文件不重复解析/改写 |

本次仅补文档，不递增前端版本。实施 B 时按当时 `package.json` 版本递增修订号，不在方案里硬编码版本；用 pnpm 构建，将产物部署至新 Core 的 `wwwroot/admin`，核对页角版本/哈希。A/C/D 不改前端版本。

构建/测试输出隔离到 `temp/build`、`temp/test-out`；涉及 Desktop 的 build/test/publish 串行，使用约定 `--artifacts-path temp/build/recovery`，先 restore/build，再同目录 `--no-restore`。已有组件修复在各自边界先测，不为本方案抽新宿主依赖或重构业务。每个原子任务更新受影响 code_map、写日志、精确暂存并独立提交。

外部控制器部署到明确的新构建后，固定同一长会话与采样区间，分别验证空闲 60 秒、发送短消息、刷新/重连、改一个 Markdown、改一个代码文件、改一个项目配置，以及 D5 的遗漏通知与失败恢复。先用隔离 DataRoot/备份数据完成正确性验收，再做用户开发实例的受控性能复核；避免 dev-up 与 Desktop 同时持有同 DataRoot。

验收同时收集按文件的 platform/code-index 读取、进程增量、物理磁盘增量、SSE cursor/replayCount、stat/hash/解析文件数、项目装载数、影响计划原因及各 provider 提交水位。目标是消除**随全部历史/全部关系量增长的重复扫描**，同时证明真实变化没有遗漏；不能承诺每次聊天绝对固定字节数，真实补偿缺口、模型上下文、工具读取仍会有必要 I/O。未拿到文件级跟踪时单独列出该证据缺口，不能用进程总读取量证明某个数据库的改进。
