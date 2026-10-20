---
title: 2026-10-20 编译 Core 与 Desktop 并重启（面板标注层与制成品 P1 闭环）
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "按用户要求编译 Core+Desktop 并重启（前端不动）。到达时产品已停止（无崩溃），运行目录产物为 19:22 构建。HEAD bcd3e03 含一批 Desktop 面板能力提交：标注层（归一化 0-999 / 拖框点选）、画布文档/制成品存储（PanelCraftStore）、图像来源白名单、标注遮罩跟随滚动修复。重编 Core 0 错误/9.99s、Desktop 0 错误/17.67s，产物 19:35:33–19:35:42 晚于 HEAD 19:34:03；PuddingDesktop.dll 1,161,728，PanelCraftStore/PanelAnnotation/PanelImageBridge/PanelCanvasDocument 标记命中。冷启动 109.2s、0 崩溃；Browser Bridge 首次 Hello 被取消后 gen=2 重连 accepted（protocol=3 build=1.0.0+bcd3e03）；在线程序集 SHA 与产物一致。前端 18:21 构建已含唯一前端提交 777144e，无需重建。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, panel-annotation, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-编译Core与Desktop并重启-工具区入口修复.md]
related_files: [Source/PuddingDesktop.Foundation/PanelAnnotation.cs, Source/PuddingDesktop.Foundation/PanelCraftStore.cs, Source/PuddingDesktop/PanelImageBridge.cs]
slug: changelog-2026-10-20-rebuild-core-desktop-panel-annotation-craft
draft: false
---

# 2026-10-20 编译 Core 与 Desktop 并重启（面板标注层与制成品 P1 闭环）

> 用户要求：「编译 Core 和 Desktop，重启 Core 和 Desktop」。按此范围执行：**前端不重编**。

## 1. 到达时状态与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `bcd3e03`（19:34:03），工作树干净 |
| 产品进程 | **已停止**（无 Desktop/Core；锁与 80 端口已释放；无崩溃事件）⇒ 无需停进程 |
| 运行产物 | 19:22:29–19:22:40 构建（前序控制器产物，缺 19:29/19:34 两个修复提交） |
| 待部署 | 18:07 以来的面板 P1 批次：标注层（坐标归一化 0–999、拖框/点选）、画布文档与制成品存储（`PanelCraftStore`）、图像来源白名单（`PanelImageSource`）、Desktop 侧图像桥（`PanelImageBridge`）、标注遮罩跟随滚动/缩放修复 |

## 2. 重编

| 步骤 | 结果 |
|---|---|
| Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 9.99s** |
| Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（连带发布 Core）→ exit 0，**0 错误 / 168 警告 / 17.67s** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 19:35:33 | 466432 | `35D5C865F39FA30E` |
| `PuddingDesktop.dll` | 19:35:33 | **1161728** | `1462D4A0B714CD1E` |
| `core/PuddingAgent.dll` | 19:35:42 | 14336 | `FCF21B65FF134DA9` |
| `core/PuddingHost.dll` | 19:35:42 | 1132544 | `0D3CC6CC1F343971` |
| `core/PuddingRuntime.dll` | 19:35:41 | 4840448 | `E384F8E7735CE775` |
| `core/PuddingPlatform.dll` | 19:35:41 | 7389696 | `5E39B5B5FAA378E3` |
| `core/PuddingCore.dll` | 19:35:37 | 3870720 | `8560E8D000BDA4CA` |
| `core/PuddingBrowser.AgentTools.dll` | 19:35:38 | 220160 | `15213A19723AD213` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（19:34:03）⇒ 部署生效。
- 新代码标记命中（`PuddingDesktop.dll`）：`PanelCraftStore`、`PanelAnnotation`、`PanelImageBridge`、`PanelCanvasDocument`。

## 4. 前端（未重编）

本批唯一前端提交为 `777144e`（18:21:19「添加到对话」）；`dist` 与运行目录均为 **18:21:38** 构建（`umi.9f112d89.js`，`v6.9.15`），已包含该提交的修改 ⇒ 前端无需重建；其余改动均在 C#/Desktop 侧。

## 5. 启动与验证

| 项 | 结果 |
|---|---|
| 启动 | Desktop pid **66248**（19:35:55）→ Core pid **55692**（19:37:19）→ Ready **19:39:25** |
| 冷启动 | Core `ready total=109249ms`（`logging-ready 26.6s`、`services-registered` delta 69.7s）；未被 60s 看门狗误杀 |
| 状态 | `WorkbenchReady / Ready`，`lastError=null`、`restartAttempts=0`、**0 崩溃事件** |
| 两端一致性 | Browser Bridge 首次 Hello 于 19:39:30 被取消（`hello_cancelled`，WebView 初始化竞争）；19:39:31 gen=2 重连 **accepted**：`protocol=3 build=build=1.0.0+bcd3e032886abf8aa9cd5800fb34315c3d6848a` |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |

## 6. 注意 / 未完成

- 到达时产品已停止（前序操作关闭，无崩溃）——本次只做重编+启动。
- 冷启动 109.2s（全量重编后首次加载/防护扫描）；启动租约修复保证不被看门狗误杀。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证标注层/制成品）。
- 前端未重编（本批无需）；后续若有前端提交，仍需先 `pnpm build` 再回填运行目录。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261020i/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261020-1935.log`、`temp/build/restart-20261020-1936.log`。
