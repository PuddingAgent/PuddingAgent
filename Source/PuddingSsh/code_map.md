# PuddingSsh · code_map

> 设计依据：[SSH 工具组件化设计与实施方案](../../Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md)
> （§4 组件与依赖边界、§7 工具合同、§9 并发/输出/超时、§10 诊断合同、§12 实施顺序）。
> 交付规程状态：**A1 / S1 已交付，S2–S5 未开始**（独立测试工程、协调器、接入宿主都还没做）。
>
> 边界硬规则（`Docs/10_conventions/组件化交付规程.md` §2.2，判据是**编译期失败**）：
> `ProjectReference` **必须为 0**；不得引用 `PuddingCore` / `PuddingRuntime` / `PuddingPlatform` /
> `PuddingHost` / `PuddingDesktop` / `PuddingAgent` 或 EF Core / ASP.NET Core / Roslyn / MSBuild；
> 唯一允许的包引用是 `SSH.NET`（实现细节），**公开合同不得出现第三方类型**。

## 1. 这个组件是什么

把「一条 SSH 命令怎么安全地提交、怎么在不被输出淹没的前提下取回结果、怎么在有界时间内终止」
从 Runtime 的工具/审批/DI 里抽出来，成为可以在宿主运行中独立构建（规程 R1）的叶子组件。

**负责**：目标/限额/结果 DTO、认证材料的内存所有权、握手前主机密钥裁定端口、
独占连接的 Exec、stdout+stderr 有界采集、取消/超时后的 TERM 与连接关闭、稳定错误码。

**不负责**（留给引用方，S5 才做）：DataRoot 与 Agent 主体解析、`.ssh` 目录选择与 ACL 检查、
known_hosts/pin 的**读取与解析**（端口已就位，实现属 A2）、主机可见性 ACL 与 revision 校验、
远程命令不变量、并发队列与 deadline 合成（A3）、审批与审计、工具注册与展示。

## 2. 文件索引

| 文件 | 职责 | 关键符号 | 约束 |
|---|---|---|---|
| `Contracts/SshTargets.cs` | 冻结目标与主机描述 | `SshTargetRef`、`SshHostDescriptor`、`SshIdentityScopes`、`SshShellKinds` | `HostRevision`/`TrustRevision` 是**权威快照**，不是调用方可覆盖值；作用域不能按次切换 |
| `Contracts/SshLimits.cs` | 限额与硬上限唯一真源 | `SshOperationLimits.ResolveOperationTimeout/ResolveOutputBudget`、`CleanupGrace`、`MaxOutputBytesHardLimit`、`WaitQueueHardLimit` | 限额**只能缩小**：`min(请求, 主机, 硬上限)`；清理宽限不延长业务许可 |
| `Contracts/SshRequests.cs` | Test/Execute 请求 | `SshTestRequest`、`SshExecuteRequest`、`SshRequestLimits.MaxCommandUtf8Bytes` | 无 env / stdin / shellKind 覆盖 / PTY；命令 16 KiB 上限 |
| `Contracts/SshResults.cs` | 结果与事实枚举 | `SshExecutionStatus`、`SshExecutionState`、`SshTestResult`、`SshExecuteResult` | `ExitCode` 未收到服务端 exit-status 时**为 null**，不得伪造 0；stderr 非空 ≠ 失败 |
| `Contracts/SshPorts.cs` | 公开最小端口 + 认证材料所有权 | `ISshClient`、`ISshIdentityProvider`、`ISshHostKeyVerifier`、`ISshTransportFactory`、`ISshTransportSession`、`SshIdentityMaterial`、`SshHostKeyEvidence/Decision` | 不返回 `SshClient`、不外泄第三方事件类型；`SshIdentityMaterial` 释放时清零密钥 |
| `Diagnostics/SshDiagnostics.cs` | 错误码/阶段/线上名唯一真源 | `SshErrorCodes`、`SshPhase`、`SshWireNames` | 公开错误码是稳定合同；枚举→字符串只在此处定义 |
| `Diagnostics/SshFailureClassifier.cs` | 异常类别 + 提交事实 → 稳定错误码 | `SshComponentException`、`SshHostKeyRejectedException`、`SshFailureClassifier.Classify/IsRetrySafe` | A2 会补齐凭据/目录/known_hosts 分类；不靠异常文本猜语义 |
| `Execution/SshBoundedOutputCollector.cs` | 双流同时排空 + 共享预算 + 增量 UTF-8 解码 | `SshBoundedOutputCollector` | 固定缓冲（2×预算 + 16 KiB 池化读缓冲）；捕获满**继续计数并丢弃**；截断不产生 U+FFFD；**禁止**整份 `Result`/`ReadToEnd` |
| `Transport/SshNetTransportFactory.cs` | 建连：密钥、认证方法、默认拒绝的主机密钥回调 | `SshNetTransportFactory` | `RetryAttempts=1`（库默认 10）、`MaxSessions=1`（库默认 10）、`Timeout` 由 deadline 决定；回调在 `ConnectAsync` 之前安装；加密私钥 ⇒ `ssh.passphrase_required` |
| `Transport/SshNetTransportSession.cs` | 独占连接的 Exec、退出证据、终止与释放 | `SshNetTransportSession` | 取消/超时对 exec channel 发 TERM 后关连接；无退出码/信号 ⇒ `execution_state=unknown`；释放路径不抛新错误覆盖业务结果 |

## 3. 不变量（改这个组件前先读）

1. **默认拒绝的主机密钥**：握手回调先装、只认验证器明确 Trust；未知/变化 ⇒ `ssh.host_key_untrusted` / `ssh.host_key_mismatch`，且**认证不会发生**（探针以服务端 `Accepted publickey` 计数为 0 取证）。
2. **验证失败不落地秘密**：加密私钥只回 `ssh.passphrase_required`，错误文本、结果与日志都不含口令与私钥路径。
3. **退出码不伪造**：只有服务端给出 exit-status 才填 `ExitCode`；只给信号则填 `ExitSignal`；两者都没有 ⇒ `ssh.exit_status_missing` + `execution_state=unknown`。
4. **取消 ≠ 远端已死**：组件发 TERM 后即释放连接，是否真的退出由远端证据判定；`retry_safe` 只在**确定未提交**时为 true。
5. **无自动重放**：任何已提交命令都不自动重试；连通性失败也不自动重连。
6. **输出与输出量解耦**：捕获有界（预算内），内存不随远端输出增长（见 §4 实测）。
7. **限额只能缩小**：`ResolveOperationTimeout` / `ResolveOutputBudget` 是唯一入口，调用方不能借参数放大。
8. **零上层依赖**：任何 `ProjectReference` 或对 Pudding 上层程序集的 `using` 都是边界破裂。
9. **会话不含并发保护**：一个 `ISshTransportSession` 代表一条独占连接，同一时刻只应有一个 Exec；
   并发准入/排队/`ssh.busy` 由 A3 的 `ISshClient` 实现负责（`MaxSessions=1` 是库侧兜底，不是队列）。

## 4. 验证与门禁（A1 / S1 实测）

| 位置 | 覆盖 |
|---|---|
| `Source/PuddingSsh.Probe/` | 真实协议探针（`fingerprint` / `hostkey` / `encrypted` / `deadline` / `cancel` / `throughput` / `matrix`），只用组件公开合同，退出码表达成败 |
| `TestScripts/ssh-probe/setup-wsl-sshd.sh` | 一次性受控 OpenSSH 夹具：Ubuntu 26.04.1 LTS（WSL2）+ `OpenSSH_10.2p1 Ubuntu-2ubuntu3.7`，全部密钥与配置都在 `/tmp/pudding-ssh-probe`，不触碰系统 sshd |
| `TestScripts/ssh-probe/run-ssh-probe.ps1` | 夹具生命周期 + 逐场景运行 + **服务端认证证据**核对（拒绝场景 `Accepted publickey` 增量必须为 0）+ 指纹与 `ssh-keygen` 交叉核对 |

命令（输出只进 `temp/`）：

```powershell
dotnet build Source/PuddingSsh.Probe/PuddingSsh.Probe.csproj --artifacts-path temp/build/ssh --nologo
powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1
```

2026-10-08 实测结论（完整数字见 [A1 库能力实测报告](../../Docs/14_reports/2026-10-08-PuddingSsh-A1-库能力实测报告.md)）：
7 个场景 exit 0、`failures = 0`；错指纹 137 ms 内被拒且服务端零认证；取消 1.53 s 收敛并回报 `ExitSignal=TERM`，
远端 PID 已消失、标记文件未生成；240 MiB 双流下捕获恒为 64 KiB、驻留托管内存 < 0.4 MiB。

**已知测量事实（A3 必须处理）**：托管**总分配量**与收到字节数近似线性（1× 18.96 MB / 3× 56.04 MB ≈ 0.22 字节/字节），
来源是库内部按包缓冲的 churn（可回收，非驻留）；共享捕获预算由先填满的流独占，stderr 可能捕获为 0 字节。

## 5. 尚未交付（A2–A5）

- **A2**：`Source/PuddingSshTests/` 独立测试工程、known_hosts（明文/逗号分隔/`|1|salt|hash`）与 pin 验证器、POSIX `cd` 封装、完整错误分类器。
- **A3**：`ISshClient` 协调器（并发准入 2/主机、Core 总并行 8、等待队列 32、deadline 合成、`ssh.busy`）、假传输故障矩阵。
- **A4**：真实 OpenSSH 全矩阵（两类 `.ssh`、撤销记录、`@revoked`、错误账号、断线、变异取红）与 S4 边界断言。
- **A5（S5）**：登记 `PuddingAgentNetwork.slnx`、Runtime 主机目录/ACL/revision/凭据适配、三个 Agent 工具与 DI 装配。
