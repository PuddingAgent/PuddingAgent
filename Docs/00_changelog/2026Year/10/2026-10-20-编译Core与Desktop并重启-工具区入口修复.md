---
title: 2026-10-20 编译 Core 与 Desktop 并重启（折叠工具区入口修复）
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "按用户要求编译 Core+Desktop 并重启（前端不动）。到达时产品已停止（无崩溃），只做重编+启动。HEAD 31b9e42 为单提交：折叠态工具区入口不再压住前端顶栏、能力调用必展开工具区（MainWindow.xaml/.cs）。重编 Core 0 错误/6.55s、Desktop 0 错误/14.06s，产物 18:07:55–18:08:02 晚于 HEAD 18:05:48，ToolEntryButton 标记命中。冷启动 137.6s、0 崩溃、restartAttempts=0；Bridge Hello protocol=3 build=1.0.0+31b9e42 握手成功；在线程序集 SHA 与产物一致。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, ui-fix, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-编译Core与Desktop并重启-画布落盘与主题.md]
related_files: [Source/PuddingDesktop/MainWindow.xaml, Source/PuddingDesktop/MainWindow.xaml.cs]
slug: changelog-2026-10-20-rebuild-core-desktop-tool-entry-fix
draft: false
---

# 2026-10-20 编译 Core 与 Desktop 并重启（折叠工具区入口修复）

> 用户要求：「编译 Core 和 Desktop，重启 Core 和 Desktop」。按此范围执行：**前端不重编**。

## 1. 到达时状态与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `31b9e42`（18:05:48），工作树干净 |
| 产品进程 | **已停止**（无 Desktop/Core；锁与 80 端口已释放；无崩溃事件）⇒ 无需停进程 |
| 待部署 | 单提交 `31b9e42`：折叠态工具区入口不再压住前端顶栏；能力调用必展开工具区（`MainWindow.xaml` / `MainWindow.xaml.cs`） |

## 2. 重编

| 步骤 | 结果 |
|---|---|
| Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 6.55s** |
| Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（连带发布 Core）→ exit 0，**0 错误 / 168 警告 / 14.06s** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 18:07:55 | 466432 | `7550B691049F43E7` |
| `PuddingDesktop.dll` | 18:07:55 | 1143808 | `6710D9B6D872B9AE`（内容更新） |
| `core/PuddingAgent.dll` | 18:08:02 | 14336 | `87B7E0D40FCE7483` |
| `core/PuddingHost.dll` | 18:08:01 | 1132544 | `5BF2EA0277BECC3E` |
| `core/PuddingPlatform.dll` | 18:08:01 | 7389696 | `C3A99FF9187FC4F0` |
| `core/PuddingCore.dll` | 18:07:58 | 3870720 | `253644A9A94364EB` |
| `core/PuddingBrowser.AgentTools.dll` | 18:07:58 | 220160 | `73DDE52A2FB1D811` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（18:05:48）⇒ 部署生效。
- 新代码标记命中：`PuddingDesktop.dll` → `ToolEntryButton`（XAML 折叠入口新字段）。

## 4. 前端（未重编，保持不变）

本批无前端源码改动；运行目录保持 15:22 的 `v6.9.15 · 4059f9a` 干净构建。

## 5. 启动与验证

| 项 | 结果 |
|---|---|
| 启动 | Desktop pid **33788**（18:08:14）→ Core pid **66440**（18:09:29）→ Ready **18:11:55** |
| 冷启动 | Core `ready total=137559ms`（`logging-ready 26.9s`、`services-registered` delta 98.7s）；未被 60s 看门狗误杀 |
| 状态 | `WorkbenchReady / Ready`，`lastError=null`、`restartAttempts=0`、**0 崩溃事件** |
| 两端一致性 | Core 日志 `Browser Bridge Hello: protocol=3 build=build=1.0.0+31b9e426f7e237c05e67decc971b25…`（首连 accepted）⇒ 两端均为 `31b9e42` 构建 |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |

## 6. 注意 / 未完成

- 到达时产品已停止（前序操作关闭，无崩溃）——本次只做重编+启动。
- 冷启动 137.6s（全量重编后首次加载/防护扫描）；启动租约修复保证不被看门狗误杀。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证工具区交互）。
- 前端未重编（按请求）；后续若有前端提交，仍需先 `pnpm build` 再回填运行目录。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261020h/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261020-1808.log`、`temp/build/restart-20261020-1808.log`。
