---
name: puddingagent-message
description: Send messages from Codex or an external client to an Agent inside local PuddingAgent, then verify delivery or terminal replies through the HTTP receipt API. Use when asked to contact a PuddingAgent Agent or check an earlier message.
---

# PuddingAgent 消息通道

方向：**Codex / 外部客户端 → PuddingAgent 内的 Agent**。它不能向 Codex 聊天投递。
发送须属于用户明确授权的通信任务；本 skill 本身不授权发送其他消息。

## 凭据与运行环境

- 凭据只从 `D:\Keys\puddingagent-external-api-gpt.json` 读取；不要输出、复制进仓库、传命令行或放环境变量。
- JSON 字段：`baseUrl`、`tokenId`、`bearerToken`、`workspaceId`。本机使用 `http://127.0.0.1`、工作区 `default`；文件 ACL 仅当前用户与 SYSTEM 可访问。
- 脚本 [scripts/pudding_message.py](scripts/pudding_message.py) 使用 Python 3.10+ 标准库。本机 `py -3` 可用。所有命令在仓库根执行，`$client` 指向该脚本；可用绝对路径从其他目录调用，但必须把 `--state-dir` 指向同一份持久目录。
- 请求正文、幂等键和回执保存在 `.work/pudding-message/<operation>/state.json`，该目录被 Git 忽略。保留此目录才能恢复请求；不要把业务消息或回执放进可提交文件。消息正文也可能敏感。

```powershell
$client = '.pudding/skills/puddingagent-message/scripts/pudding_message.py'
py -3 -B $client doctor
py -3 -B $client agents
```

`doctor` 检查 Token 身份和作用域；`agents` 列出收件人状态。使用确切的 `agentId`，不要仅凭昵称发给另一个 Agent。已验证的收件人：鲸鲸 `default.global_general-assistant.6a8`；后续发送前复核启用/冻结状态。

## 发送和恢复

1. 把用户授权的消息写成 UTF-8 文件，放入 `.work/pudding-message/`。每条逻辑消息选择一个唯一的 `--operation`，并记录下来。
2. 调用 `send`。同一消息的重试必须复用 operation、正文、目标、Token 身份和状态目录。
3. 有 `messageId` 后反复调用 `status`；不要因等待超时换 operation 重发。

```powershell
py -3 -B $client send --agent default.global_general-assistant.6a8 `
  --message-file .work/pudding-message/message.txt --operation example-unique-id
py -3 -B $client status --operation example-unique-id
```

默认等待最多 30 秒，目标为终态回复。`--wait-seconds 0..45` 控制单次等待；`--wait-for delivered` 可只等 canonical 会话接收。脚本每次至多发一个 POST，不自动循环重发。已记录 messageId 的 `send` 也只查询 GET。

脚本返回 JSON 与退出码：

| 退出码 / result | 可以说什么 |
|---|---|
| `0 / reply_confirmed` | 对应消息的执行 `succeeded`，有非错误终态回复；报告 messageId 和 reply。 |
| `0 / delivery_confirmed` | 所有 delivery 已 ACK，且有 conversationId；消息进入 canonical 会话。**不等于模型已读完、任务成功或已有回复。** |
| `2 / pending` | 暂无足够证据，或网络结果未知；报告当前状态、operation、messageId，继续查回执。不能说“发送失败，所以重发”。 |
| `1 / error` 或 `delivery_or_execution_failed` | 协议/本地错误或服务端失败；说明具体 error 与已有回执。执行失败不抹掉已送达事实。 |

HTTP **202 只表示接单**；`deliveryStatus=accepted` + ACK + conversationId 表示会话接收；`executionStatus=succeeded` + completedAtUtc + reply + replyIsError=false 才作为成功回复。
验证新通道时发送唯一随机标记，要求收件 Agent 原样回复 `ACK:<标记>`，不调用工具、不写文件。比对回执里的完整 reply；通用“成功”不能证明收到正确正文。

如果 POST 超时且没有 messageId：一小时内可用同一 operation 再执行 `send`，它复用已落盘的幂等键和正文。超过一小时脚本拒绝重发，需人工从服务端核对原请求；服务端幂等记录不是永久的。不要删除状态或换键规避。409 conflict 必须排查内容/身份冲突，409 in_progress 只表示处理中。

脚本保存凭据的 SHA-256 指纹，不保存明文 Token。凭据轮换后即使 JSON 的 Token ID 忘记更新，脚本也拒绝重发旧 operation；新 Token 可能无权查询旧回执。保留原始回执并报告边界。

## API 与验证入口

- `GET /api/external/v1/token` 仅身份自检，不列出其他端点。
- `GET /api/external/v1/workspaces/{workspace}/agents`
- `POST /api/external/v1/workspaces/{workspace}/agents/{agent}/messages`：JSON `{content: ...}`，必须 `Idempotency-Key`，返回 202。
- `GET /api/external/v1/workspaces/{workspace}/agents/{agent}/messages/{messageId}`：只允许原 Token 查询自己的回执。
- 脚本仅向凭据指定的 loopback origin 发送认证头，不跟随重定向或服务端 statusUrl；不受 HTTP proxy 环境变量影响。
- 回归测试使用隔离的本地假服务，不触碰真实 Agent：`py -3 -B -m unittest discover -s .pudding/skills/puddingagent-message/scripts -p test_pudding_message.py -v`。测试临时文件在 `.work/pudding-message-tests/`。
