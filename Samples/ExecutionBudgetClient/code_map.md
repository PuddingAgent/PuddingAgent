# Samples/ExecutionBudgetClient

net10.0 控制台示例（`Exe`），仅引用 `Pudding.Api.Sdk`，演示 `IExecutionBudgetApi` 的注入式离线用法。

> 源指纹: Samples/ExecutionBudgetClient/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ExecutionBudgetClient.csproj` | 示例控制台工程，唯一工程引用为 `Pudding.Api.Sdk` | — | `Pudding.Api.Sdk` | — |
| `BudgetDemo.cs` | 固定入口 `RunAsync`：仅接受 `get <accountId>`，转成一次 `GetAsync` 并原样输出预算快照 JSON | `BudgetDemo.RunAsync` | `IExecutionBudgetApi`、`ExecutionBudgetSnapshot` | 只透传：不算可用余额、不轮询、不重试、不把 Unknown 当成零 |
| `Program.cs` | 离线壳：向 stderr 打印固定提示并以 2 退出；不读凭据也不联网 | `Program` | `BudgetDemo` | — |
| `code_map.md` | 本索引 | — | — | — |
