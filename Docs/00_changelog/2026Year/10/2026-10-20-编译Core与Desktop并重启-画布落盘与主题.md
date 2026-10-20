---
title: 2026-10-20 编译 Core 与 Desktop 并重启（画布落盘 + 主题注入）
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "按用户要求编译 Core+Desktop 并重启（前端不动）。到达时产品已由前序操作优雅停止（17:54:46，无崩溃），只做重编+启动。HEAD 82ee050 含 3 个提交：画布内容落盘（FilePanelCanvasStore，Core 重启不再清空）、htmlPath 无工作目录时接受绝对路径、面板画布主题由宿主注入。重编 Core 0 错误/6.75s、Desktop 0 错误/17.63s，产物 17:55:37–17:55:44 晚于 HEAD 17:51:57；PuddingCore.dll 3,868,160→3,870,720、PuddingDesktop.dll 1,143,296→1,143,808，FilePanelCanvasStore/PanelCanvasTheme 标记命中。冷启动 126.1s、0 崩溃、restartAttempts=0；Bridge Hello protocol=3 build=1.0.0+82ee050 握手成功；在线程序集 SHA 与产物一致。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, panel-canvas, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-编译Core与Desktop并重启-画布跟随与检查工具.md, Docs/00_changelog/2026Year/10/2026-10-20-重编Core与Desktop并重启-面板内容归Core.md]
related_files: [Source/PuddingCore/Panels/FilePanelCanvasStore.cs, Source/PuddingDesktop.Foundation/PanelCanvasTheme.cs, Source/PuddingBrowser.AgentTools/RenderPanelTool.cs]
slug: changelog-2026-10-20-rebuild-core-desktop-panel-persistence-theme
draft: false
---

# 2026-10-20 编译 Core 与 Desktop 并重启（画布落盘 + 主题注入）

> 用户要求：「编译 Core 和 Desktop，重启 Core 和 Desktop」。按此范围执行：**前端不重编**。

## 1. 到达时状态与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `82ee050`（17:51:57），工作树干净 |
| 产品进程 | **已停止**（17:54:46 优雅关闭，Bridge 断开 / SSM unsubscribe；无崩溃事件）⇒ 无需停进程 |
| 锁/端口 | `.pudding-host.lock` 可独占、80 端口空闲 |
| 待部署 | 3 个新提交：`55dfc91`（17:43 面板画布主题由宿主注入，不再写死白底）、`043ac1b`（17:48 `htmlPath` 无工作目录时接受绝对路径）、`82ee050`（17:51 画布内容落盘，Core 重启后不再清空） |

## 2. 重编

| 步骤 | 结果 |
|---|---|
| Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 6.75s** |
| Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（连带发布 Core）→ exit 0，**0 错误 / 168 警告 / 17.63s** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 17:55:37 | 466432 | `6F4EF598DF4DF789` |
| `PuddingDesktop.dll` | 17:55:37 | **1143808**（上版 1143296） | `5EA2FDF22087651A` |
| `core/PuddingAgent.dll` | 17:55:44 | 14336 | `263C7B9437CAF706` |
| `core/PuddingHost.dll` | 17:55:44 | 1132544 | `2CCF97E3D709D38B` |
| `core/PuddingRuntime.dll` | 17:55:43 | 4840448 | `1CF7CCB1346F311C` |
| `core/PuddingPlatform.dll` | 17:55:43 | 7389696 | `944E0F452E7EFF38` |
| `core/PuddingCore.dll` | 17:55:40 | **3870720**（上版 3868160） | `2A0BBD51509A67A8` |
| `core/PuddingBrowser.AgentTools.dll` | 17:55:41 | 220160 | `3093F75A89E63643` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（17:51:57）⇒ 部署生效。
- 新代码标记命中：`PuddingCore.dll` → `FilePanelCanvasStore`（落盘单一真源）与 `PanelCanvasStore`；`PuddingDesktop.dll` → `PanelCanvasTheme`（宿主注入主题）；`PuddingBrowser.AgentTools.dll` → `htmlPath`、`read_panel`。

## 4. 前端（未重编，保持不变）

本批无前端源码改动；运行目录保持 15:22 的 `v6.9.15 · 4059f9a` 干净构建。

## 5. 启动与验证

| 项 | 结果 |
|---|---|
| 启动 | Desktop pid **69916**（17:56:04）→ Core pid **30700**（17:57:06）→ Ready **17:59:24** |
| 冷启动 | Core `ready total=126105ms`（`logging-ready 38.5s`、`services-registered` delta 71.2s）；未被 60s 看门狗误杀 |
| 状态 | `WorkbenchReady / Ready`，`lastError=null`、`restartAttempts=0`、**0 崩溃事件** |
| 两端一致性 | Core 日志 `Browser Bridge Hello: protocol=3 build=build=1.0.0+82ee0501442981bfdbfdfb8bbbbe271de6d2741`（首连 accepted）⇒ 两端均为 `82ee050` 构建 |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |

## 6. 注意 / 未完成

- 到达时产品已停止（前序操作优雅关闭，无崩溃）——本次未执行停进程步骤，只做重编+启动；处置等价。
- 冷启动 126.1s（全量重编后首次加载/防护扫描）；启动租约修复保证不被看门狗误杀。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证画布落盘/主题）。
- 前端未重编（按请求）；后续若有前端提交，仍需先 `pnpm build` 再回填运行目录。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261020g/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261020-1755.log`、`temp/build/restart-20261020-1756.log`。
