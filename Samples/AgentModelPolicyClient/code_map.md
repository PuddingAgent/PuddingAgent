# Samples/AgentModelPolicyClient
> 源指纹: *.cs=2-b86c01367a74, *.csproj=1-967383037a29 · 条目数: 4 · 最近整理: 2026-11-22

net10.0 控制台示例（`Exe`），仅引用 `Pudding.Api.Sdk`，演示 `IAgentModelPolicyApi` 的注入式离线用法。

> 源指纹: Samples/AgentModelPolicyClient/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentModelPolicyClient.csproj` | 示例控制台工程，唯一工程引用为 `Pudding.Api.Sdk` | — | `Pudding.Api.Sdk` | — |
| `PolicyDemo.cs` | 固定入口 `RunAsync`：仅接受 `get <workspaceId> <agentId>`，转成一次 `GetEffectiveAsync` 调用 | `PolicyDemo.RunAsync` | `IAgentModelPolicyApi`、`EffectiveAgentModelPolicy`、`AgentRef` | 只读：不调 `UpdateAsync`、不读配置或环境变量、不自造默认模型或价格 |
| `Program.cs` | 离线壳：向 stderr 打印固定提示并以 2 退出；不读凭据也不联网 | `Program` | `PolicyDemo` | — |
| `code_map.md` | 本索引 | — | — | — |
