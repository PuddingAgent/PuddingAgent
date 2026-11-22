# PuddingSlack
> 源指纹: *.cs=8-d7c2136cea13, *.csproj=1-483f97ef766b · 条目数: 8 · 最近整理: 2026-11-09

独立 BCL-only Slack 协议组件。工程与包依赖均由构建门禁禁止；不感知 Agent、配置目录或宿主。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `SlackContracts.cs` | Slack 通道的数据契约 | `SlackCredentials`、`SlackProtocolException` | `SlackApiClient.cs` | 时间戳保持字符串，不得转 long（会丢精度） |
| `SlackEventParser.cs` | DM 与 @提及的入站事件映射 | `SlackEventParser` | `SlackContracts.cs` | 用户与频道白名单外一律丢弃；bot 与编辑事件必须过滤 |
| `SlackApiClient.cs` | Slack Web API 的单次调用客户端 | `SlackApiClient` | `SlackAppTokenProvider.cs` | — |
| `SlackAppTokenProvider.cs` | App Token 的到期前续期 | `SlackAppTokenProvider` | `SlackApiClient.cs` | 结果不明时不得重用 Refresh Token |
| `SlackSocketMode.cs` | Socket Mode 的入站接收 | `SlackSocketMode`、`SlackWebSocket` | `SlackEventParser.cs` | — |
| `SlackStore.cs` | Slack 通道的单主持久收件箱 | `SlackStore` | `SlackContracts.cs` | 已抑制的消息不得重放 |
| `SlackMessageText.cs` | 显式成员提及的转义与保留 | `SlackMessageText` | `SlackEventParser.cs` | 不得按名称猜测广播目标 |
| `SlackMemberDirectory.cs` | 只读工作区成员查询 | `SlackMemberDirectory` | `SlackMessageText.cs` | 重名或不完整不得认定唯一；分页有界、缓存短时 |

独立测试：[PuddingSlackTests](../PuddingSlackTests/code_map.md)。
