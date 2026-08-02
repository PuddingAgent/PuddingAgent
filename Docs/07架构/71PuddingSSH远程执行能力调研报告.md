# 71 Pudding 接入 SSH 远程执行能力调研报告

> - 状态：**research-only**（调研完成，暂不实现）
> - 日期：2026-08-02
> - 执行者：Pudding Agent (default.global_general-assistant.6a8)
> - 依赖：Pudding 工具系统（`PuddingToolBase<T>` + `[Tool]` attribute）、终端安全（`TerminalSecurity.cs`）

## 1. 结论摘要

| 维度 | 结论 |
|------|------|
| **推荐库** | **Renci.SshNet (SSH.NET)** — .NET 唯一成熟 SSH 客户端 |
| **许可证** | MIT |
| **NuGet** | `SSH.NET` — 2024.2.0 最新稳定版，1 亿+ 累计下载 |
| **集成模式** | 继承 `PuddingToolBase<SshArgs>`，复用三层安全模型 |
| **.NET 10 兼容** | 大概率向前兼容；不兼容时 fork 添加 `net10.0` TFM 即可 |
| **无替代品** | NSsh (2010 停更), SharpSsh (2007 停更), 微软无 SSH 客户端库 |

## 2. 推荐架构

```
┌─────────────────────────────────────────────────┐
│  ssh_execute(host, command, [timeout])           │  ← Agent 可见工具
│  ssh_upload(host, localPath, remotePath)         │  ← SFTP 上传（可选）
│  ssh_download(host, remotePath, localPath)       │  ← SFTP 下载（可选）
└──────────────────┬──────────────────────────────┘
                   │
┌──────────────────▼──────────────────────────────┐
│  SshExecuteTool : PuddingToolBase<SshArgs>       │
│  [Tool] category=Execute, permission=High         │
│  safety: RequiresShell | RequiresNetwork          │
└──────────────────┬──────────────────────────────┘
                   │
┌──────────────────▼──────────────────────────────┐
│  ISshConnectionManager (Session-scoped 连接池)    │
│  按 (host, port, username) 缓存连接               │
│  idle timeout → 自动断开                           │
└──────────────────┬──────────────────────────────┘
                   │
┌──────────────────▼──────────────────────────────┐
│  ITerminalCommandPolicy (复用现有安全策略)         │
│  ✅ 不变量拒绝: taskkill/kill/reboot/shutdown 拦截  │
│  ✅ 白名单过滤: git, dotnet, ls, cat, docker...   │
│  ✅ 三区分类: workspace-safe/agent-private/external │
└──────────────────┬──────────────────────────────┘
                   │
┌──────────────────▼──────────────────────────────┐
│  Renci.SshNet.SshClient                          │
│  HostKeyReceived → known_hosts 指纹验证            │
│  PrivateKeyFile / Password / KeyboardInteractive  │
│  SshCommand + CommandTimeout                      │
└─────────────────────────────────────────────────┘
```

## 3. 安全分层（复用现有模型）

| 层 | 机制 | 文件 | 说明 |
|----|------|------|------|
| **L1-不变量** | `ITerminalCommandPolicy` | `TerminalSecurity.cs:82-98` | `taskkill`/`kill`/`Stop-Process` 无论本地远程都拒绝 |
| **L2-区域分类** | `OperationZoneClassifier` | `HostShellTool.cs:51-60` | 三区划分，远程命令默认视为 agent-private 以上 |
| **L3-审批** | `InMemoryToolApprovalService` | — | High + Network → 自动触发运行时审批 |
| **L4-主机验证** | `HostKeyReceived` callback | SSH.NET API | SSH 指纹首次信任 + 后续验证（防 MITM） |
| **L5-凭据隔离** | `config/ssh/hosts.json` | 新建 | 密钥路径/KeyVault 引用，不入日志 |

## 4. 配置设计

```text
<DataRoot>/config/ssh/
  hosts.json          ← 主机定义（别名 + 地址 + 认证方式）
  known_hosts         ← OpenSSH 兼容指纹文件
  keys/               ← 私钥目录（权限受保护）
```

**hosts.json 示例**:
```json
{
  "hosts": [
    {
      "alias": "prod-web-01",
      "host": "10.0.1.100",
      "port": 22,
      "username": "deploy",
      "auth": {
        "type": "private_key",
        "keyPath": "keys/prod_rsa"
      }
    },
    {
      "alias": "dev-box",
      "host": "dev.example.com",
      "port": 22,
      "username": "root",
      "auth": {
        "type": "keyvault",
        "secretId": "ssh-dev-box"
      }
    }
  ]
}
```

不存明文密码；支持 `private_key`、`keyvault`、`password` 三种认证方式。

## 5. 关键 API 签名（建议）

```csharp
// 工具参数
public sealed record SshExecuteArgs
{
    public required string Host { get; init; }       // 主机别名或 host:port
    public required string Command { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

// 工具结果
public sealed record SshExecuteResult
{
    public required string Host { get; init; }
    public required int ExitCode { get; init; }
    public required string Stdout { get; init; }
    public required string Stderr { get; init; }
}

// 连接管理器接口
public interface ISshConnectionManager : IAsyncDisposable
{
    Task<SshClient> GetOrCreateAsync(
        SshHostConfig host, CancellationToken ct);
    Task ReturnAsync(SshClient client, CancellationToken ct);
}

// 主机配置
public sealed record SshHostConfig
{
    public required string Alias { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public required SshAuthConfig Auth { get; init; }
}
```

## 6. 工具执行流程

```text
1. Agent 调用 ssh_execute(host="prod-web-01", command="docker ps")
2. SshExecuteTool.ExecuteCoreAsync
   ├── 从 hosts.json 解析 SshHostConfig
   ├── ITerminalCommandPolicy.EnsureInvariantAllowed("docker ps")
   ├── OperationZoneClassifier.Classify("docker ps", remote=true)
   │     → 如果需要审批，等待 InMemoryToolApprovalService
   ├── ISshConnectionManager.GetOrCreateAsync(hostConfig, ct)
   │     ├── 连接池命中 → 复用已有连接
   │     └── 未命中 → 新建 SshClient
   │           ├── HostKeyReceived → known_hosts 验证
   │           └── ConnectAsync()
   ├── SshClient.RunCommand("docker ps")
   │     └── SshCommand.CommandTimeout = TimeoutSeconds
   ├── 返回 SshExecuteResult { ExitCode, Stdout, Stderr }
   └── ISshConnectionManager.ReturnAsync(client)
```

## 7. .NET 10 兼容性评估

| 项 | 评估 |
|----|------|
| **SSH.NET 2024.2.0 目标** | `net8.0` |
| **.NET 10 向前兼容** | 大概率直接可用（纯 C# 代码，无平台特定依赖） |
| **不兼容时方案** | Fork → 添加 `net10.0` TFM → 重新编译（< 1h） |
| **验证命令** | `dotnet add package SSH.NET && dotnet build` |

## 8. 实施步骤（预估）

| 阶段 | 内容 | 预估 | 新建文件 |
|------|------|:--:|------|
| **Phase 1** | 添加 SSH.NET 包 + 编译验证 | 1h | 无 |
| **Phase 2** | `ISshConnectionManager` + 连接池 + `known_hosts` 验证 | 2h | ~5 文件 |
| **Phase 3** | `SshExecuteTool` 工具实现 | 3h | ~3 文件 |
| **Phase 4** | `hosts.json` 配置 + 凭据管理 + KeyVault 集成 | 2h | ~2 文件 |
| **Phase 5** | SFTP 上传/下载（可选） | 2h | ~2 文件 |
| **Phase 6** | 安全审查 + 测试 + 文档 | 2h | ~3 文件 |
| **合计** | | **~12h** | **~15 文件** |

## 9. 风险与缺口

| 风险 | 级别 | 缓解 |
|------|:--:|------|
| SSH.NET .NET 10 不兼容 | 低 | Fork/手动添加 TFM |
| SshClient 并发安全性 | 中 | 需验证多通道并发；必要时加锁 |
| SshCommand 取消机制 | 中 | SSH 协议支持 channel close；需验证库支持 |
| 私钥权限泄露 | 低 | `keys/` 目录 ACL 限制 + KeyVault |
| MITM 攻击 | 低 | `known_hosts` + `HostKeyReceived` 指纹验证 |

## 10. 与现有工具的关系

| 现有工具 | SSH 工具对比 |
|----------|------------|
| `shell` (HostShellTool) | 本地命令执行 → `ssh_execute` 远程执行 |
| `terminal_start` | 本地终端 → 可扩展为远程终端（ssh session） |
| `ITerminalCommandPolicy` | 直接复用不变量和白名单 |
| `TerminalSecurity` | 复用拒绝模式，增加 SSH 特定规则 |

## 11. 不实现范围

- 不实现交互式 SSH session（`ssh -t`）
- 不实现 SSH 隧道/端口转发
- 不实现 SSH agent forwarding
- 不自动发现网络内主机
- 不存储会话历史到远程主机

## 12. 代码证据

现有代码库中可复用的关键文件：

| 文件 | 复用点 |
|------|--------|
| `Source/PuddingRuntime/Tools/BuiltIns/Shell/HostShellTool.cs` | 工具模板：`PuddingToolBase<T>` + `[Tool]` attribute |
| `Source/PuddingRuntime/Services/TerminalSecurity.cs:82-98` | 不变量拒绝模式 |
| `Source/PuddingCore/Tools/PuddingToolContracts.cs:16-30` | `ToolSafetyFlags.RequiresNetwork` 已定义 |
| `Source/PuddingCore/Tools/PuddingToolContracts.cs:138-175` | `ToolExecutionContext` 已包含 WorkspaceId/SessionId/AgentInstanceId |
| `Source/PuddingRuntime/Tools/BuiltIns/Terminal/TerminalTools.cs` | `ITerminalProcessManager` session-scoped 管理模式参考 |
