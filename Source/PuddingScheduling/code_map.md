# PuddingScheduling

| 文件 | 契约 |
|---|---|
| `CronExpression.cs` | 五段 cron（分 时 日 月 周）的解析与下一次触发计算；纯函数，时钟与时区由调用方传入 |
| `ScheduleTickPlanner.cs` | 有界计算触发、游标和跳过数；Skip 保留当前调度分钟，截断时游标推进到未来并标注跳过数下限 |
| `AutomationScheduleRecord.cs` | 持久化模型（设计 §3.2 字段）投影到 `ScheduleState`，以及 `(scheduleId, fireTime)` 幂等键的规范化：fireTime 必须转 UTC 并截到整分钟 |
| `IScheduleStore.cs` | 读取到期计划并原子提交 tick；按版本拒绝过期快照，只把触发唯一键冲突判为已记录 |
| `ScheduleStoreSchema.cs` | 计划与触发表 DDL、触发唯一约束、到期扫描索引 |
| `SqliteScheduleStore.cs` | 复用调用方的 SQLite 连接，任务写入、触发记录和游标共享事务 |

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

## 未接入宿主

按交付规程停在 S3/S4：独立工程 + 独立测试 + 边界断言。尚未加入 `PuddingAgentNetwork.slnx`，也没有任何宿主引用，因此不影响现网构建。
