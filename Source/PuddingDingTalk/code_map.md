# PuddingDingTalk
> 源指纹: *.cs=6-fd1891845b73, *.csproj=1-066bd5e6b23c, Source/PuddingDingTalk/*.md=1-c3682a24f18d · 条目数: 7 · 最近整理: 2026-11-12

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `DingTalkStream.cs` | 企业 Stream 回调接收与文本回复出口。 | `DingTalkStream` | `Source/PuddingDingTalk/DingTalkGroupHandler.cs` | — |
| `DingTalkMediaClient.cs` | 受限媒体下载与来源群图片投递。 | `DingTalkMediaClient` | `Source/PuddingDingTalk/DingTalkStream.cs` | — |
| `GroupPolicy.cs` | 校验企业消息来源并构造 guest 身份。 | `GroupSource`、`GroupIngress`、`IPublicGroupAgent`、`PublicGroupAnswers` | `Source/PuddingDingTalk/Jingjing.group.zh-CN.md` | 群主与管理员身份不授予 Pudding 权限。 |
| `DingTalkGroupHandler.cs` | 去重限流后的消息派发与钉钉回执记录。 | `DingTalkGroupHandler`、`IGroupReplyJournal` | `Source/PuddingDingTalk/GroupPolicy.cs` | — |
| `GroupFeedbackInbox.cs` | 显式反馈按条落幂等 Markdown。 | `GroupFeedbackInbox` | `Source/PuddingDingTalk/DingTalkGroupHandler.cs` | 只写固定目录，重复提交不产生新文件。 |
| `MpcalNavigationEvidence.cs` | 从已核验的界面注册源码投影公开菜单与说明。 | `MpcalNavigationEvidence` | `Source/PuddingDingTalk/GroupPolicy.cs` | — |
| `Jingjing.group.zh-CN.md` | 可安装到实例 AGENTS.md 的群聊规则段。 | — | `Source/PuddingDingTalk/GroupPolicy.cs` | — |
