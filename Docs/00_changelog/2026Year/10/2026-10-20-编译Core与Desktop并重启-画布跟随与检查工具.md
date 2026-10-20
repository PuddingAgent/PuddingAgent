---
title: 2026-10-20 编译 Core 与 Desktop 并重启（面板画布跟随 Core + 检查工具面）
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "按用户要求编译 Core+Desktop 并重启（前端不动）。到达时产品已由前序操作优雅停止（无崩溃），因此只做重编+启动。HEAD 48ee04d 含 4 个提交：面板画布跟随 Core 内容（拉取 + revision）、展示类能力独立信任来源、read/list/clear 面板工具 + htmlPath、read_panel 代理对修复。重编 Core 0 错误/7.60s、Desktop 0 错误/19.74s，产物 17:30:46–17:30:54 晚于 HEAD 17:29:09；PuddingDesktop.dll 1,137,664→1,143,296、AgentTools 199,168→220,160，read_panel/list_panels/clear_panel/htmlPath/Revision 标记命中。冷启动 109.9s、0 崩溃、restartAttempts=0；Bridge Hello protocol=3 build=1.0.0+48ee04d 握手成功；在线程序集 SHA 与产物一致。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, panel-canvas, tools, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-重编Core与Desktop并重启-面板内容归Core.md, Docs/00_changelog/2026Year/10/2026-10-20-重编Core与Desktop并重启-panel-render全链上线.md]
related_files: [Source/PuddingDesktop.Foundation/PanelCanvasFeed.cs, Source/PuddingBrowser.AgentTools/PanelInspectTools.cs, Source/PuddingBrowser.AgentTools/RenderPanelTool.cs]
slug: changelog-2026-10-20-rebuild-core-desktop-panel-feed-inspect-tools
draft: false
---

# 2026-10-20 编译 Core 与 Desktop 并重启（面板画布跟随 Core + 检查工具面）

> 用户要求：「编译 Core 和 Desktop，重启 Core 和 Desktop」。按此范围执行：**前端不重编**。

## 1. 到达时状态与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `48ee04d`（17:29:09），工作树干净 |
| 产品进程 | **已停止**（Desktop/Core 均不在；无崩溃事件，上一实例于 17:29:44 优雅关闭：Bridge 断开、SSM unsubscribe）⇒ 无需停进程 |
| 锁/端口 | `.pudding-host.lock` 可独占、80 端口空闲 |
| 待部署 | 4 个新提交：`7443836`（17:08 面板画布改为跟随 Core 内容：拉取 + revision 判定）、`0cf021f`（17:19 展示类能力改用独立信任来源）、`7e7ef04`（17:25 工具面补齐 `read_panel`/`list_panels`/`clear_panel` + `htmlPath` 发布）、`48ee04d`（17:29 `read_panel` 不再切开代理对 + 断言修正） |

## 2. 重编

| 步骤 | 结果 |
|---|---|
| Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 7.60s** |
| Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（连带发布 Core）→ exit 0，**0 错误 / 168 警告 / 19.74s** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 17:30:46 | 466432 | `7AA495CA976A7E4D` |
| `PuddingDesktop.dll` | 17:30:46 | **1143296**（上版 1137664） | `8262DAA2A29A7D43` |
| `core/PuddingAgent.dll` | 17:30:54 | 14336 | `61B0E2739E0B468D` |
| `core/PuddingHost.dll` | 17:30:54 | 1132544 | `D6E6C623B9202206` |
| `core/PuddingRuntime.dll` | 17:30:53 | 4840448 | `95417204ADCEF3ED` |
| `core/PuddingPlatform.dll` | 17:30:53 | 7389696 | `CA85757D69C73053` |
| `core/PuddingCore.dll` | 17:30:50 | 3868160 | `62719D0615DD2DA7` |
| `core/PuddingBrowser.AgentTools.dll` | 17:30:50 | **220160**（上版 199168） | `F38332093EFEAEB1` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（17:29:09）⇒ 部署生效。
- 新代码标记命中：`PuddingBrowser.AgentTools.dll` → `read_panel`、`list_panels`、`clear_panel`、`htmlPath`；`PuddingDesktop.dll` / `PuddingCore.dll` → `Revision`（画布跟随 Core 的内容版本判定）。

## 4. 前端（未重编，保持不变）

本批无前端源码改动；运行目录保持 15:22 的 `v6.9.15 · 4059f9a` 干净构建。

## 5. 启动与验证

| 项 | 结果 |
|---|---|
| 启动 | Desktop pid **125200**（17:31:10）→ Core pid **45060**（17:32:13）→ Ready **17:34:12** |
| 冷启动 | Core `ready total=109930ms`（`logging-ready 38.7s`、`services-registered` delta 57.9s）；无 60s 看门狗误杀 |
| 状态 | `WorkbenchReady / Ready`，`lastError=null`、`restartAttempts=0`、**0 崩溃事件** |
| 两端一致性 | Core 日志 `Browser Bridge Hello: protocol=3 build=build=1.0.0+48ee04dcaaa7bbf0d8bf6db5250f41fd69079ff`（首连即 accepted）⇒ Core/Desktop 均为 `48ee04d` 构建 |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |

## 6. 注意 / 未完成

- 到达时产品已停止（前序操作 17:29:44 优雅关闭，无崩溃）——本次未执行停进程步骤，只做重编+启动；处置上等价。
- 冷启动 109.9s（全量重编后首次加载/防护扫描）；启动租约修复保证不被看门狗误杀。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证面板画布/检查工具）。
- 前端未重编（按请求）；后续若有前端提交，仍需先 `pnpm build` 再回填运行目录。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261020f/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261020-1731.log`、`temp/build/restart-20261020-1731.log`。
