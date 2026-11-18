---
title: 2026-11-18 Guardian 复用前校验被监护桌面 PID
author: hyfree
date: 2026-11-18
last_reviewed: 2026-11-18
status: proposed
description: 已监听的 Guardian 必须报告它监护的桌面 PID，否则重启后旧 Guardian 继续 adopt 已退出的桌面，活的桌面静默无监护。
categories: [changelog]
tags: [guardian, desktop, silent-failure]
related_docs: [Docs/13_runbooks/Desktop-Guardian.md]
related_files: [Source/PuddingDesktop/Hosting/GuardianAdoptionPlan.cs, Source/PuddingDesktop/Hosting/DesktopApplicationCoordinator.Guardian.cs, Source/PuddingDesktop.Guardian/GuardianHttpServer.cs, Tests/PuddingDesktop.GuardianIntegrationTests/GuardianAdoptionPlanTests.cs]
slug: changelog-guardian-adoption-pid-2026-11-18
draft: false
---

# 2026-11-18 Guardian 复用前校验被监护桌面 PID

## 问题

`EnsureGuardianAsync` 只要 `/guardian/status` 应答且协议、`desktopExecutablePath`、`dataRoot` 三项一致，就把该 Guardian 视为自己的并跳过启动。

但「应答」不等于「在监护我」。实测：Desktop 于 19:07 重启为 pid 30120，而 14:33 启动的 Guardian 59808 仍带 `--adopt-pid 31432`（已退出的旧桌面）。它三项校验全过，于是新桌面认为已被监护——端口在听，监护对象却不存在。**这是「Guardian 挂了」的静默版本：没有任何日志、没有任何失败。**

`bootstrap_reboot` 每次只换 Desktop，Guardian 的 adopt-pid 永远停在第一次的值，重启多少次都不能自愈。

## 改动

- `Source/PuddingDesktop/Hosting/GuardianAdoptionPlan.cs`（新增）：纯函数 `Decide(currentPid, supervisedDesktopPid, supervisedAlive)` → `AdoptedByCurrentProcess` / `Unknown` / `OwnedByAnotherLiveDesktop` / `AdoptsExitedDesktop`。身份相等优先于存活探测（自己的 pid 必然存活，探测只增加竞态）。`Unknown`（旧版 Guardian 不返回该字段）**不判失败**，避免用缺失字段打挂正常启动。
- `Source/PuddingDesktop/Hosting/DesktopApplicationCoordinator.Guardian.cs`：`CheckGuardianAsync` 改为实例方法，解析 `desktopPid` 后走上述决策，无法复用时报出点名两端 PID 的可执行错误，交由既有 `GuardianStart` 诊断落盘。
- `Source/PuddingDesktop.Guardian/GuardianHttpServer.cs`：`/guardian/status` 增加 `desktopPid`、`corePid`。没有这两个字段，调用方无法区分「监护本机」与「监护已退出的桌面」。

## 验证

- 新增 9 条测试，独立工程 `PuddingDesktop.GuardianIntegrationTests` 9/9 通过（`-p:ArtifactsPath=temp\iso-adopt` 规避运行中 Desktop 锁产物）。
- 变异取红：把 `OwnedByAnotherLiveDesktop` 与 `AdoptsExitedDesktop` 两个返回互换 → 失败 2 条，且正是 `Decide_OtherLiveDesktop_*` 与 `Decide_OtherExitedDesktop_*`；复原后 SHA-256 `938DFA5B…` 逐位一致，再跑 9/9 绿。
- `PuddingDesktop.Guardian` 构建 0 错误；4 条 CA1416 警告位于 `PuddingSecretReferences`，与本次改动无关。

## 未完成

- **本次改动只覆盖后继重启**：当前运行的 Guardian 59808 是旧构建，不返回 `desktopPid`，因此今天这个僵尸监护仍然检测不到，需按 `Tools/Dev/redeploy-desktop.ps1 -Apply` 换出新 Guardian 后才具备该能力。
- 两份 `code_map.md` 的源指纹未刷新：本机 `python` 仅为 Microsoft Store 占位符（`--version` 返回 9009），`Tools/Docs/code_map_check.py` 无法运行。指纹此前已陈旧（`Hosting/**` 记为 3 个文件，加入 `GuardianLaunchPlan.cs` 后实为 4 个），本次变为 5 个。
