---
title: 2026-10-08 PuddingSsh A1 库能力实测报告
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: active
description: "PuddingSsh 组件 A1/S1 的真实协议实测证据：受控 WSL OpenSSH 夹具下逐场景核对指纹拒绝（服务端零认证）、建连 deadline、取消的远端证据、双流输出预算与驻留内存。"
categories: [docs, reports]
tags: [ssh, sftp, componentization, probe, evidence]
related_docs: [Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md, Docs/10_conventions/组件化交付规程.md, Docs/00_changelog/2026Year/10/2026-10-08-PuddingSsh组件A1契约与库能力实测.md]
related_files: [Source/PuddingSsh/PuddingSsh.csproj, Source/PuddingSsh.Probe/Program.cs, TestScripts/ssh-probe/run-ssh-probe.ps1, TestScripts/ssh-probe/setup-wsl-sshd.sh, Source/PuddingSsh/code_map.md]
slug: reports-pudding-ssh-a1-library-capability-probe-2026-10-08
draft: false
---

# 2026-10-08 PuddingSsh A1 库能力实测报告

本报告是 [SSH 工具组件化设计与实施方案](../12_features/SSH工具组件化设计与实施方案-2026-10-08.md)
**A1 / S1** 判据的执行证据：`net10.0` 构建、依赖审计、以及「未知指纹拒绝、取消、高速双流输出」的库能力实测。
判据逐条对应设计 §3「S1 内必须验证」。

## 1. 环境与夹具

| 项 | 值 |
|---|---|
| 探针运行环境 | Windows，.NET SDK `10.0.401`，`PuddingSsh.Probe`（`net10.0`，Debug） |
| 组件 | `Source/PuddingSsh/PuddingSsh.csproj`：`ProjectReference=0`、唯一包引用 `SSH.NET` 2026.0.0 |
| 服务端 | WSL2 Ubuntu 26.04.1 LTS + `OpenSSH_10.2p1 Ubuntu-2ubuntu3.7, OpenSSL 3.5.5 27 Jan 2026` |
| 夹具位置 | WSL `/tmp/pudding-ssh-probe`（**不触碰系统 sshd**；`setup-wsl-sshd.sh` 每次重建） |
| 监听 | `127.0.0.1:22122`，`PasswordAuthentication no`、`PubkeyAuthentication yes`、`StrictModes no`、`LogLevel VERBOSE`、日志 `-E /tmp/pudding-ssh-probe/sshd.log` |
| 密钥 | 每次运行新生成：服务端 `ed25519`+`rsa3072` 主机密钥；客户端 `id_ed25519`（无口令）与 `id_ed25519_encrypted`（口令 `probe-passphrase`，仅存在于夹具内） |

主机密钥指纹不写死：每次运行从夹具 `ssh-keygen -lf` 取得，并与探针握手观测值交叉核对。

## 2. 依赖审计（restore + 漏洞）

```powershell
dotnet restore Source/PuddingSsh/PuddingSsh.csproj --artifacts-path temp/build/ssh
dotnet list Source/PuddingSsh/PuddingSsh.csproj package --include-transitive
dotnet list Source/PuddingSsh/PuddingSsh.csproj package --vulnerable --include-transitive
```

| 项 | 结果 |
|---|---|
| 直接依赖 | `SSH.NET` 2026.0.0（固定版本，无浮动） |
| 传递闭包 | `BouncyCastle.Cryptography` 2.7.0、`Microsoft.Extensions.Logging.Abstractions` 8.0.3、`Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.2 |
| 目标框架资产 | 包内提供 `lib/net10.0`（`SSH.NET` 确有 .NET 10 目标） |
| 漏洞 | `dotnet list package --vulnerable --include-transitive`：**没有易受攻击的包**（0 条） |
| 组件边界 | `dotnet list ... reference`：**没有任何项目到项目引用**；组件源码对 Pudding 上层命名空间 `using` 命中 **0** |

## 3. 运行命令

```powershell
dotnet build Source/PuddingSsh.Probe/PuddingSsh.Probe.csproj --artifacts-path temp/build/ssh --nologo
powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1
```

运行器逐场景执行探针，并用 sshd 日志核对**认证是否真的没有发生**（拒绝场景 `Accepted publickey` 计数增量必须为 0）。
原始输出：`temp/test-out/ssh/probe-run.log` 与 `temp/test-out/ssh/probe-<scenario>.log`（`temp/` 不入库）。

## 4. 场景结果（最终一次运行，2026-10-08 23:12）

| 场景 | 关键事实 | 服务端认证增量 | 结论 |
|---|---|---|---|
| `fingerprint`（观测并一律拒绝） | `ssh.host_key_untrusted`；拒绝耗时 **138 ms**；观测 `alg=ssh-ed25519`、`blob_len=51`；指纹与 `ssh-keygen -lf` **完全一致** | **0** | 验证失败即中断，未进入认证 |
| `hostkey --expect accept` | 建连成功（186 ms）；`VerifiedHostKeySha256` == 期望指纹 | 1 | 正确指纹可用，且会话回报已验证指纹 |
| `hostkey --expect reject`（诱饵指纹） | `ssh.host_key_mismatch`；拒绝耗时 **137 ms** | **0** | 错指纹在认证之前被拒 |
| `encrypted`（加密私钥） | `ssh.passphrase_required`；异常类型 `SshComponentException`，内层 `SshPassPhraseNullOrEmptyException`；错误文本不含口令 | **0** | 首期口令需求是**明确错误**，不是静默失败 |
| `deadline`（`192.0.2.1:22`，建连 deadline 3 s） | `ssh.timeout`，实测 **3051 ms** | 0 | 建连 deadline 有界（未叠加各阶段超时） |
| `cancel`（`sleep 30; touch marker`，1.5 s 后取消） | `status=cancelled`、`execution_state=exited`、`exit_code=null`、`exit_signal=TERM`、`phase=cleanup`、组件侧耗时 **1530 ms**（总 1731 ms） | 2（被取消 + 取证各一条连接） | 取消发 TERM 后收敛；退出证据由服务端信号给出 |
| `cancel` 的**远端证据**（取消后 2 s 复查） | `SHELL_GONE`、`MARKER_ABSENT` | — | 远端 shell 已消失、后续标记未生成（不是「只让本地放弃等待」） |
| `throughput` 1×（64 MiB stdout + 16 MiB stderr，同一时刻） | 收到 **83,886,080 B**（精确相等）；捕获 **65,536 B**（=预算）；`output_truncated=true`；**225 ms**（≈354 MiB/s） | 1 | 双流同时排空、计数完整、捕获受限 |
| `throughput` 3×（192 MiB + 48 MiB） | 收到 **251,658,240 B**；捕获 **65,536 B**；**775 ms**（≈310 MiB/s） | 1 | 输出量 3 倍时捕获仍恒定 |
| Unicode 分块（预算 32 B，输出 200×「中」） | 捕获 32 B，解码为 10 个完整「中」，**无 U+FFFD** | 1 | 截断处不破坏字符 |

汇总：7 个场景（含 3 个额外连接）**全部 exit 0**，运行器 `failures = 0`，指纹交叉核对一致。

## 5. 内存测量

一次运行内先做 5 MiB 预热，再分别测 1× 与 3× 输出（捕获预算均为 64 KiB）：

| 指标 | 1×（收到 80 MiB） | 3×（收到 240 MiB） |
|---|---|---|
| 捕获字节 | 65,536 | 65,536 |
| **驻留**托管内存增量（`GC.GetTotalMemory(forceFullCollection: true)` 差值） | **322,792 B** | **387,272 B** |
| 托管**总分配量**（`GC.GetTotalAllocatedBytes(precise: true)` 差值） | 18,960,576 B | 56,042,896 B（比值 **2.96**） |
| 进程工作集增量 | 16,957,440 B | 5,939,200 B |

结论与**限制**：

1. 驻留内存与输出量无关（两次都 < 0.4 MiB，且 3× 并未增长），说明不存在「先取整份 `Result` 再截断」或按输出量累积的缓冲。若实现退化为整份缓冲，本表第一步就会因 `captured > budget` 或驻留量级跃升而取红。
2. 托管**总分配量**与收到字节近似线性（≈0.22 字节/字节，3× 输出 ≈2.96× 分配）。这是**可回收 churn**（来源是库内部按 SSH 包处理数据的临时缓冲），不是驻留泄漏；但它决定了 A3 的并发预算（8 路并行 × 高速流会带来成比例 GC 压力），因此在此如实记录。
3. 共享捕获预算由**先填满的流独占**：1× 出现过 `stdout 49,152 / stderr 16,384`，3× 出现过 `stdout 65,536 / stderr 0`。这符合「stdout + stderr 合计 64 KiB」的合同，但意味着 stderr 可能完全看不到内容。A3/A5 若要保证 stderr 可诊断性，需要在组件或展示层明确取舍（本次不改合同）。
4. 工作集读数在不同轮次波动较大（1× 16.9 MB、3× 5.9 MB），属 GC 回收时机差异，不作为判据；判据用驻留托管内存。

## 6. 本次**未**覆盖（诚实边界）

- known_hosts 的明文/逗号分隔/`|1|salt|hash` 解析与 `@revoked` 语义 —— A2（本次只验证「指纹原语与 OpenSSH 一致」：SHA256 指纹逐字相同）。
- `.ssh` 目录解析（current_user / agent_private）、Windows ACL 与 Unix owner/权限检查 —— A2/A5。
- 并发准入、等待队列、deadline 合成、`ssh.busy` —— A3。
- 断线/丢包/服务端忽略 TERM、错误账号、撤销记录、Windows OpenSSH 服务端编码 —— A4。
- 组件在**宿主运行中**的独立构建（规程 S3）已顺带满足（本次构建未触碰宿主、未登记 slnx），但 `ComponentBoundaryTests` 形式的机器可验断言属 A4。
- 内部 Agent 无法验收「新构建已被运行实例加载」；A1 结论仅限组件边界内，接入与产品态验收属 A5/A6。

## 7. 复现注意

- `run-ssh-probe.ps1` 必须保持 **UTF-8 with BOM**（含非 ASCII 文本 + Windows PowerShell 5.1 解码规则），
  `TestScripts/check-script-encodings.ps1` 是这条门禁；本机 PowerShell 为 5.1，`pwsh` 7 不在 PATH。
- 夹具每次重建密钥；如残留旧夹具 sshd 占用端口，运行器会先按配置路径终止它（否则指纹交叉核对会（正确地）失败）。
- 探针的观测模式（记录后一律拒绝）只存在于探针内，**不在交付路径**；交付路径不提供首次信任。
