---
title: 2026-10-25 编译 Core 与 Desktop 并重启 Desktop（必盈金融工具 + 浏览器 Cookie 管理）
author: hyfree
date: 2026-10-25
last_reviewed: 2026-10-25
status: archived
description: "按用户要求编译 Core+Desktop 并重启 Desktop（前端不动）。到达时产品已停止；运行目录 Core 为 10-25 21:53 构建、Desktop 壳为 10-24 22:17。HEAD 2dc621c 含 10-22 以来的必盈（biying）金融工具链 S2–S5、运行时工具发现失败不再静默、浏览器 AgentAuthorized 默认可驱动、Desktop 浏览器「清除 Cookie」；新增 PuddingFinance 组件与 PuddingDesktop.ExitObserver 子进程。重编 Core 0 错误/7.43s、Desktop 0 错误/15.28s，产物 22:13:36–22:13:44 晚于 HEAD 22:11:12；PuddingDesktop.dll 1,188,352→1,196,032、PuddingFinance.dll 486,912。启动后 17.4s Ready、0 崩溃、Bridge Hello build=1.0.0+2dc621c；前端保持 v6.10.3。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, finance, browser, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-22-编译Core与Desktop并重启Desktop.md]
related_files: [Source/PuddingFinance/PuddingFinance.csproj, Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Runtime.cs, Source/PuddingDesktop/MainWindow.xaml.cs]
slug: changelog-2026-10-25-rebuild-core-desktop-biying-cookie
draft: false
---

# 2026-10-25 编译 Core 与 Desktop 并重启 Desktop（必盈金融工具 + 浏览器 Cookie 管理）

> 用户要求：「编译 Core 和 Desktop，重启 Desktop」。按此范围执行：**前端不重编**。

## 1. 到达时状态与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `2dc621c`（22:11:12），工作树干净 |
| 产品进程 | **已停止**（无 Desktop/Core；锁与 80 端口已释放） |
| 运行产物 | Core 为 10-25 21:53 构建（前序控制器）、Desktop 壳为 10-24 22:17 构建（旧） |
| 待部署 | 10-22 11:04 以来大批提交，主要三块：<br>① 必盈（biying）金融工具链 S2–S5（`PuddingFinance` 组件、行情/财务/板块资金工具、`biying_raw` 端点直通、宿主启动注册 `BiyingFinanceRuntime`）；<br>② 运行时工具发现失败不再静默（交出「可搜到但不可曝光」的 id）；<br>③ 浏览器：AgentAuthorized 上下文页面默认可被 Agent 驱动、Desktop 浏览器「清除当前网站 Cookie / 清除全部 Cookie」 |

## 2. 重编

| 步骤 | 结果 |
|---|---|
| Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 145 警告 / 7.43s** |
| Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（连带发布 Core）→ exit 0，**0 错误 / 154 警告 / 15.28s** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 22:13:36 | 466432 | `33CAF18633FC8D15` |
| `PuddingDesktop.dll` | 22:13:36 | **1196032**（上版 1188352） | `6047CFD086F49544` |
| `core/PuddingAgent.dll` | 22:13:44 | 14336 | `F4F1DB25009CB23C` |
| `core/PuddingHost.dll` | 22:13:44 | 1133056 | `66A074AF0DBACECC` |
| `core/PuddingRuntime.dll` | 22:13:43 | 5059584 | `1F1B49C01AB90917` |
| `core/PuddingPlatform.dll` | 22:13:43 | 7408640 | `201D03DA82916002` |
| `core/PuddingCore.dll` | 22:13:40 | 3919360 | `142BFE576007B074` |
| `core/PuddingFinance.dll` | 22:13:38 | **486912**（新组件） | `AF0592AD3C033F86` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（22:11:12）⇒ 部署生效。
- 标记命中：`PuddingDesktop.dll` → `Cookie`、`ClearCookie`（浏览器 Cookie 管理）；`PuddingHost.dll`/`PuddingRuntime.dll`/`PuddingFinance.dll` → `Biying`（金融工具链已编入）。

## 4. 前端（未重编）

自 10-25 09:38 的 `v6.10.3` 构建（`umi.8de15f3e.js`，dist 与运行目录一致）之后**无前端提交** ⇒ 无需重建。

## 5. 启动与验证

| 项 | 结果 |
|---|---|
| 启动 | Desktop pid **31940**（22:14:02）→ Core pid **41016**（22:14:10）→ Ready **22:14:29** |
| 新增子进程 | `PuddingDesktop.ExitObserver` pid 17068（父=Desktop，`observer\PuddingDesktop.ExitObserver.exe`）——退出观察者随产品启动 |
| 冷启动 | `ready total=17407ms`（`logging-ready 3.5s`、`services-registered` delta 8.9s）——本轮明显快于以往 |
| 状态 | `WorkbenchReady / Ready`、Desktop Responding、`lastError=null`、`restartAttempts=0`、**0 崩溃事件** |
| 两端一致性 | Core 日志 `Browser Bridge Hello: protocol=3 build=build=1.0.0+2dc621cbb63ecc3d248d92445e929…`（首连 accepted） |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |

## 6. 注意 / 未完成

- 到达时产品已停止（前序操作关闭）——本次无停进程步骤，只做重编+启动 Desktop（Core 由其子进程拉起）。
- 本次启动异常快（17.4s），推测文件缓存/防护预扫描已热；此前全量重编后冷启动曾达 100–190s，启动租约修复保证不被 60s 看门狗误杀。
- 必盈金融工具需要 provider 配置与配额；本轮只验证**已加载**（DLL + 启动无 DI 崩溃），真实工具调用验收仍需进程内 Agent 会话。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证）。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261025a/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261025-2214.log`、`temp/build/restart-20261025-2214.log`。
