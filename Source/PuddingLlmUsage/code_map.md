# PuddingLlmUsage — code map
> 源指纹: 未生成（本文件首次整理，仓库内无指纹自检器 —— 不编造） · 条目数: 5 · 最近整理: 2026-10-30

> LLM 余额采样与消耗判定的**零依赖叶子**：`ProjectReference = 0`、`PackageReference = 0`
> （由 `PuddingLlmUsageTests.PuddingLlmUsage_AssemblyReferencesNoProjectAssemblies` 运行时断言）。

## 定位

回答一个问题：**这段时间到底花了多少钱，这个结论能不能站得住**。

它不负责「去哪查」（那是 `ILlmBalanceProbe` 的实现）、不负责「写哪里」（`ILlmUsageStore` 的实现）、
不负责「什么时候跑」（宿主的 `BackgroundService`）。这三件事换实现都不应碰本组件。

## 文件

| 文件 | 内容 |
|---|---|
| `Contracts/BalanceContracts.cs` | `LlmBalanceObservation`（成功/失败都是观测）、`LlmUsageIntervalKind{Consumption,Recharge,NoChange,Unavailable,Gap}`、`LlmUsageInterval`、`LlmUsageSummary` + `ConsumptionPerHour`、`LlmUsageAnalysisOptions(GapThreshold=30min, AmountTolerance=0)` |
| `Contracts/ILlmBalanceProbe.cs` | 采样端口。**约定：凡能描述成一次观测的失败都返回 Failure 观测，只有取消才抛** —— 用异常表达失败会迫使调用方吞掉或中断循环，两者都会让缺口在曲线上变成「没变化」 |
| `Contracts/ILlmUsageStore.cs` | 持久化端口。尝试（`LlmUsageAttemptStatus`）与观测分开；`MarkRunningAttemptsInterruptedAsync` 让被强杀的那一轮在事后可见 |
| `Contracts/LlmUsageSamplingOptions.cs` | 采样配置 + `Validate()`：provider 去重归一；**单次超时必须小于间隔**，否则两轮会重叠；启用时至少一个 provider |
| `LlmUsageAnalyzer.cs` | 观测序列 → 区间。判定顺序：失败→`Unavailable`、超阈值→`Gap`、上升→`Recharge`、持平→`NoChange`、下降→`Consumption`。`Summarize` 的分母**只用可判定时长**，跨窗口边界的区间排除 |
| `LlmSamplingSchedule.cs` | UTC 槽位对齐（以 `UnixEpoch` 为基准）。**恰好对齐时返回下一槽**，否则「刚采完」与「下一次」指向同一时刻，退化成零间隔连采 |
| `LlmBalanceSampler.cs` | 一轮：落 Started → 查余额 → 落结果。存储写不进时**不发起外部查询**；端口抛异常兜住并落 Failure；取消穿透 |

## 唯一的不变量

**无法判定时一律 `null`，绝不用 0 冒充。**

`0` 会被读者理解为「这段时间没花钱」，而实际情况可能是「没采到」「被充值掩盖」「间隔太长」。
这条由 17 条测试 + 多次变异取红守着，改任何判定分支都必须先看它有没有被破坏。

## 测试（`Source/PuddingLlmUsageTests/`，33 用例）

| 文件 | 覆盖 |
|---|---|
| `LlmUsageAnalyzerTests.cs` | 五种判定各一；跨 provider / 跨币种不得相减；乱序输入先排序；汇总分母只用可判定时长；跨边界区间排除；组件零工程依赖（反射查 `GetReferencedAssemblies`） |
| `LlmBalanceSamplerTests.cs` | 落盘顺序（begin→complete）；端口抛异常被兜住；**存储不可写时不发起外部查询**；存储失败仍如实上报查询结果；取消穿透；多 provider 串行且保序；槽位算术（含"恰好对齐"边界） |

未进 `PuddingAgentNetwork.slnx`（S5 时随宿主装配一起登记）。
