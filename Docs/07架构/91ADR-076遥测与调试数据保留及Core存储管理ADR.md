# ADR-076：遥测与调试数据保留及 Core 存储管理

> - 状态：**Proposed**
> - 日期：2026-08-20
> - 详细设计：[遥测、调试数据自动过期与 Web 存储管理设计方案](../Features/遥测调试数据自动过期与Web存储管理设计方案.md)
> - 实现状态：未实现；本文不代表代码、配置、数据库、Web 页面或生产验收已经完成

## 背景

`pudding_platform.db` 中的 `telemetry_metric_events`、`context_layer_metric_events` 和 `runtime_activity` 会随模型调用、上下文分层和组件执行持续增长。这些数据主要用于性能分析和 Debug，不应无限保留，也不应与会话正文、执行事实、计费账本、记忆和配置采用同一清理策略。

当前已有自动保留和人工 Storage 清理能力，但两条路径使用不同服务和锁；Web Admin 没有管理页面；数据类型粒度不足；在线人工清理还能请求全库 `VACUUM`。现有分析/Preview 若为得到精确候选数而完整 `COUNT(*)` 或遍历大量文件，在历史垃圾很多时也会长时间占用后台和拖慢前端。历史上重复保留 writer 已实际造成 SQLite 锁竞争和聊天受理 500，因此新增功能必须同时收敛写所有权和扫描预算。

用户进一步明确：Desktop 是启动器，暂时不承担存储空间管理。本 ADR 据此覆盖既有 Desktop Storage 扩展方向。

## 决策

### 1. 存储管理属于 Core + Web Admin

- Core 负责数据分类、分析、策略、Preview、清理作业和所有数据库写入；
- `PuddingPlatformAdmin` 增加 `/storage`；
- Desktop 不新增扫描、策略编辑、清理、作业恢复或数据库维护能力；
- 现有 Desktop Storage 视为历史实现，不再作为新功能的目标入口。

### 2. 使用代码内置语义目录

用户和 API 只使用稳定的语义类型 ID，例如：

- `diagnostics.debug-payload`
- `diagnostics.telemetry-raw`
- `diagnostics.context-layer-raw`
- `diagnostics.runtime-activity`
- `diagnostics.logs.verbose`
- `diagnostics.logs.error`

物理表、列、目录、索引和 SQL 只存在于 Core 白名单目录。配置和客户端不能提供任意表名、路径或 SQL。

### 3. 空间统计使用缓存的增量估算快照

- `/storage` 首屏只读最近一次 `StorageInventorySnapshot`，不触发扫描；
- 用户点击刷新只提交异步 refresh request，立即返回 `202`，重复请求合并；
- 后台按 50–100ms 时间片、目录/页 cursor 和有界样本逐步更新；
- 每条 SQL/目录枚举都必须由索引和 LIMIT/页数/文件数保证结构性有界；把一次性全扫简单搬到后台线程不算满足本决策；
- 禁止 overview/Preview 同步执行全量 `COUNT(*)`、全库 `dbstat` 或一次性目录遍历；
- 分类占用与候选数量允许近似，界面显示“约”、更新时间和刷新状态，不返回置信度、覆盖率或预测上下界；
- Preview 固定类型、cutoff 和作业预算，不为获取精确数字阻塞；实际数量由小批作业累计；
- Web 页面提供带图标的分类占比图、7/30/90 天趋势报表和估算标识，后台刷新时页面保持可交互。

### 4. 采用四级安全分类

- `Disposable`：允许自动和人工过期；
- `Derived`：可重建，默认人工清理；
- `Evidence`：不进入非关键一键清理，只能按独立证据保留合同治理；
- `UserData`：禁止清理。

`ChatMessages`、session/conversation event、执行/任务事实、`llm_gateway_usage_events`、`TokenUsageEvents`、`TokenUsageStats`、记忆、配置和用户文件强制受保护。

当前 `conversation_events` 归档后裁剪是独立 Evidence 保留路径，不成为 Web Storage 可选目标。

### 5. 自动与人工维护使用唯一协调器

- 一个 hosted scheduler；
- 一个 `StorageMaintenanceCoordinator`；
- 一个串行维护作业队列；
- 一个 DataRoot 级 OS 独占锁；
- 一套小批执行器和持久 cursor；
- 人工 Execute 创建异步作业，不在 HTTP 请求中同步删除到底。

禁止通过“每个服务各有一个 `SemaphoreSlim`”模拟全局互斥。现有 `RetentionPruningService` 与 `StorageMaintenanceService` 的写路径在实施时必须合并或委托到同一协调器。

### 6. 保留原始数据前先保存低基数趋势

- Debug JSON/metadata 默认 7 天后先清字段；
- 原始遥测、上下文指标和运行活动默认 14 天；
- 遥测小时聚合和上下文日聚合默认保留 365 天；
- 上下文日聚合复用既有 `context_layer_daily_rollups` 表（retention 删除前幂等补建缺失日），不新建平行聚合表；遥测小时聚合无既有物，新建 `telemetry_metric_rollups_hourly`；
- 聚合幂等提交成功后才删除原始行；
- 若聚合未实现，原始 telemetry/context 自动删除保持关闭。

策略写入 `<DataRoot>/config/system.json`，使用语义 ID 和 revision；发布包配置只提供安全默认值。缺失值不得变成 `0/false`，非法策略使自动作业 fail closed。

### 7. 在线清理不执行全库压缩

- 继续使用默认 100 行、250ms 让步、单目标单轮 200 批的在线基线；
- 维护 writer 使用短等待，遇到 SQLite busy/locked 主动让步；
- 自动与人工清理都不执行在线 `VACUUM`；
- 在线最多尝试非阻塞 `wal_checkpoint(PASSIVE)`；
- 页面分别报告逻辑删除、SQLite 可复用页和操作系统物理释放字节；
- 物理压缩若未来实现，必须使用 Core 停止后的外部维护模式和独立验收。

## 结果

### 正向结果

- 高频遥测和 Debug 数据具有明确生命周期，不再无限增长；
- 用户可以按数据类型和时间安全清理；
- Web 管理页面与 Core 存储语义对齐，Desktop 保持轻量启动器边界；
- 缓存快照和增量采样使首屏/API 延迟与垃圾总量解耦，并能用图表展示分类占比和趋势；
- 单 writer、小事务和让步机制降低聊天受理与后台维护互相阻塞的风险；
- 关键事实和用户数据在目录、API、UI 和测试四层都受保护；
- 长期趋势通过低基数聚合保留，原始高基数数据可过期。

### 成本与限制

- 需要新增聚合表、作业状态、配置 CAS、前端页面和并发验收；
- 在线删除后数据库文件可能不会立即变小；
- 大量历史积压需要多轮 slice 才能完成，不能追求一次清空；
- 分类字节数和 Preview 候选数是约数，最终处理结果才是精确事实；界面不引入预测置信度概念；
- Core 离线时 Web Storage 不可用，Desktop 不提供降级写入；
- 旧 Desktop Storage 的退役需要单独任务，本文不直接删除它。

## 被否决方案

### 继续让 Desktop 管理数据库

否决。Desktop 是启动器和进程主管，不应拥有数据库表语义或直写 SQLite；Web Admin 才是当前管理入口。

### 新增第二个自动清理 BackgroundService

否决。独立定时器和独立锁不能防止两个服务同时争用 SQLite writer，已有事故证据证明该方案不可接受。

### 客户端传表名或 SQL

否决。它扩大删除面、破坏未来 Schema 重构，也无法为受保护事实提供可靠门禁。

### 人工清理使用大事务、自动清理使用小事务

否决。用户点击不应绕过在线可用性预算；所有在线路径必须共用小批执行器。

### 清理结束立即在线 VACUUM

否决。数 GiB SQLite 的全库重写会持有长锁并要求额外磁盘空间；删除成功不应因压缩失败而产生错误承诺。

### 所有遥测整行使用同一保留期

否决。Debug payload、性能原始值、上下文指标、运行活动和长期趋势的价值与体积不同，需要可独立配置的语义类型。

### 点击“扫描”后同步完整统计

否决。历史数据规模未知，完整 `COUNT(*)`、全库 `dbstat` 或一次性文件遍历会让 API 和页面等待不可控时间。把同一个全扫放到后台线程仍会长期占用 Core/SQLite 资源，同样否决。页面必须先显示缓存快照，刷新只驱动后台有界增量估算。

## 实施与验收门禁

1. 先落语义目录和保护测试，不启用新清理；
2. 再把自动与人工写路径合并到唯一协调器；
3. 聚合完成后才启用原始 telemetry/context 自动过期；
4. Core Job API 完成后实现 Web `/storage`；
5. 定向测试必须使用系统 Temp 隔离 DataRoot，禁止触碰 `D:\data`；
6. 外部重启到明确新构建后，使用用户授权的测试 DataRoot 做真实大表与聊天并发 smoke；
7. Host composition 必须证明只有一个在线维护 hosted service；
8. 设计完成、代码存在、自动测试通过和生产接受是四个不同状态，不得混用；
9. 旧 `/api/admin/storage/databases` 端点下线必须与 Desktop 旧 Storage 页面退役捆绑为同一任务，在此之前保持双通道鉴权（Admin JWT 或 ControlToken）现状。
