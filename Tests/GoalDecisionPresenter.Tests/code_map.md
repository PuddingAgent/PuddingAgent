# Tests/GoalDecisionPresenter.Tests

Goal 决策展示器的 golden 测试工程：快照全在内存，不接 Goal 存储也不联网。

> 源指纹: Tests/GoalDecisionPresenter.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GoalDecisionPresenterTests.cs` | 覆盖决策文本的固定五行格式与空值写法 | `GoalDecisionPresenterTests` | `Samples/GoalDecisionPresenter/GoalDecisionPresenter.csproj` | 期望字符串与冻结基线逐字一致；输出固定五行且不得追加验收结论或空行 |
| `GoalDecisionPresenter.Tests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Samples/GoalDecisionPresenter/GoalDecisionPresenter.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一 ProjectReference 是样例工程 |
