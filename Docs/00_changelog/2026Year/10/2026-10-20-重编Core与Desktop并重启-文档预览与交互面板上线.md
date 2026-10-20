---
title: 2026-10-20 重编 Core 与 Desktop 并重启（Desktop 文档预览/交互面板上线）
author: hyfree
date: 2026-10-20
last_reviewed: 2026-10-20
status: archived
description: "按用户要求只重编 Core+Desktop 并重启（前端不动）。HEAD 756c3fa 含 Desktop 文档预览标签（WebView2→/admin/doc-preview）、交互面板媒体画布、单例画布修复，以及 12:00–15:22 的 document preview 全链提交；重编 Core 0 错误/13.52s、Desktop 0 错误/43.26s，产物 15:53:27–15:53:36 晚于 HEAD 15:48:26。冷启动 186.6s（services-registered 99.8s）期间启动租约持续续期、0 崩溃、restartAttempts=0；最终 Desktop 45976 / Core 43204、health 200、在线程序集 SHA 与产物一致；前端保持 15:22 的 v6.9.15/4059f9a 构建。"
categories: [docs, changelog]
tags: [deploy, desktop, core, restart, cold-start, changelog]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-20-重编Core与Desktop并重启-文档读取接口上线.md, Docs/00_changelog/2026Year/10/2026-10-19-启动失败模式修复-租约重试与启动租约前移.md]
related_files: [Source/PuddingDesktop/MainWindow.xaml.cs, Source/PuddingDesktop.Foundation/ToolWorkspaceTabs.cs, Source/PuddingCore/Documents/DocumentPathPolicy.cs]
slug: changelog-2026-10-20-rebuild-core-desktop-document-preview-panel
draft: false
---

# 2026-10-20 重编 Core 与 Desktop 并重启（Desktop 文档预览/交互面板上线）

> 用户要求：「编译，重启 Core 和 Desktop」。按此范围执行：**前端不重编**，只重编 .NET 并重启进程。

## 1. 基线与待部署差异

| 项 | 值 |
|---|---|
| HEAD | `756c3fa`（15:48:26），工作树干净 |
| 运行实例 | Desktop 58084（11:09:28）/ Core 129032（11:10:36），`WorkbenchReady/Ready` |
| 运行产物 | 11:08:44–11:08:53（上一轮） |
| 待部署 | 自 11:10 起 20+ 提交，含 document preview 全链（协议/能力层/Core 路径策略/工具/Desktop）与 Desktop 壳改动：<br>`2b7b6bb` 右侧工具区「文档预览」标签（WebView2 → `/admin/doc-preview`）<br>`9abe79c` 交互面板 = 可交互媒体画布（WebView2 载体 + 工具栏）<br>`756c3fa` 交互面板改单例画布 + 可用性边界 |

## 2. 执行

| 步骤 | 结果 |
|---|---|
| 停进程 | `core/stop` 优雅停止 → 结束 Desktop；无残留、80 端口空闲、锁可独占（未删锁） |
| 重编 Core | `dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore` → exit 0，**0 错误 / 140 警告 / 13.52s** |
| 重编 Desktop | `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore`（`BuildCoreBundle` 连带发布 Core）→ exit 0，**0 错误 / 166 警告 / 43.26s** |
| 启动 | Desktop pid **45976**（15:54:24）→ Core pid **43204**（15:55:51）→ Ready **15:59:09** |

## 3. 产物核对

| 产物 | 时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 15:53:27 | 466432 | `AA608173CEB3C55C` |
| `PuddingDesktop.dll` | 15:53:27 | **1135104**（上版 1080832） | `007A14CB52673178` |
| `core/PuddingAgent.dll` | 15:53:36 | 14336 | `41D878BDFE3F6685` |
| `core/PuddingHost.dll` | 15:53:35 | **1131520**（上版 1129984） | `9E9274926E4F059D` |
| `core/PuddingRuntime.dll` | 15:53:34 | 4840448 | `31982C6790B80FF6` |
| `core/PuddingPlatform.dll` | 15:53:35 | 7384064 | `3066AE702492F109` |
| `core/PuddingCore.dll` | 15:53:31 | **3863040**（上版 3858432） | `EE8F9B8BB22A22BA` |

- 全部 .NET 产物时间 **晚于** HEAD 提交时间（15:48:26）⇒ 部署生效。
- 新代码标记命中：`PuddingDesktop.dll` → `文档预览`、`面板画布不可用`、`OnOpenDocumentTab`、`panel-`（文档标签与交互面板画布）；`PuddingCore.dll` → `DocumentPathPolicy`、`DocumentFileReader`（文档读取唯一实现）。

## 4. 前端（未重编，保持不变）

- 本批要求仅 Core+Desktop；前端产物在 15:22 已由前序工作重建并回填（`umi.1d1ea28b.js`，`v6.9.15`）。
- `PuddingHostContent.props` 的 `PreserveNewest` 使本次 publish 未覆盖运行目录 index（内容一致），页角徽标实测 `v6.9.15 · 4059f9a · 2026-10-20 15:22`。

## 5. 冷启动与启动看门狗

| 项 | 结果 |
|---|---|
| 冷启动 | Core `ready total=186586ms`：`logging-ready 38.7s`、`services-registered` delta **99.8s**、`middleware-mapped` delta 16.8s；Desktop 冷启动约 90s 后才拉起 Core |
| 看门狗 | 期间启动租约每 5s 续期，`restartAttemptsInWindow=0`、`lastError=null`、**0 崩溃事件** ⇒ `5bdf59a` 修复继续生效（60s 超时不再误杀） |
| 观察 | 冷启动耗时呈上升（前两轮 118.9s → 134s → 本次 186.6s），与「重编后首次加载 + 本机防护扫描」一致；不影响正确性，但拉长重启窗口 |

## 6. 部署后验证

| 项 | 结果 |
|---|---|
| 进程/端口 | Desktop 45976、Core 43204；`0.0.0.0:80`；`/health/ready` → **200 ready** |
| 状态 | `WorkbenchReady` / `Ready`，`lastError=null`、`restartAttempts=0` |
| 在线身份 | `/api/system/build-identity`：`PuddingAgent/Core/Runtime/MemoryEngine/Platform` 均 `loaded=true` 且 `fileSha256` 与本轮产物逐一相等 |
| 崩溃 | 部署后 Windows 事件日志 0 条 PuddingAgent 崩溃 |

## 7. 注意 / 未完成

- **冷启动 186.6s**：如需缩短重启窗口，建议保留「重编后先预热一次再启动 Desktop」的运维习惯，或给 `temp\build\winui3` 与 `D:\Data` 加实时防护排除（本机环境问题，非代码问题；修复已保证不被看门狗误杀）。
- 重启终止了进程内 Agent 会话（用户可在网页端重新唤起继续验证文档预览/交互面板功能）。
- 前端未重编（按请求）；若后续有前端提交，仍需先 `pnpm build` 再回填运行目录。
- 未 `git push`（出网通道双向阻断）。
- 证据：`temp/test-out/deploy-20261020c/`（`pre-state.json`、`desktop-after-restart.png`、`desktop-stdout.log`）；日志 `temp/build/rebuild-20261020-1555.log`、`temp/build/restart-20261020-1555.log`。
