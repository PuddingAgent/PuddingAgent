---
title: 2026-10-19 重编 Core 与 Desktop 并重启：熔断复盘与冷启动超时
author: hyfree
date: 2026-10-19
last_reviewed: 2026-10-19
status: archived
description: "用户截图显示『自动恢复已暂停』。复盘证实 15:43 的进程内 rebuild-restart 热部署后，新 Core 与未释放的 .pudding-host.lock 竞争，15:44:36 / 15:45:45 两次 CLR20r3 崩溃，Desktop 60 秒内 3 次失败熔断。随后按用户要求在 HEAD d0212f2（含 screen_capture 两提交）重编 Core+Desktop（0 error，1.72s / 13.60s），全量重编后首次启动实测 118.9s（services-registered 单阶段 65.0s，预热后 4.3s）会超过 60s 启动超时，故先 console 预热再起 Desktop；最终 Desktop 47972 / Core 22816、4.4s 内 Ready、在线程序集哈希与产物一致。"
categories: [docs, changelog]
tags: [deployment, desktop, core, restart, startup-timeout, diagnostics]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-19-修复后重编Core与Desktop并部署重启.md, Docs/00_changelog/2026Year/10/2026-10-19-重新编译并重启Desktop与Core.md]
related_files: [Source/PuddingAgent/PuddingAgent.csproj, Source/PuddingDesktop/PuddingDesktop.csproj, Source/PuddingHost/ScreenCapture/ScreenCaptureWire.cs]
slug: changelog-2026-10-19-rebuild-restart-circuit-open-cold-start
draft: false
---

# 2026-10-19 重编 Core 与 Desktop 并重启：熔断复盘与冷启动超时

> 用户截图：Desktop 显示「**自动恢复已暂停** · Core 在短时间内连续失败，自动恢复已熔断」；随后要求「编译 Core 和 Desktop，并重启」。

## 1. 熔断事实与原因（重启前）

| 证据 | 内容 |
|---|---|
| Windows 事件日志 | 15:44:36 与 15:45:45，`.NET Runtime` 1026 + `Application Error` 1000（CLR20r3）：`PuddingAgent.exe` 未处理异常 **`System.IO.IOException: The process cannot access the file 'D:\Data\.pudding-host.lock' because it is being used by another process.`** |
| Desktop 诊断（重启前抓取） | `coreState=CircuitOpen`、`restartAttemptsInWindow=3`、`lastError=Core 在 60 秒内连续失败，自动恢复已熔断。` |
| 进程内部署记录 | `lastDeploymentResult`：15:43:19–15:44:31 `rebuild-restart` **success=false**、`coreRestarted=false`、`assembliesReloaded=false`（即热部署后重启失败） |
| 失败后的手工重试 | 15:45:54 起 Core 64128 启动并 Ready（15:46:06），但 Desktop UI 仍停在熔断面板 |

**判定**：15:43 的进程内 `rebuild-restart(desktop-build)` 停旧 Core → 部署 Core → 再拉起新 Core 的时序里，新 Core 在旧 Core 的 DataRoot 租约（`.pudding-host.lock`）尚未释放时启动，连续两次因锁占用未处理异常崩溃；Desktop 60 秒窗口内 3 次失败即熔断。**崩溃不是构建失败或数据损坏**，是同一时刻两个 Core 竞争 DataRoot 租约。

## 2. 本次操作（HEAD `d0212f2`）

1. 记录基线：HEAD `d0212f2`（15:41:19），工作树干净；旧实例 Desktop 15100 / Core 64128（处于 `CircuitOpen` 但 Core 仍 Ready）。
2. **停旧实例**：`POST /desktop/bootstrap/core/stop` 返回 `success=true`，Core 优雅退出；随后结束 Desktop；确认无 Pudding 进程、80 端口空闲、锁文件可独占打开（未删除锁）。
3. **重编（串行，`--no-restore`，日志 `temp/build/rebuild-20261019b.log`）**：
   - `dotnet build Source\PuddingAgent\PuddingAgent.csproj` → exit 0，**6 警告 / 0 错误 / 1.72s**（进程内构建已编译过同提交，本次为增量确认）；
   - `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj` → exit 0，**157 警告 / 0 错误 / 13.60s**（含 `BuildCoreBundle` 把 Core 发布进 `core/`）。
   - 本批 HEAD 含两个新提交：`2839e88 feat(screen): screen_capture 工具` 与 `d0212f2 fix(screen): displays 序列化失败修复`。
4. **部署**：产物直接落 Desktop 实际运行目录 `temp/build/winui3/bin/PuddingDesktop/Debug/net10.0-windows10.0.19041.0/win-x64`（`desktop.json` 的 `coreExecutablePath` 指向其 `core/PuddingAgent.exe`）；`core/PuddingHost.dll` 15:48:11 写入，大小 **1,065,984**（上一版 1,016,832），`screen_capture` / `ScreenCaptureWire` / `ScreenCaptureGeometry` / `ScreenCaptureTool` 标记均命中。
5. **重启（含预热）**：先以 Console 模式启动一次同产物 Core 预热（`--data-root D:\Data`，等待 Ready 后停止，确认锁释放），再启动 `PuddingDesktop.exe`。

## 3. 冷启动超时（本次实测，可解释前两轮失败）

全量重编会重写 250+ 个产物文件；**重编后第一次启动**的耗时会远超平时（本机实测）：

| 阶段 | 冷启动（预热那次） | 预热后（Desktop 实际 Core） |
|---|---|---|
| `data-root-bootstrapped` | **24.3s** | 约 0.03s |
| `services-registered`（本阶段 delta） | **65.0s** | 4.3s |
| `middleware-mapped`（delta） | 12.0s | 0.13s |
| 进程 → Ready | **118.9s** | **8.5s** |

⇒ Desktop 的 `desktop.core.startupTimeoutSeconds=60` **装不下全量重编后的首次启动**（118.9s），第 1 次会被判超时而 kill；预热后同产物 8.5s 即可 Ready。本轮 Desktop 外壳自身冷启动也用了约 63s 才拉起 Core（WinUI/WebView2 首次加载）。这与上一轮（15:11–15:15）观察到的「冷启动 3 次超时、预热后 4.4s」一致。

## 4. 最终状态（已验证）

| 项 | 值 |
|---|---|
| Desktop | PID **47972**（15:50:32 启动，Responding，窗口 `Pudding`） |
| Core | PID **22816**（15:51:35 启动，15:51:43.97 Ready，8.5s） |
| 监听 / 健康 | `0.0.0.0:80`；`GET /health/ready` → **200 ready** |
| Desktop 诊断 | `desktopState=WorkbenchReady`、`coreState=Ready`、`lastError=null` |
| 在线身份 | `/api/system/build-identity`：5 个关键程序集 `loaded=true` 且 `fileSha256` 与本批落盘文件逐一相等；`PuddingHost.dll`（身份端点未跟踪该程序集）模块路径=包内 core，文件 SHA-256 `163013B2…` 与产物一致，Core 启动时间晚于产物写入时间 ⇒ 屏幕采集新代码已在线 |
| 前端 | 未重编（不变）：服务身份 `v6.9.10 / b9b1460 / dirty=false / builtAt 14:04`，`/admin/index.html` 字节与包内一致；页角徽标 `v6.9.10 · b9b1460 · 2026-10-19 14:04` |
| 截图 | `temp/test-out/deploy-20261019-2/desktop-after-restart.png`（工作台正常、Core 已连接、状态「就绪」） |
| 证据 | `temp/test-out/deploy-20261019-2/post-verification.json`、`pre-state.json`、`warmup-stdout.log` |

## 5. 建议与注意

- **建议把 `<DataRoot>/config/system.json` 的 `desktop.core.startupTimeoutSeconds` 从 60 提升到 180**（或让部署脚本在全量重编后先做一次预热启动）。否则「全量重编 → 重启 Desktop」每次都会先在 60s 超时上失败 1–3 次、甚至熔断，需要人工重试。
- 启动/重启/崩溃恢复/退出回收的**进程外结论**以本记录第 4 节为准；产品功能 smoke（真实模型会话、screen_capture 实拍等）仍需**新会话在进程内**执行，不得由本记录替代。
- 重启终止了重启前仍在运行的进程内 Agent 会话（session `206a9b48…`）；其已提交的工作（`2839e88`、`d0212f2`）不受影响，未提交的工作区改动重启前已确认为空。
- 重启后 error 日志仅两条既有噪声：Feishu 通道未绑定唯一 Agent；Codex MCP `127.0.0.1:5100` 连接被拒。
