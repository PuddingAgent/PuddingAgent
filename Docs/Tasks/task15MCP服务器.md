# Task 15 — MCP 服务器集成方案 (Model Context Protocol)

> **状态：** ✏️ 设计中
> **依赖：** Task 11 (权限沙盒)、Task 14 (SKILL 插件化)
> **目标：** 将 MCP 作为"超级插件驱动"集成到 PuddingAssistant，实现与社区 MCP 服务器的即插即用对接

---

## 目录

1. [设计原则](#一设计原则)
2. [架构定位](#二架构定位)
3. [支持层级](#三支持层级)
4. [协议对接](#四协议对接)
5. [技能自动发现](#五技能自动发现)
6. [权限桥接](#六权限桥接)
7. [自适应进化](#七自适应进化)
8. [MCP 管理面板](#八mcp-管理面板)
9. [核心实现](#九核心实现)
10. [实现路线](#十实现路线)

---

## 一、设计原则

MCP (Model Context Protocol) 是 Anthropic 推出的行业标准，解决了"每个工具都要写一遍接口"的痛点。支持 MCP 意味着 PuddingAssistant 可以直接连接社区上百个现成的 MCP 服务器。

| 原则 | 说明 |
|---|---|
| **标准协议** | 基于 JSON-RPC 2.0，与社区 MCP 生态完全兼容 |
| **进程隔离** | MCP Server 运行在独立进程中，崩溃不影响主程序 |
| **权限统一** | 所有 MCP 操作经过 `PermissionGuard` 拦截 |
| **按需激活** | 除非任务需要，否则不启动 MCP 服务器进程 |
| **与插件化互补** | MCP 是外部能力源，`ISkillPlugin` 是内部能力源，两者共享 `IToolRegistry` |

---

## 二、架构定位

MCP 位于软件桥梁层，作为外部能力源接入 Agent 体系：

```
┌─────────────────────────────────────────────────────┐
│  Agent (Leader / Worker)                             │
│  调用工具 → IToolRegistry                            │
├─────────────┬──────────────────┬────────────────────┤
│  内置插件    │  外部插件 (DLL)   │  MCP 适配层         │
│  ShellTool  │  EverythingPlugin │  McpToolAdapter    │
│  FileTool   │  GitHubTrend...   │  ↕ JSON-RPC 2.0   │
├─────────────┴──────────────────┴────────────────────┤
│                PermissionGuard                        │
├─────────────────────────────────────────────────────┤
│  MCP Server (独立进程)                                │
│  sqlite-mcp │ github-mcp │ filesystem-mcp │ ...     │
└─────────────────────────────────────────────────────┘
```

### 角色划分

| 角色 | 实体 | 说明 |
|---|---|---|
| **Host** | PuddingAssistant 软件 | 管理 MCP Server 的生命周期 |
| **Client** | `McpClient` 适配器 | 发送 JSON-RPC 请求、接收响应 |
| **Server** | 独立进程 | 社区提供的 MCP Server（Node.js / Python / Go） |

---

## 三、支持层级

分三个阶段逐步增强 MCP 能力：

| 等级 | 功能 | 场景 |
|---|---|---|
| **L1: 客户端支持** | PuddingAssistant 作为 Host，连接现有 MCP Server | 直接使用社区的 Google Search / PostgreSQL / GitHub MCP |
| **L2: 内部转发器** | 将本地 Everything / Roslyn 包装为 MCP 协议 | Agent 之间通过 MCP 标准共享工具，无需重复开发 |
| **L3: 远程路由** | 连接另一台机器或云端的 MCP Server | 跨设备操作、利用云端算力处理大型索引 |

---

## 四、协议对接

### 4.1 传输层

MCP 支持两种传输方式：

| 传输方式 | 机制 | 适用场景 |
|---|---|---|
| **Stdio** | stdin/stdout 管道通信 | 本地 MCP Server（最常见） |
| **SSE + HTTP** | Server-Sent Events | 远程 MCP Server |

### 4.2 Stdio 传输流程

```
PuddingAssistant                          MCP Server (独立进程)
    │                                       │
    │── Process.Start(mcp-server) ────────→ │  启动进程
    │                                       │
    │── stdin: {"method":"initialize"} ───→ │  握手
    │←─ stdout: {"capabilities":...}  ─────│
    │                                       │
    │── stdin: {"method":"tools/list"} ──→  │  发现工具
    │←─ stdout: {"tools":[...]}      ─────│
    │                                       │
    │── stdin: {"method":"tools/call"} ──→  │  调用工具
    │←─ stdout: {"content":[...]}    ─────│
    │                                       │
    │── 关闭 stdin ────────────────────→    │  终止进程
```

### 4.3 JSON-RPC 消息格式

```json
// → 请求
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "query",
    "arguments": { "sql": "SELECT * FROM users LIMIT 5" }
  }
}

// ← 响应
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      { "type": "text", "text": "id | name | email\n1 | Alice | ..." }
    ]
  }
}
```

---

## 五、技能自动发现

### 5.1 发现流程

MCP Server 连接后，自动提取工具清单并注册：

```
McpClient 连接 MCP Server
  → 发送 initialize (握手)
  → 发送 tools/list (发现)
  → 遍历返回的工具列表
    → 每个工具的 inputSchema → 转换为 ToolParameterSchema
    → 包装为 McpToolAdapter
    → 注册到 IToolRegistry
  → Leader 自动感知新技能
```

### 5.2 动态注入到 LLM

发现的 MCP 工具与内置插件统一注入：

```
Available Skills:
  [Built-in] ShellTool, FileTool
  [Plugin]   FileFinder v1.4 ✅, CodeFormatter v2.0 ✅
  [MCP]      sqlite.query ✅, github.search_repos ✅, slack.post_message ⏸️
```

---

## 六、权限桥接

MCP 协议本身不带权限 UI。PuddingAssistant 在 Client 和 Server 之间插入 `PermissionGuard`：

### 6.1 拦截策略

```
LLM 请求 tools/call
  → McpToolAdapter 接收
  → PermissionGuard.ValidateCommand(toolName, args)
    → 路径校验（filesystem.write 的 path 参数）
    → 命令白名单（是否为已知安全操作）
    → 通过 → 转发给 MCP Server
    → 拒绝 → 返回 SECURITY ERROR 给 LLM
    → 需授权 → 弹出布丁授权气泡
```

### 6.2 工具分级映射

MCP 工具自动映射到安全级别：

| MCP 工具类型 | 映射级别 | 示例 |
|---|---|---|
| 只读查询 | L0 | `sqlite.query (SELECT)`, `github.search_repos` |
| 项目内写入 | L1 | `filesystem.write (项目内)`, `git.commit` |
| 系统级操作 | L2 | `filesystem.write (项目外)`, `docker.run`, `shell.exec` |

---

## 七、自适应进化

### 7.1 静默嗅探

启动时扫描配置目录，按需激活：

```
./mcp_configs/
  ├── sqlite-mcp.json      ← 已配置
  ├── github-mcp.json      ← 已配置
  └── slack-mcp.json       ← 已配置但未启用
```

### 7.2 懒启动

MCP Server 不随 PuddingAssistant 启动，仅在以下条件触发：

- LLM 首次请求调用该 MCP 工具
- 用户手动在管理面板中启动
- Leader 规划阶段判断需要该能力

### 7.3 廉价模型压缩

MCP 的 `Resource`（如超长文档）获取后，先经感官过滤（Task 12）处理：

```
MCP Resource (长文档)
  → OutputDistiller 截断/结构化蒸馏
  → 或 Qwen 0.5B 本地摘要
  → 精简结果 → 传递给 Agent
```

---

## 八、MCP 管理面板

### 8.1 桌面端面板

```
┌──────────────────────────────────────────────────┐
│  🔌 MCP 服务器管理                                │
│                                                    │
│  状态   名称              版本   传输    操作       │
│  ──────────────────────────────────────────────── │
│  🟢    sqlite-mcp        v0.6   stdio   [停止]    │
│  🟢    github-mcp        v1.2   stdio   [停止]    │
│  🟡    slack-mcp         v0.3   sse     [启动]    │
│  ⚪    filesystem-mcp    v1.0   stdio   [安装]    │
│                                                    │
│  [ + 添加 MCP 服务器 ]                             │
│                                                    │
│  ── JSON-RPC 通信日志 ──                           │
│  → tools/list                    [200ms]           │
│  ← 3 tools discovered                             │
│  → tools/call: sqlite.query     [150ms]           │
│  ← result: 5 rows               [OK]              │
└──────────────────────────────────────────────────┘
```

### 8.2 状态灯

| 状态 | 颜色 | 含义 |
|---|---|---|
| 🟢 运行中 | 绿色 | MCP Server 进程正常，工具可用 |
| 🟡 待机 | 黄色 | 已配置但未启动（懒加载） |
| ⚪ 未安装 | 灰色 | 未配置，可一键安装 |
| 🔴 错误 | 红色 | 进程崩溃或通信超时 |

### 8.3 一键添加

用户可通过以下方式添加 MCP 服务器：

- **配置文件：** 手动编辑 `mcp_configs/*.json`
- **GitHub 链接：** 输入仓库地址，自动下载并配置
- **NPM/PyPI 包名：** `npx @modelcontextprotocol/server-sqlite`

---

## 九、核心实现

### 9.1 MCP 配置模型

```csharp
public record McpServerConfig(
    string Name,
    string Command,           // "npx" / "python" / 可执行文件路径
    string[] Args,            // ["@modelcontextprotocol/server-sqlite", "db.sqlite"]
    string Transport = "stdio",
    Dictionary<string, string>? Env = null,
    bool AutoStart = false);
```

### 9.2 McpClient

```csharp
/// <summary>MCP 客户端：管理与单个 MCP Server 的通信。</summary>
public class McpClient : IAsyncDisposable
{
    private Process? _process;
    private StreamWriter? _stdin;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _nextId;

    public string ServerName { get; }
    public McpServerStatus Status { get; private set; } = McpServerStatus.Stopped;
    public IReadOnlyList<McpToolDefinition> Tools { get; private set; } = [];

    public McpClient(McpServerConfig config) => ServerName = config.Name;

    /// <summary>启动 MCP Server 进程并完成握手。</summary>
    public async Task ConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = config.Command,
                Arguments = string.Join(' ', config.Args),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (config.Env is not null)
        {
            foreach (var (key, value) in config.Env)
                _process.StartInfo.EnvironmentVariables[key] = value;
        }

        _process.Start();
        _stdin = _process.StandardInput;
        _ = Task.Run(() => ReadResponseLoopAsync(ct), ct);

        // 握手
        await SendRequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "PuddingAssistant", version = "0.1.0" }
        }, ct);

        await SendNotificationAsync("notifications/initialized", ct);

        // 发现工具
        var toolsResult = await SendRequestAsync("tools/list", new { }, ct);
        Tools = ParseToolDefinitions(toolsResult);
        Status = McpServerStatus.Running;
    }

    /// <summary>调用 MCP 工具。</summary>
    public async Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        var result = await SendRequestAsync("tools/call", new
        {
            name = toolName,
            arguments = JsonSerializer.Deserialize<JsonElement>(argumentsJson)
        }, ct);

        // 提取 content[0].text
        return ExtractTextContent(result);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _stdin?.Close();
            await _process.WaitForExitAsync();
        }
        _process?.Dispose();
        Status = McpServerStatus.Stopped;
    }
}

public enum McpServerStatus { Stopped, Starting, Running, Error }
```

### 9.3 McpHostService

```csharp
/// <summary>MCP 宿主服务：管理所有 MCP Server 的生命周期。</summary>
public class McpHostService(IToolRegistry registry, PermissionGuard guard) : IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients = new();

    /// <summary>从配置目录加载所有 MCP 服务器定义。</summary>
    public async Task LoadConfigsAsync(string configDir, CancellationToken ct)
    {
        if (!Directory.Exists(configDir)) return;

        foreach (var file in Directory.GetFiles(configDir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var config = JsonSerializer.Deserialize<McpServerConfig>(json);
            if (config is null) continue;

            var client = new McpClient(config);
            _clients[config.Name] = client;

            if (config.AutoStart)
                await StartServerAsync(config.Name, ct);
        }
    }

    /// <summary>启动指定 MCP Server 并注册其工具。</summary>
    public async Task StartServerAsync(string name, CancellationToken ct)
    {
        if (!_clients.TryGetValue(name, out var client)) return;

        var config = GetConfig(name);
        await client.ConnectAsync(config, ct);

        // 将 MCP 工具注册到统一的 IToolRegistry
        foreach (var tool in client.Tools)
        {
            var adapter = new McpToolAdapter(client, tool, guard);
            registry.Register(adapter);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
    }
}
```

### 9.4 McpToolAdapter

```csharp
/// <summary>将 MCP 工具适配为 ITool 接口。</summary>
internal class McpToolAdapter(McpClient client, McpToolDefinition tool, PermissionGuard guard) : ITool
{
    public string Name => $"mcp.{client.ServerName}.{tool.Name}";
    public string Description => tool.Description;
    public ToolParameterSchema Parameters => ConvertSchema(tool.InputSchema);

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        // 权限拦截
        var check = guard.ValidateCommand(tool.Name, targetPath: null);
        if (!check.IsAllowed)
            return $"[SECURITY ERROR]: {check.DenialReason}";

        return await client.CallToolAsync(tool.Name, argumentsJson, ct);
    }
}

public record McpToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);
```

---

## 十、实现路线

### ✅ 已完成

- `ITool` / `IToolRegistry` 抽象
- `PermissionGuard` 设计（Task 11）
- `ISkillPlugin` 插件化设计（Task 14）

### 🚧 下一步

| 优先级 | 任务 | 说明 |
|---|---|---|
| **P0** | `McpClient` (Stdio) | 进程管理 + JSON-RPC 2.0 通信 + 握手 |
| **P0** | `McpToolAdapter` | MCP 工具 → ITool 适配 |
| **P0** | `McpHostService` | 多 MCP Server 生命周期管理 |
| **P1** | 权限桥接 | PermissionGuard 拦截 MCP 操作 |
| **P1** | 工具自动发现 | `tools/list` → 动态注册到 IToolRegistry |
| **P1** | 配置文件格式 | `mcp_configs/*.json` 定义 + 校验 |
| **P2** | SSE + HTTP 传输 | 支持远程 MCP Server |
| **P2** | MCP 管理面板 UI | 状态灯、通信日志、一键添加 |
| **P2** | 懒启动逻辑 | LLM 首次调用时才启动对应 Server |
| **P3** | L2 内部转发器 | 将 Everything/Roslyn 包装为 MCP Server |
| **P3** | L3 远程路由 | 跨设备 MCP Server 连接 |
