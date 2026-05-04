# QA 审阅报告 — 架构合规性 & 代码整洁度

| 项目 | 值 |
|------|-----|
| 报告编号 | QA-Architecture-Cleanliness-20260504 |
| 审阅日期 | 2026-05-04 |
| 审阅模型 | GPT-5.3-Codex (独立 QA) |
| 开发模型 | Claude Sonnet 4.6 / MiniMax-M2.7 |
| 审阅范围 | 全仓库架构合规性 · 代码整洁度 · 安全审查 |
| **QA 结论** | **FAIL** — 存在 P0/P1 阻断问题，必须修复后重审 |

---

## 1. 审阅范围

| 模块 | 审阅内容 |
|------|---------|
| PuddingAgent | Program.cs (DI 组合根)、BuiltInAgentTemplates.cs |
| PuddingCore | 依赖图分析、命名空间检查 |
| PuddingController | 所有控制器鉴权状态、RuntimeDispatcher、SessionRouter |
| PuddingRuntime | AgentExecutionService、DirectLlmClient、RuntimeExecuteController |
| PuddingPlatform | KeyVaultService、ChatApiController、WorkspaceApiController、PlatformDbContext |
| PuddingPlatformAdmin | 跳过（前端，本次仅覆盖后端）|

架构参考：`Docs/07架构/` 系列文档（尤其 `01总览与分层.md`、`04PuddingController与Gateway.md`、`03PuddingRuntime.md`）

---

## 2. P0 — 严重/阻断

### P0-01 · Controller 层所有 HTTP 端点无任何鉴权保护

**位置**：`Source/PuddingController/Controllers/` 全部文件

```
MessageIngressController.cs    — /api/messageingress/*
LlmProxyController.cs          — /api/internal/llm/*
WorkspaceController.cs         — /api/workspace/*
RuntimeRegistryController.cs   — /api/registry/*
```

**问题描述**：
上述控制器类均无 `[Authorize]` 注解，且 Program.cs 中未为这些路由段配置全局认证策略。单进程架构下全部模块共享同一 Kestrel 端口 (8080)，导致这些敏感端点与对外 Platform API 暴露在同一地址上：

- `/api/messageingress` — 任何匿名请求可向任意 Workspace 注入消息、触发 Agent 执行
- `/api/internal/llm/chat` — 外部可直接调用 LLM Proxy，消耗 API 配额
- `/api/workspace` — Workspace 数据（含 Agent 配置）可被匿名读写
- `/api/registry` — Runtime 节点注册表可被篡改，伪造运行时节点

**影响**：架构文档 §04 明确 Controller 层负责路由与鉴权；当前实现完全跳过了鉴权层。
**修复方向**：在 Controller 路由前注册 JWT Bearer 中间件，并对所有 Controller 路由段加 `[Authorize]` 或全局策略；无鉴权需求的内部端点应限制为仅本地回环访问（绑定 localhost 或使用单独端口并防火墙隔离）。

---

### P0-02 · Runtime 层所有 HTTP 端点无任何鉴权保护

**位置**：`Source/PuddingRuntime/Controllers/` 全部文件

```
RuntimeExecuteController.cs    — /api/runtime/execute/*
RuntimeSessionController.cs    — /api/runtime/sessions/*
SkillsController.cs            — /api/runtime/skills/*
```

**问题描述**：
与 P0-01 相同模式。`/api/runtime/execute` 接受 `RuntimeDispatchRequest` 可直接在任意 Workspace/Session 上触发 Agent 执行循环（LLM 调用 + 工具执行）；`/api/runtime/execute/stream` 还会向调用方推送 SSE 流。外部攻击者无需任何凭证即可枚举 Session 状态、执行任意 Agent 操作、调度 Shell 工具（若 CapabilityPolicy 被滥用）。

**修复方向**：同 P0-01；若确为进程内调用，考虑将 Runtime 控制器绑定到仅进程内可达的管道或 Unix Socket，彻底避免外部暴露。

---

### P0-03 · LLM API Key 通过 HTTP Request Body 明文传输

**位置**：
- `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs` — lines 82, 204
- `Source/PuddingPlatform/Services/PlatformApiClient.cs` (消费 LlmConfig)

**代码片段**（line 82）：
```csharp
var config = new LlmConfig
{
    Model    = model?.ModelId,
    Endpoint = provider.Endpoint,
    ApiKey   = provider.ApiKey,   // ← 数据库中 KeyVault 保护的密钥，此处取出后明文写入请求体
    // ...
};
```

**问题描述**：
`KeyVaultService` 对 API Key 提供了 AES-256-GCM 加密保护，但 ChatApiController 将其解密后立即以明文形式放入 `LlmConfig.ApiKey` 字段，随 `RuntimeDispatchRequest` 经 HTTP 传输到 Controller 的 `/api/messageingress` 端点。由于 P0-01/P0-02 所述鉴权缺失，任何能截获这条内部 HTTP 请求的人都能获取 LLM provider 的 API Key（第三方 API 密钥）。即使单进程内部通信，在日志、异常堆栈、调试会话中也存在泄露风险。

**修复方向**：在 `RuntimeDispatchRequest` 中传递 `KeyVaultId`（即加密后的引用），在 Runtime 层通过 `IKeyVaultService.InjectAsync` 在实际调用 LLM 前注入密钥，保持密钥全程不离开 KeyVault 服务。

---

## 3. P1 — 警告/阻断风险

### P1-01 · 多处直接 `new HttpClient()` — Socket Exhaustion 风险

**位置（共 5 处）**：

| 文件 | 行 |
|------|----|
| `Source/PuddingController/Services/RuntimeDispatcher.cs` | 42, 79 |
| `Source/PuddingController/Controllers/RuntimeRegistryController.cs` | 155 |
| `Source/PuddingRuntime/Services/RuntimeSelfRegistrationService.cs` | 84 |
| `Source/PuddingAgent/Connectors/WebhookConnector.cs` | 74 |

**问题描述**：每次请求创建独立的 `HttpClient` 实例，无法复用底层 TCP 连接池，高并发下快速耗尽操作系统 socket 文件描述符，导致服务不可用。

**修复方向**：注入 `IHttpClientFactory`，统一通过 `_httpClientFactory.CreateClient("xxx")` 获取客户端。

---

### P1-02 · AgentExecutionService._histories 无淘汰机制 — 内存泄漏

**位置**：`Source/PuddingRuntime/Services/AgentExecutionService.cs` line 44

```csharp
private readonly ConcurrentDictionary<string, List<ChatMessage>> _histories = new();
```

**问题描述**：
`AgentExecutionService` 注册为 Singleton，`_histories` 字典按 SessionId 追踪对话历史，永不删除 Session 条目（`TrimHistory` 只裁剪消息条数，不移除 Session Key）。在 `WaitingEvent` 状态下 `Remove(sessionId)` 被明确跳过。长期运行的 Agent 服务将无限积累历史条目，直至 OOM 崩溃。同样的问题存在于 `AgentSessionManager._instances`。

**修复方向**：引入基于 `LastAccessedAt` 的后台清理定时任务，超过会话超时（`SessionTimeout`）的条目从两个字典中移除。

---

### P1-03 · KeyVault 主密钥无持久化保障 — 数据不可恢复风险

**位置**：`Source/PuddingPlatform/Services/KeyVaultService.cs` — `ResolveMasterKey()`

**问题描述**：
未配置 `PUDDING_KEYVAULT_MASTER_KEY` 环境变量时，代码自动生成一个随机 256-bit 临时密钥（仅记 Warning 日志）。进程重启后临时密钥丢失，SQLite 数据库中已加密的 API Key 等数据将**永久无法解密**。这对生产部署构成数据丢失风险。

**修复方向**：启动时检测密钥来源，若为临时生成则应拒绝接受需要持久化 KeyVault 数据的操作（或至少将 Warning 升级为 Error 并阻断启动）。文档中应明确要求生产环境必须设置 `PUDDING_KEYVAULT_MASTER_KEY`。

---

### P1-04 · DirectLlmClient 非流式路径 ToolCalls 字段被忽略 — 逻辑缺陷

**位置**：`Source/PuddingRuntime/Services/DirectLlmClient.cs` — `ChatAsync()`

**问题描述**：
`ChatAsync` 在解析 OpenAI 响应后，始终以 `new LlmResponse(replyContent, null, null, usage)` 返回，`ToolCalls` 参数硬编码为 `null`，导致任何走非流式路径（同步 Agent Loop / P2P 中继等场景）的 function-calling 请求的工具调用结果被完全丢弃。

**修复方向**：解析 `response.choices[0].message.tool_calls`，若存在则映射为 `ToolCallItem[]` 并填充 `LlmResponse.ToolCalls`，与 `OpenAiLlmGateway` 保持一致。

---

### P1-05 · Session Cookie 配置不当 — CSRF 风险

**位置**：`Source/PuddingAgent/Program.cs` lines 113-117

```csharp
options.Cookie.SameSite = SameSiteMode.None;
options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
```

**问题描述**：
`SameSite.None` 允许跨站请求携带 Cookie，`SecurePolicy.SameAsRequest` 在 HTTP（非 HTTPS）环境下允许该 Cookie 通过明文传输。两者组合使得在开发或 HTTP 部署场景中，攻击者可通过跨站请求伪造（CSRF）劫持登录态。

**修复方向**：生产环境应设置 `SecurePolicy = CookieSecurePolicy.Always`；若无需跨域 Cookie，应将 `SameSite` 改为 `Lax` 或 `Strict`。

---

## 4. P2 — 建议/代码质量

### P2-01 · God Class: AgentExecutionService (1025 行)

**位置**：`Source/PuddingRuntime/Services/AgentExecutionService.cs`

两条执行路径（`ExecuteAsync` / `ExecuteStreamAsync`）各约 400+ 行，逻辑高度重复，包含 Loop 控制、护栏判断、工具调用、历史管理、记忆写回等多种职责。建议拆分为：
- `AgentLoopRunner` — 核心 Loop 状态机
- `AgentHistoryManager` — 历史追加/裁剪/注入
- `AgentToolExecutor` — 工具调用/护栏判断

---

### P2-02 · BuiltInAgentTemplates.cs 存在两份副本

**位置**：
- `Source/PuddingCore/Platform/BuiltInAgentTemplates.cs`
- `Source/PuddingAgent/BuiltInAgentTemplates.cs`

两文件命名空间均为 `PuddingCode.Platform`，内容几乎相同。双副本极易在维护时产生不一致。应删除 `PuddingAgent` 中的副本，仅保留 `PuddingCore` 中的版本。

---

### P2-03 · 命名空间 `PuddingCode.*` 与项目名 `PuddingCore` 不一致

**位置**：`Source/PuddingCore/` 所有文件

项目名为 `PuddingCore`，命名空间为 `PuddingCode.*`（与 git 仓库名一致，属历史遗留）。初次接触代码库的开发者需要额外认知负担才能理解对应关系，且 IDE 自动命名空间提示会产生混淆。建议评估是否统一为 `PuddingCore.*` 命名空间（影响范围较大，需单独排期）。

---

### P2-04 · PuddingPlatform 引入冗余数据库提供商依赖

**位置**：`Source/PuddingPlatform/PuddingPlatform.csproj`

```xml
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" ... />
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" ... />
```

架构文档明确当前 V1 仅使用 SQLite，但项目引用了 PostgreSQL 和 SQL Server EF Core 提供商。冗余依赖增加了依赖攻击面（supply chain）并增大了发布包体积。确认不需要时应移除。

---

### P2-05 · RuntimeDispatcher 硬编码 Fallback Endpoint

**位置**：`Source/PuddingController/Services/RuntimeDispatcher.cs` line 17

```csharp
private string _fallbackEndpoint = "http://localhost:5100";
```

魔法字符串应通过配置读取（`appsettings.json` 或环境变量），以支持不同部署环境的覆盖。

---

### P2-06 · WorkspaceApiController (Platform 层) 超过 500 行

**位置**：`Source/PuddingPlatform/Controllers/Api/WorkspaceApiController.cs` (估计 ~687 行)

包含工作区 CRUD、成员管理、Agent 配置、技能包、频道等多个业务域的混合处理，建议按域拆分为多个 Controller。

---

## 5. 依赖图合规检查

架构分层（Core → Runtime/Controller → Platform → Agent）整体合规，具体结果：

| 层 | 引用方向 | 状态 |
|---|---------|------|
| PuddingCore | 无上层依赖 | ✅ |
| PuddingRuntime | → PuddingCore, PuddingMemoryEngine | ✅ |
| PuddingController | → PuddingCore, PuddingGateway | ✅ |
| PuddingPlatform | → PuddingCore only | ✅ |
| PuddingAgent | → 所有模块（组合根） | ✅ |

> **注意**：PuddingAgent 中的 `BuiltInAgentTemplates.cs` 副本（P2-02）若单独编译，会与 PuddingCore 中的同名类冲突，需处理。

---

## 6. 异常处理 & 日志合规

| 检查项 | 结果 |
|--------|------|
| AgentExecutionService catch (OperationCanceledException) | ✅ 正确处理，日志记录 |
| AgentExecutionService catch (Exception) | ✅ 日志 + 返回失败结果 |
| ExecuteStreamAsync finally 块资源清理 | ✅ |
| KeyVaultService.ResolveMasterKey 临时密钥 Warning | ⚠️ Warning 级别不足（见 P1-03）|
| RuntimeDispatcher 调用失败是否有日志 | 需进一步确认（未详细审阅）|

---

## 7. 问题汇总

| ID | 严重度 | 标题 | 文件 |
|----|--------|------|------|
| P0-01 | 🔴 P0 | Controller 层端点无鉴权 | PuddingController/Controllers/ |
| P0-02 | 🔴 P0 | Runtime 层端点无鉴权 | PuddingRuntime/Controllers/ |
| P0-03 | 🔴 P0 | LLM API Key 明文传输 | ChatApiController.cs lines 82, 204 |
| P1-01 | 🟠 P1 | 直接 new HttpClient() | RuntimeDispatcher.cs, 等 5 处 |
| P1-02 | 🟠 P1 | _histories/_instances 无淘汰 | AgentExecutionService.cs line 44 |
| P1-03 | 🟠 P1 | KeyVault 主密钥无持久化保障 | KeyVaultService.cs |
| P1-04 | 🟠 P1 | DirectLlmClient ToolCalls 被忽略 | DirectLlmClient.cs |
| P1-05 | 🟠 P1 | Session Cookie 配置不当 | Program.cs lines 113-117 |
| P2-01 | 🟡 P2 | God Class AgentExecutionService | AgentExecutionService.cs |
| P2-02 | 🟡 P2 | BuiltInAgentTemplates 双副本 | PuddingCore/ & PuddingAgent/ |
| P2-03 | 🟡 P2 | 命名空间与项目名不一致 | PuddingCore 所有文件 |
| P2-04 | 🟡 P2 | PuddingPlatform 冗余 DB 依赖 | PuddingPlatform.csproj |
| P2-05 | 🟡 P2 | RuntimeDispatcher 魔法字符串 | RuntimeDispatcher.cs line 17 |
| P2-06 | 🟡 P2 | WorkspaceApiController 超长 | WorkspaceApiController.cs |

---

## 8. QA 结论

> **FAIL**

存在 3 项 P0 安全阻断问题（Controller/Runtime 层端点无鉴权、LLM API Key 明文传输）及 5 项 P1 问题，**不可合并/不可发布**。

### 必须修复后重审（P0）
- [ ] P0-01: 为 Controller 层端点添加鉴权
- [ ] P0-02: 为 Runtime 层端点添加鉴权（或网络隔离）
- [ ] P0-03: 重构 LLM Key 传递方式，改用 KeyVaultId 引用

### 建议在本迭代修复（P1）
- [ ] P1-01: 替换所有 `new HttpClient()` 为 `IHttpClientFactory`
- [ ] P1-02: 为 `_histories` 和 `_instances` 添加过期清理
- [ ] P1-03: 临时密钥场景添加启动阻断保护
- [ ] P1-04: DirectLlmClient 补充 ToolCalls 解析
- [ ] P1-05: 修正 Session Cookie 安全配置

---

*报告生成时间：2026-05-04 | 下次重审须由不同模型执行（参见 AGENTS.md QA 交错调度规则）*
