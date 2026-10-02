# 25 运行时契约闭环与可观测性验收基线 ADR

> 状态：**proposed**
> 日期：2026-05-20
> 范围：执行引擎 facade 接入、前后端契约一致性、E2E 证据链闭环
> 前置：[24核心架构组件边界与执行引擎拆分ADR](24核心架构组件边界与执行引擎拆分ADR.md)、[23运行时可观测性闭环与E2E验证基线ADR](23运行时可观测性闭环与E2E验证基线ADR.md)

---

## 1. 背景

ADR-024 创建了 6 个 Core 契约和 4 个 facade 实现，但存在以下未闭合问题：

1. **执行路径未完全接入**：`AgentExecutionService` 的工具调用、子代理调用、会话输出仍直接调用 `_skillRuntime` / `_subAgentManager` / `_ssm`，未经过新 facade。

2. **前后端契约不一致**：`SubAgentRunDetailDto` 后端返回嵌套 `Summary`，前端按扁平字段读取；`EventStats.byStatus/byComponent` 后端返回数组，前端定义为 `Record<string, number>`。

3. **LLM 配置语义混乱**：ADR-024 初版将 `ProfileId` 错误映射为 `ModelId`，缺少 provider/profile/model/role 分离。

4. **E2E 证据链未闭环**：前端 debug API 只有读取路径无写入路径，Playwright 测试未断言 trace/session/evidence。

5. **Timeline 默认排序未支持执行顺序**：通用 timeline 固定倒序，无法按执行流正序展示。

本 ADR 定义闭环所需的剩余工作，不新增架构方向。

---

## 2. 决策

### ADR-025-A：完成执行引擎 facade 接入

**决定**：`AgentExecutionService` 的所有外部调用（LLM、context、tool、sub-agent、session-output）必须经过 facade，旧直连路径作为 fallback 保留。

接入点：

| 调用点 | 当前状态 | 目标 |
|--------|---------|------|
| 非流式 LLM | ✅ 已接入 `ILlmInvocationService` | — |
| 流式 LLM | ❌ 直连 `_llmClient.ChatStreamAsync` | `ILlmInvocationService.InvokeStreamAsync` |
| Context 装配 | ⚠️ 仅首次接入 | 重组装路径也经 `IContextAssemblyService` |
| Tool 调用 | ❌ 直连 `_skillRuntime.InvokeAsync` | `IToolInvocationService.InvokeAsync` |
| Sub-Agent 调用 | ❌ 直连 `_subAgentManager.SpawnAsync` | `ISubAgentInvocationService.InvokeAsync` |
| Session Output | ❌ 直连 `_ssm.AppendAsync` / `Append()` | `ISessionOutputWriter.WriteFrameAsync` |

### ADR-025-B：前后端契约对齐

**决定**：所有诊断 API 的 DTO 必须与前端 type 一致，并通过 contract test 验证序列化往返。

关键修复：

| 端点 | 问题 | 修复 |
|------|------|------|
| SubAgentRun detail | 嵌套 `Summary` vs 扁平字段 | ✅ 前端已修（用 `summary.*`） |
| Event stats | 数组 vs `Record<string,number>` | ✅ 前端已修（用数组） |
| Timeline sort | 固定 desc | ✅ 已加 `SortOrder` 参数 |
| All diagnostic API | 无 contract test | 需加 DTO 序列化往返测试 |

### ADR-025-C：LLM 配置契约正式化

**决定**：`LlmInvocationProfile` 成为 LLM 调用的唯一配置入口，禁止把 profileId/modelId 混用。

契约（已在 ADR-024 Phase 1 更新）：

```csharp
public sealed record LlmInvocationProfile
{
    public required string ProviderId { get; init; }   // openai / deepseek / local
    public required string ProfileId { get; init; }     // conscious.default / subconscious.default
    public required string ModelId { get; init; }       // gpt-4o / deepseek-chat
    public string Role { get; init; } = "conscious";    // conscious / subconscious
}
```

### ADR-025-D：E2E 证据链闭环

**决定**：端到端测试必须覆盖"UI 发起会话 → 拿到 traceId/sessionId → 后端 evidence API 可查 → 断言关键组件执行顺序"。

实施：

- Admin 前端写入 `sessionStorage` 的 trace/session 路径。
- Playwright 测试：导航到 `/?debug=1`，发送消息，等待 done 帧，从 `window.__PUDDING_DEBUG__` 读取 lastTraceId/lastSessionId，调用 diagnostics API 验证 evidence 包含 agent_execution → context_pipeline → llm_gateway 有序记录。
- Docker smoke 添加 evidence API 验证步骤。

---

## 3. 任务拆分

### Phase 1：执行引擎 facade 接入（P0）

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-CLOSE-001 | LLM 流式路径经 facade | `AgentExecutionService` 流式调用 → `ILlmInvocationService.InvokeStreamAsync` |
| ARCH-CLOSE-002 | Context 重组装路径经 facade | memory-enabled 路径 → `IContextAssemblyService` |
| ARCH-CLOSE-003 | Tool 调用经 facade | `foreach (var call in toolCalls)` → `IToolInvocationService.InvokeAsync` |
| ARCH-CLOSE-004 | Sub-Agent 调用经 facade | `_subAgentManager.SpawnAsync` → `ISubAgentInvocationService.InvokeAsync` |
| ARCH-CLOSE-005 | Session output 经 facade | `Append(SSE frame)` → `ISessionOutputWriter.WriteFrameAsync` |

### Phase 2：契约一致性（P0）

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-CLOSE-006 | 诊断 API DTO contract tests | SubAgentRunDetail / EventStats / TimelineItem 序列化往返测试 |
| ARCH-CLOSE-007 | LLM 配置契约测试 | `LlmInvocationProfile` 序列化 / provider/profile/model 不混用 |

### Phase 3：E2E 证据链（P1）

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-CLOSE-008 | Debug API 写入路径 | Admin 前端写入 traceId/sessionId 到 sessionStorage |
| ARCH-CLOSE-009 | Playwright evidence 断言 | E2E 测试查 evidence API 并断言组件执行顺序 |
| ARCH-CLOSE-010 | Docker smoke evidence | docker-smoke.ps1 添加 evidence API 验证 |

### Phase 4：QA 与文档收口（P1）

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-CLOSE-011 | QA 报告 | 全量编译/测试/契约一致性/残余风险 |
| ARCH-CLOSE-012 | Tasks.md 更新 | ADR-025 状态行 |
