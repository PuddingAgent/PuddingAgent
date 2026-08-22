# 遥测、调试数据自动过期与 Web 存储管理设计方案

> - 状态：**Proposed（设计完成，尚未实现或验收）**
> - 日期：2026-08-20
> - 决策入口：[ADR-076 遥测与调试数据保留及 Core 存储管理](../07架构/91ADR-076遥测与调试数据保留及Core存储管理ADR.md)
> - 适用范围：`pudding_platform.db` 中的非关键遥测/性能/调试数据，以及 Core 日志和可重建派生数据
> - 产品入口：`PuddingPlatformAdmin` Web 管理端 `/storage`
> - 明确排除：本方案不新增 Desktop 存储管理职责，不修改源码、配置、数据库或 `D:\data`

## 1. 目标与结论

本方案让 PuddingAgent 自行控制高频遥测与调试数据的增长，同时允许管理员按“数据类型 + 时间”预览并清理非关键数据。

目标架构固定为：

1. **Core 拥有全部存储语义和写操作**。数据库分析、自动过期、清理预览、清理作业和策略持久化都由 Core 完成。
2. **Web Admin 提供管理页**。入口位于 `PuddingPlatformAdmin`，Desktop 继续只承担启动器、Core 进程主管和 WebView2 容器职责。
3. **用户只选择语义数据类型，不选择表名、路径或 SQL**。所有可清理对象来自代码内置白名单。
4. **自动清理和人工清理共用一个维护协调器、一个串行写队列和同一套批处理执行器**，不再存在两个独立 SQLite 清理 writer。
5. **关键事实默认且强制受保护**。会话正文、执行事实、计费账本、记忆、任务、配置和用户文件不进入“一键清理”。
6. **在线清理只释放数据库可复用页，不自动执行全库 `VACUUM`**。页面必须分别显示“逻辑删除”“库内可复用空间”和“已归还操作系统空间”。
7. **空间统计是异步、增量、近似的快照，不是用户点击后触发的一次性精确扫描**。页面先显示最近快照，后台按时间片、游标和采样逐步刷新；界面只显示约占空间、更新时间和刷新状态，不引入预测置信度概念。

## 2. 当前实现证据与主要缺口

| 现状 | 设计判断 |
|---|---|
| `RetentionPruningService` 已按 100 行小批、250ms 批间让步、单表单轮 200 批运行，默认关闭 `VACUUM` | 保留这组在线安全基线，并演进为唯一维护协调器下的自动触发器 |
| 自动保留当前覆盖 `telemetry_metric_events`、`runtime_activity` 和归档后的 `conversation_events` | `context_layer_metric_events` 未进入自动过期；证据保留和非关键遥测清理需要拆成不同安全等级 |
| `StorageMaintenanceService` 已支持遥测、运行活动、重复索引和失效代码索引的 Preview/Execute | 人工清理与后台保留服务使用不同的进程内锁，仍可能同时写 `pudding_platform.db` |
| 人工诊断删除当前使用 5,000 行批次，并可选择在线 `VACUUM` | 批次过大且在线全库压缩会放大 writer 锁风险；目标方案统一为小批并取消在线自动 `VACUUM` |
| Preview 十分钟有效，但 Execute 同步完成 | 大量清理缺少可取消、可观察、可恢复的作业模型 |
| 当前清理请求按语义 ID，基础安全方向正确 | 类型粒度仍偏粗：遥测与上下文指标被合并，无法分别设置保留期；调试 JSON 也不能单独过期 |
| 当前保留配置使用物理表名 | 管理 API 和配置不应暴露物理表结构，应改用稳定语义类型 ID |
| Desktop 已有历史 Storage 页面，Web Admin 没有 `/storage` | 新功能只落到 Core + Web Admin；Desktop 历史页面不扩展 |
| 当前数据库清理 Preview 会对超期行执行完整 `COUNT(*)`，文件页也容易把“重新扫描”理解成完整遍历 | 百万级历史垃圾下可能长时间占用后台读线程和 SQLite 资源；目标方案改为缓存快照 + 有界增量估算，刷新和 Preview 都不得等待全量精确统计 |

最重要的事故约束是：同一 `pudding_platform.db` 只能有一个在线维护写协调器。此前两个保留服务同时运行曾造成 `SQLite Error 5: database is locked`，并使聊天受理在 `BeginTransactionAsync` 阶段失败。因此本方案不能再增加第二个 BackgroundService 或让人工 Execute 绕过统一协调器。

## 3. 数据安全分级

### 3.1 四个安全等级

| 等级 | 定义 | 自动过期 | 人工按时间清理 | 例子 |
|---|---|---:|---:|---|
| `Disposable` | 仅用于短期性能或 Debug 定位，删除不改变业务事实 | 是 | 是 | 原始遥测、运行活动、Debug JSON、普通日志 |
| `Derived` | 可从权威事实或源码重建 | 可选 | 是 | 失效代码索引、冗余索引、聚合缓存 |
| `Evidence` | 回放、审计、计费或恢复需要的事实 | 仅走独立证据保留策略 | 否 | conversation/session event、LLM 用量账本、任务事件 |
| `UserData` | 用户创作或显式配置的数据 | 否 | 否 | ChatMessages、记忆、Agent、Workspace、配置、附件 |

`Evidence` 并不等于永不归档，但它不能被标成“非关键数据”供普通 Storage 清理。若未来需要裁剪，必须有独立 ADR、归档/恢复合同和审计门禁。

### 3.2 强制保护清单

以下对象必须显示在 Storage 页面，但状态恒为“受保护”，不能勾选：

- `ChatMessages`、`room_messages` 和用户正文；
- `session_event_log`、`conversation_events`、conversation head/checkpoint/catalog；
- conversation turn、execution run、command、control message、delivery/outbox 等执行事实；
- `llm_gateway_usage_events`：Provider 返回的计费事实；
- `TokenUsageEvents`：会话、角色和上下文归因账本；
- `TokenUsageStats`：长期聚合账本；
- Workspace Task、Task Event、Assignment、Orchestration Run/Event；
- Agent、Skill、Memory、Knowledge、配置、密钥、用户与权限；
- 源代码、用户文件、附件和不可重建 Artifact。

当前 `conversation_events` 的“先归档、后按保留期删除”属于独立证据保留机制，不进入本方案的非关键清理选择器。后续实现可以让它也经过同一个维护协调器排队，但 UI 仍只显示其策略状态，不提供一键选择。

## 4. 语义数据类型目录

Core 内置 `StorageDataClassCatalog`。目录项包含稳定 ID、展示名称、安全等级、物理对象、时间列、允许动作、默认/最小/最大保留期、是否需要先聚合、依赖索引和空间统计方式。API 只返回目录投影，不接受客户端自定义目录项。

### 4.1 首批目录

| 语义类型 ID | 物理范围 | 处理方式 | 默认保留 | 自动 | 人工 |
|---|---|---|---:|---:|---:|
| `diagnostics.debug-payload` | `telemetry_metric_events.debug_json`、`runtime_activity.metadata_json` | 超期后字段置空，不先删除整行 | 7 天 | 是 | 是 |
| `diagnostics.telemetry-raw` | `telemetry_metric_events` | 先写入小时聚合，再删除超期原始行 | 14 天 | 是 | 是 |
| `diagnostics.context-layer-raw` | `context_layer_metric_events` | 先写入日聚合，再删除超期原始行 | 14 天 | 是 | 是 |
| `diagnostics.runtime-activity` | `runtime_activity` | 删除组件/操作级运行活动明细 | 14 天 | 是 | 是 |
| `diagnostics.logs.verbose` | Core 产生的 Debug/Information 日志文件 | 按文件签名与最后写入时间删除 | 7 天 | 是 | 是 |
| `diagnostics.logs.error` | Error 及以上日志和结构化错误日志 | 按文件签名与最后写入时间删除 | 30 天 | 是 | 是 |
| `diagnostics.rollups` | 遥测小时聚合、上下文日聚合 | 删除超期聚合数据 | 365 天 | 是 | 是 |
| `code-index.obsolete-scopes` | Covered/Removed 或长期失效的代码索引作用域 | 删除可重建派生行，不删除源码 | 不按时间自动 | 否 | 是 |
| `storage.redundant-indexes` | 代码确认等价或失效的 SQLite 索引 | 重新校验定义后删除 | 不适用 | 否 | 是 |

默认值是“平衡”预设，不是写死的产品上限：管理员可在目录允许范围内设置 1–365 天；聚合数据允许 30–1,825 天。`0` 永远不是“立即删除”，也不作为合法保留期。禁用自动清理使用显式 `enabled=false`。

### 4.2 为什么单独清理 Debug 字段

`telemetry_metric_events` 同时保存可聚合数值和最多 16KiB 的 `debug_json`。如果只能整行删除，用户无法短期保留 Debug 详情、较长期保留性能趋势。先把 `debug_json`/`metadata_json` 独立置空，可以显著降低内容体积，同时保留时间、类别、名称、状态、耗时和错误码等低基数字段。

字段清空只增加 SQLite freelist，不保证数据库文件立即缩小；页面必须显示“转为库内可复用空间”，不能显示为“已释放到磁盘”。

### 4.3 长期趋势聚合

删除原始性能数据前先幂等聚合：

- `telemetry_metric_rollups_hourly`：UTC 小时、category、name、status、source，以及 count、error count、duration sum/min/max、numeric sum/min/max；
- `context_layer_metric_rollups_daily`：UTC 日期、provider/model、layer name、change reason，以及 token/raw/gzip/cache hit/cache miss 的 sum/count；
- 高基数的 session、trace、execution、user、content hash 和任意 Debug JSON 不进入聚合；
- 聚合使用唯一键 + upsert，作业重试不能重复计数；
- 只有聚合提交成功后，才允许删除对应高水位以内的原始行。

如果第一期不实现聚合，则 `diagnostics.telemetry-raw` 和 `diagnostics.context-layer-raw` 的默认自动删除必须保持关闭，不能在没有长期趋势替代物时静默丢失分析能力。

## 5. 自动过期机制

### 5.1 增量空间清单与估算快照

空间总览由 `StorageInventorySampler` 持续维护 `StorageInventorySnapshot`，而不是由页面请求同步扫描：

```text
轻量文件/SQLite 元数据 ─┐
sqlite_stat1/有界页采样 ├─> 50–100ms inventory slice ─> cursor
目录分片枚举           ┤                           └─> snapshot merge
历史快照               ┘                                  └─> GET overview
```

快照至少包含：

- `snapshotId`、`capturedAtUtc`、`updatedAtUtc`、`schemaVersion`；
- 总数据库主文件、WAL、日志和 DataRoot 已知分类的逻辑大小；
- 每个语义数据类型的 `estimatedBytes`、`estimatedRows/files`、最早/最晚时间估算；
- `isRefreshing`、当前阶段、最近成功 slice 和下次计划时间；
- 与上一快照相比的增长/下降趋势。

统计规则：

1. 数据库文件、WAL 文件和卷可用空间使用廉价元数据读取，可视为精确值；
2. SQLite 总页数、空闲页使用 `page_count/page_size/freelist_count`，不遍历业务行；
3. 各表/各类型空间优先使用已有 `sqlite_stat1`、时间索引的首尾键和有界 B-tree/page 样本；禁止为了页面调用 `ANALYZE`；
4. `dbstat` 只作为后台可取消的分片采样器，每个 slice 有页数/时间预算和 resume cursor，绝不在 HTTP 请求中全表执行；
5. 没有 `dbstat` 时，用有界行样本估算平均 payload 大小，再结合统计行数/索引区间计算；界面统一以“约”标识该分类占用；
6. 文件目录按顶层分类和目录 cursor 分片枚举，每个 slice 限制文件数与时间，不跟随 reparse point；
7. 每条 SQL 和每次目录枚举本身必须通过索引、`LIMIT`、页数或文件数做到结构性有界；不能只是把全扫移到线程池再依赖 Cancellation；
8. 每个 slice 默认 50–100ms，之后主动让步；单轮后台刷新默认最多 2 秒，未完成留待下一轮；
9. 全 DataRoot 只有一个合并型 sampler reader；检测到前台请求、维护 writer 或 SQLite busy 时立即暂停，不并行启动“每类一个扫描线程”；
10. 新结果只原子合并受影响分类；某个分类失败不清空上一份有效快照；
11. 快照历史最多每小时保留一个点、保留 90 天，用于趋势图；快照自身必须有界轮转；
12. `GET overview` 只读内存/持久快照，不触发扫描、`COUNT(*)`、`dbstat` 或目录遍历。

用户点击“刷新估算”只提交一个 refresh request；如果后台已经刷新，则合并请求并提高优先级，不创建第二个扫描器。API 立即返回 `202`，页面继续使用旧快照并渐进显示新分类结果。

### 5.2 自动清理调度语义

- Core 启动后等待 60 秒，但不会“每次启动必清理”；根据持久化 `lastCompletedAtUtc` 判断是否到期；
- 默认每 24 小时检查一次，并加入小幅随机抖动，避免多个环境同一时间开始；
- 用户修改策略后只重新计算下一次计划，不在保存配置的 HTTP 请求中直接清理；
- 低磁盘空间只会提前触发“已经超过用户保留期”的数据，不会擅自缩短保留期；
- Core 不 Ready、前台写压力高、已有维护作业、Schema 未就绪或 DataRoot 安全校验失败时不运行；
- 进程重启后从已提交 cursor 继续，不能从零重复聚合或跳过未处理数据。

### 5.3 单写者执行模型

```text
自动调度器 ─┐
            ├─> StorageMaintenanceCoordinator
Web Preview ┤       ├─ 语义目录/策略校验
Web Execute ┘       ├─ 单进程串行作业队列
                    ├─ DataRoot 级独占维护锁
                    ├─ 小批 SQLite/File 执行器
                    └─ durable job snapshot + progress events
```

约束：

1. 只有 `StorageMaintenanceCoordinator` 能取得写执行器；
2. 现有自动 `RetentionPruningService` 和人工 `StorageMaintenanceService` 在实现时必须合并到该协调器，不能只是各自再加一个 `SemaphoreSlim`；
3. DataRoot 级独占锁由 Core 持有，用于防止误启动的第二个 Core 或维护进程并行执行；锁崩溃后由 OS 自动释放；
4. 分析查询可以并行，但不得在页面刷新时运行全库 `dbstat`；
5. 人工作业优先于到期自动作业，但不能抢占正在提交的单个小事务；取消只发生在批次边界。

### 5.4 在线安全预算

默认在线合同沿用已验证基线：

- 每批最多 100 行；允许按表和设备能力在 50–500 之间配置，但不能由客户端直接指定；
- 批间至少让步 250ms；
- 单目标单轮最多 200 批，即默认最多 20,000 行；
- 单个事务目标时长小于 100ms，超过预算记录 Warning 并降低下一批大小；
- 维护连接使用短 busy timeout，遇到 `SQLITE_BUSY/LOCKED` 立即让步并延迟重试，不等待 30 秒占住维护调用；
- 每次在线 slice 默认最多运行 30 秒；未完成部分写入 cursor，稍后续行；
- 自动和人工作业都遵守相同预算，人工点击不能切换为大事务模式。

SQLite 批量删除继续使用静态白名单生成的形式：按 `(timestamp, id/rowid)` 顺序选择有限主键，再用这些主键删除。配置和 API 的任何字符串都不能拼接为表名、列名或 SQL 片段。

### 5.5 磁盘空间回收

在线自动作业：

- 不执行 `VACUUM`；
- 不执行阻塞式 `wal_checkpoint(TRUNCATE)`；
- 可在低写压力时尝试 `wal_checkpoint(PASSIVE)`，失败只记录状态；
- 将删除得到的页计为 `ReusableBytes`，供 SQLite 后续写入复用。

“把字节归还操作系统”的数据库压缩是另一个维护模式。第一期页面只展示说明和建议，不提供在线压缩按钮。未来若实现，必须在 Core 完全停止、磁盘空间足够且由外部维护入口执行，不能把全库 `VACUUM` 放回在线清理路径。

## 6. 用户按数据类型和时间清理

### 6.1 选择模型

用户可以：

- 多选 `Disposable`/允许人工清理的 `Derived` 数据类型；
- 选择“早于 7/14/30/90 天”或自定义截止日期；
- 对每个类型使用同一截止时间，或进入高级模式分别设置；
- 选择是否包含普通日志、Error 日志、可重建代码索引；
- 查看受保护数据，但不能选择；
- 选择“只清 Debug 详情”而不删除完整性能行。

服务器把“早于 N 天”在生成预览时转换为固定的 `cutoffUtc`。删除条件统一为 `eventTime < cutoffUtc`，等于截止时间的行保留；UI 按用户时区显示，API 只传 UTC。

### 6.2 Preview

`Preview` 是一个有界估算，不以“先完整数完再允许清理”为目标。它必须返回：

- `previewId`、`catalogVersion`、`policyRevision`、创建/过期时间；
- 固定 `cutoffUtc`、每个目标的物理动作（清字段/删行/删文件/删索引）；
- `estimatedCandidateRows/files`、最早/最晚时间、估计逻辑字节、估计可复用页；
- `inventorySnapshotId` 和统计更新时间；
- 会保留的聚合数量；
- 保护声明、不可逆说明和统计 Warning；
- 是否存在活跃索引任务、数据库锁、Schema 缺失或空间统计不可用。

Preview 只执行有索引的首尾定位、有限样本和固定时间预算，不运行全量 `COUNT(*)`。即使历史垃圾有上亿行，Preview 也应在短时间内返回估算。用户确认的是“语义类型 + 固定截止时间 + 作业安全预算”，不是一个精确候选数量；Execute 可以发现与估算不同的实际数量，并在页面持续修正。

每个 Preview 同时固化 `maxRowsPerJob/maxFilesPerJob/maxRuntimeSlices`。实际发现量超过预算或显著超出估算上界时，作业进入 `needs_confirmation`，保留 cursor，用户确认后继续，避免一次误操作处理无界数据。

Preview 有效期十分钟，只保存在 Core 内存；Core 重启、策略 revision 改变、目录版本改变或到期后必须重新预览。预览后的旧数据仍按固定 `cutoffUtc` 判断；任何实际变化都受已确认的作业预算限制。

### 6.3 Execute 与作业状态

执行不在 HTTP 请求中同步跑到底：

1. `POST Execute` 消耗一次 `previewId`，创建 durable `jobId` 并立即返回 `202 Accepted`；
2. 作业状态为 `queued/running/paused_busy/needs_confirmation/cancelling/completed/partial/failed/cancelled`；
3. 页面按 `jobId` 轮询或 SSE 读取进度；
4. 每批提交 discovered/processed/deleted/cleared/skipped/failed/reusable bytes、remaining estimate 和 cursor；
5. 单批失败不会回滚此前已成功批次；作业终态明确显示部分成功；
6. 取消只阻止新批次，当前事务正常结束；
7. 清理完成后重新分析 DataRoot 和数据库，不用删除行数推测文件缩小量。

维护作业事实不写入正在被大规模清理的 `pudding_platform.db`。建议使用 `<DataRoot>/maintenance/storage/jobs/<jobId>/job.json` 原子快照和有界 `events.jsonl`；只记录语义 ID、计数、时间、错误码和耗时，不记录 SQL、Token、用户正文或 Debug payload。完成记录保留 90 天且最多 1,000 个作业，由同一协调器安全轮转。

## 7. Web Admin 存储管理页

### 7.1 路由与产品边界

- 路由：`/storage`；
- 前端：`Source/PuddingPlatformAdmin/src/pages/storage/`；
- 权限：仅 `admin`；
- 数据来源：同源 Core API；
- Desktop：不新增 API Client、ViewModel、XAML 页面、磁盘扫描或清理编排。

现有 Desktop Phase 1B-S 页面属于历史实现，本方案不扩展它。是否隐藏或退役旧入口应作为单独的 Desktop 整理任务，不阻塞 Web Storage 上线。

### 7.2 页面信息架构

```text
存储空间                                           [重新扫描]
Pudding 数据约 20.9 GB   更新于 19:30:28   状态：后台空闲   [刷新估算]
数据库主文件 / WAL / 库内可复用页 / 日志 / 其他                [策略设置]

分类占比（圆环/横向堆叠图）           近 30 天存储趋势（堆叠面积图）
● 遥测 31%  ● Debug 18%              日期 | 遥测 | Debug | 日志 | 其他
● 运行活动 14% ● 日志 8%             08-01 ───────────────── 08-20
● 受保护事实 25% ● 其他 4%

报表摘要
类型 | 约占空间 | 占比 | 约记录数 | 7日增长 | 保留策略 | 更新于

自动管理
状态：已开启   上次结果：完成   本次删除 120,341 行
Debug 详情 7天 | 性能原始数据 14天 | 上下文指标 14天 | Error 日志 30天

可清理数据                                           [预览所选]
[ ] Debug 详情              1.2 GB   2,301,114 项   最早 2026-07-01
[ ] 原始性能遥测            3.4 GB   1,730,155 行   最早 2026-06-12
[ ] 上下文/缓存指标         2.1 GB   1,067,442 行   最早 2026-06-12
[ ] 运行活动明细            890 MB   1,067,442 行   最早 2026-07-03
[ ] 普通日志                402 MB   5,366 文件
截止时间：[早于 14 天 ▼]

受保护数据
会话与执行事实 | LLM 计费/Token 账本 | 记忆 | 任务 | 配置       [受保护]

清理作业
时间 | 触发来源 | 数据类型 | 状态 | 已处理/总数 | 库内可复用 | 详情/取消
```

页面使用 Admin 现有主题 Token，支持亮/暗色，不复制 WPF 控件或 Desktop ViewModel。用户提供的截图可作为信息层级参考，但实现应遵循 Web Admin 壳层和异步路由加载边界。

### 7.3 非阻塞刷新与报表

- 首屏从缓存 `StorageInventorySnapshot` 立即渲染；没有快照时显示可操作的骨架页和“正在后台估算”，不挂起整个路由；
- “刷新估算”调用异步 refresh API 后立即恢复按钮，不用全屏 Spin 等待后台完成；
- 页面按 snapshot/event 增量更新分类，不因一类仍在采样而清空其他图表；
- 分类图使用圆环图或横向堆叠条，必须同时提供图标、颜色、文字标签、估算字节和占比，不能只靠颜色表达；
- 趋势报表读取有界历史快照，默认 30 天，可切换 7/30/90 天；数据点按小时/天聚合，不把百万行原始数据传给浏览器；
- 表格展示类型图标、估算符号 `≈`、更新时间和“估算中/已更新/暂不可用”状态；不显示置信度或覆盖率；
- 图表和报表使用动态 import，仅在 `/storage` 路由加载；大量作业历史使用分页/虚拟化；
- SSE/轮询只传变更后的 snapshot summary，前端对相同 revision 短路，避免重复 React commit；
- 用户离开页面时取消浏览器订阅，但不取消 Core 的共享增量估算任务。
- 后台估算期间筛选器、图表 tooltip、滚动、切页和清理策略编辑必须保持响应；不得用全屏遮罩锁住页面。

### 7.4 关键交互

- 首屏只读缓存 overview，不跑 `COUNT(*)` 或全库 `dbstat`；数据库明细按需展开；
- 自动策略以独立 Drawer/Modal 编辑，保存前显示每类最小/最大范围；
- 清理按钮先 Preview，确认框列出每种动作和受保护声明；
- Preview 候选为 0 时不进入确认；
- 作业运行期间页面可以关闭，重新打开后按 `jobId` 恢复；
- 允许取消，但显示“当前小批事务完成后停止”；
- 清理后若文件大小不变，明确解释“空间已进入 SQLite 可复用页”；
- 错误显示稳定 `errorCode` 与 `errorId`，不只显示通用 HTTP 失败。

## 8. Core API 合同

目标 API：

```text
GET  /api/admin/storage/overview
GET  /api/admin/storage/data-classes
POST /api/admin/storage/inventory/refresh
GET  /api/admin/storage/inventory/refresh/{refreshId}
GET  /api/admin/storage/inventory/events
GET  /api/admin/storage/retention-policy
PUT  /api/admin/storage/retention-policy
POST /api/admin/storage/cleanup/previews
POST /api/admin/storage/cleanup/jobs
GET  /api/admin/storage/cleanup/jobs
GET  /api/admin/storage/cleanup/jobs/{jobId}
POST /api/admin/storage/cleanup/jobs/{jobId}/cancel
GET  /api/admin/storage/cleanup/jobs/{jobId}/events
```

关键请求示意：

```json
{
  "targetIds": [
    "diagnostics.debug-payload",
    "diagnostics.runtime-activity"
  ],
  "olderThanDays": 14
}
```

规则：

- `olderThanDays` 与 `cutoffUtc` 二选一；
- 只接受目录中 `manualCleanupAllowed=true` 的 ID；
- `GET overview` 只返回最近快照；`POST inventory/refresh` 合并重复请求并立即返回 `202`；
- Preview 的 candidate/bytes 是约数，不能通过查询参数要求同步精确扫描；
- `PUT policy` 必须携带 `expectedRevision`，使用 CAS 防止两个页面互相覆盖；
- Execute 只接受未消费、未过期的 `previewId` 和幂等 `requestId`；
- 新 Web API 只使用登录态 Admin JWT；Desktop ControlToken 不属于目标架构；
- 400/401/403/404/409/410/423/429 返回统一 ProblemDetails 和稳定错误码；
- API 永不返回完整本机绝对路径、SQL、用户正文、Debug JSON、Token 或密钥。

现有 `/api/admin/storage/databases` 可在实施期内部重用分析器，但目标合同应收敛为上述语义 API，不为开发阶段旧 DTO 增加长期兼容层。

## 9. 配置合同

用户策略写入 `<DataRoot>/config/system.json`，发布包 `appsettings.json` 只提供安全默认值。Core 通过 Admin API 校验并原子写入配置，Web 不直接编辑文件，策略不存业务数据库。

```json
{
  "storageManagement": {
    "policyRevision": 3,
    "automaticCleanup": {
      "enabled": true,
      "runIntervalHours": 24,
      "startupDelaySeconds": 60,
      "maxSliceSeconds": 30,
      "batchSize": 100,
      "batchDelayMs": 250,
      "maxBatchesPerTargetPerSlice": 200,
      "targets": {
        "diagnostics.debug-payload": { "enabled": true, "retentionDays": 7 },
        "diagnostics.telemetry-raw": { "enabled": true, "retentionDays": 14 },
        "diagnostics.context-layer-raw": { "enabled": true, "retentionDays": 14 },
        "diagnostics.runtime-activity": { "enabled": true, "retentionDays": 14 },
        "diagnostics.logs.verbose": { "enabled": true, "retentionDays": 7 },
        "diagnostics.logs.error": { "enabled": true, "retentionDays": 30 },
        "diagnostics.rollups": { "enabled": true, "retentionDays": 365 }
      }
    },
    "inventorySampling": {
      "enabled": true,
      "refreshIntervalMinutes": 30,
      "sliceBudgetMs": 100,
      "maxFilesPerSlice": 1000,
      "maxPagesPerSlice": 2000,
      "snapshotHistoryDays": 90
    }
  }
}
```

容错必须 fail closed：

- 缺少配置项使用代码安全默认值；
- 缺失数字不能解析为 `0`，缺失布尔不能解析为 `false`；
- 非法 target、范围外保留期或无法读取的配置使自动作业暂停并在页面报警，不按猜测值清理；
- 配置文件写入使用临时文件、flush、原子替换和 revision；
- 策略变更写审计日志，但不写用户正文或 Secret。

## 10. SQLite 与文件安全细则

### 10.1 SQLite

- 每个可清理表必须有与时间 + 主键顺序匹配的 retention 索引；
- 每批先选择有限主键，再在同一短事务内聚合/清字段/删除；
- 使用 UTC，旧时间格式不合法的行跳过并报告，不把解析失败当成最旧数据；
- 聚合、cursor 和删除形成可重试提交边界；
- Schema/索引不存在时暂停对应目标，不动态接受任意表名；
- 分析只读取 `page_count`、`page_size`、`freelist_count`、WAL 大小和有界 count/min/max；
- 禁止 overview/Preview 执行无界 `COUNT(*)`；候选数量来自索引边界、统计信息和有界采样；
- `dbstat` 只允许后台针对已知对象分片采样，并有页数、时间、cursor、超时和取消；
- 不依赖全局 30 秒 busy timeout 保护前台；维护 writer 主动短等待、快速让步。

### 10.2 日志文件

- 只处理 DataRoot 下目录目录项明确声明的日志根；
- 不跟随 symbolic link、junction 或 reparse point；
- Preview 固化规范化路径、长度、最后写入时间和文件标识；
- Execute 前重新验证签名和 cutoff；
- 正在写入、变化、越界或后缀不在白名单的文件跳过；
- 不使用通用递归删除命令，不删除日志根目录；
- 单文件失败不终止整个作业。

## 11. 可观测性与审计

Core 记录低基数事件：

- `storage.maintenance.scheduled`
- `storage.maintenance.started`
- `storage.maintenance.batch_completed`
- `storage.maintenance.paused_busy`
- `storage.maintenance.completed`
- `storage.maintenance.partial`
- `storage.maintenance.failed`
- `storage.policy.updated`
- `storage.inventory.refresh_requested`
- `storage.inventory.slice_completed`
- `storage.inventory.snapshot_published`

字段只包含 jobId、trigger、targetId、cutoff、candidate/deleted/cleared/skipped/failed、duration、busy retry、reusable/physical bytes 和稳定错误码。不要把这些维护遥测再次写入被同一作业清理的高频遥测表，避免自激增长；作业事件写入有界维护日志。

建议稳定错误码：

- `storage_preview_expired`
- `storage_preview_consumed`
- `storage_policy_conflict`
- `storage_target_protected`
- `storage_target_unknown`
- `storage_schema_unavailable`
- `storage_maintenance_busy`
- `storage_job_not_cancellable`
- `storage_dataroot_unsafe`
- `storage_compaction_offline_required`

## 12. 文件级实施计划

### 12.1 Core 合同与执行器

| 文件/目录 | 计划 |
|---|---|
| `Source/PuddingCore/Storage/` | 新增语义目录、策略、Preview、Job、状态与错误合同；不引用 EF/WPF/ASP.NET Core |
| `Source/PuddingPlatform/Services/StorageManagement/StorageDataClassCatalog.cs` | 固定白名单和安全分级 |
| `Source/PuddingPlatform/Services/StorageManagement/StorageInventorySampler.cs` | 有界时间片、目录/页 cursor、采样估算与请求合并 |
| `Source/PuddingPlatform/Services/StorageManagement/StorageInventorySnapshotStore.cs` | 当前快照原子发布与 90 天有界历史，供趋势报表读取 |
| `Source/PuddingPlatform/Services/StorageManagement/StorageAnalysisService.cs` | 只读取缓存 overview，并提供目标级有界估算；不执行同步全扫 |
| `Source/PuddingPlatform/Services/StorageManagement/StorageMaintenanceCoordinator.cs` | 单写者队列、DataRoot 锁、前台压力让步、取消和 slice 续行 |
| `Source/PuddingPlatform/Services/StorageManagement/StorageCleanupExecutor.cs` | 小批聚合、清字段、删行、文件签名重校验 |
| `Source/PuddingPlatform/Services/StorageManagement/StorageMaintenanceJobStore.cs` | DataRoot 下 durable 作业快照与有界事件 |
| `Source/PuddingPlatform/Services/StorageManagement/StorageRetentionPolicyService.cs` | `system.json` 原子读写、CAS 和范围校验 |
| `Source/PuddingPlatform/Services/RetentionPruningService.cs` | 实施时改为唯一调度入口或由新 Worker 取代；不能与新协调器并行写 |
| `Source/PuddingHost/Storage/StorageMaintenanceService.cs` | 拆出/委托到共享协调器，移除独立删除锁和在线 VACUUM 路径 |
| `Source/PuddingHost/Controllers/StorageManagementController.cs` | 收敛为新语义 API、Admin 鉴权、202 Job 和 ProblemDetails |
| `Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 组合根断言只注册一个在线维护 hosted service |

### 12.2 Web Admin

| 文件/目录 | 计划 |
|---|---|
| `Source/PuddingPlatformAdmin/config/routes.ts` | 增加 `/storage` 管理菜单，保持异步 Admin 壳边界 |
| `Source/PuddingPlatformAdmin/src/pages/storage/index.tsx` | 总览、类型选择、策略和作业列表 |
| `Source/PuddingPlatformAdmin/src/pages/storage/StorageOverviewCharts.tsx` | 分类占比图、30 天趋势图、分类图标、更新时间和无障碍标签 |
| `Source/PuddingPlatformAdmin/src/pages/storage/StorageReportTable.tsx` | 类型、约占空间、增长、策略、刷新状态报表 |
| `Source/PuddingPlatformAdmin/src/pages/storage/api.ts` | Core API、ProblemDetails 与 job events 客户端 |
| `Source/PuddingPlatformAdmin/src/pages/storage/types.ts` | 与 Core wire DTO 对齐 |
| `Source/PuddingPlatformAdmin/src/pages/storage/StoragePolicyDrawer.tsx` | 语义 target 策略编辑和 CAS 冲突处理 |
| `Source/PuddingPlatformAdmin/src/pages/storage/CleanupPreviewModal.tsx` | Preview 明细、保护声明和确认 |
| `Source/PuddingPlatformAdmin/src/pages/storage/*.test.tsx` | 受保护项、截止时间、Preview、作业恢复、取消和空间口径测试 |

### 12.3 Desktop

本功能不修改 `Source/PuddingDesktop`。不得新增 `CoreStorageManagementClient` 能力、WPF Storage 设置或 Desktop 侧数据库写入。现有 Desktop Storage 的退役/隐藏若需要，另立任务并单独验收。

## 13. 分期

### Phase 0：目录与保护门禁

- 建立语义目录和强制保护测试；
- 建立缓存快照、增量 sampler、slice/cursor 和约数展示合同；
- 冻结默认策略、空间口径和错误码；
- 校验真实 Schema、时间列和保留索引；
- 暂不启用新删除路径。

### Phase 1：单协调器自动过期

- 合并自动/人工写入入口；
- 实现 Debug 字段过期、runtime activity 小批清理和 durable cursor；
- 实现聚合表后再启用 raw telemetry/context 自动删除；
- 移除在线 VACUUM。

### Phase 2：Core Job API

- 实现 Overview/Catalog/Policy/Preview/Job/Cancel/Events；
- 迁移现有同步 Execute 到 202 作业；
- 实现策略文件 CAS 和审计。

### Phase 3：Web Storage 页面

- 完成 `/storage` 分类图、趋势报表、增量刷新、策略设置、近似 Preview、作业列表和错误呈现；
- 使用隔离 Temp DataRoot 完成前后端自动测试；
- 不触碰 Desktop 和真实 `D:\data`。

### Phase 4：生产验收

- 外部重启到明确新构建；
- 在用户明确授权的测试 DataRoot 上运行真实大表清理；
- 同时执行聊天受理、SSE、后台 activity 写入和 Storage 作业；
- 只有实时验证无锁竞争、无越权删除后，才能标记生产接受。

## 14. 验收标准

### 14.1 安全

- API 无法通过任意 target、表名、路径、SQL 或时间绕过白名单；
- `ChatMessages`、session/conversation event、LLM 用量账本、任务、记忆、配置始终不可选择；
- Preview 过期/消费/重启后不可执行；
- Preview 不为追求精确候选数执行全表扫描；Execute 始终受固定类型、cutoff 和作业预算约束；
- Debug 字段清理不改变指标数值、状态、耗时和聚合结果；
- 日志清理不越过 DataRoot，不跟随 reparse point。

### 14.2 正确性

- `eventTime < cutoffUtc` 删除，等于 cutoff 的数据保留；
- 每个语义类型可以独立设置保留期；
- 聚合成功后才删 raw，重试不重复聚合；
- 作业取消/崩溃/重启后从已提交 cursor 继续；
- 候选、已处理、跳过、失败和最终剩余数可核对；
- 预览估算、实际发现量、最终精确处理量和估算偏差分别展示，不把估算伪装成精确值；
- 页面区分逻辑大小、WAL、freelist 可复用页和操作系统实际释放字节。

### 14.3 并发与性能

- Host composition 测试证明只有一个维护 hosted service；
- 自动和人工请求同时到达时只有一个 writer 执行；
- 清理百万行测试中每批不超过预算，取消在批次边界生效；
- 清理期间新的唯一聊天请求持续返回受理成功，不出现新的 `SQLite Error 5/6`；
- 页面刷新不触发无界 `dbstat` 或数分钟全表扫描；
- `GET overview` 在百万/亿级历史数据下仍只读缓存快照，响应时间与表行数基本无关；
- “刷新估算”在 200ms 内返回 `202`，后台每个 sampler slice 有界并可让步/续行；
- 把相同全扫简单放进 `Task.Run` 或后台线程仍视为失败；单条查询/枚举也必须结构性有界；
- 自动作业不会在每次 Core 重启时重复全扫。

### 14.4 产品

- Admin `/storage` 可查看占用、自动策略、可清理类型、受保护项和作业历史；
- 页面提供带图标的分类占比图、7/30/90 天趋势报表、约数标识、更新时间和刷新状态；后台刷新期间页面保持可交互；
- API/UI 不返回或显示 confidence、coverage、上下界等预测器字段；
- 后台估算时页面滚动、筛选、tooltip、路由切换和策略编辑无长任务卡顿或全屏等待；
- 用户可以按类型和时间预览、确认、观察、取消；
- Core 离线时 Web 页面不可用是符合边界的，Desktop 不降级为直写数据库；
- 本设计落地前只能标记“设计完成”，不能描述为实现、自动验收或生产接受。

## 15. 非目标

- 不清理用户会话、记忆、任务、配置、Secret、源代码或用户 Artifact；
- 不把 Desktop 变成存储管理客户端；
- 不在在线 Core 中执行全库 `VACUUM`；
- 不为旧表名配置或同步 Execute DTO 建长期兼容层；
- 不用自动化测试、脚本或本设计直接清理 `D:\data`；
- 不把“删除行数”冒充为“操作系统已释放字节”。
