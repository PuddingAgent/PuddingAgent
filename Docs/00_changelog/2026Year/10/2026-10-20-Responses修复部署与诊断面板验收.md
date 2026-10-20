---
title: Responses 修复部署与诊断面板验收
author: Codex
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "记录真实 DeepSeek Responses smoke、Core 制品部署与诊断面板验证。"
categories: [docs, changelog]
tags: [responses, deployment, diagnostics]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-会话失败诊断持久化与展示.md, Docs/00_changelog/2026Year/10/2026-10-20-会话压缩视觉路由与截断保护.md]
related_files: [Source/PuddingPlatformAdmin/src/pages/chat/components/ErrorDiagnosticPanel.tsx, Source/PuddingPlatformAdmin/package.json]
slug: changelog-2026-10-20-responses-deployment-verification
draft: false
---

# Responses 修复部署与诊断面板验收

日期：2026-10-20。

- Core 制品来自 `2d892fb` 干净工作树，通过 Desktop 控制面事务部署。执行前确认无活动 Command/Run、会话预约或绑定到执行中任务的有效 Assignment；未清理历史任务与队列数据。部署结果 `success/assembliesReloaded/coreRestarted=true`，准备与加载的托管清单 SHA-256 均为 `42d254cfb73181ca53573652daddfe60859bdf71e0c3beed08dae3211d8f3547`，Core Ready，`/health/ready` 返回 ready。
- 单独比对 `PuddingCore.dll`、`PuddingRuntime.dll`、`PuddingPlatform.dll` 与制品哈希一致；HTTP `/admin/index.html` 与已部署静态制品一致。
- 真实 DeepSeek `deepseek-flash` Responses 两轮 smoke：合成输入触发 echo 工具调用，回放完整 output 和 function_call_output 后流式回复成功；终态 `response.completed`、无 `[DONE]`，第二轮 input/output usage 为 71/2。未上传真实用户历史。
- 浏览器在已部署 6.9.16 页面确认徽标 `2d892fb`、无 dirty；真实历史诊断接口返回 200。浏览器内注入合成失败响应验证展开才请求（1 次）、复制报告含错误 ID/请求体大小、无页面异常；未向生产会话写入合成失败事件。
- 视觉检查后整理 `ErrorDiagnosticPanel.tsx` 的按钮行、间距、报告边框与滚动区域；读取中禁用复制，版本递增到 6.9.17。面板测试 3/3，类型检查通过。

未完成：6.9.17 的构建/加载及最终浏览器核验待下文补记。原始事故含上传阶段 Socket 10053；大请求网络稳定性不由小请求 smoke 证明。Runtime 扩大验证中原有限流等待测试阻塞的情况见压缩修改日志。
