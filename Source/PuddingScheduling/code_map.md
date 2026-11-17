# PuddingScheduling

| 文件 | 契约 |
|---|---|
| `CronExpression.cs` | 五段 cron（分 时 日 月 周）的解析与下一次触发计算；纯函数，时钟与时区由调用方传入 |
| `ScheduleTickPlanner.cs` | 单次 tick 的决策：该触发哪些时刻、游标推到哪、丢了多少；三档错过补偿策略（Skip/RunOnce/RunAll），无数据库无时钟 |

叶组件不引用 Core、Host、Platform、Runtime 或 Desktop，只依赖 BCL。

## 为什么是纯函数

`NextOccurrence(TimeZoneInfo, DateTimeOffset afterUtc)` 不读系统时钟、不做 IO，因此日期规则可以完全由表驱动单测覆盖：月末、闰年、日/周同时受限时的 OR 语义、夏令时不存在的时间点。这类逻辑靠人工 review 抓不住，必须靠会失败的测试。

DST 处理策略：本地时间不存在（春季前跳）则跳过；本地时间重复（秋季回拨）按标准偏移解析，保证同一触发点不会被返回两次。

## 测试门禁

`Source/PuddingSchedulingTests` —— 29 个用例（cron 19 + tick 决策 10）。变异验证两处，均已实测：

注意：该用例最初写成 `0 0 20 * 5`，而 2026-11-20 同时满足「20 号」和「周五」，OR 与 AND 都落到同一天，变异因此存活。改用 `0 0 25 * 5` 后变异才被杀掉 —— 断言必须选在两种语义分歧的位置。

第二处：把 `CatchUpPolicy.Skip` 改成表现如 `RunOnce`，`SkipPolicy_MissedDays_FiresNothing_ButAdvancesPastNow` 变红（已实测，TEST_EXIT=2，复原后文件 SHA 逐位一致）。

## 未接入宿主

按交付规程停在 S3/S4：独立工程 + 独立测试 + 边界断言。尚未加入 `PuddingAgentNetwork.slnx`，也没有任何宿主引用，因此不影响现网构建。
