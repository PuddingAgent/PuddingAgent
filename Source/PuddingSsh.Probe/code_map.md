# PuddingSsh.Probe · code_map
> 源指纹: *.cs=7-6d85b89adfa6, *.csproj=1-d4bc4debd751 · 条目数: 7 · 最近整理: 2026-11-22

SSH 组件的独立协议探针（控制台 net10.0）。连接受控的真实 OpenSSH，用退出码表达成败；只引用 `PuddingSsh`，不装配 Core、不读 DataRoot。

**入口与配置**：`PuddingSsh.Probe <scenario> [选项]`，`matrix` 顺序跑全部场景。全部取值由外部脚本显式给出，探针不做目录搜索、也不做首次信任。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 按 scenario 分发并执行库能力断言 | `Program`、`RunCoordinatorAsync`、`RunThroughputAsync` | `Source/PuddingSsh/code_map.md` | 只走组件公开合同；断言失败 exit 1、用法错误 exit 2 |
| `ProbeOptions.cs` | 命令行选项解析与退出码语义 | `ProbeOptions`、`ProbeUsageException`、`ProbeLog` | `Program.cs` | 场景名与选项名是外部脚本合同，改名即断掉运行器 |
| `ProbeSupport.cs` | 探针专用的主机密钥验证与身份读取 | `ProbeHostKeyVerifier`、`ProbeIdentityProvider`、`Mutable` | `ProbeOptions.cs` | 这是探针专用近似实现，不得当成交付路径的信任逻辑 |
| `Program.CwdGuardScenario.cs` | cwd 守卫挡住整个脚本的实测 | `RunCwdGuardAsync` | `Program.cs` | 依赖真实 POSIX shell；纯测试只能断言字节串 |
| `Program.LimitScenarios.cs` | 取消与超时的协议边界收口 | `RunPreCancelAsync`、`RunLateConnectAsync`、`RunNoEofAsync`、`RunSubmitFailureAsync` | `Program.cs` | 远端 marker 须独立复核，探针自述不算证据 |
| `Program.ScopeScenarios.cs` | 作用域目录与 known_hosts 的端到端场景 | `RunKnownHostsAsync`、`RunScopeDirsAsync`、`RunIgnoreTermAsync`、`RunDisconnectAsync` | `Program.cs` | 组件不得写 `.ssh` 目录；认证次数由 sshd 日志核对 |
| `Program.TransferScenario.cs` | 真实 OpenSSH 传输的往返与中止验证 | `RunTransferAsync` | `Program.cs` | 服务端证据另由 sha256sum 与目录清点交叉核对 |

**项目约束**：它不是组件测试工程（S2 的假传输测试另在 `PuddingSshTests`），而是「库能力实测」的执行者；远端临时目录默认为 `/tmp/pudding-ssh-probe`。
