# 批量创建架构改进任务卡 - Part 2
$ErrorActionPreference = "Stop"
$python = "python"
$script = ".github/skills/todo-api/todo_api.py"

function Invoke-CreateTask {
    param($Title, $Priority, $Stage, $Tags, $Goal, $OutOfScope, $Acceptance, $Impact, $EntryPoints, $Risk, $ExecType, $HumanOwner, $Description)
    
    $cmdArgs = @(
        "create",
        "--title", $Title,
        "--project", "Pudding",
        "--task-owner", "WangXianQiang",
        "--priority", $Priority,
        "--stage", $Stage,
        "--goal", $Goal,
        "--out-of-scope", $OutOfScope,
        "--impact-scope", $Impact,
        "--risk-notes", $Risk,
        "--executor-type", $ExecType,
        "--human-owner", $HumanOwner,
        "--description", $Description
    )
    
    foreach ($tag in $Tags) { $cmdArgs += "--tag"; $cmdArgs += $tag }
    foreach ($ac in $Acceptance) { $cmdArgs += "--acceptance-criteria"; $cmdArgs += $ac }
    foreach ($ep in $EntryPoints) { $cmdArgs += "--entry-point"; $cmdArgs += $ep }
    
    & $python $script @cmdArgs
}

# ============================================
# Task: 流式路径进程内优化
# ============================================
Invoke-CreateTask `
    -Title "流式SSE路径进程内优化 — 取消HTTP Relay改用Channel<T>" `
    -Priority "P1" `
    -Stage "ready" `
    -Tags @("architecture", "optimization", "SSE", "ClaudeCode对比") `
    -Goal "将当前SSE流式路径中Platform→Controller→Runtime的多层HTTP Relay改为同进程内Channel<T>/IAsyncEnumerable直传，消除不必要的网络开销和序列化成本" `
    -OutOfScope "不改变SSE协议格式（前端仍通过EventSource接收）、不改变Controller的路由和鉴权逻辑" `
    -Acceptance @(
        "同进程内的模块间通信不再走HTTP（Platform→Controller→Runtime）",
        "仍保留HTTP端点给外部调用者（前端浏览器）",
        "dotnet build && dotnet test 通过",
        "Chat流式响应延迟降低（对比优化前后）"
    ) `
    -Impact "ChatApiController.cs / PlatformApiClient.cs / MessageIngressController.cs / RuntimeDispatcher.cs / RuntimeExecuteController.cs" `
    -EntryPoints @(
        "Source/PuddingPlatform/Controllers/Api/ChatApiController.cs",
        "Source/PuddingPlatform/Services/PlatformApiClient.cs",
        "Source/PuddingController/Controllers/MessageIngressController.cs",
        "Source/PuddingController/Services/RuntimeDispatcher.cs",
        "Source/PuddingRuntime/Controllers/RuntimeExecuteController.cs",
        "参考Claude Code: Docs/claude-reviews-claude/architecture/00-overview.md（AsyncGenerator管道模式）"
    ) `
    -Risk "需保留HTTP端点作为外部接口（前端浏览器调用），仅在进程内部模块间切换为直接调用。需确保不破坏当前SSE event:字段类型（delta/usage/error/done）" `
    -ExecType "hybrid" `
    -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

在对比Claude Code架构分析报告后，发现Claude Code内部通信全部使用AsyncGenerator（yield）管道而非HTTP Relay。我们的Docs/07架构/12多轮会话与工具调用执行.md 明确记录了当前SSE流式路径的8层HTTP Relay：

```
Platform ChatApiController → PlatformApiClient(HTTP) → MessageIngressController(HTTP)
  → SessionRouter → RuntimeDispatcher(HTTP) → RuntimeExecuteController(HTTP)
  → AgentExecutionService → IRuntimeLlmClient → LlmProxyController(HTTP) → OpenAiLlmGateway
```

但架构总文档（Docs/架构.md）声明已从"进程间RPC"变为"进程内方法调用"——而流式路径仍走HTTP，与同步路径的设计不一致。

## 问题

1. **不必要的序列化开销**：每层HTTP Relay都要JSON序列化/反序列化SSE帧
2. **延迟累积**：8层HTTP Relay的TCP握手和HTTP头部处理增加端到端延迟
3. **设计不一致**：同步路径已是进程内调用，流式路径仍走HTTP

## 参考来源

- Claude Code架构分析：Docs/claude-reviews-claude/architecture/00-overview.md — 「整个引擎通过yield通信，提供背压、取消和类型安全」
- Claude Code架构分析：Docs/claude-reviews-claude/architecture/01-query-engine.md — 「AsyncGenerator作为通信协议」
- 我们的架构文档：Docs/07架构/12多轮会话与工具调用执行.md — SSE完整流式路径
- 我们的架构文档：Docs/架构.md — 「从进程间RPC变为进程内方法调用」
- Microsoft文档：System.Threading.Channels — Channel<T> 高性能生产者-消费者模式

## 具体修改方案

1. **保留HTTP端点**：ChatApiController和MessageIngressController的HTTP端点保留给前端浏览器
2. **进程内直传**：PlatformApiClient内部检测同进程时，改用IAsyncEnumerable<ServerSentEventFrame>直接调用而非HttpClient
3. **取消传播**：通过CancellationToken链实现取消信号的进程内传播
4. **背压支持**：利用Channel<T>或IAsyncEnumerable的天然背压机制
5. 在关键节点增加SimpleLogger日志，记录通信方式（HTTP vs InProc）和延迟数据
"@

# ============================================
# Task: P2P广域网发现方案
# ============================================
Invoke-CreateTask `
    -Title "P2P广域网节点发现方案设计 — DHT/mDNS混合发现" `
    -Priority "P1" `
    -Stage "ready" `
    -Tags @("architecture", "P2P", "network", "ClaudeCode对比") `
    -Goal "设计并文档化广域网场景下的P2P节点发现方案，补充当前仅覆盖局域网mDNS/UDP的不足" `
    -OutOfScope "V1不实现广域网发现代码，仅完成方案设计和文档" `
    -Acceptance @(
        "产出广域网发现方案设计文档（含架构图）",
        "对比至少2种方案：DHT(Kademlia) vs 信令服务器 vs DNS-SD广域网扩展",
        "明确V1/V2分阶段实施路径",
        "包含安全认证设计（防止恶意节点加入）"
    ) `
    -Impact "Docs/07架构/07协作网络与治理.md（新增广域网章节）" `
    -EntryPoints @(
        "Docs/07架构/07协作网络与治理.md",
        "Source/PuddingCore/（P2P协议定义）",
        "参考：IPFS/libp2p的DHT实现（Kademlia算法）",
        "参考：Claude Code bridge系统 Docs/claude-reviews-claude/architecture/13-bridge-system.md"
    ) `
    -Risk "广域网P2P面临NAT穿透问题，需评估是否需要中继服务器。安全认证需防止Sybil攻击" `
    -ExecType "hybrid" `
    -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code的13-bridge-system.md展示了远程控制协议的设计思路——通过轮询-调度-心跳循环实现跨网络通信。我们的Docs/07架构/07协作网络与治理.md 当前仅描述了局域网内的mDNS/UDP节点发现，对广域网场景仅一笔带过「DHT方案」，无实质设计。

## 问题

1. Agent进程需要在不同网络（如办公室和家庭）之间协作时，mDNS无法跨子网工作
2. 缺少广域网下的节点身份认证机制
3. 未定义跨NAT的通信方案

## 参考来源

- Claude Code架构分析：Docs/claude-reviews-claude/architecture/13-bridge-system.md（远程控制协议：轮询-调度-心跳循环）
- libp2p Kademlia DHT规范：https://github.com/libp2p/specs/tree/master/kad-dht
- IPFS网络架构：https://docs.ipfs.tech/concepts/dht/
- 我们的架构文档：Docs/07架构/07协作网络与治理.md

## 具体修改方案

1. 在Docs/07架构/07协作网络与治理.md 中新增「广域网扩展」章节
2. 方案对比（表格形式）：
   | 方案 | 优点 | 缺点 | V1适用性 |
   | DHT(Kademlia) | 去中心化、自组织 | NAT穿透困难 | V2 |
   | 信令服务器 | 简单可靠 | 单点故障 | V1可选 |
   | DNS-SD广域网 | 标准协议 | 需要DNS配置 | V1可选 |
3. 推荐V1采用「可选信令服务器 + mDNS局域网」混合模式
4. V2规划完整DHT方案
5. 安全设计：基于Agent密钥对的节点身份验证
"@

Write-Host "Tasks Part 2 created!"
