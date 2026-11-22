# Samples/AgentWorkClient
> 源指纹: *.cs=2-228f78aa9cd1, *.csproj=1-ca20e2d2c1e3 · 条目数: 4 · 最近整理: 2026-11-22

net10.0 控制台示例（`Exe`），仅引用 `Pudding.Api.Sdk`，演示 `IAgentWorkApi` 的注入式离线用法。

> 源指纹: Samples/AgentWorkClient/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentWorkClient.csproj` | 示例控制台工程，唯一工程引用为 `Pudding.Api.Sdk` | — | `Pudding.Api.Sdk` | — |
| `WorkDemo.cs` | 固定入口 `RunAsync`：`submit`/`get` 各转成恰好一次工作 API 调用，并按回执或错误映射退出码 | `WorkDemo.RunAsync` | `IAgentWorkApi`、`AgentWorkException` | `effectOutcome=unknown` 单独退 4；无轮询、无重试、无状态查询与取消命令 |
| `Program.cs` | 离线壳：向 stderr 打印固定提示并以 2 退出；不读凭据也不联网 | `Program` | `WorkDemo` | — |
| `code_map.md` | 本索引 | — | — | — |
