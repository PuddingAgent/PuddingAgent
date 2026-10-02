---
title: 浏览器与 WebView2 验收
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: 每次修改聊天链路后至少完成：
categories: [docs, how-debug]
tags: [浏览器与, 验收, how_debuge]
related_docs: [Docs/08_how_debuge/README.md]
related_files: []
slug: how-debuge-07-浏览器与webview2验收
draft: false
---

# 浏览器与 WebView2 验收

> 本文档是 [How-Debuge 调试与诊断手册](README.md)（主索引）的主题分册，由原根目录 `How-Debuge.md` 于 2026-10-02 按主题拆分而来。
> 新增本主题的经验请直接追加到本文件；跨主题内容请回到主索引选择分册。

## 8. 浏览器验收

每次修改聊天链路后至少完成：

1. 登录并打开同一个 Conversation。
2. 发送一个唯一文本，例如 `E2E_<时间戳>`。
3. 确认 POST 返回 202。
4. 确认 SSE 实时显示用户消息和助手回复。
5. 等待历史同步周期，再确认回复没有消失。
6. 刷新页面，确认消息从持久化投影恢复。
7. 断开并恢复后端，确认 SSE 能重连和 replay。
8. 检查当前运行周期不存在新的 Error。

不能只以“页面出现文字”作为通过条件。

### 8.1 React 根节点空白

页面标题和静态 HTML 已加载、但 Chat 工作台整体空白时，先检查浏览器控制台，不要先归因于
代理、SSE 或历史接口。Mako 开发服务器出现
`Runtime error found, and it will cause a full reload` 时，继续读取紧随其后的第一条
`ReferenceError` 及组件栈；例如 Hook 在依赖数组中读取尚未初始化的 `const`，会在
`AgentMessageBubble` 首次渲染时直接中断整个 React 根节点。

修复后至少同时验证：

1. 受影响组件的聚焦 Jest 测试从相同异常恢复为通过；
2. `npm run build` 成功；
3. 重新加载 `/admin/chat` 后工作台和 Composer 可见；
4. 仅统计重新加载时间点之后的浏览器 Error，避免把修复前缓存的控制台记录误判为新错误。

