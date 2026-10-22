---
title: 2026-10-22 编译 Core 与 Desktop 并重启 Desktop（压缩修复 + 画布标注/工具栏）
author: hyfree
date: 2026-10-22
last_reviewed: 2026-10-22
status: archived
description: "按用户要求编译 Core+Desktop 并重启 Desktop（前端不动）。到达时产品已停止；运行目录 Core 为 10-22 10:54 构建、Desktop 壳仍是 10-21 19:21。HEAD 52c0894 含 10-21 19:20 以来的压缩修复（完成执行跨度联合预算、默认裁剪身份保留、截断摘要恢复+关联诊断）与画布改进（文字标注真正绘制、光标/快捷键/状态快照、工具栏开关按钮）。重编 Core 0 错误/8.21s、Desktop 0 错误/45.73s，产物 11:04:31–11:04:44 晚于 HEAD 11:01:56；Desktop.dll 1,173,504→1,177,088、Runtime.dll 4,952,576。启动后 Desktop 57168 / Core 9160、Ready 107.9s、health 200、0 崩溃、Bridge Hello build=1.0.0+52c0894；前端保持 v6.9.17。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, compaction, panel, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-21-诊断LLM上传中断并重编重启.md]
related_files: [Source/PuddingRuntime/Services/BoundedCompactionSummary.cs, Source/PuddingDesktop/MainWindow.xaml.cs, Source/PuddingDesktop.Foundation/PanelAnnotation.cs]
slug: changelog-2026-10-22-rebuild-core-desktop-restart-compaction-annotation
draft: false
---

# 2026-10-22 编译 Core 与 Desktop 并重启 Desktop（压缩修复 + 画布标注/工具栏）

> 用户要求：「编译 Core 和 Desktop，重启 Desktop」。按此范围执行：**前端不重编**。

## 1. 到达时状态与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `52c0894`（11:01:56），工作树干净 |
| 产品进程 | **已停止**（无 Desktop/Core；锁与 80 端口已释放） |
| 运行产物 | Core 为 10-22 10:54 构建（前序控制器）、Desktop 壳仍为 10-21 19:21 构建（旧） |
| 待部署 | 10-21 19:20 以来 12+ 提交：压缩链（`5b95a59`/`cb67b3e` 完成执行跨度联合预算、`1d42486` 默认裁剪身份保留、`3728e80` 截断摘要恢复+关联诊断）、画布（`2518cc6` 文字标注真正绘制/光标/快捷键/状态快照、`52c0894` 工具栏开关按钮与选中态回显） |

## 2. 重编

| 步骤 | 结果 |
|---|---|
| Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 8.21s** |
| Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（连带发布 Core）→ exit 0，**0 错误 / 218 警告 / 45.73s** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 11:04:31 | 466432 | `CD1EC71C11B78B51` |
| `PuddingDesktop.dll` | 11:04:31 | **1177088**（上版 1173504） | `715BB5B13FF54BCB` |
| `core/PuddingAgent.dll` | 11:04:44 | 14336 | `2E5A1E25DFF44D02` |
| `core/PuddingHost.dll` | 11:04:43 | 1132544 | `0983E0E167BB51FE` |
| `core/PuddingRuntime.dll` | 11:04:42 | **4952576**（上版 4876800） | `EAB36F8DFF219CAD` |
| `core/PuddingPlatform.dll` | 11:04:42 | 7395328 | `EA9A41147BBDDDF8` |
| `core/PuddingCore.dll` | 11:04:38 | 3874304 | `1912482609CE5185` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（11:01:56）⇒ 部署生效。
- 新代码标记命中（`PuddingDesktop.dll`）：`Toolbar`、`Annotat`（工具栏开关与标注层）。

## 4. 前端（未重编）

- 运行目录保持 10-20 23:31 的 `v6.9.17` 构建（`umi.429a848d.js`）；自该构建后仅 1 个前端提交 `e77f4c8`（10-20 23:31:34），已被该构建包含。其余改动均在 C#/Desktop 侧。
- 注意：`Source/PuddingPlatformAdmin/dist` 仍是 10-20 18:21 的旧产物，与运行目录不一致——完整部署前必须先 `pnpm build`。

## 5. 启动与验证

| 项 | 结果 |
|---|---|
| 启动 | Desktop pid **57168**（11:05:29）→ Core pid **9160**（11:06:41）→ Ready **11:08:42** |
| 冷启动 | Core `ready total=107908ms`（`logging-ready 26.2s`、`services-registered` delta 68.4s）；未被 60s 看门狗误杀 |
| 状态 | `WorkbenchReady / Ready`、Desktop Responding、`lastError=null`、`restartAttempts=0`、**0 崩溃事件** |
| 两端一致性 | Core 日志 `Browser Bridge Hello: protocol=3 build=build=1.0.0+52c08943be94ebc2d5f92f9b7003f…`（首连 accepted） |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |

## 6. 注意 / 未完成

- 到达时产品已停止（前序操作关闭）——本次无停进程步骤，只做重编+启动 Desktop（Core 由其子进程拉起）。
- 冷启动 107.9s（全量重编后首次加载/防护扫描）；启动租约修复保证不被看门狗误杀。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证压缩/标注行为）。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261022a/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261022-1105.log`、`temp/build/restart-20261022-1105.log`。
