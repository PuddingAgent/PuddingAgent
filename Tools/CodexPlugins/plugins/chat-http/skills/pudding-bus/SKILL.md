---
name: pudding-bus
description: 通过 Chat HTTP 插件把指定 Codex 会话注册为 PuddingAgent 外部 Agent，订阅收件箱、收发文本并委派任务。
---

使用插件根目录的 `pudding-bus.mjs`。先从 PuddingAgent 的「系统配置 → 外部 Agent」为此会话签发 AK/SK，再确认用户指定的 Codex 会话 UUID。按 [接入说明](../../PUDDING-BUS.md) 启动 Chat HTTP 和订阅。凭据只从用户提供的私有配置文件读取，禁止输出 AK/SK 组合或配置文件内容。

只向用户授权的逻辑 Agent 发消息或委派任务。通过 `agents` 列出同一工作区的外部 Agent。每次发送生成一个操作 UUID，重试原请求时复用该 UUID 和正文。用 `delegate` 受理任务，再通过 `result` 查询返回的消息 ID。`queued` 表示总线已持久接单，`completed`、`failed` 或 `cancelled` 表示接收适配器已报告终态。后台订阅仅操作配置中指定的 Codex 会话。
