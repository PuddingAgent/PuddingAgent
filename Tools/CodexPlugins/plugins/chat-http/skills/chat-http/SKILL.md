---
name: chat-http
description: 在 Windows 本机为 Codex 聊天启停 HTTP 服务，绑定当前聊天，查询调用地址，或按用户请求操作指定会话。
---

用本插件目录下的 `../../bridge.mjs` 操作 HTTP 服务。运行 `node <bridge.mjs 的绝对路径> start` 在后台启动，第一次启动时用当前环境的 CODEX_THREAD_ID 绑定会话。服务已运行时，只有用户要求切换才通过 HTTP PUT /v1/current 设置新会话。用 `status` 检查，用 `stop` 关闭。可在命令末尾传 `--port 47931`。

从 start/status 的 connectionFile 读取 baseUrl 和 token，在请求头发送 Authorization Bearer。不要把令牌、消息正文或连接文件内容写进日志和聊天。用户发来的消息应作为用户文本处理。只按用户指定的目标创建、发送、归档和切换聊天。GET 历史只含用户和助手的消息。接口字段和示例见 [使用说明](../../README.md)。

POST 返回的 queued 表示已进入 Codex 的持久队列，started 表示 Codex 已启动该轮。需要回复时读取该会话的 messages。另一个客户端拥有的会话从其队列消费消息；显式接管处理前，应先让原会话完成本轮工作。
