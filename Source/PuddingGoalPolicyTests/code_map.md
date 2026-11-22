# PuddingGoalPolicyTests
> 源指纹: *.cs=3-e74b32b9eca9, *.csproj=1-e85e9aaceba0 · 条目数: 0 · 最近整理: 2026-11-22

目标续跑策略的独立测试工程：用决策矩阵与最小循环轨迹锁定纯函数决策与下一轮键的稳定性。

> 源指纹: Source/PuddingGoalPolicyTests/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GoalFixtures.cs` | 提供冻结的策略快照与事实构造器 | `GoalFixtures` | `ContinuationPolicySnapshot`、`GoalPolicyFacts` | 默认值是整张决策矩阵的基线，改任一默认字段都会牵动两个测试类 |
| `GoalPolicyTests.cs` | 锁定续跑决策矩阵的每个分支判定 | `GoalPolicyTests` | `GoalPolicyFacts`、`GoalFixtures` | 模型给出 Accept 但无证据不构成完成；重复结算必须返回同一决策与同一键 |
| `GoalPolicyTraceTests.cs` | 最小循环轨迹：从 Chat 单轮到修复后完成与证据不足永不过关 | `GoalPolicyTraceTests` | `GoalFixtures` | 截断或不足的证据永远不能过关；预算耗尽必须停机而不是无限重试 |
| `PuddingGoalPolicyTests.csproj` | MSTest.Sdk 的可执行测试工程，只引用 GoalPolicy 组件 | — | `../PuddingGoalPolicy` | `UseVSTest=false`，产物是自宿主测试可执行文件 |
