# PuddingSsh · code_map

> 设计依据：[SSH 工具组件化设计与实施方案](../../Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md)
> （§4 组件与依赖边界、§6 凭据与主机验证、§7 工具合同、§9 并发/输出/超时、§10 诊断合同、§12 实施顺序）。
> 宿主接入前置合同：[ADR-096](../../Docs/07_architecture/110ADR-096SSH执行前置围栏与有界终止ADR.md)、[代码级修补方案](../../Docs/12_features/SSH-S5前置修补与代码级施工方案-2026-10-12.md)与[资源生命周期补充方案](../../Docs/12_features/SSH资源生命周期补充修补方案-2026-10-12.md)。组件没有宿主消费方，三个 Agent 工具尚未装配。
>
> 边界硬规则（`Docs/10_conventions/组件化交付规程.md` §2.2，判据是**编译期失败**）：
> `ProjectReference` **必须为 0**；不得引用 `PuddingCore` / `PuddingRuntime` / `PuddingPlatform` /
> `PuddingHost` / `PuddingDesktop` / `PuddingAgent` 或 EF Core / ASP.NET Core / Roslyn / MSBuild；
> 唯一允许的包引用是 `SSH.NET`（实现细节），**公开合同不得出现第三方类型**。
> 边界由 `PuddingSshTests/ComponentBoundaryTests` 运行期复核（含变异取红，见 §4）。

## 1. 这个组件是什么

把「一条 SSH 命令怎么安全地提交、怎么在不被输出淹没的前提下取回结果、怎么在有界时间内终止」
从 Runtime 的工具/审批/DI 里抽出来，成为可以在宿主运行中独立构建与测试（规程 R1/R4）的叶子组件。

**负责**：目标/限额/结果 DTO、认证材料的内存所有权、握手前主机密钥裁定（known_hosts / pin 的解析与匹配）、
POSIX 执行封装与请求形状校验、**并发准入（每主机 2 / Core 8 / 等待队列 32）、deadline 合成、停止与释放**、
独占连接的 Exec、stdout+stderr 有界采集、取消/超时后的 TERM 与连接关闭、稳定错误码与异常分类。

**不负责**（留给引用方，A5 才做）：DataRoot 与 Agent 主体解析、`.ssh` 目录选择与 ACL/owner 检查、
identity 文件名解析（禁止 `..`/绝对路径/symlink 逃逸）、主机可见性 ACL 与 revision 校验、
远程命令不变量（`SshRemoteCommandPolicy`）、并发队列与 deadline 合成（A3）、审批与审计、工具注册与展示。

## 2. 文件索引

| 文件 | 职责 | 关键符号 | 约束 |
|---|---|---|---|
| `Contracts/SshTargets.cs` | 冻结目标与主机描述 | `SshTargetRef`、`SshHostDescriptor`、`SshIdentityScopes`、`SshShellKinds` | `HostRevision`/`TrustRevision` 是**权威快照**，不是调用方可覆盖值；作用域不能按次切换 |
| `Contracts/SshLimits.cs` | 限额与硬上限唯一真源 | `SshOperationLimits.ResolveOperationTimeout/ResolveOutputBudget`、`CleanupGrace`、`MaxOutputBytesHardLimit`、`WaitQueueHardLimit` | 限额**只能缩小**：`min(请求, 主机, 硬上限)`；清理宽限不延长业务许可 |
| `Contracts/SshRequests.cs` | Test/Execute 请求 | `SshTestRequest`、`SshExecuteRequest`、`SshRequestLimits.MaxCommandUtf8Bytes` | 无 env / stdin / shellKind 覆盖 / PTY；命令 16 KiB 上限 |
| `Contracts/SshResults.cs` | 结果与事实枚举 | `SshExecutionStatus`、`SshExecutionState`、`SshTestResult`、`SshExecuteResult` | `ExitCode` 未收到服务端 exit-status 时**为 null**，不得伪造 0；stderr 非空 ≠ 失败 |
| `Contracts/SshPorts.cs` | 公开最小端口 + 认证材料所有权 | `ISshClient`（`TestAsync`/`ExecuteAsync` 收**冻结上下文** + 请求）、`ISshIdentityProvider`、`ISshHostKeyVerifier`、`ISshTransportFactory`、`ISshTransportSession`、`SshIdentityMaterial`、`SshHostKeyEvidence/Decision` | 不返回 `SshClient`、不外泄第三方事件类型；`SshIdentityMaterial` 释放时清零密钥 |
| `Contracts/SshHostContext.cs` | 引用方**冻结**后交给组件的输入 | `SshHostContext`（`Target`/`Limits`/`Identity`/`Trust`/`DefaultCwd`/`ExecutionDeadlineUtc`） | 组件不重读配置、不按别名解析、不接受调用方覆盖这些值 |
| `Diagnostics/SshDiagnostics.cs` | 错误码/阶段/线上名唯一真源 | `SshErrorCodes`（含 A2 新增 `ssh.request_invalid`）、`SshPhase`、`SshWireNames` | 公开错误码是稳定合同；枚举→字符串只在此处定义 |
| `Diagnostics/SshFailureClassifier.cs` | 异常类别 + 执行事实 → 稳定错误码/重试安全性 | `SshComponentException`、`SshHostKeyRejectedException`、`SshFailureFacts`、`SshFailure`、`Classify`、`IsRetrySafe` | 不看异常文本；同一种异常在**提交前后**语义不同（connect_failed vs connection_lost） |
| `Validation/SshHostTokens.cs` | known_hosts 主机 token 语义 | `SshHostTokens.ForTarget` | 22 端口裸主机名；非默认端口与 IPv6 用 `[host]:port`；**不做** DNS/地址互换 |
| `Validation/SshKnownHostsParser.cs` | known_hosts 解析（明文/逗号/`\|1\|salt\|hash`/`@revoked`） | `SshKnownHostsEntry`、`SshKnownHostsParseResult`、`Parse`、`ComputeHashedHostToken` | 通配/否定/`@cert-authority`/未知标记/非 v1 哈希/非法公钥 ⇒ **整行丢弃并 fail-closed** |
| `Validation/SshTrustSnapshot.cs` | 冻结信任快照 + 单目标验证器 | `SshTrustSource`、`SshTrustSnapshot.FromKnownHosts/FromPin/CreateVerifier/ComputeSha256Fingerprint`、`KnownHostsHostKeyVerifier`、`PinHostKeyVerifier` | 两种来源互斥、无首次信任；pin 指纹由**出示的 blob 现算**；撤销按密钥生效 |
| `Validation/SshPosixCommand.cs` | POSIX 执行封装与 cwd/命令校验 | `SshPosixExecutionPlan`、`Compose`、`Quote`、`IsValidCwd`、`ValidateCommand` | 精确串 = `cd -- '<cwd>' \|\| exit $?` 独立一行 + `\n` + **原样**命令（ADR-096 D1）；cwd 必须干净绝对路径；命令 16 KiB/NUL |
| `Contracts/SshTransferContracts.cs` | 传输契约（切片 B） | `SshUploadRequest`/`SshDownloadRequest`、`SshOverwritePolicy`、`SshTransferLimits`（默认 64 MiB/硬上限 256 MiB）、`SshUploadPlan`/`SshDownloadPlan` | 本地与远端路径分别建模；默认禁止覆盖；计划含同目录唯一临时文件名 |
| `Validation/SshLocalPathPolicy.cs` | 本地路径策略（切片 B） | `SshLocalPathPolicy`、`ISshLocalFileSystem`/`SshLocalFileSystem` | 单一权威包含判定（`GetRelativePath`，不用字符串前缀）；逐段拒绝重解析点；拒绝目录/非普通文件；声明 TOCTOU 限制 |
| `Validation/SshRemotePathPolicy.cs` | 远端 POSIX 路径策略（切片 B） | `SshRemotePathPolicy` | 绝对路径/控制字符/目录标记校验、词法消解 `..`、允许根包含、父目录 realpath 根内、临时同目录名 |
| `Validation/SshTransferValidator.cs` | 传输请求校验与计划（切片 B） | `SshTransferValidator` | 0/负数请求 `ssh.request_invalid`、超硬上限 `ssh.transfer_too_large`、非法配置 `ssh.config_invalid` |
| `Validation/SshRequestValidator.cs`（R5：与协调器共用同一 validator） | 请求/目标形状校验与有效限额合成 | `ValidateTarget`、`ValidateTest`、`ValidateExecute` | 连接前拒绝畸形输入；生效超时 = `min(请求, 主机上限, 宿主 deadline)` |
| `Execution/SshBoundedOutputCollector.cs` | 双流同时排空 + 共享预算 + 增量 UTF-8 解码 | `SshBoundedOutputCollector` | 固定缓冲（2×预算 + 16 KiB 池化读缓冲）；捕获满**继续计数并丢弃**；截断不产生 U+FFFD；**禁止**整份 `Result`/`ReadToEnd` |
| `Execution/SshCoordinatorOptions.cs` | 协调器预算 | `SshCoordinatorOptions`（Core 总并行 / 等待队列 / 清理宽限） | 默认取 `SshOperationLimits` 的硬上限；引用方只能收窄 |
| `Diagnostics/SshOutcomePolicy.cs` | 退出证据×输出完整性的纯判定（ADR-096 D4） | `SshOutcomePolicy.Decide`、`SshOutputIncompleteReasons` | 只有「exit=0 + 两条流读完 + 无执行失败」才是 `Completed`；信号结束/无退出证据/输出不完整各有独立错误码 |
| `Diagnostics/SshDiagnosticText.cs` | 公开诊断有界化（ADR-096 D5） | `SshDiagnosticText.Bound/FromException/Join`、`MaxLength=512` | 诊断只给稳定因果类别与异常类型名；不复制第三方 Message/known_hosts 整行；超长必标记截断 |
| `Execution/SshStreamDrainResult.cs` | 单条流的排空事实（ADR-096 D4） | `SshStreamDrainResult`（`ReachedEnd`/`ReadFailed`/`Aborted`） | 正常 EOF、读取失败、被中止是三个不同事实；读异常不得伪装成 EOF |
| `Execution/SshBoundedDrain.cs` | 有界排空（ADR-096 D3） | `SshBoundedDrain.WaitAsync` | 排空/TERM/关闭/Dispose **共用一份预算**；超时只登记中止，绝不二次等待；迟到完成被观察 |
| `Execution/SshCleanupQuarantine.cs` | 清理隔离区 | `SshCleanupQuarantine`（`Pending`/`Completed`/`IsFull`） | 容量固定；未结束的资源仍被追踪并占计数；满额即拒绝新增工作 |
| `Execution/SshStageWorkerPool.cs` | 有界阶段 worker 池 | `SshStageWorkerPool`、`SshStageCapacityExceededException` | 阶段端口的同步启动段不得阻塞业务线程；容量固定，满额 `ssh.busy` |
| `Execution/SshIdentityOwner.cs` | 身份材料的所有权状态机（L2） | `SshIdentityOwner`、`SshIdentityOwnership` | `LeaseOwned → Connecting → SessionOwned → Released` 原子且幂等；转移在**调用 connect 之前**完成 |`r`n| `Execution/SshResourceLease.cs` | 单次操作的资源记账 | `SshResourceLease`、`SshCleanupOutcome` | 身份释放幂等；阶段超时把收尾交给迟到 owner（先关连接再清零材料）；准入槽位幂等释放 |
| `Execution/SshOperationLifetime.cs` | 单次操作的**时间与终止事实**（ADR-096 D2） | `SshStopReason`/`SshStopReasons`、`SshOperationLifetime`（`RemainingOperation`/`RemainingConnect`/`CheckBeforeSideEffect`/`WaitStageAsync`） | 预算在**受理时**算一次；剩余额度用单调时间戳；计时器走注入的 `TimeProvider`；阶段晚返回只做材料清零/连接回收，绝不开启下一阶段 |`r`n| `Execution/SshOperationCoordinator.cs`（切片 B：`UploadAsync`/`DownloadAsync` 复用同一套准入/围栏/租约/关闭预算） | `ISshClient` 实现：准入排队、deadline、凭据、建连、执行、释放、停止 | `SshOperationCoordinator`（含 `StopAsync`/`IAsyncDisposable`）、`InFlightOperations`、`WaitQueueLength` | 准入按 `hostId + hostRevision` 分门；操作总时长从**受理**起算（含排队）；队满立即 `ssh.busy`；清理宽限只做资源回收 |
| `Transport/SshNetTransportFactory.cs` | 建连：密钥、认证方法、默认拒绝的主机密钥回调 | `SshNetTransportFactory` | `RetryAttempts=1`（库默认 10）、`MaxSessions=1`（库默认 10）、`Timeout` 由 deadline 决定；回调在 `ConnectAsync` 之前安装；加密私钥 ⇒ `ssh.passphrase_required` |
| `Contracts/SshTransferPorts.cs` | 传输会话与 SFTP 通道端口（切片 B） | `ISshTransferSession`、`ISshSftpChannel`、`SshTransferCapabilities`、`SshTransferOutcome` | 能力必须**实测**；不支持即 `ssh.transfer_unsupported`；清理失败必须报告残留 |
| `Transport/SshSftpChannel.cs` | SSH.NET 的 SFTP 通道（切片 B） | `SshSftpChannel` | 排他创建、标准/posix rename、realpath（ChangeDirectory+WorkingDirectory）、64 KiB 流式复制与超限中止 |
| `Transport/SshSftpTransferSession.cs` | 传输会话与发布语义（切片 B） | `SshSftpTransferSession`、`SshTransferLimitExceededException` | Fail=排他创建原子占位（失败即删除）；Overwrite=临时文件+posix-rename；清理由实测能力支撑、不使用竞态冒充 |
| `Transport/SshNetTransportSession.cs` | 独占连接的 Exec、退出证据、终止与释放 | `SshNetTransportSession` | 取消/超时对 exec channel 发 TERM 后关连接；无退出码/信号 ⇒ `execution_state=unknown`；释放路径不抛新错误覆盖业务结果 |

## 3. 不变量（改这个组件前先读）

1. **默认拒绝的主机密钥**：握手回调先装、只认验证器明确 Trust；未知 ⇒ `ssh.host_key_untrusted`，
   已登记主机换了密钥 ⇒ `ssh.host_key_mismatch`，命中 `@revoked` ⇒ `ssh.host_key_revoked`，且**认证不会发生**。
2. **看不懂的信任记录 = 拒绝**：known_hosts 里出现通配/否定模式、`@cert-authority`、未知标记、非 `|1|` 哈希
   或非法公钥时，整份信任快照不可用（`ssh.known_hosts_unsupported`）。忽略 `!` 否定会**扩大**信任，
   忽略通配/CA 会误判「主机是否已知」，所以不允许「跳过看不懂的行继续信任」。
3. **撤销按密钥生效**（与 OpenSSH 一致）：出示的密钥命中 `@revoked` 记录即拒绝；
   被撤销的是**另一把**密钥时，已登记密钥仍可用（否则主机密钥轮换会被误杀）。
4. **pin 不看调用方给的指纹**：由出示的 blob 现算 SHA256 再比较；算法或指纹不一致都是 `ssh.host_key_mismatch`。
5. **验证失败不落地秘密**：加密私钥只回 `ssh.passphrase_required`，错误文本、结果与日志都不含口令与私钥路径。
6. **退出码不伪造**：只有服务端给出 exit-status 才填 `ExitCode`；只给信号则填 `ExitSignal`；两者都没有 ⇒
   `ssh.exit_status_missing` + `execution_state=unknown`。
7. **取消 ≠ 远端已死**：组件发 TERM 后即释放连接，是否真的退出由远端证据判定；`retry_safe` 只在
   **确定未提交**且错误码属 `dns_failed/connect_failed/busy` 时为 true。
8. **无自动重放**：任何已提交命令都不自动重试；连通性失败也不自动重连。
9. **输出与输出量解耦**：捕获有界（预算内），驻留内存不随远端输出增长（见 §4 实测）。
10. **限额只能缩小**：`ResolveOperationTimeout` / `ResolveOutputBudget` / `ValidateExecute` 是唯一入口。
11. **限额要么合法、要么拒绝**（ADR-096 D5）：`SshCoordinatorOptions.Validate()`（Core 1..8、队列 0..32、清理 >0 且 ≤2 s、隔离区 ≤32、worker ≤32）与
    `SshOperationLimits.Validate()`（connect/默认/max 操作 ≤600 s、默认≤max、捕获 1..256 KiB、每主机 1..2）在**构造与受理处**拒绝；
    非法配置一律 `ssh.config_invalid` 且零身份/零连接/零提交，不得靠 `Math.Max` 静默夹取掩盖越权配置。
12. **请求 0/负数属非法请求**：`SshRequestValidator.ValidateExecute` 报 `ssh.request_invalid`；
    合法但超主机预算的请求只收窄；过期宿主 deadline 绝不把零/负额度传给库。
13. **执行串在审批前即确定，cwd 是整脚本前置守卫**（ADR-096 D1）：`cd -- '<单引号编码 cwd>' || exit $?`
    独立成行，随后是**原样**命令；分隔符固定 `\n`。守卫不能只挂在命令第一段上——分号/换行/`||`/pipeline/
    后台列表在 cd 失败后一段都不得执行。权威实现入口 `Validation/SshPosixCommand.cs`。
14. **零上层依赖**：任何 `ProjectReference` 或对 Pudding 上层程序集的 `using` 都是边界破裂。
15. **会话不含并发保护**：一个 `ISshTransportSession` 代表一条独占连接，同一时刻只应有一个 Exec；
    并发准入/排队/`ssh.busy` 由 `SshOperationCoordinator` 负责（`MaxSessions=1` 是库侧兜底，不是队列）。
16. **操作总时长从「受理」起算**：含排队、建连、执行与清理；排队中到期即 `ssh.timeout`，且**不建连**。
    宿主绝对 deadline（`ExecutionDeadlineUtc`）只能进一步收紧，不会放宽。
17. **队满立即拒绝**：等待队列（默认 32）满额直接返回 `ssh.busy`，绝不创建无界等待任务；
    排队中的操作必须可被取消（`ssh.cancelled`）。
18. **清理宽限是资源回收，不是执行许可**：截止后最多再等 `CleanupGrace`（默认 2 秒），随后释放独占连接，
    并把「已提交但无退出证据」如实报成 `execution_state=unknown`——不得声称远端进程已结束。
19. **停止不延长业务许可**：`StopAsync`/`DisposeAsync` 取消所有在飞操作、等待清理宽限后返回；
    停止期间到达的请求返回 `ssh.busy`。释放路径（含 Dispose 卡死）不得改写业务结果。
20. **取消/截止是前置围栏**（ADR-096 D2）：受理、取得槽位后、取到身份后、建连后（提交前）各检查终止事实；
    已取消的请求**不读身份、不建连、不提交**。阶段只拿**剩余**额度，换阶段不重置预算。
21. **提交事实先于发送**（ADR-096 D4）：进入可能发送 exec 的调用**之前**推进 `SubmissionEvidence`；
    此后任何失败都是「远端状态未知、不可重试」；只有底层明确给出未发送证据才允许保持 `not_submitted` 与 `retry_safe`。
22. **清理有界且有人负责**（ADR-096 D3）：TERM/关闭/排空/Dispose 共用同一份清理预算；预算用尽即 `RequestAbort()`
    并进入固定容量隔离区（满额 `ssh.busy`）；未结束的资源**不得**被宣称已释放（诊断标注 `cleanup_pending`）。
23. **同步启动段不得卡住业务等待**：阶段端口在有界 worker 池上调用；worker 满额同样 `ssh.busy`。
24. **同步入口也在有界调度内**（L3）：exec 与 Dispose 的**调用本身**在有界 worker 上（不是取得 Task 后才计时）；
    超时后句柄由租约继续持有；`StopAsync` 进入即建立停止 deadline，`Cancel` 在 worker 上纳入有界等待，
    未返回的取消回调由 `CancelCallbacksPending` 如实报告。
25. **清理预算只有一份**（L3）：从清理开始起算的绝对预算，TERM/排空/关闭/Dispose 只取剩余额度；
    清理是资源回收，不受业务 deadline 影响（不得「按业务剩余取 0」或「每步新开 2 秒」）。
26. **终止事实与远端证据分开**：本地取消/截止一旦确立，迟到的 `Completed` 只保留远端证据
    （execution_state/exit_code/exit_signal/输出），状态与错误码仍按本端终止原因；诊断拼接后统一 512 字符有界。
27. **业务返回 ≠ 资源结束**（ADR-096 D3 补充）：业务计数与资源计数分离；exec/reader/关闭/迟到回收在**调度之前**登记，
    `ResourcesCompleted` 只在义务清零且登记集合为空后完成；有挂起工作时不释放准入槽；`PendingResourceLeases` 与
    `StopAsync.PendingResources` 如实报告未收敛的资源。
28. **容量是原子硬上限**：worker/隔离区令牌用 CAS 发放（不是 `IsFull` + `Increment` 两步），取不到立即 `ssh.busy`；
    隔离区转移失败保留原准入槽，`Pending/Peak ≤ Capacity` 恒成立；容量 0 表示禁用转移，而不是拒绝所有正常请求。
29. **关闭有独立有界容量**：关闭工作走单独池，不会被卡死的 connect/exec 饿死；关闭是共享单次状态机（`CloseOnceAsync`）；
    `CleanupState=Completed` 需要关闭证据（会话自报 `Closed`），「调用过 Abort」或「异常已观察」都不算。
30. **输出完整性独立表达**（ADR-096 D4）：`output_complete` + `output_incomplete_reason`（`read_failed`/`aborted`/`unknown`）
    与 `output_truncated` 是三个不同字段；**预算截断仍算完整**（继续计数到 EOF），读失败/中止才算不完整。
    exit=0 但输出不完整 ⇒ `ssh.output_incomplete` 且保留已知退出码。
31. **公开诊断有界且无载荷**（ADR-096 D5）：`Diagnostic` 只放稳定因果码/原因码 + 行号或异常类型名，
    长度上限 512 并标记截断；known_hosts 解析错误用 `code=<code> line=<n>`，不得含原始行。
32. **清理状态是结构化字段**：结果里的 `CleanupState` 为 `NotStarted`/`Completed`/`Pending`；
    `Pending` 表示资源仍在隔离区被追踪，**不得**声称独占连接已释放。
33. **终止原因不混淆**：`stop_reason=caller_cancelled|host_stopping|operation_deadline|connect_deadline`；
    Core 停止写成 `ssh.cancelled`，绝不因为「外部 token 没取消」而写成 `ssh.timeout`；迟到结果只回收、不恢复。

## 4. 验证与门禁

| 位置 | 覆盖 |
|---|---|
| `Source/PuddingSshTests/`（**只引用组件**） | **137 个用例**：known_hosts/pin（真 fixture）、POSIX 封装与请求校验、**cwd 整脚本守卫（`SshPosixCommandGuardTests`）**、错误分类矩阵、有界采集白盒、S4 边界断言，以及假传输故障矩阵 |
| `Source/PuddingSshTests/SshOperationCoordinatorFaultMatrixTests.cs` + `SshFakes.cs` | 假 `ISshTransportFactory`/会话/身份提供者：每主机 2 与 Core 8 的准入、队列满 `ssh.busy`、排队取消/到期、revision 分门、凭据失败、建连失败/拒绝/到期、提交前后抛错的区别、恶意会话在宽限后被放弃、停止与释放有界 |
| `Source/PuddingSshTests/Fixtures/` | **真实 OpenSSH** 产物：`ssh-keyscan` 明文记录、`ssh-keygen -H` 哈希记录、`@revoked` 变体、不支持形态、`ssh-keygen -lf -E sha256` 指纹与 `ssh-keygen -F` 交叉核对结论 |
| `TestScripts/ssh-probe/make-known-hosts-fixtures.sh` | fixture 生成器（含 port-22/IPv6 真实扫描与 derived 标注），证明期望值来自 OpenSSH 而非本组件 |
| `Source/PuddingSsh.Probe/` + `TestScripts/ssh-probe/run-ssh-probe.ps1` | 真实协议探针 **16 个场景**（`fingerprint`/`hostkey`/`encrypted`/`deadline`/`cancel`/`throughput`/`coordinator`/**`knownhosts`(accept/revoked/rotated/hashed)**/**`dirs`**/**`ignoreterm`**/**`disconnect`**/**`auth`**），并核对每个场景的服务端认证次数与**实际使用的客户端密钥指纹**（拒绝场景必须为 0） |

命令（输出只进 `temp/`）：

```powershell
dotnet build Source/PuddingSsh.Probe/PuddingSsh.Probe.csproj --artifacts-path temp/build/ssh --nologo
dotnet test Source/PuddingSshTests/PuddingSshTests.csproj --artifacts-path temp/build/ssh --results-directory temp/test-out/ssh --nologo
powershell -File TestScripts/ssh-probe/run-ssh-probe.ps1
```

2026-10-08 实测（完整数字见 [A1 报告](../../Docs/14_reports/2026-10-08-PuddingSsh-A1-库能力实测报告.md)、
[A2 报告](../../Docs/14_reports/2026-10-08-PuddingSsh-A2-独立测试与信任验证报告.md) 与
[A3 报告](../../Docs/14_reports/2026-10-08-PuddingSsh-A3-协调器与假传输故障矩阵报告.md)）：

- A1 真实协议：错指纹 137 ms 被拒且服务端零认证；取消 1.53 s 收敛并回报 `TERM`；
  240 MiB 双流下捕获恒为 64 KiB、驻留托管内存 < 0.4 MiB。
- A2 独立测试：哈希主机记录匹配通过（等价于我们的 HMAC-SHA1 host token 与 OpenSSH 一致）；
  我们算的 SHA256 与 `ssh-keygen -lf` 逐字相同。
- A3 假传输矩阵：**122/122 通过**（含 35 例故障矩阵）；真实协议 `coordinator` 场景的 `accepted_publickey` 增量恰为 3
  （错指纹与非法请求都没有认证）。
- A4 真实全矩阵：**16 场景 `failures = 0`**；`knownhosts-revoked`/`knownhosts-rotated` 各 **0 次认证**；
  `knownhosts-hashed`（OpenSSH `ssh-keygen -H`）通过；`dirs` 场景服务端日志显示两类目录的**客户端密钥各被用一次**、
  缺文件 ⇒ `credential_unavailable`、两目录快照逐字节不变；`ignoreterm` 得到 `execution_state=unknown` 且远端进程
  仍 `SHELL_ALIVE`；`disconnect` ⇒ `connection_lost` + `unknown` + 不可重试；`auth` ⇒ `authentication_failed`。
- **变异取红**（A4 复跑）：给测试工程或组件加一条指向 `PuddingContextPolicy` 的 `ProjectReference` ⇒ 边界套两次都
  **3/5 变红**；复原后 csproj 哈希逐位相同、全量复绿。

**已知测量事实与限制**：

- 托管**总分配量**与收到字节近似线性（1× 18.96 MB / 3× 56.04 MB ≈ 0.22 字节/字节），来源是库内部按包缓冲的 churn
  （可回收，非驻留）；共享捕获预算由先填满的流独占，stderr 可能捕获为 0 字节。
- `SshOperationCoordinator` 的每主机门按 `hostId + hostRevision` 存在字典里，**不做淘汰**：条目数随「配置 revision 的
  历史数量」增长（每次改配置 +1，单条目极小）。连接复用与租约/失效策略属设计 §11.2 的切片 D，届时一并设计。
- 「两类 `.ssh`」在组件侧只到**形状**层面（目录/文件名由引用方冻结传入、互不回退、组件只读）；真实路径解析与
  ACL/owner 检查属 Runtime（A5）。Windows OpenSSH 服务端（编码/引号合同）按设计另行交付。

## 5. 尚未交付（A5）

- **A5（S5）**：登记 `PuddingAgentNetwork.slnx`、Runtime 主机目录/ACL/revision/凭据适配（含 identity 文件名与 path 逃逸校验）、
  三个 Agent 工具（`ssh_hosts`/`ssh_test`/`ssh_execute`）与 DI 装配、宿主组合测试。
