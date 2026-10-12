---
title: 2026-10-12 SSH R2：操作 lifetime 与阶段前置围栏
author: hyfree
date: 2026-10-12
last_reviewed: 2026-10-12
status: archived
description: "按 ADR-096 D2 引入 SshOperationLifetime：受理时算一次绝对预算、各阶段只拿剩余额度、身份/建连用有界阶段等待、迟到结果只做材料清零与连接回收；并修正 Core 停止被写成超时的问题。"
categories: [docs, changelog]
tags: [ssh, fix, lifecycle, cancellation, regression]
related_docs: [Docs/07_architecture/110ADR-096SSH执行前置围栏与有界终止ADR.md, Docs/12_features/SSH-S5前置修补与代码级施工方案-2026-10-12.md, Docs/14_reports/2026-10-12-SSH-S1-S4审阅与S5准入结论.md]
related_files: [Source/PuddingSsh/Execution/SshOperationLifetime.cs, Source/PuddingSsh/Execution/SshOperationCoordinator.cs, Source/PuddingSshTests/SshOperationLifetimeFenceTests.cs, Source/PuddingSshTests/SshFakes.cs, TestScripts/ssh-probe/run-ssh-probe.ps1, Source/PuddingSsh/code_map.md]
slug: changelog-ssh-r2-operation-lifetime-2026-10-12
draft: false
---

# 2026-10-12 SSH R2：操作 lifetime 与阶段前置围栏

## 目标 / 背景

审阅报告 **F2 / P1**：调用前已取消仍会读身份、建连、执行；身份或建连忽略 token 时操作永不返回；阶段晚返回后
仍会继续下一阶段（甚至提交 exec）；Core 停止会被写成操作超时。按 [ADR-096 D2](../../07_architecture/110ADR-096SSH执行前置围栏与有界终止ADR.md)
引入操作 lifetime，把「取消/截止」变成**前置围栏**。本切片仍只动组件与独立测试/探针。

## 改动

- `Source/PuddingSsh/Execution/SshOperationLifetime.cs` — 新增（internal + 公开的 `SshStopReason`/`SshStopReasons`）：
  - **受理时算一次**绝对预算；剩余额度用注入 `TimeProvider` 的**单调时间戳**计算，另用 `TimeProvider.CreateTimer`
    建预算计时器（不再用系统 `CancelAfter`），因此系统时间调整或换阶段都不会给出第二份完整预算。
  - `StopReason` 区分 `caller_cancelled` / `host_stopping` / `operation_deadline` / `connect_deadline`；
    **Core 停止优先于调用方取消，且绝不写成 timeout**。公开错误码仍是既有的 `ssh.cancelled`/`ssh.timeout`，
    原因以稳定 token `stop_reason=<wire>` 出现在诊断里。
  - `CheckBeforeSideEffect()`：已终止即抛，副作用之前必须通过。
  - `WaitStageAsync()`：有界等待阶段任务；到期/终止时把迟到结果交给 owner（迟到身份 → 销毁材料，
    迟到连接 → 回收），失败被读取即视为已观察，并提供 `LateSuccessesObserved`/`LateFailuresObserved` 计数。
- `Source/PuddingSsh/Execution/SshOperationCoordinator.cs` — 两条入口（Test/Execute）统一为
  「校验 → 围栏 → 准入 → 围栏 → 身份 → 连接 → 围栏 → 提交」：
  - 受理处、取得槽位后、取到身份后、建连后（提交前）各有前置检查；已取消的请求**不读身份、不建连、不提交**。
  - 身份与建连改用 `WaitStageAsync`；调用阶段端口的**同步启动段**也在 try 内（同步抛出同样分类为凭据/建连失败）。
  - exec 阶段只拿 `RemainingOperation`（不再重置预算）；`AdmitAsync` 在等待门之前/取得槽位之后检查终止。
- `Source/PuddingSshTests/SshOperationLifetimeFenceTests.cs` — 新增 12 例回归（预先取消两入口零副作用、
  宿主 deadline 过期零副作用、身份忽略 token 有界且晚成功只销毁材料、身份晚失败被观察、建连忽略 token 有界且
  迟到会话被回收且零 exec、建连晚失败/晚拒绝被观察、跨阶段共享预算、Core 停止报 `host_stopping` 而非 timeout、
  调用方取消报 `caller_cancelled`、排队中取消不读身份）。测试用 TCS 控制晚返回，操作统一用**有界等待**断言。
- `Source/PuddingSshTests/SshFakes.cs` — `FakeIdentityProvider` 增加 `OnGetAsync`（模拟忽略 token / 晚返回 / 晚失败）。
- `TestScripts/ssh-probe/run-ssh-probe.ps1` — 新增 `Initialize-ProbeBuild`：`temp/build` 被并发清理后
  探针 dll 可能消失（只剩 exe 会以「application to execute does not exist」崩掉），此时按需重建一次。

## 验证

**先取红再复绿**：

- 新增回归在旧实现下 **9/12 失败**（预先取消仍执行、身份/建连忽略 token 时永不返回、Core 停止被写成 timeout）。
  取红时把操作等待改为有界等待（`WaitOrFailAsync`），让缺陷表现为断言失败而不是整轮挂起。
- 修补后：`dotnet test Source/PuddingSshTests/PuddingSshTests.csproj` → **149/149 通过**，0 警告 0 错误。
- 真实协议：`powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1` → **17 场景 `failures = 0`**。
- 实测要点：排队中取消不读身份（服务端认证数 0）；建连忽略 token 时操作在 deadline+清理宽限内返回且
  `execution_state=unknown`；迟到连接被回收且 `ExecuteCount=0`；Core 停止返回 `ssh.cancelled` + `stop_reason=host_stopping`。

## 已知限制（R3 处理，不在本切片兑现）

- 阶段端口的**同步启动段**目前仍在调用线程上执行：本切片只保证「不等待它」，没有把它移进有界 worker。
  R3 用资源租约/有界 worker 接管这一点（ADR-096 明确「不声称只加 WaitAsync 就完成本切片」）。
- 排空与 Dispose 仍各自等待，尚未共用同一份清理预算；迟到会话的回收路径也尚未有界化 —— 属 R3。

## 未完成 / 后续

- **R3**：非阻塞 `RequestAbort()`、提交事实、`SshResourceLease`（单一清理预算、迟到资源所有权、worker/租约可查询有界）。
- **R4**：输出完整性（EOF / 读失败 / 超时分离）与公开诊断有界。
- **R5**：硬限额机器校验 + 扩展真实协议矩阵收口 + 重新申请 S5。
- 本次未改 Runtime/Host/DI/slnx；S5 仍保持未准入。
