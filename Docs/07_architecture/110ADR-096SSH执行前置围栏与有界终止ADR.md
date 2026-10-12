---
title: ADR-096：SSH 执行前置围栏与有界终止
author: hyfree
date: 2026-10-12
last_reviewed: 2026-10-12
status: proposed
description: "规定 SSH 的工作目录守卫、取消与截止前置围栏、提交事实、输出完整性和资源回收合同，作为独立组件进入 S5 的前置约束。"
categories: [docs, architecture]
tags: [adr, ssh, cancellation, lifecycle, security]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/12_features/SSH-S5前置修补与代码级施工方案-2026-10-12.md, Docs/14_reports/2026-10-12-SSH-S1-S4审阅与S5准入结论.md, Docs/10_conventions/组件化交付规程.md]
related_files: [Source/PuddingSsh/Validation/SshPosixCommand.cs, Source/PuddingSsh/Execution/SshOperationCoordinator.cs, Source/PuddingSsh/Execution/SshBoundedOutputCollector.cs, Source/PuddingSsh/Transport/SshNetTransportSession.cs, Source/PuddingSsh/Transport/SshNetTransportFactory.cs]
slug: architecture-adr-096-ssh-execution-fences-and-bounded-termination
draft: false
---

# ADR-096：SSH 执行前置围栏与有界终止

状态：Proposed。本文定义施工合同，不代表修补已实现。实施文件与回归门禁见[代码级施工方案](../12_features/SSH-S5前置修补与代码级施工方案-2026-10-12.md)，当前验收证据见[审阅报告](../14_reports/2026-10-12-SSH-S1-S4审阅与S5准入结论.md)。

## 1. 范围与已有决策

保留 SSH 叶子组件、独占连接、跨平台当前用户/Agent 私有 `.ssh`、known_hosts/pin、无自动重放与无 KeyVault 的设计。修补只在组件与独立测试边界内进行，门禁通过前不得新增宿主引用、DI 或 slnx 登记。

本 ADR 修正原方案的 `cd && <原始命令>` 工作目录保证，并明确取消、提交与资源回收的事实边界。它不引入远程 shell 安全解析器，也不承诺任意托管代码能够被强制终止。

## 2. 决策

### D1. 工作目录必须是整个脚本的前置守卫

有 cwd 的 POSIX 执行串统一为：

```sh
cd -- '<按现有 Quote 编码的 cwd>' || exit $?
<原始 command>
```

换行是固定合同的一部分。`cd` 失败时当前 shell 退出，后续命令中的分号、换行、`||` 与后台列表都不得被执行。`cwd=null` 时保持原始命令。不得用 `set -e` 代替守卫，不增加 subshell 改变原始脚本语义。

原始命令仍由远端账号权限与既有审批控制；cwd 不成为文件访问沙箱。审批与执行必须调用同一 Compose，精确执行串变化属于工具定义变化，旧审批不得跨新定义复用。

### D2. 取消/截止是前置围栏，不能只把 token 传给下一层

在受理、准入前后、身份获取后、建连后和提交前检查统一的终止事实。已经取消或过期的请求不读取身份、不建连、不提交。

每个阶段共用一个操作绝对 deadline；不得在 exec 阶段重新得到完整的原始预算。身份或建连晚返回，只能触发材料清零与连接回收，绝不能开启下一阶段。

调用方取消、Core 停止、操作 deadline、连接 deadline 保留各自原因。关闭 Core 产生的取消不能自动写成操作超时；`Task` 方法名不证明其同步启动部分可取消。

### D3. 业务返回有界，未回收的资源仍有人负责且占用预算

排空、建连、执行和释放都不得在预算耗尽后无条件 await。终止后先尝试当前 channel 的 TERM，再执行 Abort 关闭独占连接，回收过程最多消耗同一份清理宽限。

不能把 `Task.WhenAny` 到期、调用 Dispose 或本地等待结束当作“连接已释放”。未完成工作仍由资源租约持有：迟到材料销毁、迟到连接关闭、任务异常观察都必须安排；关闭前占用原槽位或有界隔离槽位，预算耗尽后拒绝新的工作。禁止无限创建脱离追踪的 Task.Run/流读取任务。

对于不合作的受控测试端口，业务调用在预算内结束并报告 cleanup pending；对于真实网络适配器，必须验证 Abort 能关闭实际 socket/reader。若真实库无法做到，先在组件边界重新裁定适配方式，不接入 Core。

### D4. 远端退出、提交与输出完整性分别表达

进入可能发送 exec 的调用之前推进 `submission_attempted`。除非底层提供明确的未发送证据，提交窗口异常统一为远端状态 unknown、不可重试。只在证明尚未触及提交且失败属可重试连接/排队类时设置 retry_safe。

服务端给出 exit-status/exit-signal 决定远端退出事实；stdout/stderr 的 EOF 决定输出是否读完。读异常、排空超时或丢失缓冲均不得伪装为完整 EOF。增加 `output_complete`、不完整原因及 `cleanup_state`，保留已知退出码，不把输出问题写成远端执行未发生。

只有明确退出码 0、所需输出完整且没有执行失败时，完整操作才返回 Completed。信号结束、输出不完整或未收到退出证据必须有明确状态/错误码。预算截断与读取失败是不同事实：预算截断可以 `output_complete=true`，同时 `output_truncated=true`。

### D5. 硬限额在组件入口强制，公开错误不得携带原始载荷

协调器、主机限额与请求分别校验；Core 并发最多 8、等待队列最多 32、每主机并发最多 2、操作时长最多 600 秒、捕获最多 256 KiB、清理宽限最多 2 秒。非法配置明确拒绝，不能用 Math.Max 把错误配置伪装成有效值。

保留稳定 phase 与错误类别，不将第三方异常 Message、known_hosts 整行或私钥路径直接放进公开 Diagnostic。解析错误采用行号和原因码；所有 DTO 的诊断字段都有长度上限。原始异常不成为结果合同。

## 3. 再准入原则

修补完成后先通过纯组件回归，再执行受控 OpenSSH 协议矩阵。S5 的路径/ACL/身份、目标 revision、工具审批与宿主生命周期工作仍按原计划执行；不得用 S5 包装层补救组件的取消、工作目录或输出保证。

## 4. 取舍

保留真实 SSH.NET 能力与已有测试资产；新增的资源租约是为约束迟到工作，非连接池。若无法证明进程内某个同步调用可终止，可以有界隔离并停止继续受理，不得以无限放弃线程换取表面超时成功。

对原设计的工作目录封装作显式变更，而非将命令限制成单个词；这样保留 Agent 执行复合 POSIX 脚本的业务能力，也使目录失败保证可被真实 shell 验证。
