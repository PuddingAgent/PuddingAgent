---
title: 2026-10-08 PuddingSsh A3 协调器与假传输故障矩阵报告
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: active
description: "PuddingSsh A3/S3 的证据：ISshClient 协调器（准入排队、deadline、清理宽限、停止）在 35 例假传输故障矩阵下全绿，并用真实 OpenSSH 走通 ssh_test/执行/错指纹/非零退出/非法请求全链路。"
categories: [docs, reports]
tags: [ssh, componentization, concurrency, deadline, evidence]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/10_conventions/组件化交付规程.md, Docs/14_reports/2026-10-08-PuddingSsh-A2-独立测试与信任验证报告.md, Docs/14_reports/2026-10-08-PuddingSsh-A1-库能力实测报告.md, Docs/00_changelog/2026Year/10/2026-10-08-PuddingSsh组件A3协调器与故障矩阵.md]
related_files: [Source/PuddingSsh/Execution/SshOperationCoordinator.cs, Source/PuddingSsh/Execution/SshCoordinatorOptions.cs, Source/PuddingSsh/Contracts/SshHostContext.cs, Source/PuddingSshTests/SshOperationCoordinatorFaultMatrixTests.cs, Source/PuddingSshTests/SshFakes.cs, Source/PuddingSsh.Probe/Program.cs, TestScripts/ssh-probe/run-ssh-probe.ps1, Source/PuddingSsh/code_map.md]
slug: reports-pudding-ssh-a3-coordinator-and-fault-matrix-2026-10-08
draft: false
---

# 2026-10-08 PuddingSsh A3 协调器与假传输故障矩阵报告

本报告是 [SSH 工具组件化设计与实施方案](../12_features/SSH工具组件化设计与实施方案-2026-10-08.md) **A3 / S3** 的执行证据：
「独占连接协调器、deadline、并发队列、输出收集和释放」，判据为「**假传输故障矩阵全绿**；组件可在宿主运行时独立构建测试」。

## 1. 交付物

| 文件 | 内容 |
|---|---|
| `Source/PuddingSsh/Contracts/SshHostContext.cs` | 引用方**冻结**后交给组件的输入：`Target` / `Limits` / `Identity` / `Trust` / `DefaultCwd` / `ExecutionDeadlineUtc` |
| `Source/PuddingSsh/Contracts/SshPorts.cs` | `ISshClient` 改为接收「冻结上下文 + 请求」两个参数；`ISshTransportSession` 增加 `HostKeyAlgorithm`（`ssh_test` 结果用） |
| `Source/PuddingSsh/Execution/SshCoordinatorOptions.cs` | Core 总并行（默认 8）、等待队列（默认 32）、清理宽限（默认 2 s） |
| `Source/PuddingSsh/Execution/SshOperationCoordinator.cs` | `ISshClient` 实现：形状/revision 校验 → 准入排队 → 凭据 → 建连 → 执行 → 释放；`StopAsync`/`IAsyncDisposable` |
| `Source/PuddingSshTests/SshFakes.cs` + `SshOperationCoordinatorFaultMatrixTests.cs` | 假传输/假会话/假身份提供者与 35 例故障矩阵 |
| `Source/PuddingSsh.Probe`（新 `coordinator` 场景）+ `TestScripts/ssh-probe/run-ssh-probe.ps1` | 真实 OpenSSH 上的全链路场景与「每场景认证次数」断言 |

## 2. 落实的语义（设计 §8/§9）

1. **操作总时长从「受理」起算**：含排队、建连、执行与清理。排队中到期 ⇒ `ssh.timeout` + `phase=queued`，且
   **不建连**（测试断言 `ConnectCount` 不变）。
2. **并发门按 `hostId + hostRevision`**（每主机默认 2），并叠加 Core 总并行（默认 8）；
   换 revision 会得到**新的门**，不会与旧配置共用额度。
3. **等待队列满额立即 `ssh.busy`**：不创建无界等待任务；排队中的操作可被调用方取消（`ssh.cancelled`）。
4. **清理宽限是资源回收**：截止后再等最多 2 秒（可配置），随后释放独占连接；若会话仍不返回，
   结果如实报 `execution_state=unknown`，**不声称远端进程已结束**。
5. **提交前后形状可区分**：会话同步抛错 ⇒ `phase=submitting` + `not_submitted` + `retry_safe=true`；
   异步抛错（已提交）⇒ `connection_lost` + `unknown` + `retry_safe=false`。
6. **停止不延长业务许可**：`StopAsync` 取消在飞操作、最多等一个清理宽限；停止后到达的请求返回 `ssh.busy`。
7. **释放路径不污染结果**：会话 Dispose 卡死时用同一宽限放弃等待，业务结果保持不变。

## 3. 假传输故障矩阵（35 例，全绿）

```powershell
dotnet test Source/PuddingSshTests/PuddingSshTests.csproj --artifacts-path temp/build/ssh --results-directory temp/test-out/ssh --nologo
```

`已通过! - 失败: 0，通过: 122，已跳过: 0，总计: 122`（本轮 122 = A2 的 87 + A3 的 35；构建 0 警告 0 错误，耗时约 0.3 s）。

| 组 | 覆盖 | 例数 |
|---|---|---|
| 准入与排队 | 每主机第 3 条排队、revision 分门、Core 总并行、队满 `ssh.busy`（不建连）、排队取消、排队到期、失败后槽位不泄漏 | 7 |
| 校验与 revision | 目标 revision 不一致 ⇒ `target_changed`（不取凭据、不建连）、命令空/空白/NUL ⇒ `request_invalid`、非法 cwd 与非法默认 cwd 分开、宿主 deadline 过期、端口越界 | 8 |
| 凭据 | 口令需求码透传、IO 失败 ⇒ `credential_unavailable`、成功/失败两条路径都**销毁认证材料**（`ObjectDisposedException`） | 4 |
| 建连 | `connect_failed` 且可重试、指纹拒绝携带 `host_key_mismatch` 且不可重试、建连 deadline 有界、会话被释放、命令串与预算正确传入 | 5 |
| 执行 | 结果透传 + 总时长、审批前确定的执行串、提交后抛错 ⇒ `connection_lost`/`unknown`、提交前抛错 ⇒ `submitting`/`retry_safe`、调用方取消、本端到期把会话的 `cancelled` 改写为 `timeout`、恶意会话在宽限后被放弃、Dispose 卡死不阻塞 | 8 |
| 停止与 `ssh_test` | `StopAsync` 取消在飞操作、停止后 `ssh.busy`、`ssh_test` 不执行 shell 且报 phases、`ssh_test` 建连失败、`ssh_test` 使用冻结信任并绑定目标端点 | 5 |

## 4. 真实协议全链路（8 场景，`failures = 0`）

```powershell
powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1
```

新增的 `coordinator` 场景经 `ISshClient` 在真实 OpenSSH 上跑完整链路，运行器同时核对**服务端认证次数**：

| 操作 | 结果 |
|---|---|
| `ssh_test` | `completed`、认证成功、`VerifiedHostKeySha256` 与 `ssh-keygen` 一致、phases=`queue,credentials,connect`、190 ms |
| `ssh_execute "echo coordinator-ok; pwd"`（默认 cwd `/tmp`） | `completed`、`exit_code=0`、stdout=`coordinator-ok|/tmp`（默认 cwd 确实生效）、86 ms |
| 错指纹（诱饵 pin） | `ssh.host_key_mismatch`、`retry_safe=false`、`execution_state=not_submitted` |
| `exit 3` | `failed`、`exit_code=3`、`ssh.remote_exit_nonzero`（业务失败 ≠ 传输失败） |
| `echo\0bad` | `ssh.request_invalid`、`phase=validation`（连接之前拒绝） |

服务端证据：该场景 `Accepted publickey` 增量恰为 **3**（test + echo + exit 3）；错指纹与非法请求在 sshd 日志里
只留下 `Connection closed ... [preauth]`，即**没有认证**。8 个场景合计 `failures = 0`。

## 5. 过程中的环境问题（已修）

- 共享工作区的其他会话按仓库卫生约定清空 `temp/test-out/*` 时，正好删除了本运行器的输出目录，
  导致一轮探针被打断。运行器已改为**每次写入前重建输出目录**（`Initialize-OutDir`），对并发清理具备韧性。
- 一次 `dotnet build --no-restore` 因 `temp/build/ssh/obj` 被同一清理删除而失败（NETSDK1004），
  重新 `restore` 后恢复；这不影响任何结论，但说明并行会话共享 `temp/` 时构建命令要允许重跑。

## 6. 本次**未**覆盖（诚实边界）

- A4：真实 OpenSSH 侧的两类 `.ssh` 目录、ACL/权限、撤销/换 key 的端到端拒绝、错误账号、断线、
  服务端忽略 TERM、Windows OpenSSH 服务端；组件侧变异取红（S4 补充）。
- A5：宿主装配、Runtime 目录/revision/凭据适配、三个 Agent 工具与审批绑定；产品态验收。
- 协调器的每主机门按 `hostId + hostRevision` 存在字典里且**不做淘汰**：条目数随配置 revision 历史增长
  （每次改配置 +1，单条目极小）。连接复用与租约/失效策略属设计 §11.2 的切片 D，届时一并设计。
