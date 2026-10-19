---
title: 2026-10-19 修复后重编 Core 与 Desktop 并部署重启
author: hyfree
date: 2026-10-19
last_reviewed: 2026-10-19
status: archived
description: "按用户要求重编 Core+Desktop → 部署 → 重启：HEAD ebe3920（含 14:49–14:53 三处修复）编译 0 error；产物落在 Desktop 实际运行目录并由在线 /api/system/build-identity 证明确实加载；首次重启连续 3 次 Core 启动超过 60s 超时，预热后再启 4.4s Ready，最终 Desktop 15100 / Core 30812、0.0.0.0:80、前端 6.9.10/b9b1460 服务字节一致。"
categories: [docs, changelog]
tags: [deployment, desktop, core, restart, build]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-19-重新编译并重启Desktop与Core.md]
related_files: [Source/PuddingAgent/PuddingAgent.csproj, Source/PuddingDesktop/PuddingDesktop.csproj]
slug: changelog-2026-10-19-rebuild-core-desktop-after-fixes
draft: false
---

# 2026-10-19 修复后重编 Core 与 Desktop 并部署重启

> 起因：HEAD `ebe3920` 含 14:39–14:53 的三处提交（`runtime_identity` 部署新鲜度判定、`tool_stats` 按 ToolRunner 采样、偏好写入落到「用户偏好」书），而运行中实例仍是 14:04 构建、14:10 启动的旧产物，需按用户要求重编 Core+Desktop → 部署 → 重启。

## 1. 操作

1. **基线**：HEAD `ebe3920`（提交时间 14:53:46），工作树干净；旧实例 Desktop PID 123624 / Core PID 59204（14:10 启动，监听 `0.0.0.0:80`）。
2. **停旧实例**：`POST /desktop/bootstrap/core/stop`（`X-Control-Token`，返回 `success=true`，Core 优雅退出）→ 结束 Desktop 进程 → 确认无 Pudding 进程、80 端口空闲、`D:\Data\.pudding-host.lock` 无持有者（未删除锁文件）。
3. **重编（串行，`--no-restore`）**：
   - `dotnet build Source\PuddingAgent\PuddingAgent.csproj` → exit 0，**139 警告 / 0 错误 / 15.62s**；
   - `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj` → exit 0，**155 警告 / 0 错误 / 25.39s**（其 `BuildCoreBundle` 同时把 Core 发布到 Desktop 目录的 `core/`）；
   - 合计 42s，构建日志 `temp/build/rebuild-20261019.log`。
4. **部署**：本机 Desktop 的实际运行目录就是 `desktop.json` 中 `coreExecutablePath` 所在包：
   `temp/build/winui3/bin/PuddingDesktop/Debug/net10.0-windows10.0.19041.0/win-x64/`（其 `core/PuddingAgent.exe` 即运行中的 Core）。构建产物直接落在该目录，**无需另拷**；并清理 `core/wwwroot/admin` 中上一版前端遗留的 6 个 `umi.17b23814.*` 静态文件（含 `.gz`/`.br`）。
5. **重启**：启动 `...win-x64\PuddingDesktop.exe`，Core 由 Desktop 作为子进程拉起并监听 `0.0.0.0:80`。

## 2. 新产物核对

| 产物 | 写入时间 | 大小 | SHA-256（前 16） |
|---|---|---|---|
| `PuddingDesktop.exe` | 15:10:44 | 466432 | `86201CA8A1E83832` |
| `PuddingDesktop.dll` | 15:10:44 | 1079808 | `F7CFDC5A15D2E550` |
| `core/PuddingAgent.exe` | 15:10:55 | 162304 | `23BCEEC31B686A27` |
| `core/PuddingHost.dll` | 15:10:54 | 1016832 | `6F5A10C732A68E00` |
| `core/PuddingRuntime.dll` | 15:10:53 | 4821504 | `6C49B8B4CF96E2CC` |
| `core/PuddingPlatform.dll` | 15:10:53 | 7371264 | `9B2EE58DFD7388D8` |

- **新代码确实编进产物**（`core/PuddingRuntime.dll` UTF-16 字符串扫描）：`CanonicalBookTitle`（`d3eb445`）、`query_component` 与 `No tool-runner activity in the sample carries`（`953e72d`）、`runtime_identity`（`167d23f`）均命中。
- **前端未重编**（范围是 Core+Desktop）：bundle `core/wwwroot/admin` 与 `Source/PuddingPlatformAdmin/dist` 的 **159 个文件逐字节一致**（额外文件仅为 `.gz`/`.br` 压缩派生）；dist 构建身份 `6.9.10 / b9b1460 / dirty=false / builtAt 2026-10-19 14:04:13`；`git diff b9b1460..HEAD -- Source/PuddingPlatformAdmin` 为空，前端源码自 dist 构建后未改动。

## 3. 首次重启的启动超时（如实记录，未定性为代码缺陷）

- 15:11:56 首次启动 Desktop 后，Core 连续 **3 次**启动失败，Desktop 报
  `Core did not emit a Ready signal or renew its startup lease within 60 seconds`（`desktop.core.startupTimeoutSeconds=60`）：
  - 第 2 次尝试 `data-root-bootstrapped total=24532ms`（同一阶段在预热后为 30ms）、`logging-ready total=24598ms`；
  - 第 3 次尝试 `logging-ready total=96ms`，但 60s 内未出现 `services-registered`（预热后该阶段为 294ms）。
- 用**同一份新产物**以 Console 模式手工起探针（`--data-root D:\Data`）：`ready total=12883ms`、`/health/ready` 200，说明构建本身可正常启动。
- 15:17:44 再次启动 Desktop：**4.4s 后 Core Ready**（`coreStartedAt 15:17:44.85` → `coreReadyAt 15:17:49.23`）。
- 结论：失败集中在全量重编后的前几次冷启动，表现为启动/文件引导阶段异常变慢而越过 60s 超时；预热后恢复正常。**根因未最终定位**（可疑方向：本机 Defender 对 250+ 个新写 DLL 的首次扫描，以及首次启动时数据根/程序集冷加载）。后续若复现，先调大 `<DataRoot>/config/system.json` 的 `desktop.core.startupTimeoutSeconds` 或排查实时防护，**不要**据此改代码。

## 4. 部署后验证（进程外控制器口径）

| 项 | 结果 |
|---|---|
| 进程 | Desktop PID **15100**（Responding，窗口 `Pudding`）；Core PID **30812**（Desktop 子进程） |
| 监听/健康 | `0.0.0.0:80` LISTEN；`GET /health/ready` → **200 `ready`** |
| 运行身份 `/api/system/build-identity` | PID 30812；`PuddingAgent` / `PuddingCore` / `PuddingRuntime` / `PuddingMemoryEngine` / `PuddingPlatform` 均 `loaded=true`，且 **`fileSha256` 与本次构建落盘文件逐一相等** ⇒ 新程序集已真正加载（非旧进程） |
| Desktop 控制面诊断 | `desktopState=WorkbenchReady`、`coreState=Ready`、`coreExecutablePath` 指向包内 `core\PuddingAgent.exe`、`dataRoot=D:\Data`、`lastError=null` |
| 前端服务 | `/admin/index.html`、`/admin/umi.f463e717.js` 的 HTTP 字节与构件逐字节一致；服务内注入身份 `v6.9.10 / b9b1460 / dirty=false / builtAt 14:04`；截图见 `temp/test-out/deploy-20261019/desktop-after-restart.png`（页角徽标 `v6.9.10 · b9b1460 · 2026-10-19 14:04`，状态栏「Core 运行中 127.0.0.1」） |
| 证据 | `temp/test-out/deploy-20261019/post-verification.json`（基线 `pre-artifacts.json`、`pre-diagnostics.json`；手工探针日志 `core-manual-stdout.log`） |

## 5. 未完成 / 注意

- **两段式验收**：本次由进程外控制器完成「重编 → 部署 → 重启 → 在线身份核对」；产品功能 smoke（真实模型会话、浏览器工具等）仍需新会话在进程内执行，**启动/重启/崩溃恢复/退出回收结论以外部控制器为准**。
- 新启动后 error 日志有 2 条既有噪声（Feishu 通道未绑定唯一 Agent；Codex MCP `127.0.0.1:5100` 连接被拒），与本次改动无关。
- 旧产物未单独备份：`temp/build` 为构建产物目录，可由 HEAD `ebe3920` 重编复现；数据库与配置未改动，未做数据迁移。
- 前端本批未重编（需求范围 Core+Desktop），故前端构建身份仍为 `b9b1460`；若后续有前端改动，需按前端版本管理规则重编并重新部署。
