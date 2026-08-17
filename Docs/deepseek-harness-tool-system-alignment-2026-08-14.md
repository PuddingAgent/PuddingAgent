# PuddingAgent 工具系统对齐 deepseek-harness 优化方案

> 日期：2026-08-14
> 状态：设计方案，未实施
> 参考仓库：`E:\github\deepseek\deepseek-harness`
> Pudding 仓库：`E:\github\AgentNetworkPlan\PuddingAgent`
> 关联 UI 方案：`Docs/deepseek-harness-message-card-alignment-2026-08-14.md`
> 上位架构：`Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`；本文的工具 Registry、执行 Hook、durable facts 与插件生命周期均受该方案约束。

## 1. 结论

PuddingAgent 不应照搬 deepseek-harness 的 TypeScript 插件框架，但应采用它围绕 DeepSeek 模型形成的工具协议：**同一份类型定义同时约束模型输入、程序返回、模型可见结果、可回放 UI 和执行策略**。

Pudding 当前已经具备统一注册表、权限/防火墙、动态工具发现、子代理能力边界、后台 Terminal 和多协议 LLM 网关，这些基础必须保留。真正需要重构的是工具调用中间层：

1. 将 `ToolExecutionResult(Output/Error string)` 升级为“经 Schema 验证的 canonical JSON value + 独立模型渲染 + 结构化错误”。
2. 让模型产生的 `tool_call_id` 端到端不变，贯穿执行、事件、遥测、历史和 UI；嵌套调用另有内部 token 与 `rootCallId`。
3. 把当前单体式执行服务拆成有确定顺序的 `validate → policy → guard → execute → validate output → render → post-process → finalize → persist` 管线。
4. 消费现有 `ConcurrencySafe` 声明：连续安全调用有界并发，独占调用成为顺序屏障，结果仍按模型调用顺序入历史。
5. 建立统一的超大输出 spill 协议，保留完整结果的可检索引用，模型只接收有界 head/tail 预览，不再由各工具随意截断。
6. 工具自己输出与具体前端无关的 presentation intent；实时和回放使用同一份持久化元数据，前端不再按工具名猜卡片。
7. 在上述协议稳定后，为 DeepSeek Agent 增加可配置的 `native | code | both` 工具呈现模式；Code Mode 只作为复杂多工具工作流的优化路径，不替代原生 function calling。

优先级上，**调用身份和 canonical output 是 P0，Code Mode 是 P2**。如果先做 Code Mode 或前端卡片，而底层仍传递字符串并丢失调用身份，只会把现有歧义扩散到更多层。

## 2. 范围与原则

本方案覆盖模型发现工具、生成参数、执行、并发、权限、错误恢复、输出留存、事件审计和 UI 投影，不重复讨论消息气泡的视觉细节。

采用以下原则：

- 模型看到的是稳定、简洁、可自纠错的协议，不是 CLR、数据库或 UI 实现细节。
- canonical value 是事实；模型文本和 UI 卡片都是它的投影，不能反过来让程序解析展示文本。
- 一次调用只有一个外部 `callId`；跨层不得重新生成。
- 权限只允许单调收紧。任何后置扩展都不能把前面已经拒绝的调用重新放行。
- 工具参数一经记录即不可被语义重写；Secret 解析属于受保护资源绑定，不属于模型参数改写。
- 成功、失败、取消、超时、拒绝和输出无效均是明确终态，不能依赖字符串关键字推断。
- DeepSeek 优化必须通过真实 transcript、token、prefix hash、耗时和成功率 A/B 验证，不能只凭编译通过判断。

## 3. deepseek-harness 的关键设计证据

| 能力 | 参考位置 | 对 Pudding 的意义 |
|---|---|---|
| 完整执行管线 | `packages/core/tools/README.md`、`docs/tool-execution-pipeline.md` | pre-execute、单调 guard、execute wrapper、post-execute、finalize、observe 各自职责清晰 |
| 统一输入/输出 Schema | `packages/core/tools/src/`、`docs/cookbook/adding-a-tool.md` | 参数和返回值都经同一 Schema 体系验证；无须解析 prose |
| canonical value 与呈现分离 | `packages/core/tools/README.md` | execute 返回 lossless JSON；模型文本、持久化 meta 和 UI 卡片独立投影 |
| 不可变调用身份 | `packages/core/tools/README.md` | `callId/name/arguments/agent/token/parent` 在分发过程中不可变 |
| Tool-owned UI | `packages/core/tools/README.md`、`packages/shell/tool-bash/src/index.ts` | 工具返回 generic/terminal/diff/search/read/web 意图，客户端不硬编码工具名 |
| 并发分类 | `packages/core/tools/README.md` | 只有显式 `isConcurrencySafe(args) == true` 才并发；独占调用是屏障 |
| Code Mode | `packages/core/tools/README.md`、`packages/code-runtime/README.md` | 生成强类型工具 SDK；内部调用仍经过完整权限和审计管线 |
| 超大输出 spill | `packages/spill/spill-policy/README.md`、`packages/spill/spill/README.md` | 完整文本保存到 session-scoped store；模型只接收预算内预览和检索提示 |
| 稳定 Schema 顺序 | `packages/core/system-prompt/README.md`、`tests/tool-order.spec.ts` | 工具排序不受插件加载顺序影响，保持 DeepSeek 请求前缀稳定 |
| 生产工具范例 | `packages/shell/tool-bash/src/index.ts`、`packages/fs/tool-fs/`、`packages/fs/tool-fs-search/` | 参数约束、canonical output、超时、后台 job、presentation 同时设计 |
| 子代理工具族 | `packages/subagent/tool-subagent*` | 启动、控制、查询和回报是协议化能力，不把子代理内部状态拼成不可解析文本 |

这些做法并非只有 DeepSeek 才能使用，但对 DeepSeek 特别重要：工具描述、Schema 集合和顺序会重复进入请求前缀；稳定性影响 prefix cache，清晰错误影响下一轮自纠错，多工具编排的回合数直接影响延迟和 token 成本。

## 4. Pudding 当前基线

### 4.1 应保留的能力

- `IPuddingToolRegistry` 已统一内置工具、Manifest/Workspace 来源和 MCP 能力。
- `ToolPermissionLevel`、`ToolSafetyFlags`、`SubAgentExposure`、`CapabilityPolicy` 已表达大部分安全事实。
- `AgentFirewall` 已集中处理 capability、authorization、approval 和 sandbox。
- `ToolExposurePlanner` 已按名称稳定排序，并在工具超过 24 个时用 `search_tools` 延迟暴露。
- Agent Loop 已把 Assistant tool calls 与 Tool result 组成标准闭环，并记录 reasoning、usage 和 prefix snapshot。
- `terminal_start/wait/read/cancel` 已具备后台进程的基础生命周期。
- 子代理使用系统管理的 600 轮、2400 次工具调用、24 小时预算，支持可恢复 `budget_exhausted`，不应退回模型自选小预算。

### 4.2 已确认的协议缺口

| 当前实现 | 问题 | 影响 |
|---|---|---|
| `ToolExecutionResult` 只有 `Success/Output/Error/ExitCode` | canonical data、模型文本、UI 文本混在 string 中 | Agent、Code Mode、前端和诊断都要重复解析文本 |
| First-party 反射生成的 `ToolParameterSchema` 只有 name/type/description/required（外部 raw schema 另论） | 内置工具缺少 nested items、enum、const、oneOf、范围、pattern、additionalProperties 的统一生成与验证 | 错误参数经常到工具内部才发现，自纠错信息不统一 |
| 无 output schema | 工具可返回任意字符串；执行层无法验证 | `search_tools` 等调用者必须再次 `JsonDocument.Parse(output)` |
| `PuddingToolExecutionService` 重新生成 GUID | 上游 `ToolInvocationRequest.ToolCallId` 未传入 `ToolExecutionRequest` | 模型调用、运行事件、遥测和 UI 无法可靠关联同一次调用 |
| Streaming `tool_call`/`tool_result` SSE 只有 name/arguments/output | 帧中缺少 `toolCallId` | 同名并发或连续调用只能依赖相邻顺序猜测 |
| Buffered/Streaming loop 使用 `foreach` 顺序执行 | `ConcurrencySafe` 只是元数据，未成为调度合同 | DeepSeek 一轮返回多个只读工具时仍串行等待 |
| 防火墙、调用、遥测集中在一个服务方法 | 扩展只能继续堆条件或包裹外层 | 超时、重试、spill、结果校验难以形成确定顺序 |
| 参数标准化允许兼容别名 | 执行输入可能不同于公开 Schema | 长期会造成模型协议与真实执行协议漂移 |
| 各工具自行截断或只返回 tail | 没有统一完整结果引用 | 模型不知道结果是否完整，审计也可能找不到原文 |
| 工具返回 Markdown/JSON/string 混合 | UI 只能按工具名和字符串形状特殊处理 | 实时与历史回放容易不一致 |
| 部分领域错误用 `Success=true` 包装 `{status:"error"}` | 传输成功和业务失败混淆 | Agent 无法统一决定重试、换参、审批或停止 |

其中最紧急的问题可以由当前源码直接复现：

```text
AgentExecutionService
  ToolInvocationRequest.ToolCallId = provider call.Id
    → ToolInvocationService 保留该 id
      → PuddingToolExecutionService
        ToolExecutionRequest.ToolCallId = Guid.NewGuid()  // 身份断裂
```

## 5. 目标架构

```mermaid
flowchart LR
    A["Agent step: prompt + visible tools"] --> B["LLM native call / run_code"]
    B --> C["Immutable ToolInvocation"]
    C --> D["Input schema validation"]
    D --> E["Typed Hook: arguments transform"]
    E --> F["Pre-policy + tool.before_execute Guard"]
    F --> G["tool.execute Around: timeout / retry / metrics"]
    G --> H["Tool provider"]
    H --> I["Canonical JSON validation"]
    I --> J["tool.result Transform + model renderer"]
    J --> K["Post-process + spill + finalize"]
    K --> L["Durable tool.result"]
    L --> M["Model history"]
    L --> N["Replayable presentation intent"]
    N --> O["Admin / Desktop UI"]
```

目标不是增加更多 DTO，而是建立一个事实源：

- `ToolDefinition` 声明模型协议和执行策略；
- `ToolInvocation` 声明一次不可变调用；
- `ToolRunResult` 声明规范化终态；
- `tool.call` / `tool.result` 是持久化事实；
- 模型历史和 UI 都从这些事实投影。

## 6. 新工具定义合同

下面是目标 C# 形状示意，不是要求一次提交完成的最终 API：

```csharp
public sealed record ToolDefinition
{
    public required string Id { get; init; }
    public required string Description { get; init; }
    public required JsonSchema InputSchema { get; init; }
    public required ToolOutputDefinition Output { get; init; }

    public ToolPermissionFacts Permission { get; init; } = new();
    public TimeSpan? DefaultTimeout { get; init; }
    public Func<JsonElement, bool>? IsConcurrencySafe { get; init; }
    public Func<ToolPresentationInput, ToolPresentationIntent?>? Present { get; init; }
}

public sealed record ToolOutputDefinition
{
    public required JsonSchema Schema { get; init; }
    public required Func<JsonElement, JsonElement, ToolContent> Render { get; init; }
    public Func<JsonElement, JsonElement, JsonElement?>? BuildPresentationMeta { get; init; }
}
```

约束如下：

1. First-party 工具必须声明 input 和 output schema；注册时即校验定义合法性。
2. 工具主体只返回 canonical JSON value，不返回 Markdown 卡片。
3. Registry 对返回值做 lossless JSON materialize、output schema 校验和冻结，再执行 renderer。
4. renderer 只负责模型可见内容；presentation projector 只负责可回放 UI 元数据。
5. tool-specific timeout 和 concurrency classifier 是 host metadata，不发送给模型。
6. Schema、描述和排序必须确定性生成；相同工具集产生 byte-stable 的 LLM 定义。

### 6.1 Schema 能力

Pudding 不需要复制 Schemastery，但新 Schema 层至少要支持：

- `string/number/integer/boolean/null/array/object`；
- nested `properties/items`；
- `required`、`enum`、`const`、`oneOf`；
- `minimum/maximum`、`minLength/maxLength`、`pattern`；
- 显式 `additionalProperties`；
- 属性路径明确的验证错误，如 `$.timeout_seconds: must be <= 600`。

推荐使用 Core 内部的不可变 Schema AST，并由 source generator 从属性/记录生成；LLM Provider 适配器只负责把 AST 投影为 OpenAI Responses、Chat Completions 或 Anthropic wire schema。禁止三个 Gateway 分别手写不同的降级逻辑。

### 6.2 MCP 边界

MCP 是外部协议边界，不是需要删除的“历史兼容层”。处理方式应是：

- 输入 Schema 在 MCP adapter 注册时规范化为 Pudding Schema AST；
- MCP 没有可靠 output schema 时，用显式 `json | text | content_blocks` 联合输出合同，不伪造强类型；
- MCP structuredContent 保留为 canonical value，content 作为模型渲染 fallback；
- MCP 错误映射为统一 `ToolError`，但保留 server/tool/original code 供诊断。

## 7. 规范化执行结果

```csharp
public abstract record ToolRunResult
{
    public required ToolContent Content { get; init; }
    public JsonElement? PresentationMeta { get; init; }
    public IReadOnlyList<DeferredContext> AdditionalContexts { get; init; } = [];
}

public sealed record ToolSuccess : ToolRunResult
{
    public required JsonElement Value { get; init; }
    public bool ConcludesTurn { get; init; }
}

public sealed record ToolFailure : ToolRunResult
{
    public required ToolError Error { get; init; }
}

public sealed record ToolError(
    string Code,
    string Message,
    ToolErrorKind Kind,
    bool Retryable = false,
    string? RecoveryHint = null);
```

建议的稳定错误类别：

| Kind | 例子 | Agent 建议动作 |
|---|---|---|
| `InvalidArguments` | 缺参数、类型错误、跨字段约束失败 | 修正参数后重试 |
| `PermissionDenied` | capability 或 sandbox 拒绝 | 遵循 recovery hint；不可绕过 |
| `ApprovalRequired` | 需要用户审批 | 发起一次批准流程，不盲目重试 |
| `NotFound` | tool/job/file/agent 不存在 | 重新发现或校验 id |
| `Conflict` | CAS、重复执行、状态不允许 | 刷新状态后决定 |
| `Timeout` | 工具 deadline 到期 | 缩小任务、转后台或调大受限预算 |
| `Cancelled` | 用户/父执行取消 | 停止派生工作 |
| `ProviderFailure` | 外部 API/进程失败 | 按 retryable 和退避策略处理 |
| `InvalidToolOutput` | 工具返回值不符合自身 output schema | 记为实现错误，不让模型猜格式 |
| `OutputLimit` | acquisition 或 program output 超限 | 读取 spill 引用或缩小查询 |

`Error.Code` 用于程序、遥测和测试；给模型的 `Message + RecoveryHint` 要具体但不泄露内部异常栈。Code Mode 的工具调用异常只公开 `toolName/message`，内部 code 仍留在审计事实中。

## 8. 调用身份与事件合同

### 8.1 身份规则

- `CallId`：Provider/模型产生的调用 id，或只在 Provider 缺失时由协议适配器稳定合成；一旦进入 Agent Loop 就不可更换。
- `ExecutionToken`：Registry 在进程内创建的 opaque token，仅用于防止跨调用错误复用，不持久化。
- `RootCallId`：普通调用等于 `CallId`；Code Mode/组合工具的内层调用指向外层根调用。
- `ParentExecutionToken`：仅进程内表达嵌套关系，子工具不能取得父调用可变对象。
- `NestedCallId`：确定性生成，如 `<root>:code:<sequence>`，便于日志和回放配对。

P0 必须完成：

1. `IPuddingToolExecutionService.ExecuteAsync` 接收 `ToolInvocation`，不再接收散落的 `toolId/argumentsJson/context`。
2. 删除统一执行层的 `Guid.NewGuid()`，直接使用 caller 的 `CallId`。
3. Streaming 和 Buffered 事件均携带 `callId/name/status/startedAt/endedAt`。
4. `tool.call` 和 `tool.result` 以 `(sessionId, callId)` 配对；禁止按工具名或相邻顺序关联。
5. 遥测、SubAgent archive、消息投影和 UI `ToolCallRow` 使用同一 id。

### 8.2 参数冻结与 KeyVault

Pudding 需要 Secret 注入，因此应显式区分：

- `ModelArguments`：模型提交、经 Schema 验证、脱敏后持久化的不可变 JSON；
- `BoundArguments`：在受保护阶段把 Secret placeholder 解析成执行资源后的内存对象，禁止日志/事件/UI 回显；
- 参数绑定不得新增、删除或改变与 Secret 无关的业务字段。

这样既满足“调用参数不可被管线任意重写”，也保留 Pudding 的 KeyVault 能力。Firewall 应基于 `ModelArguments` 与独立权限事实判断，不读取含 Secret 的序列化文本。

## 9. 执行管线

核心提交顺序固定如下；允许扩展的步骤通过上位架构的 Typed Hook Registry 注册，不能靠加载顺序或在 Agent Loop 中插入任意回调：

1. Resolve visible tool definition。
2. Materialize lossless `ModelArguments`；`tool.arguments.transform` 只在冻结前执行，每个变换结果重新校验。
3. Input schema validation 并冻结参数；失败直接产生 `InvalidArguments`，不进入权限或工具主体。
4. Secret/resource binding，得到只在执行内存存在的 `BoundArguments`。
5. Pre-policy + `tool.before_execute` Guard：`Allow | Deny | Ask`；deny 单调不可逆，冻结后的参数不可再修改。
6. `tool.execute` Around chain：deadline、受控 retry、metrics、trace；wrapper 只能替换 operational cancellation token，必须显式调用 `next()` 才继续。
8. Tool body 执行并协作响应 cancellation。
9. Canonical value materialize + output schema validation。
10. `tool.result.transform` 按确定顺序变换 canonical result 并重新验证；随后 `Output.Render` 生成模型 content，`BuildPresentationMeta` 生成持久化 UI meta。
11. Post-process：内容策略、additional context、spill；禁止静默改写 args。
12. Tool-owned finalizer 维护最终内容不变量。
13. 原子追加 durable `tool.result`，然后通知 live observers。
14. Agent Loop 按模型调用顺序把 result 和 deferred contexts 追加到 history。

`AgentFirewall` 不需要推翻；它应成为 pre-policy + monotonic guard 的核心 provider。当前 capability/authorization/sandbox 三道 gate 可以保留，只需把决定规范化为统一结果并固定不可逆顺序。

### 9.1 重试纪律

- Registry 默认不重试有副作用工具。
- 只有工具显式声明 idempotency 语义，或请求携带稳定 idempotency key 时，执行 wrapper 才能自动重试。
- PermissionDenied、InvalidArguments、ApprovalRequired 不自动重试。
- ProviderFailure 可按 error code、retryable、Retry-After 和共享 deadline 有界重试。
- 每次物理尝试属于同一 `CallId`，另有 `attempt` 序号；历史只产生一个规范化终态，审计保留尝试明细。

## 10. DeepSeek 的模型可见工具设计

### 10.1 描述和 Schema

每个工具描述应依次回答：

1. 做什么；
2. 何时使用/何时不要使用；
3. 关键输入边界；
4. 成功返回哪些 canonical 字段；
5. 失败后该如何恢复。

跨工具的公共规则不要复制进每个 description，而由稳定的 tool guidance section 提供。工具描述中不得出现 React 组件名、数据库表、DI 服务或“此字段仅供前端”之类实现术语。

### 10.2 工具集合与 prefix cache

Pudding 已经按名称稳定排序，这是正确方向。建议在此基础上增加 Agent 配置项 `toolPresentation` 和稳定工具包：

```json
{
  "toolPresentation": "native",
  "toolKits": ["core", "workspace", "code"],
  "deferredToolDiscovery": true
}
```

建议工具包：

- `core`：goal、message、status、sub-agent、task、sleep；
- `workspace`：read/write/patch/search；
- `code`：terminal、git、code intelligence；
- `browser`：七项 browser tools；
- `research`：web/search/knowledge；
- `admin`：高权限诊断与管理。

规则：

- Agent turn 开始前由 manifest 和 capability policy 确定基础工具包；同一稳定阶段顺序和定义 byte-identical。
- `search_tools` 保留为稀有能力恢复路径，但返回 typed canonical `loadedToolIds`，Loop 不再解析 output string。
- 激活后的工具集合在当前模型 round 内冻结，只能从下一 round 生效。
- 工具注册/卸载、scope restriction 或动态发现导致 prefix 变化时，记录原因和新 hash。
- 不应宣称 Code Mode 必然减少 schema token；deepseek-harness 自身也只承诺“SDK + transport”替换原生 schema 形状。必须实测。

### 10.3 DeepSeek A/B 指标

- 有效工具调用率：参数一次通过并进入 tool body 的比例；
- 参数修复轮数：`InvalidArguments` 后成功需要的额外轮数；
- 每任务 LLM round、tool call、输入/输出 token；
- prefix hash 变化次数、cache hit tokens 和 cache hit ratio；
- 多只读调用的 wall-clock；
- 工具失败后同参盲重试率；
- spill 后主动 read/grep 取回比例；
- Native 与 Code Mode 的任务成功率、延迟、token 和错误类型。

## 11. 并发调度

`ToolSafetyFlags.ConcurrencySafe` 目前只是描述符，目标是让它成为可执行合同。

调度规则：

1. 未声明、参数无效、classifier 异常或返回 false，一律 `exclusive`。
2. 连续 `parallel` 调用进入有界 rolling pool；默认并发度建议 4，配置上限不超过 10。
3. 遇到 `exclusive`：先 drain 前面的并发组，独占执行完成后才启动后续调用。
4. policy、事件开始/结束、history tool results 保持模型原始顺序；只允许实际 body 重叠。
5. 写同一文件、同一 job、同一 goal、同一会话状态的调用默认 exclusive。
6. 只读不自动等于安全；依赖共享游标、消费型 stream 或非线程安全 client 的只读工具仍应 exclusive。
7. 父 turn 取消后，pool 停止启动新调用，取消并 drain 已启动调用再关闭 turn。

第一批可评估并发的工具：`file_read`、纯查询 grep/glob、只读 git status/log、独立 HTTP search。第一批保持独占：write/patch、commit、goal_update、消息发送、审批、子代理启动/恢复、job output 消费。

## 12. 输出预算与 spill

### 12.1 分层限制

输出限制必须区分三层：

1. Acquisition cap：Provider/进程/网络读取的真实安全上限，防止 OOM；
2. Canonical value：在合理资源上限内保存完整、可编程的结果；
3. Model-facing content cap：控制进入上下文的字节/token，超限时 spill。

当前各工具直接 `Truncate` 会把 2 和 3 混在一起。目标 spill 协议：

```json
{
  "locator": "spill://session/<id>/<callId>/result",
  "bytes": 184223,
  "sha256": "...",
  "retrieval": {
    "tool": "file_read",
    "args": { "path": "...", "offset": 0, "limit": 200 }
  }
}
```

模型结果使用预算内 head/tail 预览，并明确显示：省略字节数、完整结果 locator、推荐 read/grep 参数。要求：

- 预览 + notice 的 UTF-8 总大小不超过配置上限；
- spill 失败不把成功调用改成失败，也不能隐藏原始结果；记录 warning 并采用安全 fallback；
- canonical value 不因模型 content spill 被破坏；
- store 按 session owner 隔离，locator 是 opaque identifier，不假设永远是本地路径；
- spill source 记录 `toolName/callId/label/hash/bytes`；
- 读取 locator 仍经过 workspace/session 权限校验；
- `read` 自身避免形成 `read → spill → read` 死循环，使用 offset/limit 确保结果有界。

## 13. 后台任务合同

以现有 Terminal 为基础抽象 `IJobRuntime`：

- producer 在成功发布 handle 后返回 canonical `{ jobId, kind, owner, createdAt }`；
- 发布 handle 前，call cancellation 可取消启动；发布后，外层 call 取消只停止等待，不自动杀死 job；
- `job_read/job_wait/job_cancel/job_list` 采用统一所有权和 session fence；
- job output 是增量 cursor 协议，返回 `nextOffset/truncated/completed/exit`；
- Agent/Session dispose、Desktop/Core shutdown 对 job 有明确回收政策；
- 长任务不应靠把 timeout 设为数小时占住一次工具调用。

这样可把 Terminal 的正确生命周期复用于浏览器下载、索引、批量导入和外部工作流，而不是为每种工具新建一套 start/wait/cancel。

## 14. Tool-owned presentation 与回放

工具合同输出 provider-neutral intent：

```text
generic | terminal | diff | search | read | web | delegation | job
```

intent 只包含可持久化数据，不包含 React node、CSS 类或本地组件实例。约束：

- `PresentCall(args)` 和 `PresentResult(args, durable result)` 必须是纯函数；
- 需要 result-time 信息的卡片通过 `PresentationMeta` 持久化；
- older/invalid args 只降级到 generic card，不能让历史回放崩溃；
- UI 不按 `toolName === "shell"` 之类条件选择卡片；
- 模型 content 不为 UI 添加 fenced code、相对路径或 diff chrome；
- 实时 SSE 和历史 run archive 复用同一 `ToolCallViewModel`。

具体行式 UI、ReasoningRow、ToolCallRow 和 DelegationRow 由 `Docs/deepseek-harness-message-card-alignment-2026-08-14.md` 定义。本方案负责提供其需要的 `callId/status/arguments/content/error/presentationMeta/timestamps` 事实。

## 15. Code Mode 方案

### 15.1 定位

Code Mode 适合以下 DeepSeek 场景：

- 一轮内对多个独立文件/查询做 fan-out，再由程序筛选和聚合；
- 有循环、条件、排序、去重等确定性数据处理；
- 需要减少“调用一个工具 → 模型读结果 → 再调用下一个工具”的中间 LLM round。

不适合：

- 一两个简单工具调用；
- 需要频繁用户审批的高风险动作；
- 依赖隐藏宿主 API 或任意文件/网络访问；
- canonical output 尚未完成的字符串型工具。

### 15.2 安全架构

- `run_code` 在隔离 worker/process 中运行，不直接暴露 .NET ServiceProvider、文件系统、网络或环境变量。
- worker 只能调用由当前 Agent scope 生成的 `tools.<name>(typedArgs)` binding。
- 每个 binding 重新进入完整 Pudding 工具管线，不能绕过 firewall、approval、sandbox、telemetry 或 spill。
- generated SDK 从同一 input/output schema 生成，工具顺序和文本必须确定性。
- 内部调用使用有界并发池与 exclusive barrier。
- 外层结束时 abort 并 drain 内部调用，确保所有 durable events 落在当前 turn 内。
- 程序只取得 canonical values；模型可见文本和内部错误 code 不作为程序 API。
- 中间 canonical values 不写入模型历史；外层 `logs/result` 才受 output cap 和 spill 约束。

### 15.3 配置

```json
{
  "toolPresentation": "native",
  "codeMode": {
    "enabled": false,
    "language": "javascript",
    "maxParallelSubCalls": 4,
    "maxRuntimeSeconds": 30,
    "maxOutputBytes": 4194304
  }
}
```

初始默认必须是 `native`。只为明确选中的 DeepSeek 测试 Agent 开启 `both` 做 A/B；稳定后才允许 `code`。不得按 Provider 全局启用，因为同一 Provider 下模型能力、价格和协议可能不同。

## 16. 插件与生命周期

deepseek-harness 的工具注册随插件 fiber dispose 自动注销。Pudding 的完整 PluginActivation、scope、依赖、trust、atomic reload 和 disposer 设计由 `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md` 定义；工具系统是第一个迁入该 Host 的 capability registry。

Registry 仍应提供 .NET 等价的显式 registration handle：

```csharp
IDisposable Register(ToolDefinition definition, ToolScope scope);
```

适用规则：

- Built-in 工具可保持 host 生命周期 singleton；
- Workspace/Manifest/MCP 工具注册返回 handle，reload 先构造并验证新集合，再原子 swap，最后 dispose 旧集合；
- 同 scope 重名直接失败，不做“最后注册者覆盖”；
- Agent scoped tool 可 shadow global tool，但 schema、lookup、execution 必须使用同一 scope 解析；
- `restrict` 只缩小 global 可见集合，不能当作最终安全边界；执行仍经过 firewall。

## 17. 分阶段实施

### P0-A：调用身份与事件闭环

涉及：

- `Source/PuddingCore/Tools/PuddingToolContracts.cs`
- `Source/PuddingRuntime/Tools/Platform/ToolInvocationService.cs`
- `Source/PuddingRuntime/Tools/Platform/PuddingToolRegistry.cs`
- `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs`
- `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Streaming.cs`

交付：删除执行层新 GUID；SSE/durable event/telemetry 全部携带同一 callId；实时与历史可按 id 配对。

### P0-B：Schema 与 canonical output

涉及：

- 扩展 `Source/PuddingCore/Models/ToolParameterSchema.cs` 或新增 Core Schema AST；
- 新增 input/output validator、lossless JSON materializer；
- `PuddingToolBase<TArgs, TResult>` 或 source-generated definition；
- Gateway 统一从 AST 投影 Provider schema。

先迁移 `search_tools`、`file_read`、`search_grep`、`terminal_start/wait/read`、`goal_read`，用它们覆盖 discovery、read、search、job 和状态查询五类合同。

### P0-C：结构化错误、Typed Hook 与执行管线

交付：统一错误 taxonomy；接入 `tool.arguments.transform`、`tool.before_execute` Guard、`tool.execute` Around 与 `tool.result.transform`；拆出 pre-policy/execute/post/finalize；AgentFirewall 接入；deadline、metrics、结果校验成为 wrapper；旧 string error adapter 仅存在于迁移窗口内。

### P1-A：输出 spill 与后台 Job

交付：`ISpillStore`、session-scoped local provider、post-execute spill policy、统一 `IJobRuntime`；迁移 shell/search/web/diagnostics 的散落截断逻辑。

### P1-B：并发调度与 presentation

交付：Buffered/Streaming 共用一个 tool-call scheduler；有界并发 + exclusive barrier；持久化 presentation meta；前端按 intent 渲染并通过 callId 回放。

### P2：DeepSeek Code Mode

交付：隔离 code runtime、确定性 typed SDK、`run_code`、嵌套调用事件、drain/cancel、Agent manifest 配置和 Native/Code A/B 报告。

### P3：PluginActivation 生命周期与全量迁移

交付：按上位架构实现 dependency graph、scope、effect-style registration、staging activation、atomic reload、drain/dispose；MCP 规范化 adapter；所有 first-party 工具强制 output schema，删除迁移期 string result adapter。

## 18. 文件级建议

| 层 | 建议文件/目录 | 职责 |
|---|---|---|
| Core | `Tools/Definitions/` | ToolDefinition、Schema AST、OutputDefinition、presentation vocabulary |
| Core | `Runtime/ToolInvocationContracts.cs` | immutable invocation、identity、result、error、deferred context |
| Runtime | `Tools/Execution/ToolExecutionPipeline.cs` | 固定阶段编排 |
| Runtime | `Tools/Execution/ToolPolicyStage.cs` | Firewall/approval/monotonic guards |
| Runtime | `Tools/Execution/ToolResultMaterializer.cs` | lossless JSON、output validation、render/finalize |
| Runtime | `Tools/Execution/ToolCallScheduler.cs` | parallel pool 和 exclusive barrier |
| Runtime | `Tools/Output/` | spill store、preview policy、retrieval reference |
| Runtime | `Tools/Jobs/` | 通用 background job runtime |
| Runtime | `Tools/CodeMode/` | SDK generation、isolated runtime、binding dispatch |
| Agent Loop | Buffered/Streaming partial | 只编排 turn，不再各自实现不同工具语义 |
| Platform | session/run archive projection | durable tool.call/result 与 presentation meta |
| Admin | chat process projection | 从 durable facts 构建 ToolCallViewModel |

保持分层约束：Runtime 不能引用 Platform；通用合同放 Core，数据库和 HTTP/SSE 实现放 Platform，Desktop 只消费 Loopback API/Bridge，不能承载 Agent 业务逻辑。

## 19. 验收矩阵

| 维度 | 必须通过的证据 |
|---|---|
| Schema | required/type/enum/nested/oneOf/additionalProperties 的正反例；错误含 JSON path |
| Output | 工具返回错误类型或错误形状时成为 `InvalidToolOutput`，不会写入伪成功历史 |
| Identity | Provider call → invocation → event → telemetry → archive → UI 的 callId 完全一致 |
| Policy | Ask/Deny/Guard 顺序固定；后置 hook 无法撤销 deny；Code Mode 内调同样受限 |
| Cancellation | 前台工具、并发组、Code worker、已发布 job 的生命周期分别符合合同 |
| Concurrency | safe calls 真正重叠；exclusive barrier 前后无越序；history 顺序稳定 |
| Spill | UTF-8 总预算不超限；full bytes/hash 可验证；失败 fallback 不篡改成功语义 |
| Replay | 实时与刷新后的 call/result/status/presentation 一致；旧事件降级 generic 不崩溃 |
| Cache | 相同 Agent/工具集连续请求的 schema 顺序、prefix hash 一致 |
| Discovery | `search_tools` canonical value 可直接被 Loop 消费；激活下一轮才生效 |
| MCP | text、structuredContent、image/content blocks、server error 均正确规范化 |
| DeepSeek smoke | Native 与 Both/Code 使用同一任务集，记录成功率、轮数、token、缓存、耗时 |

测试层次：

1. Core schema/property-based tests；
2. Runtime pipeline/scheduler/spill/job 单元测试；
3. 使用真实组合根的 transcript snapshot，固定 system prompt、工具 schema、call/result 序列；
4. Streaming 与 Buffered 行为一致性测试；
5. Admin replay projection 和 UI focused tests；
6. 用户明确选择测试 Agent/DataRoot 后执行真实 DeepSeek 可见 smoke。不得读取或复制 `D:\data` 中的 LLM Secret 绕过准入。

## 20. 成功门槛

P0 完成条件：

- 代表性 first-party 工具不再让调用方解析 Output prose；
- tool call 身份端到端一致；
- invalid args/output、permission、timeout、cancel 有稳定 code；
- Streaming/Buffered 使用相同的执行结果合同；
- prefix snapshot 对相同工具集稳定；
- 不破坏现有 AgentFirewall、动态工具发现和子代理预算合同。

Code Mode 进入默认候选的条件：

- 复杂多工具任务成功率不低于 Native；
- 中位 LLM round 或总延迟有明确改善；
- token 成本没有不可接受回退；
- 内部调用全部可审计、可取消、可 drain；
- 高权限工具无绕过路径。

## 21. 不建议做的事情

- 不把 deepseek-harness 整个插件容器或前端组件直接移植到 Pudding。
- 不在每个 LLM Gateway 分别维护一套 Tool Schema 语义。
- 不通过 prompt 要求模型“自行保证 JSON 正确”来替代 runtime validation。
- 不继续新增 `Success=true + {status:error}` 的领域错误格式。
- 不用字符串截断替代 spill，也不把 `D:\data` 当构建/测试输出目录。
- 不因 Code Mode 存在就允许代码进程直接访问宿主文件、网络或 Secret。
- 不把 `ConcurrencySafe` 等同于 `ReadOnly`，不对未声明工具乐观并发。
- 不为了尚不存在的历史数据保留长期双合同；开发阶段应完成 first-party 工具原地升级并删除迁移 adapter。

## 22. 最终建议

第一轮施工只做三个纵向切片：

1. 修复 callId 端到端身份，并让 ToolCall/ToolResult 事件可配对；
2. 建立 canonical output + structured error 基础合同；
3. 迁移 `search_tools` 与 Terminal 工具族，验证 typed discovery、后台 job、spill 和 UI projection 的完整闭环。

这三个切片完成后，再启用并发调度；最后以 DeepSeek 测试 Agent 对 `native` 和 `both` 做 A/B，决定 Code Mode 是否值得进入默认产品路径。
