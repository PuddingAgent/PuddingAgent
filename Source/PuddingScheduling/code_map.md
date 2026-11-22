# PuddingScheduling


> 源指纹: Source/PuddingScheduling/**=8-6218db89de52 · 条目数: 7 · 最近整理: 2026-11-22
| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CronExpression.cs` | 五段 cron 解析与下一次触发计算 | `CronExpression` | `ScheduleTickPlanner.cs` | 纯函数：时钟与时区由调用方传入，不得读系统时钟 |
| `ScheduleTickPlanner.cs` | 有界计算触发、游标与跳过数 | `ScheduleTickPlanner`、`TickPlan`、`CatchUpPolicy` | `CronExpression.cs` | Skip 必须保留当前调度分钟；截断时游标推进到未来并标注跳过数下限 |
| `AutomationScheduleRecord.cs` | 持久化模型投影到 `ScheduleState` 并规范化幂等键 | `AutomationScheduleRecord`、`ScheduleFireKey` | `ScheduleState` | fireTime 必须转 UTC 并截到整分钟，否则唯一索引挡不住重复 |
| `IScheduleStore.cs` | 到期计划读取与 tick 原子提交端口 | `IScheduleStore`、`ScheduleTickWrite` | `SqliteScheduleStore.cs` | 按版本拒绝过期快照；只把触发唯一键冲突判为已记录 |
| `ScheduleStoreSchema.cs` | 计划与触发表的 DDL 与到期扫描索引 | `ScheduleStoreSchema` | `SqliteScheduleStore.cs` | 触发唯一约束不可退化为无效 CHECK，否则重复触发不被拦 |
| `SqliteScheduleStore.cs` | 写入、触发与游标共享同一事务 | `SqliteScheduleStore` | `ScheduleStoreSchema.cs` | 同一定义重启要保留游标，定义变更才递增版本 |
| `ScheduleTickService.cs` | 从注入时钟取一轮时间并逐项规划提交 | `ScheduleTickService`、`ScheduleTickResult` | `IScheduleStore.cs` | 时钟必须注入，不得内部读 `DateTime.UtcNow` |

叶组件只依赖 BCL。项目的 MSBuild 门禁拒绝 ProjectReference，测试工程提供 SQLite 驱动。

## 为什么是纯函数

`NextOccurrence(TimeZoneInfo, DateTimeOffset afterUtc)` 不读系统时钟、不做 IO，因此日期规则可以完全由表驱动单测覆盖：月末、闰年、日/周同时受限时的 OR 语义、夏令时不存在的时间点。这类逻辑靠人工 review 抓不住，必须靠会失败的测试。

DST 处理策略：本地时间不存在（春季前跳）则跳过；本地时间重复（秋季回拨）按标准偏移解析，保证同一触发点不会被返回两次。

## 测试门禁

`Source/PuddingSchedulingTests` 覆盖 cron、tick 决策和 SQLite 持久化契约。

注意：该用例最初写成 `0 0 20 * 5`，而 2026-11-20 同时满足「20 号」和「周五」，OR 与 AND 都落到同一天，变异因此存活。改用 `0 0 25 * 5` 后变异才被杀掉 —— 断言必须选在两种语义分歧的位置。

第二处：把 `CatchUpPolicy.Skip` 改成表现如 `RunOnce`，`SkipPolicy_MissedDays_FiresNothing_ButAdvancesPastNow` 变红（已实测，TEST_EXIT=2，复原后文件 SHA 逐位一致）。

第三处：把 `ToState()` 的两个位置参数交换（`NextRunAtUtc` 与 `LastFireTimeUtc` 互换），`ToState_MapsEachFieldByName_NotByPosition` 与 `ToState_DoesNotConfuseNextRunWithLastFire` 同时变红（TEST_EXIT=2）。record 的按名初始化一旦写错顺序，编译器不会拦，只会静默把游标和上次触发时刻喂反。

第四处：去掉 `NormalizeFireTime` 里的 `ToUniversalTime()`，`FireKey_SameInstantInDifferentOffsets_IsOneKey` 与 `NormalizeFireTime_ReturnsUtcKind` 变红（TEST_EXIT=2）。这两条锁的是同一件事：唯一索引要挡得住重复，先得让两侧算出同一个键。

第五处：把 `automation_runs` 的唯一约束换成无作用的 `CHECK (1 = 1)`，`Schema_RejectsASecondRowForTheSameScheduleAndFireTime` 与 `Schema_RejectsDuplicateEvenWhenTheFireTimeIsExpressedWithADifferentOffset` 变红（TEST_EXIT=2），两条「应允许」的用例保持绿——变异只打中该打中的那两条。这组用例跑在真实 SQLite 内存库上，验的是引擎行为而不是我写的假 store。

## 宿主接线

解决方案登记组件与独立测试。Platform 的 [每日巡检 Worker](../PuddingPlatform/Services/Scheduling/HouseInspectionScheduleWorker.cs) 在启用后提供 SQLite 连接和看板任务写入，调度组件保持单向依赖边界。
