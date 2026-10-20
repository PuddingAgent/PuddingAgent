---
title: 2026-10-20 重编 Core 与 Desktop 并重启（文档读取接口上线）
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "按用户要求只重编 Core+Desktop 并重启（前端不动）。HEAD 4c84c64 较运行实例（10:37 构建）多出 bb0fd02 文档读取接口等 5 个提交；重编 Core 0 错误/10.18s、Desktop 0 错误/36.96s，产物 11:08:44–11:08:53 晚于 HEAD 11:01:34，PuddingPlatform.dll 含 DocumentPreviewPathPolicy / api/workspaces/{workspaceId}/files 标记；新 API 实测 401（路由存在需鉴权）。冷启动 134s（services-registered 71.5s）期间启动租约持续续期，Desktop 未误杀（restartAttempts=0、0 崩溃），最终 Desktop 58084 / Core 129032、health 200、在线程序集 SHA 与产物一致。前端保持 10:35 的干净构建（v6.9.14/5b5f0b7/dirty=false）。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-按手册部署23提交到运行目录.md, Docs/00_changelog/2026Year/10/2026-10-19-启动失败模式修复-租约重试与启动租约前移.md]
related_files: [Source/PuddingPlatform/Controllers/Api/WorkspaceFileApiController.cs, Source/PuddingPlatform/Services/DocumentPreviewPathPolicy.cs]
slug: changelog-2026-10-20-rebuild-core-desktop-document-read-api
draft: false
---

# 2026-10-20 重编 Core 与 Desktop 并重启（文档读取接口上线）

> 用户要求：「编译并重启 core 和 desktop」。按此范围执行：**前端不重编**，只重编 .NET 并重启进程。

## 1. 基线与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `4c84c64`（11:01:34），工作树干净 |
| 运行实例 | Desktop 129720（10:45:04）/ Core 46632（10:49:07），`WorkbenchReady/Ready` |
| 运行产物 | 10:37:15–10:37:30 构建（含至 10:34 的提交） |
| 未部署的 C# 变更 | `bb0fd02`（11:00）新增 `WorkspaceFileApiController`（`GET api/workspaces/{workspaceId}/files/content`）+ `DocumentPreviewPathPolicy` + 测试；其余为 docs |

## 2. 执行

| 步骤 | 结果 |
|---|---|
| 停进程 | `core/stop` 优雅停止 → 结束 Desktop；无残留、80 端口空闲、`.pudding-host.lock` 可独占（未删锁） |
| 重编 Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 10.18s** |
| 重编 Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（`BuildCoreBundle` 连带发布 Core）→ exit 0，**0 错误 / 166 警告 / 36.96s** |
| 启动 | Desktop pid **58084**（11:09:28）→ Core pid **129032**（11:10:36）→ Ready **11:12:50** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 11:08:44 | 466432 | `8ADE0FC0734F4066` |
| `PuddingDesktop.dll` | 11:08:44 | 1080832 | `BDE828EA1DD6346A` |
| `core/PuddingAgent.dll` | 11:08:53 | 14336 | `7C8FD06872AD8441` |
| `core/PuddingHost.dll` | 11:08:52 | 1129984 | `078C29B58828E8B8` |
| `core/PuddingRuntime.dll` | 11:08:51 | 4840448 | `66D5CFDE9C983B1E` |
| `core/PuddingPlatform.dll` | 11:08:52 | **7385088**（上版 7378432） | `F49D9AB32B8408C7` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（11:01:34）⇒ 部署生效。
- 新功能已编入 `core/PuddingPlatform.dll`：`DocumentPreviewPathPolicy`、`api/workspaces/{workspaceId}/files` 标记均命中。

## 4. 前端（未重编，保持不变）

- 本批无前端源码改动；且 `PuddingHostContent.props` 用 `PreserveNewest` 复制 `dist` → `wwwroot/admin`，本次 publish **未覆盖**运行目录中更旧的 `dist`（10:06、dirty=true），因此运行目录继续保留 10:35 的**干净构建**：
  `umi.1feb5205.js`，注入身份 `version=6.9.14 / commit=5b5f0b7 / dirty=false / builtAt=2026-10-20T02:35:39Z`。
- 页角徽标实测 `v6.9.14 · 5b5f0b7 · 2026-10-20 10:35`（截图见证据目录）。

## 5. 重启与验证

| 项 | 结果 |
|---|---|
| 冷启动耗时 | Core `ready total=134s` 级（`logging-ready 26.5s`、`services-registered` 单阶段 **71.5s**、`middleware-mapped` 后完成）；Desktop 冷启动 67s 后才拉起 Core |
| 启动看门狗 | 全程 `PUDDING_DESKTOP_STARTING` 租约续期，`restartAttemptsInWindow=0`、`lastError=null`、**0 崩溃事件** ⇒ `5bdf59a` 的启动租约前移修复有效（60s 超时不再误杀） |
| 进程/端口 | Desktop 58084、Core 129032；`0.0.0.0:80`；`/health/ready` → **200 ready** |
| 在线身份 | `/api/system/build-identity`：`PuddingAgent/Core/Runtime/MemoryEngine/Platform` 均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |
| 新 API 实测 | `GET /api/workspaces/default/files/content?path=README.md` → **401**（路由存在、需鉴权；未部署时会 404） |
| 控制面 | 无 token 访问 8199 diagnostics → 401（鉴权正常） |

## 6. 注意 / 未完成

- **冷启动仍约 2 分钟**：全量重编后首次加载/防护扫描导致 `services-registered` 单阶段 71s；修复只保证不被看门狗误杀、不保证快速。若希望缩短，建议保持「重编后先预热一次再启动 Desktop」的运维习惯（或给构建产物目录加实时防护排除）。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证）。
- 前端未重编（按请求）；`Source/PuddingPlatformAdmin/dist` 目前仍是 10:06 的 dirty 构建，运行目录使用的是 10:35 干净构建——**下次若跑完整部署手册，请先 `pnpm build` 再回填**，不要直接依赖 dist。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261020b/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261020-1110.log`、`temp/build/restart-20261020-1110.log`。
