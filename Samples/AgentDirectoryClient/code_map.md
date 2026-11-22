# Samples/AgentDirectoryClient

net10.0 控制台示例（`Exe`），仅引用 `Pudding.Api.Sdk`，演示 `IAgentDirectoryApi` 的注入式离线用法。

> 源指纹: Samples/AgentDirectoryClient/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentDirectoryClient.csproj` | 示例控制台工程，唯一工程引用为 `Pudding.Api.Sdk` | — | `Pudding.Api.Sdk` | — |
| `DirectoryDemo.cs` | 固定入口 `RunAsync`：把 `get`/`list` 参数原样转成一次目录 API 调用，并按结果映射退出码 | `DirectoryDemo.RunAsync` | `IAgentDirectoryApi`、`AgentRef`、`AgentListQuery` | 参数错误时零 API 调用；不补第二问、不重试、不做创建/归档 |
| `Program.cs` | 离线壳：向 stderr 打印固定提示并以 2 退出；不读凭据也不联网 | `Program` | `DirectoryDemo` | — |
| `code_map.md` | 本索引 | — | — | — |
