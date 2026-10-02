---
title: PuddingAgent 高磁盘读取诊断
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: 发送消息后的零游标 SSE 回放和边界扫描，以及后台代码索引逐符号关系清理，共同造成读取放大。
categories: [docs, reports]
tags: [diagnostics, sqlite, code-index, disk-io]
related_docs: [Docs/08_how_debuge/05-常见症状.md]
related_files: [Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs, Source/PuddingCodeIndex/Services/CompositeCodeIndexer.cs, Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs, Source/PuddingCodeIntelligence/CSharp/RoslynCSharpIndexer.cs, Source/PuddingPlatform/Services/ConversationEventStore.cs, Source/PuddingPlatform/Services/SessionEventStreamService.cs, Source/PuddingPlatformAdmin/src/pages/chat/hooks/useMessageSend.ts, Source/PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventConnection.ts]
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
3. **无关文件导致全量重建**：区分“不属于任何支持语言的普通文件”与“支持语言但索引失败/项目配置变更”。Markdown 等普通文档变更不应升级全仓语言索引；项目文件、依赖配置、目录变更等仍需保留适当 reconcile。需覆盖混合语言、rename/delete 和真正失败的补偿路径。
4. **性能收敛**：待上述修复后评估批量清理/连接缓存，避免只减少日志或放慢 worker 掩盖查询放大。

交付后必须由外部控制器明确部署新 Core，再测静置、文档变更、单个代码文件变更、长会话 SSE 四种场景。对比相同时间窗口的进程读次数/字节、物理磁盘活动、索引水位与功能正确性。本任务没有执行修复或部署，不能宣称高读取已消除。

## 证据限制与收集方式

- `wpr -start FileIO -start DiskIO -filemode` 返回 Access denied / 0x80070005，未启动跟踪。因此没有文件级 ETW、物理 I/O 栈或各文件读取占比，也未终止任何已有录制。
- 进程样本与托管栈临时输出位于 `temp/test-out/io-diagnosis`，关键数值与栈已汇总在本文；按仓库卫生规则，临时输出收尾清理，不提交完整栈或数据库内容。
- 只读查询探针自己的 I/O 通过 `GetProcessIoCounters(GetCurrentProcess())` 前后差测量，未计入 PuddingAgent PID 的采样窗口。缓存、其他并发进程与在线库变化影响耗时；数值代表该次观测，不是稳定性能基准。
- 原截图监视窗口及其是否经历多次重启未知，无法把本次采样精确还原为截图当时的完整调用分布。
