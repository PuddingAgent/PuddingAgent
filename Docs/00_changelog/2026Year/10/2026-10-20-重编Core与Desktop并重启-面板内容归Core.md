---
title: 2026-10-20 重编 Core 与 Desktop 并重启（面板内容归 Core + 匿名 PUT 移除）
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "按用户要求只重编 Core+Desktop 并重启（前端不动）。HEAD e71b875 含 4 个新提交：能力 DefaultGrantable 补项、面板内容归 Core（PanelCanvasStore + HTTP 只读入口）、render_panel 先写 Core 再展示、撤掉匿名 PUT /api/panels/{id}。重编 Core 0 错误/6.57s、Desktop 0 错误/19.66s，产物 16:59:19–16:59:27 晚于 HEAD 16:57:50，PanelCanvasStore/PanelCanvasApiController/api/panels 标记命中。冷启动 153.4s，租约续期有效、0 崩溃；Browser Bridge 首次握手被取消、第二次 accepted（protocol=3 build=1.0.0+e71b875）证明确实加载新构建。API 实测 GET /api/panels 200、未知 panel 404、PUT 405（匿名写入口已移除）。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, panel-canvas, security, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-重编Core与Desktop并重启-panel-render全链上线.md, Docs/00_changelog/2026Year/10/2026-10-19-启动失败模式修复-租约重试与启动租约前移.md]
related_files: [Source/PuddingCore/Panels/PanelCanvasStore.cs, Source/PuddingPlatform/Controllers/Api/PanelCanvasApiController.cs, Source/PuddingBrowser.AgentTools/RenderPanelTool.cs]
slug: changelog-2026-10-20-rebuild-core-desktop-panel-canvas-store
draft: false
---

# 2026-10-20 重编 Core 与 Desktop 并重启（面板内容归 Core + 匿名 PUT 移除）

> 用户要求：「编译，重启 Core 和 Desktop」。按此范围执行：**前端不重编**，只重编 .NET 并重启进程。

## 1. 基线与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `e71b875`（16:57:50），工作树干净 |
| 运行实例 | Desktop 119400 / Core 54164（16:17–16:18 构建，上一轮） |
| 待部署 | 4 个新提交：`10e011b`（16:30 `shell.document_preview`/`shell.panel_render` 补进 DefaultGrantable）、`5637148`（16:50 面板内容归 Core：`PanelCanvasStore` + HTTP 只读入口）、`af1880b`（16:56 `render_panel` 改为「先写 Core、再尽力展示」）、`e71b875`（16:57 撤掉匿名 `PUT /api/panels/{id}`） |

## 2. 执行

| 步骤 | 结果 |
|---|---|
| 停进程 | `core/stop` 优雅停止 → 结束 Desktop；无残留、80 端口空闲、锁可独占 |
| 重编 Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 6.57s** |
| 重编 Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（连带发布 Core）→ exit 0，**0 错误 / 166 警告 / 19.66s** |
| 启动 | Desktop pid **107588**（16:59:48）→ Core pid **31868**（17:01:42）→ Ready **17:04:24** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 16:59:19 | 466432 | `E347489AEAFEC819` |
| `PuddingDesktop.dll` | 16:59:19 | 1137664 | `8268CE8F09C5F57B` |
| `core/PuddingAgent.dll` | 16:59:27 | 14336 | `55C10ECB06A8DCD0` |
| `core/PuddingHost.dll` | 16:59:27 | 1132544 | `5256DC2B2E62E6CB` |
| `core/PuddingRuntime.dll` | 16:59:25 | 4840448 | `95EF3CDB680D5918` |
| `core/PuddingPlatform.dll` | 16:59:26 | **7389696**（上版 7384064） | `3C23389B3FC35CA2` |
| `core/PuddingCore.dll` | 16:59:23 | **3868160**（上版 3863040） | `C75EFBAB175A38C5` |
| `core/Pudding.Contracts.dll` | 16:59:21 | 263168 | `F831AA19CEF9801F` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（16:57:50）⇒ 部署生效。
- 新代码标记命中：`PuddingCore.dll` → `PanelCanvasStore`；`PuddingPlatform.dll` → `PanelCanvasApiController`、`api/panels`；`PuddingBrowser.AgentTools.dll` → `RenderPanelTool`；`Pudding.Contracts.dll` → `PanelRender`。

## 4. 前端（未重编，保持不变）

本批无前端源码改动；运行目录保持 15:22 的 `v6.9.15 · 4059f9a` 干净构建（页角徽标不变）。

## 5. 重启与两端一致性

| 项 | 结果 |
|---|---|
| 冷启动 | Core `ready total=153420ms`（`logging-ready 33.0s`；`services-registered`/`middleware-mapped` 为主要耗时）；Desktop 冷启动约 114s 后拉起 Core |
| 看门狗 | 启动租约持续续期，`restartAttemptsInWindow=0`、`lastError=null`、**0 崩溃事件** |
| Bridge 握手 | 17:04:29 首次 Hello 被取消（`hello_cancelled`，WebView 初始化竞争）；17:04:38 重连 **accepted**：`protocol=3 build=build=1.0.0+e71b87541920800ebfda425136c9af60aafa1418 rev=e71b8754192080` ⇒ Core/Desktop 均为本轮构建 |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |

## 6. 新面板 API 实测（部署后）

| 请求 | 结果 | 说明 |
|---|---|---|
| `GET /api/panels` | **200** `[]` | 只读列表入口已在（匿名可读，空列表） |
| `GET /api/panels/not-exist` | **404** | 未知 panel 正确返回不存在 |
| `PUT /api/panels/test-panel` | **405** | 匿名写入口已移除（`e71b875` 的安全修复生效；控制器只剩 GET） |

## 7. 注意 / 未完成

- 冷启动 153.4s：全量重编后首次加载/防护扫描，启动租约修复保证不被 60s 看门狗误杀。
- Bridge 首次 Hello 取消属启动期 WebView 初始化竞争，随后自动重连成功；无需人工处理，但若频繁出现可另行排查。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证面板链路）。
- 前端未重编（按请求）；后续若有前端提交，仍需先 `pnpm build` 再回填运行目录。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261020e/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261020-1700.log`、`temp/build/restart-20261020-1700.log`。
