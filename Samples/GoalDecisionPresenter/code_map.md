# Samples/GoalDecisionPresenter

net10.0 类库示例（非 `Exe`），仅引用 `Pudding.Api.Contracts`，演示目标决策快照的纯展示格式化。

> 源指纹: Samples/GoalDecisionPresenter/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GoalDecisionPresenter.csproj` | 示例类库工程，唯一工程引用为 `Pudding.Api.Contracts` | — | `Pudding.Api.Contracts` | — |
| `GoalDecisionPresenter.cs` | 纯格式化入口 `Format`：把决策快照渲染成固定五行文本，未知原因码原样保留 | `GoalDecisionPresenter.Format`、`GoalDecisionSnapshot` | `GoalDecisionSnapshot` | 不写 GoalPhase、不推导下一状态、不解析自然语言、不改输入快照 |
| `code_map.md` | 本索引 | — | — | — |
