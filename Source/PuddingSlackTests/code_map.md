# PuddingSlackTests

只引用 PuddingSlack。`SlackTests.cs` 验证协议过滤、线程路由、持久化/ACK 顺序、崩溃恢复及发送结果分类；无需 Slack 凭据或宿主进程。

`SlackTokenRotationTests.cs` 验证轮换 Token 原样传输、到期前刷新、双 Token 持久化、并发互斥与不确定刷新防重试。

`SlackChannelTests.cs` 验证授权 bot / 自身过滤、线程和频道额度、重启与抑制重放、出站显式成员提及及广播转义。

`SlackMemberDirectoryTests.cs` 验证名称/ID 解析、最小化目录数据、重名/删除成员、部分分页不误报唯一，以及缺权限诊断。
