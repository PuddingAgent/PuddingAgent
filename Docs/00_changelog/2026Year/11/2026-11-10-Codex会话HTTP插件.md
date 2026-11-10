---
title: 2026-11-10 Codex 会话 HTTP 插件
author: hyfree
date: 2026-11-10
last_reviewed: 2026-11-10
status: archived
description: "为本机 HTTP 客户端提供 Codex 聊天管理、消息投递、回复读取和队列回执。"
categories: [docs, changelog]
tags: [codex, plugin, http]
related_docs: []
related_files: [Tools/CodexPlugins/plugins/chat-http/README.md, TestScripts/test-codex-chat-http.mjs, code_map.md]
slug: changelog-codex-chat-http-20261110
draft: false
---

# 2026-11-10 Codex 会话 HTTP 插件

## 改动

- [本地插件包与接口说明](../../../../Tools/CodexPlugins/plugins/chat-http/README.md)提供回环 HTTP 服务、当前会话绑定、聊天创建、改名、归档、恢复和分页读取。消息送入 Codex 原生队列，显式接续可在同一会话继续执行。带消息 ID 的请求保留去重回执。
- [本地插件市场](../../../../Tools/CodexPlugins/.agents/plugins/marketplace.json)登记插件。[根代码地图](../../../../code_map.md)登记调用入口。插件使用安装在本机的 Codex CLI，凭据位于当前用户的私有目录。
- [隔离集成测试](../../../../TestScripts/test-codex-chat-http.mjs)以两个 Codex app-server 客户端、临时用户目录和本机 Responses 测试替身验证 HTTP 调用。

## 验证

- `node --test TestScripts/test-codex-chat-http.mjs` 报告 6 项通过，含顶层集成用例。覆盖鉴权、参数、当前会话、忙时投递、跨进程队列、按消息 ID 重试、分页、改名、归档、恢复和单次 POST 接续后读回回复。
- 本机安装 `chat-http@pudding-local` 0.1.2。CLI 报告已启用，`skills/list` 返回已启用的插件技能。启动安装副本后，确认监听地址为 IPv4 回环，GET 能读回本聊天的初始请求和后续补充。连接文件的 ACL 只授予当前账户。
- 仓库全量文档检查的基线已有 62 个 Front Matter 问题和 134 个失效链接。本日志独立校验并核对所引用的文件。

## 未验证范围

真实桌面聊天使用只读验证。发送与模型回复在隔离会话中验证，模型输出由本机测试替身生成。
