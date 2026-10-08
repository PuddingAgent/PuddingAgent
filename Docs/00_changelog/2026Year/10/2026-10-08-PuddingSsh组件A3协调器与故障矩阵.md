---
title: 2026-10-08 PuddingSsh 组件 A3：ISshClient 协调器与假传输故障矩阵
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "按设计方案 A3/S3 交付 ISshClient 协调器：冻结上下文、并发准入（每主机 2/Core 8/队列 32）、deadline 合成、清理宽限与停止；35 例假传输故障矩阵全绿，并用真实 OpenSSH 走通全链路；未接宿主。"
categories: [docs, changelog]
tags: [ssh, tools, componentization, concurrency, testing]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/10_conventions/组件化交付规程.md, Docs/14_reports/2026-10-08-PuddingSsh-A3-协调器与假传输故障矩阵报告.md, Docs/14_reports/2026-10-08-PuddingSsh-A2-独立测试与信任验证报告.md]
related_files: [Source/PuddingSsh/Execution/SshOperationCoordinator.cs, Source/PuddingSsh/Execution/SshCoordinatorOptions.cs, Source/PuddingSsh/Contracts/SshHostContext.cs, Source/PuddingSsh/Contracts/SshPorts.cs, Source/PuddingSshTests/SshOperationCoordinatorFaultMatrixTests.cs, Source/PuddingSshTests/SshFakes.cs, Source/PuddingSsh.Probe/Program.cs, TestScripts/ssh-probe/run-ssh-probe.ps1, Source/PuddingSsh/code_map.md, code_map.md]
slug: changelog-pudding-ssh-a3-coordinator-and-fault-matrix
draft: false
---

# 2026-10-08 PuddingSsh 组件 A3：ISshClient 协调器与假传输故障矩阵

## 目标 / 背景

执行[SSH 工具组件化设计与实施方案](../../../12_features/SSH工具组件化设计与实施方案-2026-10-08.md) 的 **A3 / S3**：
「独占连接协调器、deadline、并发队列、输出收集和释放」，判据为「假传输故障矩阵全绿；组件可在宿主运行时独立构建测试」。
按[组件化交付规程](../../../10_conventions/组件化交付规程.md)，**S5 之前不改宿主**：未登记 `PuddingAgentNetwork.slnx`、
未加消费方引用、未进 DI、未改 Runtime/Host/Desktop。

## 改动

### 组件新增/升级

- `Source/PuddingSsh/Contracts/SshHostContext.cs` — 新增。引用方冻结后交给组件的输入（目标 / 限额 / 凭据来源 /
  信任集合 / 默认 cwd / 宿主 `ExecutionDeadlineUtc`）；组件不重读配置、不按别名解析、不接受覆盖。
- `Source/PuddingSsh/Contracts/SshPorts.cs` — 升级。`ISshClient.TestAsync/ExecuteAsync` 改为 `(SshHostContext, 请求, ct)`；
  `ISshTransportSession` 增加 `HostKeyAlgorithm`（`ssh_test` 结果与诊断用）。
- `Source/PuddingSsh/Execution/SshCoordinatorOptions.cs` — 新增。Core 总并行（8）/ 等待队列（32）/ 清理宽限（2 s）。
- `Source/PuddingSsh/Execution/SshOperationCoordinator.cs` — 新增。`ISshClient` 实现：
  形状与 revision 校验 → 并发准入（每主机按 `hostId + hostRevision` 分门）→ 凭据 → 建连（用冻结信任快照生成验证器）→
  执行 → 释放；`StopAsync`/`IAsyncDisposable` 取消在飞操作并最多等一个清理宽限。
- `Source/PuddingSsh/Diagnostics/`、`Validation/` — 未改语义；协调器复用 A2 的分类器与校验器（因此
  `ssh.request_invalid`/`config_invalid`/`target_changed` 在连接之前就能稳定返回）。

### 测试与探针

- `Source/PuddingSshTests/SshFakes.cs` — 新增假传输工厂 / 假会话 / 假身份提供者与上下文构造工具。
- `Source/PuddingSshTests/SshOperationCoordinatorFaultMatrixTests.cs` — 新增 **35 例**故障矩阵：准入与排队（7）、
  校验与 revision（8）、凭据（4）、建连（5）、执行（8）、停止与 `ssh_test`（5）。
- `Source/PuddingSsh.Probe` 新增 `coordinator` 场景：经 `ISshClient` 在真实 OpenSSH 上跑
  `ssh_test` → 成功执行（含默认 cwd）→ 错指纹 → `exit 3` → 非法请求；`run-ssh-probe.ps1` 增加该场景并断言
  它的服务端认证次数**恰为 3**（错指纹与非法请求不得认证）。
- `TestScripts/ssh-probe/run-ssh-probe.ps1` — 修复：输出目录改为**每次写入前重建**。共享工作区里其他会话按
  「临时产物只进 temp」的约定清空 `temp/test-out/*` 时，正好删掉了本运行器的日志目录并打断了一轮运行。

### 文档与索引

- `Docs/14_reports/2026-10-08-PuddingSsh-A3-协调器与假传输故障矩阵报告.md` — 新增（完整证据）。
- `Source/PuddingSsh/code_map.md`（状态 → A1–A3/S1–S3、协调器文件行、不变量 14–17、门禁与已知限制）、
  `code_map.md`（§2 组件行 + §5 测试/探针行）— 更新。

## 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 组件构建 | `dotnet build Source/PuddingSsh/PuddingSsh.csproj --artifacts-path temp/build/ssh --no-restore` | exit 0，0 警告 0 错误 |
| 独立测试 | `dotnet test Source/PuddingSshTests/PuddingSshTests.csproj --artifacts-path temp/build/ssh --results-directory temp/test-out/ssh` | **122/122 通过**（A2 87 + A3 35），0 警告 0 错误，约 0.3 s |
| 真实协议 | `powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1` | **8 场景 `failures = 0`**；`coordinator` 场景 `accepted_publickey_delta = 3` |
| 边界断言语义 | 随独立测试执行 | 组件/测试工程仍只有 1 条 `ProjectReference`；变异取红结论不变（A2 已记录，本次未改动该套断言） |

关键实测数字（真实 OpenSSH 10.2p1 / Ubuntu 26.04.1 LTS 夹具）：

- `ssh_test` 190 ms，phases = `queue,credentials,connect`，`VerifiedHostKeySha256` 与 `ssh-keygen` 一致。
- 默认 cwd 生效：`ssh_execute "echo coordinator-ok; pwd"` → stdout `coordinator-ok|/tmp`，`exit_code=0`，86 ms。
- 错指纹 → `ssh.host_key_mismatch` + `retry_safe=false` + `not_submitted`；`exit 3` → `failed` + `exit_code=3` +
  `ssh.remote_exit_nonzero`；`echo\0bad` → `ssh.request_invalid` + `phase=validation`。

## 未完成 / 后续

- **A4**：真实 OpenSSH 全矩阵（两类 `.ssh`、ACL/权限、撤销/换 key 端到端拒绝、错误账号、断线、服务端忽略 TERM、
  Windows 服务端）与组件侧补充变异取红。
- **A5（S5）**：登记解决方案、Runtime 适配（目录/ACL/revision/凭据 + identity 文件名与 path 逃逸校验）、
  三个 Agent 工具与 DI 装配。
- 已知限制（已登记进 code_map）：协调器的每主机门按 `hostId + hostRevision` 存字典且不淘汰，
  条目数随配置 revision 历史增长（单条目极小）；连接复用与租约/失效策略属设计 §11.2 切片 D。
- 本次未提交任何 `temp/` 产物；未读写 `D:\data`；未使用生产主机；未触碰宿主任何工程。
