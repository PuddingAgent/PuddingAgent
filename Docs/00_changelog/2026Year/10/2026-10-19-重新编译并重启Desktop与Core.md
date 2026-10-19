---
title: 2026-10-19 重新编译并重启Desktop与Core
author: hyfree
date: 2026-10-19
last_reviewed: 2026-10-19
status: archived
description: "按用户要求重编译前端 6.9.10/Core/Desktop 并重启产品进程；首次启动因旧 Core 残留 .pudding-host.lock 导致新 Core 启动崩溃，清理全部 Pudding 进程后恢复；现单 Desktop+单 Core，端口 80，/conversation-open 85.7ms。"
categories: [docs, changelog]
tags: [deployment, desktop, core, restart]
related_docs: [Docs/14_reports/默认助手启动后消息加载缓慢诊断与修复方案-2026-10-18.md]
related_files: [Source/PuddingDesktop/PuddingDesktop.csproj, Source/PuddingAgent/PuddingAgent.csproj]
slug: changelog-2026-10-19-rebuild-restart-desktop-core
draft: false
---

# 2026-10-19 重新编译并重启Desktop与Core

## 操作

1. 停止旧 Desktop（PID 81900）及其 Core（PID 125320），确认无 Pudding 进程/80 端口监听。
2. 重编译：
   - 前端 `pnpm run build` → **6.9.10**（bundle budget ok，`umi.f463e717.js`）；
   - `dotnet build Source/PuddingAgent/PuddingAgent.csproj` 成功（0 error）；
   - `dotnet build Source/PuddingDesktop/PuddingDesktop.csproj` 成功（0 error，155 warnings）。
3. 启动 Desktop 实际路径：
   `temp/build/winui3/bin/PuddingDesktop/Debug/net10.0-windows10.0.19041.0/win-x64/PuddingDesktop.exe`
   其 `core/` 已随 Desktop 构建更新为 6.9.10 前端。

## 遇到的问题

- 第一次启动后 Desktop 窗口长时间黑屏、无 Core；事件日志显示新 Core 因
  `System.IO.IOException: The process cannot access the file 'D:\Data\.pudding-host.lock' because it is being used by another process`
  崩溃——旧 Desktop 的 Core 未完全回收，残留句柄占用 DataRoot 租约。
- 处理：清理全部 `PuddingDesktop`/`PuddingAgent` 进程，确认锁释放后重新启动；未删除锁文件、未绕过互斥。

## 最终状态

| 项 | 值 |
|---|---|
| Desktop | PID 123624，窗口 `Pudding`，Responding=True |
| Core | PID 59204（Desktop 子进程），监听 `0.0.0.0:80` |
| 前端 | `/admin/index.html` 200，引用 `umi.f463e717.js` → **6.9.10** |
| `/conversation-open` | 200，**85.7ms**，20 条历史，activeTurn terminal=1 |
| 产物 | 前端/Core/Desktop 均 14:04–14:05 重建；工作树干净（无未提交代码改动） |

验证截图：`temp/test-out/desktop-restarted-1019.png`（桌面工作台已正常加载，右下状态栏 Core 运行中）。

## 未完成/注意

- 本次只重启产品进程；真实 WebView2 性能与两段式生命周期验收仍需外部控制器判定。
- 前端构建是否带 `+dirty` 取决于构建时工作树；本次构建时工作树干净。
