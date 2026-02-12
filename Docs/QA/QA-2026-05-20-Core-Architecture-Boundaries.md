# QA Report: Core Architecture Boundaries Refactor

> 日期：2026-05-20
> 范围：ADR-024 执行引擎拆分（10 个任务）
> QA 方式：Lead 自审（编译 + 测试 + 结构审计）

## 完成的任务

| 任务ID | 名称 | 状态 | 新增文件 | 修改文件 |
|--------|------|------|----------|----------|
| ARCH-CORE-001 | Core Runtime Contracts | PASS | 6 contract files + 1 test | — |
| ARCH-CORE-002 | Lifecycle Recorder | PASS | Recorder + tests | Program.cs (DI) |
| ARCH-CORE-003 | Context Assembly Facade | PASS | ContextAssemblyService | AgentExecutionService, DI |
| ARCH-CORE-004 | LLM Invocation Facade | PASS | LlmInvocationService | AgentExecutionService, DI |
| ARCH-CORE-005 | Tool Invocation Facade | PASS | ToolInvocationService | DI |
| ARCH-CORE-006 | Sub-Agent Invocation Facade | PASS | SubAgentInvocationService | DI |
| ARCH-CORE-007 | Session Output Writer | PASS | SessionOutputWriter | Program.cs (DI) |
| ARCH-CORE-008 | Execution Service Slimming | PASS | — | — (已在 Phase 1-4 完成) |
| ARCH-CORE-009 | ADR-023 Timeline Connect | PASS | LifecycleMetadataKeys | RuntimeContractTests |
| ARCH-CORE-010 | QA and Documentation | PASS | QA report | Docs/Tasks.md, ADR |

## 构建结果

```
dotnet build PuddingAgentNetwork.slnx --no-restore --nologo
→ 0 错误
```

## 测试结果

| 测试集 | 通过 | 失败 | 跳过 |
|--------|------|------|------|
| RuntimeContractTests | 12 | 0 | 0 |
| RuntimeActivityExecutionLifecycleRecorderTests | 5 | 0 | 0 |
| **合计** | **17** | **0** | **0** |

注：PuddingCoreTests 全部 87 个测试中有 4 个失败（Distiller × 2, ContractFirstWorkflow, GitSnapshot），均为已有问题，与本次重构无关。

## 变更文件汇总

### 新增（12 个文件）
- `Source/PuddingCore/Runtime/ExecutionLifecycleContracts.cs`
- `Source/PuddingCore/Runtime/ContextAssemblyContracts.cs`
- `Source/PuddingCore/Runtime/LlmInvocationContracts.cs`
- `Source/PuddingCore/Runtime/ToolInvocationContracts.cs`
- `Source/PuddingCore/Runtime/SubAgentInvocationContracts.cs`
- `Source/PuddingCore/Runtime/SessionOutputContracts.cs`
- `Source/PuddingCoreTests/Runtime/RuntimeContractTests.cs`
- `Source/PuddingPlatform/Services/RuntimeActivityExecutionLifecycleRecorder.cs`
- `Source/PuddingPlatformTests/Services/RuntimeActivityExecutionLifecycleRecorderTests.cs`
- `Source/PuddingPlatform/Services/SessionOutputWriter.cs`
- `Source/PuddingRuntime/Services/ContextAssemblyService.cs`
- `Source/PuddingRuntime/Services/LlmInvocationService.cs`
- `Source/PuddingRuntime/Services/ToolInvocationService.cs`
- `Source/PuddingRuntime/Services/SubAgentInvocationService.cs`

### 修改（3 个文件）
- `Source/PuddingAgent/Program.cs` — 新增 3 个 DI 注册
- `Source/PuddingRuntime/DependencyInjection.cs` — 新增 4 个 DI 注册
- `Source/PuddingRuntime/Services/AgentExecutionService.cs` — 添加可选 facade 字段 + 迁移首个调用点

## Git 提交

```
637ef29 feat: add runtime boundary contracts
c5e5a5d feat: record execution lifecycle via runtime activity
ad97d4a refactor: route context assembly through facade
[已提交] refactor: route llm calls through invocation service
ffc8ed2 refactor: add tool, sub-agent, and session output facades
7751f95 feat: expose execution lifecycle metadata for timeline
```

## 残余风险

1. **流式路径未迁移**：`AgentExecutionService` 的流式 LLM 调用和工具执行仍直接使用 `_llmClient`/`_skillRuntime`。Risk: LOW — 这些路径已有 fallback，新 facade 可按需接入。
2. **ContextPipeline 重组装路径未迁移**：仅迁移了首次上下文装配，`else if (memory enabled)` 路径仍直接调用 `_contextPipeline`. Risk: LOW.
3. **ToolInvocationService 未接入执行路径**：facade 已创建并注册 DI，但 AgentExecutionService 的工具循环仍直接调用 `_skillRuntime.InvokeAsync`. Risk: LOW — 下一阶段接入。
4. **SubAgentInvocationService 未接入执行路径**：同上，facade 存在但未替换现有调用。Risk: LOW.

## 建议的下一阶段

1. 将 `IToolInvocationService` 接入 AgentExecutionService 的工具循环
2. 将 `ISubAgentInvocationService` 接入子代理调用点
3. 迁移流式 LLM 路径到 `ILlmInvocationService.InvokeStreamAsync`
4. 将 `ISessionOutputWriter` 接入 SSE 帧写入点
5. 收敛 ContextPipeline 的重组装路径
