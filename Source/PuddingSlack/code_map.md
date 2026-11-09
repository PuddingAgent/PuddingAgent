# PuddingSlack

独立 BCL-only Slack 协议组件。工程与包依赖均由构建门禁禁止；不感知 Agent、配置目录或宿主。

| 文件 | 用途 |
|---|---|
| `SlackContracts.cs` | 身份、准入、文本事件、发送状态与回执；时间戳保持字符串 |
| `SlackEventParser.cs` | DM / @提及映射；用户与频道白名单、bot/编辑事件过滤 |
| `SlackApiClient.cs` | auth.test、apps.connections.open、chat.postMessage；429 与不确定结果分类 |
| `SlackAppTokenProvider.cs` | App-Level Token 到期前续期；凭据存储端口、单次刷新日志、结果不明禁止重用 Refresh Token |
| `SlackSocketMode.cs` | WebSocket 重连、控制帧、持久化后 ACK；可替换 socket 测试端口 |
| `SlackStore.cs` | 单进程租约、持久收件箱、去重墓碑、发送前日志；授权 bot 的线程/频道额度持久化，已抑制消息不重放 |
| `SlackMessageText.cs` | 保留显式成员提及，转义其他尖括号语法，避免广播和名称猜测 |
| `SlackMemberDirectory.cs` | 只读工作区成员查询；有界分页和短时缓存，重名/不完整不认定唯一，输出最小身份和原生提及 |

独立测试：[PuddingSlackTests](../PuddingSlackTests/code_map.md)。
