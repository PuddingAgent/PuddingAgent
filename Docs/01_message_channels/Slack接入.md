---
title: Slack 接入
author: Codex
date: 2026-11-04
last_reviewed: 2026-11-04
status: active
description: "通过 Socket Mode 接收 Slack 私聊和频道提及，使用隔离会话及持久发送回执回复。"
categories: [docs, message_channels]
tags: [slack, connectors, socket-mode]
related_docs: [Docs/13_runbooks/Desktop-Guardian.md, Docs/08_how_debuge/05-常见症状.md]
related_files: [Source/PuddingSlack/SlackStore.cs, Source/PuddingHost/Connectors/SlackConnector.cs, TestScripts/test-pudding-slack-credentials.ps1]
slug: slack-channel
draft: false
---

# Slack 接入

首版使用 Socket Mode：Core 主动连接 Slack，无需公网回调。支持单个 App 安装、文本私聊、频道 @提及及线程回复。一个 Slack App 只能绑定一个 Pudding 渠道，渠道只能绑定一个启用的 Agent；不要让另一套客户端同时连接同一个 App，否则事件会被分发到不同连接。

## 创建与配置

1. 在 [Slack 应用管理](https://api.slack.com/apps) 选择从 manifest 创建，导入 [slack-app-manifest.json](slack-app-manifest.json)，选择目标工作区。
2. 在 Basic Information 的 App-Level Tokens 生成具有 `connections:write` 权限的 Access Token；在 OAuth & Permissions 安装应用，取得 Bot User OAuth Token。App Token 可以是 `xapp-…` 或轮换式 `xoxe.xapp-…`，必须原样复制全部内容，不删前缀。Signing Secret 和 Verification Token 不用于 Socket Mode。
3. 在 Git 仓库外保存 UTF-8 JSON，例如 `D:\Keys\puddingagent-slack.json`。字段为 `appToken` 和 `botToken`，均为字符串。不要把实际 token 放入命令行、环境变量、聊天、仓库或日志。文件 ACL 应取消继承，只允许运行 Core 的用户、SYSTEM 和管理员读取，写权限限文件所有者及系统管理员。
4. 用 `pwsh -File TestScripts/test-pudding-slack-credentials.ps1 -CredentialFile D:\Keys\puddingagent-slack.json` 获取脱敏的 Team ID、工作区名称和 Bot User ID。默认只调用 `auth.test`；加 `-TestSocketMode` 会原样使用 App Token 调用 `apps.connections.open`。探针不连接 WebSocket、不发送消息，不能代替收发验收。Team ID 为 `T…`，不是工作区显示名称或网址前缀。
5. 在 Pudding 工作区「渠道管理」新增 Slack，填写 Team ID、Basic Information 中的 App ID（`A…`）、凭据文件绝对路径，并绑定 Agent。填写允许交互的真人用户 ID（`U…` / `W…`）、允许提及的频道 ID（`C…` / `G…`），以及可选的机器人用户 ID `allowedBotUserIds`（填 `U…`，不是 `B…` Bot ID）。真人和机器人分别授权；频道白名单为空时只接受真人白名单的私聊。
6. 把 App 邀请进测试频道。使用 [Guardian](../13_runbooks/Desktop-Guardian.md) 重启 Core，使连接配置生效。保存配置只表示写盘，不表示已连接。运行时先通过 `auth.test` 校验 Team ID，再接受事件；App ID、Team ID、用户与频道均需匹配。

允许的用户能够使用绑定 Agent 的能力，应将此白名单视为对该 Agent 的授权。Slack 用户身份为 `slack:<teamId>:<userId>`，不映射成桌面管理员；首版不授予 `/yolo` 等特权指令权限。Slack 原生 slash commands 不在 manifest 中注册。

## App-Level Token 轮换

启用 App-Level Token rotation 后，Access Token 约 12 小时过期。凭据文件另外需要 `appRefreshToken`、`clientId` 和 `clientSecret`；后两项来自同一 App 的 App Credentials。字段值原样保存，不把 Refresh Token 写到 `appToken`。此实现只自动刷新 App-Level Token，Bot/User Token 的自动轮换尚未实现。

`SlackAppTokenProvider` 首次没有到期信息时刷新一次，随后每分钟检查，在到期前 10 分钟刷新；每次连接 Socket 前也检查。`oauth.v2.access` 返回的 Access Token、Refresh Token、到期时间 `appTokenExpiresAt` 一起原子写回凭据文件，新值持久化成功后才用于连接。文件旁的 `.lock` 只作独占租约，临时文件创建时即复制原文件 ACL，替换后保持 ACL；不要在 Core 运行期间手动编辑该凭据文件。

刷新调用前持久化 `pendingRefreshTokenHash`。超时、异常响应或返回新 Token 后写盘失败，都可能使一次性的 Refresh Token 已被消耗，此时报告 `app_refresh_outcome_unknown`，重启也不会盲目复用它。需要在 Slack 创建新的一对 Access/Refresh Token 后停止 Core、替换文件内对应字段并清除旧到期时间，再启动；不要仅删除 pending 字段强制重试原 Refresh Token。

凭据缺字段报告 `app_rotation_credentials_missing`；文件不可写报告存储错误。刷新接口拒绝和 Socket 连接成功是不同判据。协议依据：[Token 类型与轮换](https://docs.slack.dev/authentication/tokens/#rotating-app-level-tokens)、[刷新协议](https://docs.slack.dev/authentication/using-token-rotation/#refresh-a-token)。

## 会话与消息语义

- DM：每个渠道安装、Agent、Team 和 DM 会话复用一个 Pudding 会话；已有 DM 线程的回复保留 `thread_ts`。
- 频道：同一根线程复用会话，不同根线程隔离。每轮都必须 @机器人，首版没有订阅整个频道的 `message.channels`。
- `ts` / `thread_ts` 始终是字符串。Pudding 内部 Channel ID 与 Slack Channel ID 分开保存。
- 自己发出的 bot 消息、未授权 bot、bot 私聊、编辑/删除事件以及非文本事件不启动 Agent。授权 bot 只能在允许的频道显式 @ 本应用触发。首版不下载附件、不投影飞书语音/图片指令、不向 Slack 流式输出或发送按钮交互。
- 单条出站文本上限为 35,000 UTF-16 字符，超限明确拒绝并保留原始终态，不静默截断。暂不自动拆分长回复。

## 前端可见性与成员提及

聊天左栏常驻「渠道会话」，列出渠道私聊和线程；新会话在可见页面每 10 秒刷新，重新聚焦窗口也刷新。轮询只取最近 100 条活动渠道会话，不回填全文历史。选中后复用会话历史、canonical 事件、SSE 和过程回放，显示实际输入、工具执行和终态回复。执行者取 `agentInstanceId`，不把 Slack 路由 `principalId` 当作 Agent ID；主会话投影只用于选中的主会话，不能覆盖渠道线程。

此入口是观察视图：顶部显示渠道线程标题，底部用只读说明替代消息输入框，提示回原渠道回复。主会话仍独立，避免 Web 输入被误认为已经发给 Slack。

每条渠道回复下的「执行过程」按当前 conversationId/turnId 读取 `/api/conversations/{id}/process`，无需回放整个历史。复用主聊天过程样式；默认按 toolCallId 合并工具调用与结果为卡片，参数/输出按需展开，思考摘要折叠。「原始记录」保留全部事件、sequence、退出码与截断标记。展开才读取，每页最多 100 条；更早记录需点击加载，空展示页仍沿服务端游标前进。读取失败不解释为没有调用。运行中展开的过程每 5 秒刷新，页面隐藏暂停；切换会话取消旧请求，15 秒超时后允许重试。

历史来信使用已持久化的 Slack 用户 ID 标识发送者，机器人显示 `Slack bot`；不会把所有渠道成员标成当前登录的管理员。尚无身份元数据的实时占位显示「渠道成员」，不猜测身份或展示管理员头像。历史发送者显示仍使用 ID，成员查询工具不会改写历史身份。

Agent 的正常回复可包含 Slack 原生 `<@U…>` / `<@W…>`，人和其他应用的 bot 用户使用相同格式。来信中提及其他人的 ID 原样保留，回复时可沿用；纯显示名 `@名字` 不自动猜测或转换成通知。出站开启 mrkdwn，关闭自动名称扩展并转义其他尖括号语法；`<!channel>`、用户组和伪装链接不会产生广播。当前支持在回复里提及，不包含跨频道主动发送工具。

不知道成员 ID 时调用 `slack_lookup_member(query)`，传精确显示名、真实姓名、登录名或 User ID；人和 bot 均可查询。工具只在可信 Slack 输入的主会话 Turn 或其委派子代理中使用，由命令绑定当前 Connector、工作区和 Agent，模型不能选另一个工作区。需在 OAuth & Permissions 的 Bot Token Scopes 添加 `users:read` 并 Reinstall to Workspace；旧安装不会因仓库 manifest 改动自动增加授权。不需要 `users:read.email`。

工具只调用 `users.list`，不发送消息。结果包含 `status`、`complete`、`candidates`、`mention`、`observedAt`、`scope`。只有 `resolved` 的顶层 `mention` 可放进正常回复（不能放在代码块）；`ambiguous`、`incomplete`、`not_found` 不能猜 ID。每页最多 200 人、最多 5 页、总计 20 秒；未遍历完整目录就返回 incomplete，即使恰好找到一个同名成员。完整结果缓存 5 分钟，只保留 ID、用户名、显示名、真实姓名与 bot 标志，不持久化通讯录，不回传邮箱。查询工作区成员不证明其已加入当前频道，也不证明通知送达或对方 Agent 已响应。

Connector 尚未就绪或正在停止时，查询请求没有发给 Slack，返回生命周期不可用提示；这不能证明 Token 无效。验收发消息后应等本轮终态再重启，避免测试到主动停止的实例。

为防止 bot 互相 @ 无限运行，每个线程在最近一次新的真人消息后最多接受 **1 轮 bot 触发**，新线程也最多 1 轮；每个频道全部授权 bot 合计 **每分钟最多 3 轮**。真人消息仍须通过白名单并 @ 应用才重置线程额度。额度持久化，重启不会恢复；重复或已抑制消息不会重新获得额度。抑制会 ACK 并记录去重墓碑，诊断字段 `messagesSuppressed` 是本次进程的抑制计数。若额度写入后崩溃，可能保守消耗一次额度，不会多放行。

## 持久化与回执

`PuddingSlack` 是零项目/包引用的叶子组件，构建门禁禁止反向引用。Host 负责配置、Agent 绑定和生命周期；Platform 保持 canonical Message Fabric / Conversation 投影。Desktop Shell 无 Slack 业务代码。

数据位于 `<DataRoot>/channels/<channelId>/slack/`：

| 记录 | 含义 |
|---|---|
| `pending/` | 收件箱，落盘并刷新后才能 ACK Socket Mode。Agent 执行不阻塞 ACK |
| `completed/` | 按 Team/Channel/message ts 去重的墓碑，不保留消息正文 |
| `outgoing/` | 发送前先写入 Unknown，收到结果后原子更新；按 operationId 哈希寻址 |
| `bot-threads/` · `bot-rate/` | bot 的线程额度和频道分钟窗口；与收件箱共用独占租约 |
| `.owner.lock` | 单进程独占句柄，不得删除以绕过租约 |

收件箱最多积压 10,000 条；写盘或容量失败不 ACK。宿主失败后重试交接，内部 message/clientRequest ID 保持稳定。墓碑和出站回执首版不自动清理，需纳入数据备份与容量监控；任意删去去重记录会降低重放保护。

出站状态：

| 状态 | 判据与后续动作 |
|---|---|
| `sent` | Slack 返回 `ok=true`，频道匹配且存在消息 `ts`；表示服务端已接受，不表示用户已读 |
| `retryable` | 明确的 429，按 `Retry-After` 重试同一发送，不重新运行 Agent |
| `rejected` | 参数、权限等明确拒绝；投递进入死信，不盲目重试 |
| `unknown` | 发送超时、断网、服务器不确定错误或发送中进程退出；进入死信，保留证据，自动重发被禁止 |

不宣称端到端 exactly-once。HTTP 发送完成但响应丢失时，无法证明 Slack 是否创建了消息；保留 unknown 正是为了避免“假成功”及重复发送。人工核对频道后再决定是否以新请求补发，不应删掉账本强制重试。

需要常规 Pudding API 登录认证的诊断接口：

- `GET /api/slack/channels/{channelId}/diagnostics`：动态注册的 Slack Connector 状态与计数。`connected` 才表示收到了 Socket Mode hello。
- `GET /api/slack/channels/{channelId}/deliveries/{operationId}`：返回状态、原因码、Slack Channel ID、消息 ts 和 retryAt；`operationId` 是消息元数据 `gateway_idempotency_key`（终态回复的稳定 MessageId）。404 表示无回执，409 表示连接器不可用，均不表示已送达。

## 实机验收

使用显式测试频道和白名单用户，记录客户端发出的唯一文本标记、Pudding conversation/message ID、operationId 和 Slack 回执 ts：

1. DM 发中文，核对聊天中实际出现完整回复，并取得 `sent` 与对应 ts。
2. 在两个新频道线程各 @机器人，同一线程继续 @一次；核对两条根线程使用不同 Pudding 会话，同线程复用且回复不跑到频道根部。
3. 重放同一事件、重启后重放同一发送：不能新增 Agent Turn 或重复 Slack 消息。
4. 非白名单用户、bot 自身及编辑事件不能启动 Agent；授权 bot 的第 2 次连续线程触发被抑制，真人新的 @ 输入后才允许下一轮。
5. 在前端选中渠道会话，核对输入和回复与 Slack 同一线程一致，工具过程来自该会话；主会话消息不能混入，不显示 Web 发送输入框。让 Agent 回复实际 `<@成员ID>`，核对 Slack 返回消息保留该 token，并在 Slack 客户端确认渲染为提及；返回 `sent` 仍不代表对方已读或其 Agent 已响应。
6. 用离线可控传输验证 429 和响应丢失。unknown 不能自动转为 sent；发送失败不能重跑 Agent。不要靠主动制造真实重复消息来测试幂等。

这些是验收判据，不代表已经在真实 Slack 工作区执行。

官方协议：[Socket Mode](https://docs.slack.dev/apis/events-api/using-socket-mode/)、[App manifest](https://docs.slack.dev/reference/app-manifest/)、[chat.postMessage](https://docs.slack.dev/reference/methods/chat.postMessage/)、[限流](https://docs.slack.dev/apis/web-api/rate-limits/)。
