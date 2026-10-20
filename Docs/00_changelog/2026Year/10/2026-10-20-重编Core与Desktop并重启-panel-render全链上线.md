---
title: 2026-10-20 重编 Core 与 Desktop 并重启（panel.render 全链上线）
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "按用户要求只重编 Core+Desktop 并重启（前端不动）。HEAD 72de494 含 panel.render 三段提交（线缆登记 / shell.panel_render 能力层 / Agent→交互面板全链）；重编 Core 0 错误/8.75s、Desktop 0 错误/16.89s，产物 16:17:15–16:17:39 晚于 HEAD 16:15:40，RenderPanel/RenderPanelTool/PanelRender 标记命中。冷启动 110.6s，租约续期全程有效、restartAttempts=0、0 崩溃；Browser Bridge Hello 报 protocol=3、build=1.0.0+72de494，证明两端均为新构建。最终 Desktop 119400 / Core 54164、health 200、在线程序集 SHA 与产物一致。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, panel-render, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-重编Core与Desktop并重启-文档预览与交互面板上线.md, Docs/00_changelog/2026Year/10/2026-10-19-启动失败模式修复-租约重试与启动租约前移.md]
related_files: [Source/Pudding.Contracts/Desktop/PanelRenderContracts.cs, Source/PuddingBrowser.AgentTools/RenderPanelTool.cs, Source/PuddingDesktop/MainWindow.xaml.cs]
slug: changelog-2026-10-20-rebuild-core-desktop-panel-render-chain
draft: false
---

# 2026-10-20 重编 Core 与 Desktop 并重启（panel.render 全链上线）

> 用户要求：「编译，重启 Core 和 Desktop」。按此范围执行：**前端不重编**，只重编 .NET 并重启进程。

## 1. 基线与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `72de494`（16:15:40），工作树干净 |
| 运行实例 | Desktop 45976（15:54:24）/ Core 43204（15:55:51，上一轮构建） |
| 待部署 | 3 个新提交：`cb6f571`（16:01 线缆登记 `panel.render`）、`7e3ceda`（16:09 `shell.panel_render` 能力层 + 契约）、`72de494`（16:15 Agent→交互面板完整链路：线缆映射 + 入口 + `RenderPanelTool` + WinUI 渲染） |

## 2. 执行

| 步骤 | 结果 |
|---|---|
| 停进程 | `core/stop` 优雅停止 → 结束 Desktop；无残留、80 端口空闲、锁可独占 |
| 重编 Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 8.75s** |
| 重编 Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（连带发布 Core）→ exit 0，**0 错误 / 166 警告 / 16.89s** |
| 启动 | Desktop pid **119400**（16:17:43）→ Core pid **54164**（16:18:43）→ Ready **16:20:48** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 16:17:15 | 466432 | `27E3966E56882028` |
| `PuddingDesktop.dll` | 16:17:15 | **1137664**（上版 1135104） | `902E87FD7C124AA2` |
| `core/PuddingAgent.dll` | 16:17:23 | 14336 | `21EB4ADF9DCAF927` |
| `core/PuddingHost.dll` | 16:17:23 | **1132544**（上版 1131520） | `9D6C04588E232E3A` |
| `core/PuddingRuntime.dll` | 16:17:21 | 4840448 | `88B6C75EB44FD5A0` |
| `core/PuddingPlatform.dll` | 16:17:22 | 7384064 | `038C86429B577450` |
| `core/PuddingCore.dll` | 16:17:18 | 3863040 | `E67ADAB24C0F95CD` |
| `core/Pudding.Contracts.dll` | 16:17:17 | **263168**（上版 252416） | `5CA23DA137EBCEF9` |
| `core/PuddingBrowser.AgentTools.dll` | 16:17:19 | **199168**（上版 188928） | `4088D79EC64A2EDE` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（16:15:40）⇒ 部署生效。
- 新代码标记命中：`PuddingDesktop.dll` → `RenderPanel`；`Pudding.Contracts.dll` → `PanelRender`（新契约）；`PuddingBrowser.AgentTools.dll` → `RenderPanelTool`（Agent 侧工具）。

## 4. 前端（未重编，保持不变）

本批无前端源码改动；运行目录保持 15:22 的 `v6.9.15 · 4059f9a` 干净构建（页角徽标不变）。

## 5. 重启与看门狗

| 项 | 结果 |
|---|---|
| 冷启动 | Core `ready total=110609ms`（`logging-ready 33.4s`、`services-registered` delta **62.6s**、`middleware-mapped` delta 4.0s）；Desktop 冷启动约 64s 后拉起 Core |
| 看门狗 | 启动租约持续续期，`restartAttemptsInWindow=0`、`lastError=null`、**0 崩溃事件** |
| 两端构建一致性 | Core 日志 `Browser Bridge Hello: protocol=3 build=build=1.0.0+72de4943541da5dda53a33c…` ⇒ Core 与 Desktop 均已是 `72de494` 构建、协议修订 3 握手成功 |

## 6. 部署后验证

| 项 | 结果 |
|---|---|
| 进程/端口 | Desktop 119400、Core 54164；`0.0.0.0:80`；`/health/ready` → **200 ready** |
| 状态 | `WorkbenchReady / Ready`，`lastError=null`、`restartAttempts=0` |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |
| 崩溃 | 部署后 Windows 事件日志 0 条 PuddingAgent 崩溃 |

## 7. 注意 / 未完成

- 冷启动耗时本轮 110.6s（上一轮 186.6s）——随「重编改动面 / 首次加载 / 防护扫描」波动，启动租约修复保证不被 60s 看门狗误杀。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证交互面板的 Agent→画布链路）。
- 前端未重编（按请求）；后续若有前端提交，仍需先 `pnpm build` 再回填运行目录。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261020d/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261020-1617.log`、`temp/build/restart-20261020-1618.log`。
