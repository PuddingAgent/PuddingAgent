# AgentConfigurationClient（只读 SDK 示例）

普通 .NET 控制台程序，只引用 `Pudding.Api.Sdk`，通过公共 `IAgentConfigurationApi` 读取一个 Agent 的元数据配置。
它不了解 manifest/JSON/数据库，也不携带插件授权：凭据是管理端 JWT，只应放在仓库之外。

## 构建

```powershell
dotnet build Samples/AgentConfigurationClient/AgentConfigurationClient.csproj --nologo
```

唯一 `ProjectReference` 是 `Source/Pudding.Api.Sdk/Pudding.Api.Sdk.csproj`；本工程不引用宿主、存储实现或任何 NuGet 包。

## 参数

```powershell
dotnet run --project Samples/AgentConfigurationClient -- `
  --base-url http://127.0.0.1:5199 `
  --credential-file <仓库外的凭据文件路径> `
  --workspace <workspaceId> `
  --agent <agentId>
```

四个参数全部必填：`--base-url`、`--credential-file`、`--workspace`、`--agent`。
未知参数、重复参数、缺少参数或参数值为空都以退出码 2 结束（stderr 只有一条说明，不回显凭据内容）。

`--base-url` 规则：

- 必须是绝对 `http` / `https` URL；
- 禁止 userinfo、query 与 fragment；
- 明文 `http` 只允许 loopback（`localhost`、`127.0.0.1`、`::1`），其他主机必须 `https`；
- 不使用自定义 `HttpMessageHandler`，TLS 证书校验始终开启；
- 末尾斜杠由程序归一化，HTTP 请求超时为 15 秒。

## 凭据占位文件格式

凭据文件是 JSON，只读取 `accessToken` 一个字段（占位值，真实 JWT 不得提交进仓库）：

```json
{
  "accessToken": "<management JWT>"
}
```

字段缺失、不是字符串或为空白时以退出码 2 结束；文件内容不会被回显，也不作为命令行参数传入。

## 输出

stdout 恰好一行 JSON，只含六个字段：

```json
{"workspaceId":"ws","agentId":"agent","revision":"<opaque revision>","displayName":"\u9CB8\u9CB8","description":null,"templateId":"template"}
```

示例不关闭默认的 `System.Text.Json` 编码器，因此非 ASCII 字符会以 `\uXXXX` 转义输出（解析后还原为原文，如上例的 `\u9CB8\u9CB8` 即「鲸鲸」）；换行、引号、反斜线与控制字符同样是合法 JSON 转义，stdout 始终只有一行。
`revision` 是不透明字符串，本示例只原样展示，不解析、不参与计算。
`AppliedTarget`/`ObservedAt`/回执等概念不在输出内：本示例读取的是配置投影（configuration-source），
不代表 UI 已刷新，也不代表正在执行的 Run 已切换。

## 退出码

| 退出码 | 含义 |
|---|---|
| `0` | 成功，stdout 有一行 JSON |
| `1` | `ConfigurationApiException`：stderr 只写稳定的 `Code`（如 `permission.denied`、`resource.not_found`），不输出任何配置值 |
| `2` | 参数、Base URL 或凭据文件不合法 |
| `130` | Ctrl+C 取消（取消的 `CancellationToken` 传递到 SDK 调用） |

`0`/`1`/`2`/`130` 之外的异常（例如传输中断）不会被吞掉或改写成成功，按运行时默认失败处理。

## 只读边界

- 只调用 `Agents.GetAsync` 与 `Configuration.GetAsync`；**不调用** `UpdateAsync`、`ReadChangesAsync`，不创建 Agent、不写配置。
- 不读取磁盘上的 manifest、JSON 配置或数据库；唯一的磁盘读取是 `Program` 载入凭据文件。
- 不拥有、也不 `Dispose` 注入的 `IAgentConfigurationApi`、`TextWriter` 或 `HttpClient`（`HttpClient` 由 `Program` 自己创建并释放）。
- 不承诺创建、启动或执行 Agent，也不承诺修改任何配置。
- 本示例只读该版本化配置 API 的投影；插件/第三方凭据不能冒用该入口。

## 索引

本目录与测试工程（`Tests/AgentConfigurationClient.Tests`）的索引由主代理登记进解决方案与根 `code_map.md`；施工者不改解决方案。
