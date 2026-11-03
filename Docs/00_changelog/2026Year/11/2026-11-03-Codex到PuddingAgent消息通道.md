---
title: 2026-11-03 Codex 到 PuddingAgent 消息通道
author: hyfree
date: 2026-11-03
last_reviewed: 2026-11-03
status: archived
description: "验证本机 External API，凭据保存于仓库外，新增持久幂等请求和回执查询 skill，并以唯一 ACK 完成真实消息验收。"
categories: [docs, changelog]
tags: [external-api, skill, messaging, receipts]
related_docs: []
related_files: [.pudding/skills/puddingagent-message/SKILL.md, .pudding/skills/puddingagent-message/manifest.json, .pudding/skills/puddingagent-message/scripts/pudding_message.py, .pudding/skills/puddingagent-message/scripts/test_pudding_message.py, Source/PuddingPlatform/Controllers/External/V1/ExternalWorkspaceAgentController.cs, code_map.md]
slug: changelog-2026-11-03-codex-to-puddingagent-messaging
draft: false
---

# 2026-11-03 Codex 到 PuddingAgent 消息通道

## 已确认的方向与接口

用户明确选择 Codex → PuddingAgent 内的 Agent。本接口不能据此推断为向 Codex 聊天投递。

- 首次通过 curl 实测 `GET http://127.0.0.1/api/external/v1/token`：HTTP 200，JSON 对象包含 tokenId、name、displayPrefix、scopes、workspaces、expiresAtUtc、status；状态 Active，工作区 default，权限含 agents.read 和 messages.send。
- token 自检响应不提供端点发现列表。通过 `ExternalWorkspaceAgentController` 与 DTO 核对消息路径、Idempotency-Key、回执和终态语义，再用运行中 HTTP 服务验收。
- 收件人目录 HTTP 200；选择唯一启用且未冻结的普通 Service Agent 鲸鲸。审计 Agent 不作为测试收件人。

## 交付

- 新增 [.pudding/skills/puddingagent-message/SKILL.md](../../../../.pudding/skills/puddingagent-message/SKILL.md)、manifest、标准库 Python 客户端和隔离 HTTP 回归测试；根代码地图登记入口。
- 客户端提供 doctor、agents、send、status。请求正文、幂等键、Token 身份及凭据指纹先落盘再 POST；同一 operation 的并发调用互斥，目标/正文/凭据变化拒绝重发。已取得 messageId 后仅 GET。
- 接单、canonical 会话 ACK、成功终态回复分别呈现。默认等待回复；网络结果未知或未达到证据阈值退出 2，不能据此声称送达或换键重发。无 messageId 的重放限一小时；超过窗口需核对服务端。
- 凭据位于仓库外 `D:\Keys\puddingagent-external-api-gpt.json`，按服务/API/Token 名称命名。禁用权限继承，仅当前用户 MAXSUN\hyfree 与 SYSTEM 有 FullControl；不改整个 Keys 目录权限。不在命令参数、环境变量、日志、skill 中保存 Token。
- `C:\Users\hyfree\.codex\skills\puddingagent-message` 为指向仓库 skill 的目录联接，提供 Codex 个人技能发现入口，不复制第二份实现。当前进程可以直接执行脚本；后续会话从技能目录发现它。
- 凭据不属于仓库。运行请求、回执和验证产物位于已忽略的 `.work/pudding-message/`；这些文件仍可能含业务消息，需按用户数据保留，不提交。

## 验证

- 客户端隔离回归：14 通过、0 失败。覆盖 202 未送达、仅投递未执行、执行失败、断线重试保持键和正文、已知消息只读查询、内容/凭据变化拒绝重放、超时窗口、409 冲突与处理中、重定向、错误身份/格式回执、凭据回显脱敏与并发操作。
- skill-creator 的 quick_validate：通过。验证器需要 PyYAML，依赖仅安装于 `.work/pudding-message/validation-20261103/validation-deps/`；运行客户端无需第三方依赖。
- 真实消息 `extmsg-95ce0c239ada55645e392f0530c793f4`，会话 `206a9b48ec904ebb93e7541131fbb835`。UTC 03:11:03.857 收到投递 ACK，03:12:20.189 执行终止为 succeeded，replyIsError=false。
- 唯一测试标记的完整回复为 `ACK:smoke-d15aa4ff6bf94a7e841bab56f7eef40b`，与发送正文逐字比对一致。证明本次模型收到对应内容并产生回复，而不只是 HTTP 接单。
- 对相同路径/正文/Idempotency-Key 做真实 POST 重放，HTTP 202，messageId、conversationId、delivery 记录和 reply 全部相同，没有产生第二条消息。
- 回执与验证记录：`.work/pudding-message/validation-20261103/`；请求日志：`.work/pudding-message/smoke-d15aa4ff6bf94a7e841bab56f7eef40b/state.json`。
- 凭据 ACL 写入后复查通过；变更文件和本次验证产物扫描未发现凭据原文。新增文档 Front Matter 与改动链接定向检查通过。

## 边界与未完成部分

本次端到端收发目标已完成。未修改、部署或重启 Pudding 服务端；未做断电恢复/长时间停机/Token 到期后恢复验收。客户端保存证据并明确区分未知、投递、执行，不承诺服务端永久幂等或永久可查。当前 Agent 未执行 commit 或 push。
