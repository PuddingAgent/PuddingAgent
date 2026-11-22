# PuddingToolGuidance
> 源指纹: Source/PuddingToolGuidance/**=13-b087e1f5b069 · 条目数: 12 · 最近整理: 2026-11-22
> 定位：Harness 层的工具用法引导叶子组件。吃一次调用的字面信息，最多回三条短提示。
> 无 ProjectReference 也不认识宿主：它随每次工具调用执行，任何依赖都会变成热路径成本。

## 入口与配置

调用方构造 `GuidanceInput`（工具 id、方言、命令文本、成败、错误、两个计数、输出），交给 `GuidanceEngine.Evaluate`，
再用 `GuidelineSummary.AppendTo` 把提示追加到工具输出末尾。规则集合缺省取 `Rules/DefaultRules.All`。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CommandOccurrenceTracker.cs` | 窗口内同一命令出现次数的计数 | `CommandOccurrenceTracker`、`Register`、`Peek` | `ConsecutiveFailureTracker.cs` | 键要含 sessionId；跨会话共用键会把两个会话算成重复出现 |
| `ConsecutiveFailureTracker.cs` | 同一调用连续失败次数的计数 | `ConsecutiveFailureTracker`、`Register`、`Peek` | `Rules/RepeatedFailureRule.cs` | 成功必须清零；不清零会把十小时前的失败算成当前循环 |
| `GuidanceContracts.cs` | 一次工具调用的字面输入与提示载体 | `GuidanceInput`、`GuidanceAdvice`、`IGuidanceRule` | `GuidanceEngine.cs` | 规则必须是纯函数且不得抛异常；它跑在每次工具调用的热路径上 |
| `GuidanceEngine.cs` | 规则求值与提示配额去重 | `GuidanceEngine`、`Evaluate`、`GuidelineSummary`、`MaxAdvices` | `Rules/DefaultRules.cs` | 提示只追加文本；碰成功位或退出码会让工具的语义被提示篡改 |
| `Rules/BuildEfficiencyRules.cs` | 构建与测试的重复开销提示规则 | `BuildEfficiencyRules`、`All` | `Rules/DefaultRules.cs` | — |
| `Rules/DefaultRules.cs` | 默认规则集按治理主题组合 | `DefaultRules`、`All` | `GuidanceEngine.cs` | 新主题各占一个文件再并入清单；堆进方言规则会让类名与内容对不上 |
| `Rules/GitSafetyRules.cs` | git 提交与暂存的安全提示规则 | `GitSafetyRules`、`All` | `Rules/DefaultRules.cs` | — |
| `Rules/HostToolingRules.cs` | 本机工具链别名的提示规则 | `HostToolingRules`、`All` | `Rules/DefaultRules.cs` | 别名事实属于本机环境；换机器要复核而不是照抄 |
| `Rules/NestedShellInCmdRule.cs` | cmd 内嵌套调用其它 shell 的提示规则 | `NestedShellInCmdRule`、`Evaluate` | `Rules/ShellDialectRules.cs` | — |
| `Rules/RepeatedFailureRule.cs` | 连续失败达阈值时提示换观测手段 | `RepeatedFailureRule`、`Threshold` | `ConsecutiveFailureTracker.cs` | 阈值与判定同源；两处各写一个数会分叉 |
| `Rules/ResultQualityRules.cs` | 把没搜到与没搜全分开的提示规则 | `ResultQualityRules`、`All` | `GuidanceContracts.cs` | 判据只认工具输出里的原文标记；靠调用方推断会漏掉没读文档的调用者 |
| `Rules/ShellDialectRules.cs` | shell 方言误用的提示规则 | `ShellDialectRules`、`All` | `Rules/NestedShellInCmdRule.cs` | — |

## 约束与坑

- 判定的是**用法**不是意图：只看字面量判不了的（比如用户想并行还是串行）一律不提示，猜错即噪声。
- 配额是默认 3 条并按 `RuleId` 去重；提示是上下文成本，不是越多越好。
- 规则不得依赖 IO 与会话状态；需要历史的计数由调用方在 `GuidanceInput` 里传进来。
