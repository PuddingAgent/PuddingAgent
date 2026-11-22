# Samples/AgentConfigurationClient
> 源指纹: *.cs=2-66d0e049af29, *.csproj=1-ec53dd838dbe, Samples/AgentConfigurationClient/*.md=1-1a64f3e80212 · 条目数: 4 · 最近整理: 2026-11-12

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentConfigurationClient.csproj` | net10.0 控制台示例，只引用公共 SDK。 | `net10.0`、`Pudding.Api.Sdk` | `Source/Pudding.Api.Sdk/Pudding.Api.Sdk.csproj` | 无包引用；仅向 AgentConfigurationClient.Tests 开放 internals。 |
| `Program.cs` | 进程外壳：命令行与凭据解析，构造带 Bearer 的 HttpClient。 | `Program`、`CommandLineOptions`、`TryParseArguments`、`TryValidateBaseUri`、`TryReadAccessToken` | `Samples/AgentConfigurationClient/ConfigurationInspector.cs` | 退出码 0/1/2/130 是契约；访问令牌只从凭据文件读取且永不回显。 |
| `ConfigurationInspector.cs` | 固定只读入口：经 SDK 读一次配置并输出单行 JSON。 | `ConfigurationInspector`、`RunAsync` | `Samples/AgentConfigurationClient/Program.cs`、`Source/Pudding.Api.Sdk/PuddingClient.cs` | 不得调用 Update 或 ReadChanges；失败走 stderr 并映射退出码。 |
| `README.md` | 示例的构建与运行说明，含参数和退出码。 | — | `Samples/AgentConfigurationClient/Program.cs` | — |
