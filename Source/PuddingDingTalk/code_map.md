# PuddingDingTalk

| 入口 | 职责 |
|---|---|
| [DingTalkStream.cs](DingTalkStream.cs) · [DingTalkMediaClient.cs](DingTalkMediaClient.cs) | 企业 Stream 回调、文本回复、受限媒体下载及来源群图片投递 |
| [GroupPolicy.cs](GroupPolicy.cs) | 校验企业消息来源、构造 guest 身份、提取实例的公开规则段 |
| [DingTalkGroupHandler.cs](DingTalkGroupHandler.cs) | 消息去重、限流、派发给绑定 Agent 并记录钉钉回复回执 |
| [GroupFeedbackInbox.cs](GroupFeedbackInbox.cs) | 显式反馈提交，固定目录内每条一份幂等 Markdown |
| [MpcalNavigationEvidence.cs](MpcalNavigationEvidence.cs) | 从已核验的界面注册源码投影公开菜单与说明 |
| [Jingjing.group.zh-CN.md](Jingjing.group.zh-CN.md) | 可安装到实例 AGENTS.md 的独立群聊规则段 |
