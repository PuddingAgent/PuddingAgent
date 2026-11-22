# PuddingLlmUsage — code map
> 源指纹: Contracts/**=4-0e5b5e6934df, *.cs=3-ab180b933817, *.csproj=1-05551f02a9cc · 条目数: 9 · 最近整理: 2026-10-30

> LLM 余额采样与消耗判定的**零依赖叶子**：`ProjectReference = 0`、`PackageReference = 0`
> （由 `PuddingLlmUsageTests.PuddingLlmUsage_AssemblyReferencesNoProjectAssemblies` 运行时断言）。

## 定位

回答一个问题：**这段时间到底花了多少钱，这个结论能不能站得住**。

它不负责「去哪查」（那是 `ILlmBalanceProbe` 的实现）、不负责「写哪里」（`ILlmUsageStore` 的实现）、
不负责「什么时候跑」（宿主的 `BackgroundService`）。这三件事换实现都不应碰本组件。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Contracts/BalanceContracts.cs` | 余额观测、消耗区间与汇总的不可变合同 | `LlmBalanceObservation`、`LlmUsageInterval`、`LlmUsageSummary` | `LlmUsageAnalyzer.cs` | 无法判定时一律 `null`，不得用 0 冒充「这段时间没花钱」 |
| `Contracts/ILlmBalanceProbe.cs` | 余额采样端口 | `ILlmBalanceProbe` | `LlmBalanceSampler.cs` | 凡能描述成一次观测的失败都返回 Failure 观测，只有取消才抛 |
| `Contracts/ILlmUsageStore.cs` | 持久化端口：尝试与观测分开 | `ILlmUsageStore`、`LlmUsageAttemptStatus` | `LlmBalanceSampler.cs` | 被强杀的那一轮必须能事后标为中断，否则消耗缺口不可见 |
| `Contracts/LlmUsageSamplingOptions.cs` | 采样配置与 fail-fast 校验 | `LlmUsageSamplingOptions` | `LlmBalanceSampler.cs` | 单次超时必须小于采样间隔，否则两轮重叠 |
| `LlmUsageAnalyzer.cs` | 观测序列判成区间并汇总可判定时长 | `LlmUsageAnalyzer` | `Contracts/BalanceContracts.cs` | 汇总分母只用可判定时长，跨窗口边界的区间必须排除 |
| `LlmSamplingSchedule.cs` | UTC 槽位对齐的采样时刻计算 | `LlmSamplingSchedule` | `LlmBalanceSampler.cs` | 恰好对齐时必须返回下一槽，否则退化成零间隔连采 |
| `LlmBalanceSampler.cs` | 一轮采样：落 Started → 查余额 → 落结果 | `LlmBalanceSampler`、`LlmSamplingOutcome` | `Contracts/ILlmBalanceProbe.cs` | 存储写不进时不得发起外部查询；端口异常兜住并落 Failure |

## 唯一的不变量

**无法判定时一律 `null`，绝不用 0 冒充。**

`0` 会被读者理解为「这段时间没花钱」，而实际情况可能是「没采到」「被充值掩盖」「间隔太长」。
这条由 17 条测试 + 多次变异取红守着，改任何判定分支都必须先看它有没有被破坏。

## 测试（`Source/PuddingLlmUsageTests/`，33 用例）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `../PuddingLlmUsageTests/LlmUsageAnalyzerTests.cs` | 五种判定与跨币种、乱序输入的归约行为 | `LlmUsageAnalyzerTests` | `LlmUsageAnalyzer.cs` | 跨 provider 或跨币种不得相减；判定分支改动必须先跑它 |
| `../PuddingLlmUsageTests/LlmBalanceSamplerTests.cs` | 落盘顺序、端口异常兜住与槽位算术 | `LlmBalanceSamplerTests` | `LlmBalanceSampler.cs` | 取消必须穿透；存储不可写时不得发起外部查询 |

未进 `PuddingAgentNetwork.slnx`（S5 时随宿主装配一起登记）。
