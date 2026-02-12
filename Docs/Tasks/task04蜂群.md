# Task 04: 蜂群模式设计方案 (Swarm Mode)

> **目标：** 为 PuddingAssistant 设计去中心化的多智能体协作架构。核心理念：**像人类团队一样开发**——Leader 定义契约（接口/类名/方法签名），Worker 各自负责自己的模块，禁止越界修改。蜂群支持本地并行和跨机器 P2P 分布式协作。

---

## 目录

1. [背景与动机](#1-背景与动机)
2. [核心架构：契约驱动 + Leader-Worker 模型](#2-核心架构契约驱动--leader-worker-模型)
3. [契约优先开发流程](#3-契约优先开发流程)
4. [蜂群编排协议](#4-蜂群编排协议)
5. [Worker 角色与作用域隔离](#5-worker-角色与作用域隔离)
6. [Git Worktree 隔离机制](#6-git-worktree-隔离机制)
7. [P2P 分布式蜂群](#7-p2p-分布式蜂群)
8. [自治与选举机制](#8-自治与选举机制)
9. [任务看板与通信机制](#9-任务看板与通信机制)
10. [与现有架构的集成](#10-与现有架构的集成)
11. [成本控制策略](#11-成本控制策略)
12. [实现路线图](#12-实现路线图)

---

## 1. 背景与动机

### 1.1 单 Agent 的天花板

PuddingAssistant V0.1 已实现单 Agent 闭环。但以下场景中，单 Agent 模式遇到瓶颈：

| 瓶颈 | 表现 | 根因 |
| --- | --- | --- |
| **上下文溢出** | 20+ 文件重构时 Agent "遗忘"前期约定 | 单窗口 Token 上限（~200K） |
| **串行瓶颈** | 前后端 + 测试任务只能排队执行 | 单 Agent 同一时刻只能做一件事 |
| **角色混乱** | Agent 在架构设计和写代码之间反复跳跃 | 无职责分离，一个 Agent 承担所有角色 |
| **单机瓶颈** | 复杂项目受限于单台机器的算力和 API 并发数 | 无分布式能力 |

### 1.2 蜂群模式的核心思想

> 像人类团队一样开发：架构师定义契约，每个工程师只实现自己负责的模块，互不干扰。

**三层核心理念：**

1. **契约优先** — Leader 先定义接口/类/方法签名（类似 SRS），再分配实现任务
2. **作用域隔离** — 每个 Worker 只能修改自己被分配的文件/类/方法，禁止越界
3. **去中心化** — Agent 可分布在不同机器上，通过 P2P 协作，支持自治与选举

### 1.3 适用场景分析

| 场景 | 推荐模式 | 理由 |
| --- | --- | --- |
| 大型功能开发（5+ 文件） | 🐝 蜂群 | 契约拆分 + 并行开发 |
| 全栈任务（前端 + 后端 + 测试） | 🐝 蜂群 | 专业角色分工 |
| 代码重构项目 | 🐝 蜂群 | 模块级契约隔离，安全重构 |
| 跨团队/跨机器协作 | 🐝 分布式蜂群 | P2P 组网，各机器运行独立 Agent |
| 简单 Bug 修复 / 单文件修改 | 🤖 单 Agent | 低开销，直接快速 |

---

## 2. 核心架构：契约驱动 + Leader-Worker 模型

```text
                          用户输入
                             │
                             ▼
                ┌─────────────────────────┐
                │     SwarmOrchestrator    │
                │     (蜂群编排器)          │
                └────────────┬────────────┘
                             │
                             ▼
                ┌─────────────────────────────────────┐
                │         Leader Agent                 │
                │  ┌─────────────────────────────┐    │
                │  │  1. 分析需求                  │    │
                │  │  2. 设计契约（接口/签名/空实现）│    │
                │  │  3. 提交契约到 main 分支       │    │
                │  │  4. 拆分任务 + 分配 Worker     │    │
                │  │  5. 监控进度（与 Worker 并行）  │    │
                │  │  6. 合并 + 最终验证            │    │
                │  └─────────────────────────────┘    │
                └────┬───┬───┬───┬────────────────────┘
                     │   │   │   │         ▲
           ┌─────────┘   │   │   └──────┐  │ Leader 与 Worker 并行工作
           ▼             ▼   ▼          ▼  │ Leader 可随时监控、调整、重分配
      ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐
      │ Builder │  │ Builder │  │   QA    │  │  Docs   │
      │ Worker  │  │ Worker  │  │ Worker  │  │ Worker  │
      │ 模块 A  │  │ 模块 B  │  │ 测试    │  │ 文档    │
      │         │  │         │  │         │  │         │
      │ 作用域: │  │ 作用域: │  │ 作用域: │  │ 作用域: │
      │ Auth/*  │  │ Api/*   │  │ Tests/* │  │ Docs/*  │
      └────┬────┘  └────┬────┘  └────┬────┘  └────┬────┘
           │            │            │            │
           ▼            ▼            ▼            ▼
      Worktree A   Worktree B   Worktree QA   Worktree Doc
           │            │            │            │
           └────────────┴─────┬──────┴────────────┘
                              │
                      测试通过后合并
                              │
                              ▼
                         主分支 (main)
```

### 关键角色

| 角色 | 职责 | 写代码? | 并行? |
| --- | --- | --- | --- |
| **Leader** | 定义契约、拆分任务、分配 Worker、监控进度、合并结果 | ✅ 只写契约（接口/空实现） | ✅ 与 Worker 并行工作 |
| **Builder** | 实现 Leader 分配的具体模块/类/方法 | ✅ 仅限作用域内 | ✅ 多 Builder 并行 |
| **QA** | 编写和运行测试用例，验证 Builder 输出 | ✅ 仅限测试文件 | ✅ 与 Builder 并行 |
| **Docs** | 生成/更新技术文档 | ✅ 仅限文档文件 | ✅ 与 Builder 并行 |

---

## 3. 契约优先开发流程

> **核心机制：** Leader 像架构师一样，先建立"骨架"（接口、空类、方法签名 + 契约注释），然后将"填肉"的工作分配给各 Worker。

### 3.1 完整流程

```text
Step 1: Leader 分析需求
    │
    ▼
Step 2: Leader 设计契约
    │   - 创建接口文件（IAuthService.cs）
    │   - 创建空实现类（AuthService.cs），方法体为 throw NotImplementedException
    │   - 在每个方法上用注释声明契约：参数含义、返回值约定、边界条件、异常情况
    │   - 提交到 main 分支
    │
    ▼
Step 3: Leader 拆分任务
    │   - Task A: "实现 AuthService.LoginAsync，契约见注释"
    │   - Task B: "实现 AuthService.RegisterAsync，契约见注释"
    │   - Task C: "实现 TokenManager 全部方法，契约见注释"
    │
    ▼
Step 4: Leader 分配 + Worker 并行执行
    │   - Worker-1 → Task A（作用域: AuthService.LoginAsync）
    │   - Worker-2 → Task B（作用域: AuthService.RegisterAsync）
    │   - Worker-3 → Task C（作用域: TokenManager.*）
    │   - QA Worker → 同时编写单元测试
    │   - Leader 并行监控进度，可随时介入调整
    │
    ▼
Step 5: 契约验证
    │   - Worker 完成后，Leader 验证：方法签名是否匹配契约？
    │   - QA 运行测试：实现是否满足契约约定的行为？
    │
    ▼
Step 6: 合并
```

### 3.2 契约示例

Leader 生成的"骨架代码"——提交到 main 分支后，Worker 在各自 Worktree 中填充实现：

```csharp
namespace PuddingAssistant.Auth;

/// <summary>
/// 认证服务契约。
/// 【架构约束】本类禁止依赖 HttpClient，所有网络请求通过 IHttpGateway 注入。
/// </summary>
public class AuthService(ITokenManager tokenManager, IUserRepository userRepo) : IAuthService
{
    /// <summary>
    /// 用户登录。
    /// 【契约】
    /// - 参数: username 非空, password 非空且长度 >= 8
    /// - 返回: 成功返回 AuthResult.Success(token); 失败返回 AuthResult.Fail(reason)
    /// - 异常: 当 userRepo 不可达时抛出 ServiceUnavailableException
    /// - 安全: 密码比对必须使用恒时比较，防止时序攻击
    /// </summary>
    public Task<AuthResult> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        // TODO: Worker-1 实现（Task ID: task-001）
        throw new NotImplementedException();
    }

    /// <summary>
    /// 用户注册。
    /// 【契约】
    /// - 参数: email 合法格式, password 长度 >= 8 且包含大小写+数字
    /// - 返回: 注册成功返回新用户 ID; 邮箱已存在返回 AuthResult.Fail("duplicate_email")
    /// - 副作用: 成功后发送验证邮件（通过 IEmailSender）
    /// </summary>
    public Task<AuthResult> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        // TODO: Worker-2 实现（Task ID: task-002）
        throw new NotImplementedException();
    }
}
```

### 3.3 契约模型

```csharp
namespace PuddingAssistant.Models;

/// <summary>Leader 生成的契约定义</summary>
public sealed record Contract
{
    public required string Id { get; init; }

    /// <summary>契约涉及的文件路径列表</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>契约涉及的符号（类名.方法名）列表</summary>
    public required IReadOnlyList<string> Symbols { get; init; }

    /// <summary>契约描述（自然语言 + 约束条件）</summary>
    public required string Specification { get; init; }
}
```

### 3.4 为什么契约优先？

| 好处 | 说明 |
| --- | --- |
| **消除冲突** | Worker 各自负责不同方法，不会修改同一代码段 |
| **可验证** | 契约 = 验收标准，QA 可直接基于契约编写测试 |
| **可并行** | 接口确定后，所有 Worker 可同时开工，无需等待 |
| **可回滚** | 某个 Worker 失败只影响它负责的方法，不波及其他模块 |
| **减少幻觉** | Worker 只需关注一个方法的实现，上下文最小化 |

---

## 4. 蜂群编排协议

### 4.1 编排操作集

| 类别 | 操作 | 说明 |
| --- | --- | --- |
| **团队管理** | `SpawnSwarm` | 创建蜂群实例，初始化 Leader |
| | `SpawnWorker` | 根据角色模板生成 Worker Agent |
| | `DismissSwarm` | 优雅关闭蜂群，清理资源 |
| **契约管理** | `DefineContract` | Leader 创建接口/空实现并提交到 main |
| | `ValidateContract` | 验证 Worker 输出是否匹配契约签名 |
| **任务管理** | `CreateTask` | Leader 创建子任务，绑定契约和作用域 |
| | `ClaimTask` | Worker 认领待处理任务 |
| | `CompleteTask` | Worker 标记任务完成，附带结果 |
| | `FailTask` | Worker 标记任务失败，附带原因 |
| **通信** | `Broadcast` | Leader 向所有 Worker 广播消息 |
| | `SendMessage` | Agent 间点对点通信 |
| | `ReadInbox` | 读取消息收件箱 |
| **代码管理** | `CreateWorktree` | 为 Worker 创建独立 Git Worktree |
| | `MergeWorktree` | 测试通过后合并 Worker 分支到主分支 |
| | `AbandonWorktree` | 放弃并清理失败的 Worktree |

### 4.2 蜂群生命周期

```text
 1. SpawnSwarm            用户触发（/swarm 指令或自动判断）
         │
 2. Leader 设计契约        分析需求 → 创建接口/空实现 → 提交到 main
         │
 3. Leader 拆分任务        每个任务绑定：契约 ID + 作用域（文件/类/方法）
         │
 4. SpawnWorker × N       Leader 按需生成专业 Worker
         │
 5. CreateWorktree × N    每个 Worker 获得独立 Git 工作空间（基于含契约的 main）
         │
 6. 并行执行              ┌─ Workers 并行实现各自方法
         │                 └─ Leader 并行监控、可随时介入调整/重分配
         │
 7. ValidateContract      Leader 验证 Worker 输出是否匹配契约签名
         │
 8. MergeWorktree         验证通过的分支逐步合并
         │
 9. 最终回归测试           Leader 在 main 上触发全量测试
         │
10. DismissSwarm          清理所有 Worktree 和临时资源
```

---

## 5. Worker 角色与作用域隔离

每个 Worker 是一个独立的 `AgentOrchestrator` 实例，拥有：
- 独立的对话历史和上下文窗口
- 独立的工具集
- 角色专属的 System Prompt
- **作用域约束** — 明确告知哪些文件/类/方法可以修改

### 5.1 角色模板

| 角色 | System Prompt 核心 | 额外工具 |
| --- | --- | --- |
| **Leader** | "你是架构师和 Scrum Master。你的职责：设计契约（创建接口和空实现，用注释声明每个方法的参数/返回值/异常/约束），拆分任务，分配 Worker，监控进度，做合并决策。你可以在等待 Worker 的同时继续规划下一批任务。" | `DefineContract`、`SpawnWorker`、`CreateTask`、`MergeWorktree` |
| **Builder** | "你是专注的软件工程师。你只能修改以下作用域内的代码：{scope}。禁止修改作用域之外的任何文件。按照方法注释中的契约实现代码。完成后通知 Leader。" | `FileTool`（作用域受限）、`ShellTool` |
| **QA** | "你是 QA 工程师。基于契约注释编写单元测试。只有测试全部通过才标记 CompleteTask。" | `FileTool`（仅 Tests/）、`ShellTool`、`TestRunnerTool` |
| **Docs** | "你是技术文档工程师。基于契约和代码实现生成/更新文档。" | `FileTool`（仅 Docs/） |

### 5.2 作用域强制执行

Worker 的 `FileTool` 被包裹一层作用域检查器：

```csharp
namespace PuddingAssistant.Tools;

/// <summary>
/// 带作用域约束的文件工具。Worker 只能读写其被分配的文件/目录。
/// </summary>
public sealed class ScopedFileTool(FileTool inner, WorkerScope scope) : ITool
{
    public string Name => inner.Name;
    public string Description => inner.Description;
    public ToolParameterSchema Parameters => inner.Parameters;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        // 从 argumentsJson 提取 path
        // 如果是 write 操作且 path 不在 scope 内 → 拒绝
        if (IsWriteOperation(argumentsJson) && !scope.IsPathAllowed(path))
            return $"Error: 你的作用域为 [{scope}]，禁止修改 {path}";

        return await inner.ExecuteAsync(argumentsJson, ct);
    }
}

public sealed record WorkerScope(IReadOnlyList<string> AllowedPaths, IReadOnlyList<string> AllowedSymbols);
```

### 5.3 多模型调度

| 角色 | 推荐模型 | 理由 |
| --- | --- | --- |
| Leader | 大模型（Claude Opus / GPT-4o） | 需要强架构理解和契约设计能力 |
| Builder（简单方法） | 中模型（GPT-4o-mini / DeepSeek-Chat） | 契约明确，执行即可 |
| Builder（复杂算法） | 大模型 | 算法设计需要强推理 |
| QA | 中模型 | 基于契约生成测试是模式化任务 |
| Docs | 小模型 | 文档生成对推理要求低 |

---

## 6. Git Worktree 隔离机制

每个 Worker 在独立 Git Worktree 中工作，互不干扰。

### 6.1 Worktree 生命周期

```text
main 分支（含 Leader 提交的契约/空实现）
    │
    ├── git worktree add .pudding/worktrees/worker-1  swarm/auth-login
    │       └── Worker-1: 仅实现 AuthService.LoginAsync
    │
    ├── git worktree add .pudding/worktrees/worker-2  swarm/auth-register
    │       └── Worker-2: 仅实现 AuthService.RegisterAsync
    │
    ├── git worktree add .pudding/worktrees/worker-3  swarm/token-mgr
    │       └── Worker-3: 仅实现 TokenManager.*
    │
    └── git worktree add .pudding/worktrees/worker-qa  swarm/qa
            └── QA: 编写和运行测试

合并流程（契约验证 + 按序合并）：
    1. Worker 完成 → 在自己的 Worktree 内运行编译
    2. Leader ValidateContract → 方法签名是否匹配契约？
    3. 签名匹配 → git merge swarm/auth-login（通常无冲突，因为作用域不重叠）
    4. 全量测试 → QA 在 main 上运行完整测试套件
    5. 清理 → git worktree remove .pudding/worktrees/worker-1
```

### 6.2 冲突处理策略

由于契约优先 + 作用域隔离，冲突概率极低：

| 场景 | 概率 | 策略 |
| --- | --- | --- |
| 无冲突（作用域不重叠） | 高 | 自动 fast-forward 合并 |
| 共享文件冲突（如 DI 注册） | 中 | Leader 负责合并 DI 注册等共享代码 |
| 逻辑冲突 | 低 | 暂停合并，向用户展示详情，请求人工决策 |
| Worker 任务失败 | — | `AbandonWorktree`，Leader 重试或重新分配 |

---

## 7. P2P 分布式蜂群

> **目标：** 蜂群的每个 Agent 可以运行在不同计算机上，通过 P2P 网络协作。

### 7.1 网络拓扑

```text
场景 1：局域网内（直连）

    ┌──────────┐      P2P Direct      ┌──────────┐
    │ Machine A│◄─────────────────────►│ Machine B│
    │ (Leader) │                       │ (Worker) │
    └──────────┘                       └──────────┘
         ▲                                  ▲
         │          P2P Direct              │
         └──────────────┬───────────────────┘
                        │
                   ┌──────────┐
                   │ Machine C│
                   │ (Worker) │
                   └──────────┘

场景 2：跨网络（通过中继服务器）

    ┌──────────┐                       ┌──────────┐
    │ Machine A│      NAT/Firewall     │ Machine B│
    │ (Leader) │          ┃            │ (Worker) │
    │ 内网 A   │          ┃            │ 内网 B   │
    └────┬─────┘          ┃            └─────┬────┘
         │                ┃                  │
         └───────► ┌──────────────┐ ◄────────┘
                   │ Relay Server │
                   │ (中继/信令)   │
                   └──────────────┘

场景 3：混合组网

    ┌──────────┐  Direct  ┌──────────┐
    │ Machine A│◄────────►│ Machine B│    同一局域网
    └────┬─────┘          └──────────┘
         │
         │  Relay
         ▼
    ┌──────────────┐
    │ Relay Server │
    └──────┬───────┘
           │  Relay
           ▼
    ┌──────────┐  Direct  ┌──────────┐
    │ Machine C│◄────────►│ Machine D│    另一局域网
    └──────────┘          └──────────┘
```

### 7.2 P2P 协议栈

参考 libp2p 架构，PuddingAssistant 的分布式通信栈：

| 层 | 职责 | 技术选型 |
| --- | --- | --- |
| **传输层** | TCP / QUIC / WebSocket 连接 | .NET 原生 Socket + QUIC |
| **安全层** | 端到端加密、节点身份认证 | TLS 1.3 / Noise Protocol |
| **发现层** | 节点发现、NAT 穿越 | mDNS（局域网）、STUN/TURN（跨网） |
| **中继层** | 无法直连时通过中继转发 | Relay Server（自建/公共） |
| **协议层** | 蜂群消息编解码、任务同步 | 自定义 Protobuf 消息 |
| **应用层** | 蜂群编排操作（CreateTask, SendMessage 等） | 复用 §4 的编排协议 |

### 7.3 节点模型

```csharp
namespace PuddingAssistant.Swarm.Network;

/// <summary>蜂群网络中的一个节点</summary>
public sealed record SwarmNode
{
    /// <summary>全局唯一节点 ID（Ed25519 公钥派生）</summary>
    public required string NodeId { get; init; }

    /// <summary>节点角色</summary>
    public required SwarmNodeRole Role { get; set; }

    /// <summary>可达地址列表（可能有多个：LAN IP、公网 IP、中继地址）</summary>
    public required IReadOnlyList<string> Addresses { get; init; }

    /// <summary>节点能力标签（可用模型、算力等级等）</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public enum SwarmNodeRole { Leader, Builder, QA, Docs, Relay }
```

### 7.4 NAT 穿越策略

| 阶段 | 方式 | 说明 |
| --- | --- | --- |
| 1. 局域网发现 | mDNS 广播 | 同一网段内自动发现其他 PuddingAssistant 节点 |
| 2. 直连尝试 | STUN 探测 | 通过 STUN 服务器获取公网地址，尝试 UDP 打洞 |
| 3. 中继回退 | TURN / 自建 Relay | 打洞失败时通过中继服务器转发消息 |

### 7.5 分布式 Git 同步

分布式蜂群中，代码同步通过 Git 仓库完成：

| 场景 | 同步方式 |
| --- | --- |
| 同一机器上的 Worker | Git Worktree（本地） |
| 不同机器上的 Worker | 各自 clone 仓库，通过 Git remote 推送/拉取 |
| 契约提交同步 | Leader 推送到共享 remote → Worker 节点拉取 |
| 成果合并 | Worker 推送分支到 remote → Leader 节点拉取并合并 |

---

## 8. 自治与选举机制

> **目标：** 蜂群不依赖固定的 Leader，支持自我协调和领导选举，提高容错性。

### 8.1 选举场景

| 场景 | 触发条件 | 行为 |
| --- | --- | --- |
| **初始选举** | 多个节点组网，无 Leader | 自动选举能力最强的节点为 Leader |
| **Leader 故障** | Leader 心跳超时 | Worker 发起重新选举 |
| **Leader 迁移** | Leader 节点算力不足 | 主动让位给更强的节点 |
| **蜂群分裂** | 网络分区 | 各分区独立选举 Leader，恢复后协调合并 |

### 8.2 选举算法

采用 **Raft 简化变体**，适配蜂群的小规模集群特点：

```text
1. 候选阶段
   │  - Leader 心跳超时 → 任意 Worker 可发起投票请求
   │  - 候选节点广播: "我申请成为 Leader，我的能力分 = X"
   │
2. 投票阶段
   │  - 每个节点投票给能力分最高的候选者
   │  - 能力分 = f(可用模型等级, 算力, 网络延迟, 剩余预算)
   │
3. 确认阶段
   │  - 获得多数票的节点成为新 Leader
   │  - 新 Leader 从 .pudding/swarm/ 恢复蜂群状态
   │  - 广播: "我是新 Leader，继续工作"
```

### 8.3 能力分计算

```csharp
public static int ComputeCapabilityScore(SwarmNode node)
{
    var score = 0;
    score += node.Capabilities.Contains("large-model") ? 100 : 0;
    score += node.Capabilities.Contains("gpu") ? 50 : 0;
    score += node.Capabilities.Contains("high-bandwidth") ? 30 : 0;
    score += node.Capabilities.Contains("low-latency") ? 20 : 0;
    // 避免频繁切换：当前 Leader 获得额外稳定性加分
    score += node.Role == SwarmNodeRole.Leader ? 10 : 0;
    return score;
}
```

### 8.4 自协调行为

即使在无 Leader 的短暂窗口期，Worker 也能自治：

| 行为 | 说明 |
| --- | --- |
| **继续当前任务** | Worker 不因 Leader 失联而停止，继续实现已分配的契约 |
| **本地验证** | Worker 在自己的 Worktree 运行编译和测试 |
| **缓存消息** | 无法发送给 Leader 的消息暂存到本地 inbox，恢复后重发 |
| **拒绝新任务** | 无 Leader 时 Worker 不认领新任务，避免重复分配 |

---

## 9. 任务看板与通信机制

### 9.1 文件系统协调（本地模式）

本地蜂群使用 `.pudding/swarm/` 目录管理状态：

```text
.pudding/swarm/
├── config.json                  # 蜂群元数据（创建时间、成员列表、网络模式）
├── contracts/
│   ├── contract-001.json        # 契约定义（文件、符号、规格说明）
│   └── contract-002.json
├── tasks/
│   ├── task-001.json            # { id, contractId, scope, assignee, status, result }
│   ├── task-002.json
│   └── task-003.json
├── messages/
│   ├── leader.inbox.json
│   ├── worker-1.inbox.json
│   └── broadcast.json
├── worktrees/
│   └── registry.json            # Worktree 路径 ↔ Worker 映射
└── network/                     # 分布式模式特有
    ├── peers.json               # 已知节点列表
    ├── election.json            # 当前选举状态
    └── heartbeat.json           # 心跳记录
```

### 9.2 通信抽象（本地 + 分布式统一）

```csharp
namespace PuddingAssistant.Abstractions;

/// <summary>
/// 蜂群通信通道。本地模式通过文件系统实现，分布式模式通过 P2P 网络实现。
/// </summary>
public interface ISwarmTransport
{
    /// <summary>发送消息给指定节点</summary>
    Task SendAsync(string targetNodeId, SwarmMessage message, CancellationToken ct = default);

    /// <summary>广播消息给所有节点</summary>
    Task BroadcastAsync(SwarmMessage message, CancellationToken ct = default);

    /// <summary>接收消息流</summary>
    IAsyncEnumerable<SwarmMessage> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>本地文件系统实现</summary>
public class FileSwarmTransport : ISwarmTransport { /* 读写 .pudding/swarm/messages/ */ }

/// <summary>P2P 网络实现</summary>
public class P2PSwarmTransport : ISwarmTransport { /* 通过 P2P 协议栈通信 */ }
```

### 9.3 任务状态机

```text
Created → Assigned → InProgress → Testing → Completed
                         │                      │
                         └───── Failed ─────────┘
                                  │
                                  ▼
                         Reassigned (重试)
                             或
                         Abandoned (放弃)
```

### 9.4 任务模型

```csharp
public sealed record SwarmTask
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>关联的契约 ID</summary>
    public string? ContractId { get; init; }

    /// <summary>Worker 被允许修改的作用域</summary>
    public WorkerScope? Scope { get; init; }

    public string? AssignedTo { get; set; }
    public SwarmTaskStatus Status { get; set; } = SwarmTaskStatus.Created;
    public string? Result { get; set; }
    public string? FailReason { get; set; }
}

public enum SwarmTaskStatus
{
    Created, Assigned, InProgress, Testing, Completed, Failed, Abandoned
}
```

---

## 10. 与现有架构的集成

### 10.1 扩展点

蜂群模式构建在 V0.1 已实现的抽象层之上：

| 现有组件 | 蜂群模式中的角色 | 是否修改 |
| --- | --- | --- |
| `IAgentOrchestrator` | 每个 Worker 是一个独立实例 | ❌ 不修改 |
| `IToolRegistry` | 每个 Worker 可注册不同工具集 | ❌ 不修改 |
| `ILlmGateway` | 不同角色可对接不同模型 | ❌ 不修改 |
| `FileTool` | 被 `ScopedFileTool` 包装，增加作用域检查 | ❌ 包装，不修改 |
| `AgentEvent` | 新增蜂群事件子类 | ✅ 扩展 |

### 10.2 新增抽象

```csharp
namespace PuddingAssistant.Abstractions;

/// <summary>蜂群编排器。管理契约驱动的 Leader-Worker 协作。</summary>
public interface ISwarmOrchestrator
{
    IAsyncEnumerable<AgentEvent> ProcessSwarmAsync(
        string userInput, CancellationToken ct = default);
}

/// <summary>Worker 生命周期管理</summary>
public interface IWorkerManager
{
    Task<WorkerInfo> SpawnWorkerAsync(
        WorkerRole role, string taskPrompt, WorkerScope scope, CancellationToken ct = default);
    Task DismissWorkerAsync(string workerId, CancellationToken ct = default);
    IReadOnlyList<WorkerInfo> GetActiveWorkers();
}

/// <summary>契约管理</summary>
public interface IContractManager
{
    Task<Contract> DefineContractAsync(string specification, CancellationToken ct = default);
    Task<bool> ValidateContractAsync(string contractId, string worktreePath, CancellationToken ct = default);
}

/// <summary>Leader 选举</summary>
public interface ILeaderElection
{
    Task<string> ElectLeaderAsync(IReadOnlyList<SwarmNode> candidates, CancellationToken ct = default);
    Task<bool> IsCurrentLeaderAliveAsync(CancellationToken ct = default);
}

public sealed record WorkerInfo(string Id, WorkerRole Role, string Name, string WorktreePath, WorkerScope Scope);
public enum WorkerRole { Leader, Builder, QA, Docs }
```

### 10.3 新增事件类型

```csharp
namespace PuddingAssistant.Models;

// 蜂群事件，扩展 AgentEvent
public sealed record SwarmStartedEvent(int WorkerCount) : AgentEvent;
public sealed record ContractDefinedEvent(string ContractId, IReadOnlyList<string> Symbols) : AgentEvent;
public sealed record WorkerSpawnedEvent(string WorkerId, WorkerRole Role, WorkerScope Scope) : AgentEvent;
public sealed record TaskAssignedEvent(string TaskId, string WorkerId, string ContractId) : AgentEvent;
public sealed record TaskCompletedEvent(string TaskId, string WorkerId, string Summary) : AgentEvent;
public sealed record TaskFailedEvent(string TaskId, string WorkerId, string Reason) : AgentEvent;
public sealed record ContractValidatedEvent(string ContractId, bool Passed) : AgentEvent;
public sealed record MergeEvent(string Branch, bool Success) : AgentEvent;
public sealed record LeaderElectedEvent(string NodeId) : AgentEvent;
public sealed record SwarmCompletedEvent(string Summary) : AgentEvent;
```

### 10.4 CLI 集成

| 指令 | 功能 |
| --- | --- |
| `/swarm` | 手动触发蜂群模式 |
| `/swarm status` | 查看蜂群状态（Worker 列表、任务进度、契约完成度） |
| `/swarm cancel` | 取消当前蜂群，回滚未合并变更 |
| `/swarm nodes` | 查看 P2P 网络节点列表和连接状态 |
| `/swarm elect` | 手动触发 Leader 重新选举 |

---

## 11. 成本控制策略

### 11.1 成本对比

| 维度 | 单 Agent | 本地蜂群 | 分布式蜂群 |
| --- | --- | --- | --- |
| Token 消耗 | 1x | 4-15x | 4-15x（分摊到各节点） |
| 开发效率 | 1x | 5-10x | 5-10x |
| 算力需求 | 单机 | 单机（多进程） | 多机分担 |
| 网络开销 | 无 | 无 | P2P 通信 + Git 同步 |

### 11.2 控制手段

| 策略 | 实现方式 |
| --- | --- |
| **契约缩小上下文** | Worker 只看到自己负责的方法和相关接口，Token 消耗最小化 |
| **按角色分配模型** | Leader 用大模型，Builder/QA 用中模型，Docs 用小模型 |
| **Worker 上限** | 默认最多 5 个并发 Worker |
| **Token 预算** | 每个 Worker 设 Token 上限，超出自动暂停 |
| **实时费用显示** | 仪表盘实时更新蜂群总消耗（美元） |
| **自动降级** | 预算耗尽时自动退回单 Agent 模式 |
| **分布式分摊** | 各节点使用自己的 API Key，费用自然分摊 |

---

## 12. 实现路线图

### 阶段划分

| 阶段 | 版本 | 目标 | 交付物 |
| --- | --- | --- | --- |
| **Phase 1：契约 + 串行** | V0.8 | Leader 定义契约 + Worker 串行执行 | `IContractManager`、`ScopedFileTool`、作用域隔离 |
| **Phase 2：并行 + Worktree** | V0.9 | 多 Worker 并行 + Git Worktree 隔离 | Worktree 管理、并行执行、Leader-Worker 并行、自动合并 |
| **Phase 3：分布式蜂群** | V1.0 | P2P 组网 + 选举 + 跨机器协作 | `ISwarmTransport`(P2P)、`ILeaderElection`、NAT 穿越 |
| **Phase 4：生态完善** | V1.x | 多模型调度 + 费用控制 + 桌面端可视化 | Model Router、费用仪表盘、Swarm 可视化面板 |

### 前置依赖

| 依赖 | 来源 | 状态 |
| --- | --- | --- |
| `IAgentOrchestrator` (单 Agent 闭环) | Task 03 / D01-D04 | ✅ 已完成 |
| Git 快照与回滚 | D06 | 📋 规划中（Phase 2 前置） |
| 多模型调度路由 | 讨论.md §4.3 | 📋 规划中（Phase 4 前置） |
| 桌面端 Swarm 可视化 | Task 02 §4.2 | 📋 规划中（Phase 4 前置） |
