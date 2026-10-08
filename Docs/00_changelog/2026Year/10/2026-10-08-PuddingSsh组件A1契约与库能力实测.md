---
title: 2026-10-08 PuddingSsh 组件 A1：契约、端口与库能力实测
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "按设计方案 A1/S1 新建 PuddingSsh 叶子组件（DTO/端口 + SSH.NET 固定版本适配）与独立真实协议探针：构建、依赖审计、指纹拒绝/取消/双流输出实测通过；未接宿主。"
categories: [docs, changelog]
tags: [ssh, tools, componentization, probe, security]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/10_conventions/组件化交付规程.md, Docs/14_reports/2026-10-08-PuddingSsh-A1-库能力实测报告.md]
related_files: [Source/PuddingSsh/PuddingSsh.csproj, Source/PuddingSsh/Contracts/SshPorts.cs, Source/PuddingSsh/Transport/SshNetTransportFactory.cs, Source/PuddingSsh/Transport/SshNetTransportSession.cs, Source/PuddingSsh/Execution/SshBoundedOutputCollector.cs, Source/PuddingSsh.Probe/Program.cs, TestScripts/ssh-probe/run-ssh-probe.ps1, TestScripts/ssh-probe/setup-wsl-sshd.sh, Source/PuddingSsh/code_map.md, code_map.md]
slug: changelog-pudding-ssh-a1-contracts-and-library-capability
draft: false
---

# 2026-10-08 PuddingSsh 组件 A1：契约、端口与库能力实测

## 目标 / 背景

执行[SSH 工具组件化设计与实施方案](../../../12_features/SSH工具组件化设计与实施方案-2026-10-08.md) 的**首个施工任务 A1 / S1**：
新建 `PuddingSsh` 叶子工程、交付 DTO/端口、固定 `SSH.NET` 版本、用独立探针实测库能力。
按[组件化交付规程](../../../10_conventions/组件化交付规程.md)，**S5 之前不改宿主**：本次未登记 `PuddingAgentNetwork.slnx`、
未在任何消费方 csproj 加引用、未进 DI、未改 Runtime/Host/Desktop。

## 改动

### 新增组件 `Source/PuddingSsh/`（`net10.0`，`ProjectReference=0`）

- `Source/PuddingSsh/PuddingSsh.csproj` — 新增。唯一包引用 `SSH.NET` **2026.0.0**（固定版本，非浮动）。
- `Contracts/SshTargets.cs`、`Contracts/SshLimits.cs`、`Contracts/SshRequests.cs`、`Contracts/SshResults.cs` —
  新增。冻结目标 `SshTargetRef`、限额唯一真源 `SshOperationLimits`（只能缩小的 `Resolve*`）、
  Test/Execute 请求、`SshExecutionStatus`/`SshExecutionState` 与结果 DTO（`ExitCode` 可空，不伪造 0）。
- `Contracts/SshPorts.cs` — 新增。公开最小端口 `ISshClient` / `ISshIdentityProvider` / `ISshHostKeyVerifier` /
  `ISshTransportFactory` / `ISshTransportSession`，以及释放时清零的 `SshIdentityMaterial`、
  BCL-only 的 `SshHostKeyEvidence` / `SshHostKeyDecision`（**不外泄第三方事件类型**）。
- `Diagnostics/SshDiagnostics.cs` — 新增。设计 §10 的错误码、`SshPhase` 与线上名 `SshWireNames`（唯一真源）。
- `Diagnostics/SshFailureClassifier.cs` — 新增。`SshComponentException` / `SshHostKeyRejectedException` +
  「异常类别 + 提交事实 → 稳定错误码」最小分类器（A2 再补齐）。
- `Execution/SshBoundedOutputCollector.cs` — 新增。stdout/stderr **同时排空**、共享固定预算、
  捕获满继续计数并丢弃、增量 UTF-8 解码（`flush:false`，截断不产生 U+FFFD）。
- `Transport/SshNetTransportFactory.cs` — 新增。显式覆盖库的不安全默认值：`RetryAttempts=1`（库默认 10）、
  `MaxSessions=1`（库默认 10）、`Timeout` 由建连 deadline 决定；主机密钥回调在 `ConnectAsync` **之前**安装且默认拒绝；
  加密私钥 ⇒ `ssh.passphrase_required`。
- `Transport/SshNetTransportSession.cs` — 新增。独占连接 Exec、退出码/信号证据、取消/超时后 TERM 并关闭连接、
  无证据时 `execution_state=unknown`、释放路径不抛新错误覆盖业务结果。
- `Source/PuddingSsh/code_map.md` — 新增。组件文件索引、不变量、门禁与「尚未交付（A2–A5）」。

### 新增独立探针 `Source/PuddingSsh.Probe/`

- `PuddingSsh.Probe.csproj`、`Program.cs`、`ProbeOptions.cs`、`ProbeSupport.cs` — 新增。
  只引用 `PuddingSsh`；场景 `fingerprint` / `hostkey` / `encrypted` / `deadline` / `cancel` / `throughput` / `matrix`，
  用**退出码**表达成败。探针内的「观测后一律拒绝」验证器只属探针，交付路径不提供首次信任。

### 新增测试夹具脚本 `TestScripts/ssh-probe/`

- `setup-wsl-sshd.sh` — 新增。在 WSL 内重建一次性 sshd 夹具（`/tmp/pudding-ssh-probe`），不触碰系统 sshd。
- `run-ssh-probe.ps1` — 新增。夹具生命周期 + 逐场景运行 + **服务端认证证据**核对
  （拒绝场景 `Accepted publickey` 增量必须为 0）+ 指纹与 `ssh-keygen -lf` 交叉核对。
  文件保持 **UTF-8 with BOM**（非 ASCII 文本 + Windows PowerShell 5.1 解码规则，`check-script-encodings.ps1` 门禁）。

### 文档与索引

- `Docs/14_reports/2026-10-08-PuddingSsh-A1-库能力实测报告.md` — 新增（完整证据）。
- `Source/PuddingSsh/code_map.md`、`code_map.md`（§2 组件行 + §5 测试工程行）— 新增/更新。
- `.gitattributes` — 新增 `*.sh text eol=lf`。仓库此前**没有**任何追踪的 `.sh`；在 `* text=auto` +
  `core.autocrlf=true` 下，新检出会给 bash 一个 CRLF 脚本（shebang 与每条命令都会失败）。
  本次新增的夹具脚本是被 WSL bash 执行的，因此需要这条规则。

## 验证

| 项 | 命令 | 结果 |
|---|---|---|
| 组件构建 | `dotnet build Source/PuddingSsh/PuddingSsh.csproj --artifacts-path temp/build/ssh --no-restore` | exit 0，0 警告 0 错误 |
| 探针构建 | `dotnet build Source/PuddingSsh.Probe/PuddingSsh.Probe.csproj --artifacts-path temp/build/ssh` | exit 0，0 警告 0 错误 |
| 依赖审计 | `dotnet list ... package --vulnerable --include-transitive` | 无易受攻击的包（0 条）；传递闭包 3 个包 |
| 组件边界 | `dotnet list ... reference` + 上层命名空间检索 | 项目引用 **0**；禁用命名空间命中 **0** |
| 真实协议实测 | `powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1` | **7 场景全 exit 0、`failures = 0`** |
| 脚本编码门禁 | `TestScripts/check-script-encodings.ps1 -Recurse -AllowUnrunnable` | 本脚本 `OK bom=True errors=0`；5 条 `UNRUNNABLE`（`#requires 7.0`）为**既有**项，非本次引入 |

实测要点（服务端：WSL2 Ubuntu 26.04.1 LTS + `OpenSSH_10.2p1 Ubuntu-2ubuntu3.7`，夹具每次重建密钥）：

- 错指纹 137 ms 内被拒（`ssh.host_key_mismatch`）且服务端 `Accepted publickey` 增量 **0**；观测模式下同样 0 认证，
  探针观测指纹与 `ssh-keygen -lf` 逐字一致。
- 加密私钥 ⇒ `ssh.passphrase_required`（内层 `SshPassPhraseNullOrEmptyException`），错误文本不含口令，未发生认证。
- 建连 deadline 3 s ⇒ 实测 3051 ms 失败（`ssh.timeout`），未叠加各阶段超时。
- 取消 `sleep 30`：1.53 s 收敛，`exit_signal=TERM`、`exit_code=null`；2 s 后远端 `SHELL_GONE` + `MARKER_ABSENT`。
- 双流 240 MiB：收到 251,658,240 B、捕获 65,536 B（=预算）、截断标记正确、约 310 MiB/s；
  **驻留**托管内存增量 < 0.4 MiB 且不随输出量增长；Unicode 截断无 U+FFFD。
- 已知测量事实（交 A3）：托管**总分配量**与收到字节近似线性（≈0.22 B/B，属可回收 churn）；
  共享预算可能被单流占满（stderr 捕获到 0 字节）。

## 未完成 / 后续

- **A2**：`Source/PuddingSshTests/` 独立测试工程、known_hosts（明文/逗号/`|1|salt|hash`）/pin 验证器、
  POSIX `cd` 封装、完整错误分类器。
- **A3**：`ISshClient` 协调器（并发准入、等待队列、deadline 合成、`ssh.busy`）与假传输故障矩阵。
- **A4**：真实 OpenSSH 全矩阵（两类 `.ssh`、撤销记录、错误账号、断线）与 S4 边界断言/变异取红。
- **A5（S5）**：登记解决方案、Runtime 适配（目录/ACL/revision/凭据）、三个 Agent 工具与 DI 装配。
- 本次**未**验证：known_hosts 解析、`.ssh` 目录与权限检查、并发准入、Windows OpenSSH 服务端、
  产品态（新构建已被运行实例加载）验收 —— 均属上述后续步骤。
- 未提交任何 `temp/` 产物；`D:\data` 未被读写，未使用生产主机。
