---
title: 2026-10-07 agent_diagnostics 新增 runtime_identity 运行身份核实
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: "为 agent_diagnostics 增加 runtime_identity：只报进程与已加载程序集的磁盘事实（路径/大小/写入时间/SHA-256/MVID/InformationalVersion），并可用 assembly_path 核实目标产物；读不到即报 error 不编造，供 Agent 自证部署是否生效。"
categories: [docs, changelog]
tags: [agent_diagnostics, runtime_identity, deployment-verification]
related_docs: [Docs/14_reports/file-patch归因复核-独立验证-2026-10-07.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs, Source/PuddingRuntimeTests/Tools/AgentDiagnosticsToolTests.cs, Source/PuddingRuntime/code_map.md]
slug: changelog-agent-diagnostics-runtime-identity-2026-10-07
draft: false
---

# 2026-10-07 agent_diagnostics 新增 runtime_identity 运行身份核实

> 范围：`Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs`、`Source/PuddingRuntimeTests/Tools/AgentDiagnosticsToolTests.cs`、`Source/PuddingRuntime/code_map.md`

## 背景

P2 计划中的「运行身份」项一直悬空。它的痛点在本机反复出现：宿主进程锁住 `PuddingAgent/bin/Debug/net10.0/*.dll`，`dotnet build` 恒 exit 1，于是「部署到底有没有生效」只能靠外部 PowerShell 探针（比对产物 SHA-256 / 写入时间）人工确认；Agent 自身没有任何手段回答「我现在跑的是哪份产物」，部署缺口只能被外部工具发现。

## 改动

1. `agent_diagnostics` 新增 `runtime_identity` action（`GetRuntimeIdentity`）：
   - `process`：`pid` / `startedAtUtc` / `is64Bit` / `workingSetBytes` / `baseDirectory`；
   - `runtime`：`framework` / `os` / `processArchitecture` / `runtimeVersion`；
   - `assemblies`：`entry` 与 `runtime` 两个已加载程序集，各带 `name` / `location` / `file{path,bytes,lastWriteTimeUtc,sha256}` / `informationalVersion` / `mvid`；
   - `target`：可选参数 `assembly_path` 指定的目标文件，用于核实部署目录里的产物，输出同一套文件事实。
2. 诚实性优先：目标路径缺失或不可读时输出 `target.error = "file not found or unreadable"` 且 `file = null`，绝不编造大小或哈希——避免调用方把「读不到」误当成「一致」。
3. 隐私边界：只报告进程与程序集事实，不回显凭据、环境变量、机器标识或用户名。
4. 契约同步：unknown action 错误消息、`AgentDiagnosticsArgs.Action` 的 `[ToolParam]` 描述、类头 XML 注释三处一并更新。
5. `Source/PuddingRuntime/code_map.md` 的 `AgentDiagnosticsTool.cs` 行补上该约束。
6. 测试新增 5 例（`AgentDiagnosticsToolTests`）：磁盘哈希可核对（含 MVID 形状与 InformationalVersion）、目标文件哈希与字节数、缺失路径的诚实报错、无敏感回显、unknown action 提示包含新 action。

## 证据

- 变异取红（`temp/ri-mutate.py` → `temp/ri-mutate-out.txt`，三份原始输出齐备）：
  - 变异 A（`SHA256.HashData(stream)` → 空字节）：失败 2 / 通过 10，失败集合**恰好**是 `RuntimeIdentity_ReportsLoadedAssembliesWithHashVerifiableOnDisk`、`RuntimeIdentity_TargetFile_ReportsHashMatchingDiskBytes`；
  - 变异 B（缺失目标不再报 error）：失败 1 / 通过 11，恰好 `RuntimeIdentity_MissingTargetPath_ReportsErrorWithoutInventingIdentity`；
  - 两次复原 sha256 均与基线 `2ea89b4f5e01fbce0c5b7bf8b8283dcb939519d08bf79d8198bd202aa0aca729` 逐位一致；复原后全绿 12/12。`VERDICT=PASS`。
- 聚焦测试：`dotnet test --filter FullyQualifiedName~AgentDiagnostics` → 失败 0 / 通过 12。
- 全量回归（首次，本机 WSL 发行版缺失时）：`PuddingRuntimeTests` 1978 例 → 失败 1 / 通过 1971 / 跳过 6。唯一失败为 `HostShellExecutor_WslMode_UsesWindowsWorkingDirectoryMapping`（`exit code -1`），属环境型：当刻 `wsl -e pwd` 同样提示缺少已安装的 Linux 发行版，与本次改动无交集。
- 全量回归（复测，用户告知 WSL 已安装后）：现场实测 `wsl --status` → 默认分发 Ubuntu / 默认版本 2，`wsl -l -v` → `* Ubuntu Running 2`，`wsl -e pwd` → `/mnt/d/CodeProject/PuddingAgent/PuddingAgent`（rc=0）；同一条测试转绿，全量 **失败 0 / 通过 1972 / 跳过 6 / 总计 1978**（通过数 1971→1972 的差量精确对应那条 WSL 测试）。
- `python Tools\Docs\code_map_check.py`：本次改动后 `error 0 / warn 1 / gate PASS`；warn 为既有的 `stale-fingerprint`（对 HEAD 版 `code_map.md` 做对照校验同样 warn 1，且该工具 read-only，无 stamp 能力）。

## 遗留

- `runtime_identity` 要在宿主里真正可用，必须先完成自举重启（当前宿主仍加载旧程序集）；重启会终止所有运行中的子代理，需用户点头后触发。
- `code_map.md` 顶部 `源指纹 …=dc2ba84b0ee2` 长期 stale（基线即 stale），本次未一并刷新。

## 复测补记（环境变更）

初稿把 `HostShellExecutor_WslMode_UsesWindowsWorkingDirectoryMapping` 的失败标为「环境型（本机 WSL 发行版缺失）」—— 该判定在写入时点（10-07 22:57）为真，当刻探测确实提示需 `wsl.exe --install <Distro>`；环境在当日晚间被用户改变（Ubuntu 已安装且 `Running`，版本 2）。按同一命令复跑全量得 0 失败。

**教训：环境型判定必须带时点，并在环境可能变化后复测；用户纠正环境事实时，先现场实测再改记录。**
