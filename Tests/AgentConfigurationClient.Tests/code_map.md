# Tests/AgentConfigurationClient.Tests
> 源指纹: *.cs=1-04383a62d9b6, *.csproj=1-ae22cc428cba · 条目数: 2 · 最近整理: 2026-11-22

Agent 配置样例的离线测试工程：只挂样例工程与假 API，不接真实网络或真实 Core。

> 源指纹: Tests/AgentConfigurationClient.Tests/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ConfigurationInspectorTests.cs` | 覆盖配置读取的 JSON 输出与命令行参数校验 | `ConfigurationInspectorTests`、`ProgramShellTests` | `Samples/AgentConfigurationClient/AgentConfigurationClient.csproj` | stdout 只允许一行 camelCase JSON；usage 错误一律 exit 2 且不得调用 API；只有调用方取消才允许 exit 130 |
| `AgentConfigurationClient.Tests.csproj` | 定义 MSTest 运行器与本工程的唯一引用 | — | `Samples/AgentConfigurationClient/AgentConfigurationClient.csproj` | `MSTest.Sdk/4.0.1` 且 `OutputType=Exe`、`UseVSTest=false`；唯一 ProjectReference 是样例工程 |
