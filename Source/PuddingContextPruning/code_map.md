# PuddingContextPruning CodeMAP

> 职责：**在花钱的 LLM 摘要之前，用零 LLM 的确定性手段先把载荷压下来**——去重、首尾摘要、参数截断，压不动就整份回退。
> 边界：只引用 `PuddingCore`（`ChatMessage` / `ToolCall` / `ChatRole` / `LlmTextPart`）；**不引用** `PuddingRuntime` / `PuddingHost` / `PuddingPlatform` / `PuddingCodeIntelligence` / `PuddingCodeIndex`（`ProjectReference` 只有一条，编译期强制）。
> 命名空间：`PuddingContextPruning` · 状态：S1–S5 已完成（S5：`PuddingRuntime.LlmRequestBudgetGuard.Prepare` 在 `CaptureLlmRequest` 之前消费，默认开启，`deterministicPruneEnabled=false` 逐字回退旧行为）

## 定位

`ContextBudgetManager` 是 `internal` 的，本组件**不**复用它的记账口径，自带 `ITextTokenCounter`；
两者数字不必相等——本组件只关心「这次改写值不值得」的相对判断（见 §不变量 4）。

## 关键文件

| 文件 | 关键类型 / 职责 | 何时读 |
|---|---|---|
| `DeterministicPayloadCompaction.cs` | `DeterministicPayloadCompaction.TryApply`：三段策略按序执行 + 尾部保护区 + 准入地板 | 想知道「改写发生在哪一步、为什么没改」 |
| `DeterministicPruneOptions.cs` | `DeterministicPruneOptions`（10 个阈值 + `Enabled`）与 `Validate()` fail-fast | 调阈值、写配置项、排查「为什么不触发」 |
| `PruneOutcome.cs` | `PruneNoOpReason` / `PruneStats` / `PruneOutcome` 三个只读结果类型 | 消费结果、落日志、写断言 |
| `TokenCounting.cs` | `ITextTokenCounter` · `CharacterRatioTokenCounter`（默认 4 字符/token）· `PruneTokenMath` | 换计数口径、解释 `BeforeTokens` 的来源 |
| `PuddingContextPruning.csproj` | 叶子工程（1 条 `ProjectReference` + `InternalsVisibleTo` 测试工程） | 查依赖面 |

## 不变量（改代码前先读这一节）

1. **受保护尾部不可改**：`tailStart = Count - ProtectedTailMessages`，且若 `messages[tailStart].Role == Tool` 就继续向前回退——否则会把一次工具调用和它的结果劈开，模型会收到「有结果没调用」的坏序列。
2. **配对不断**：`Assistant.ToolCalls` 的 `Id`/`Name` 在截断参数时原样保留，只裁 `ArgumentsJson` 的中间段；`Tool` 消息的 `ToolCallId` 从不改写。
3. **确定性**：不读时钟、不用 `Guid`、不依赖哈希遍历顺序——相同输入必须产出逐字节相同的结果（用例 `Two_Calls_On_Equal_Inputs_Produce_Byte_Identical_Output` 锁死）。
4. **reclaim 地板**：回收量 `< MinReclaimTokens` 或 `< 剪枝前总量 × MinReclaimRatio` 时**整份回退**。回退不是保守，是保护前缀缓存：为省 300 token 去重写整个前缀，下一轮缓存命中率掉下来的代价更高。
5. **入参不可变 + 实例身份**：入参列表与其元素永不被修改；任何 no-op 路径（含地板回退）都返回**入参列表的同一个实例**，改动时元素一律复用旧引用。`PruneOutcome.Changed => NoOpReason == None`。
6. **跳过不可逆内容**：`System` 消息、`IsContextSummary` 的摘要消息、带 `ContentParts` 的多模态工具结果一律不碰。
7. **无副作用**：不写盘、不读配置、不打日志、不发网络请求——纯内存变换，可任意重放。

## 策略顺序（不可交换）

去重（相同 tool+content 只留最新一份，旧份替换为带 `toolCallId` 指针的说明）→ 首尾摘要（超 `MinToolResultChars` 保留头 `ToolResultHeadChars` + 尾 `ToolResultTailChars`）→ 参数截断（超 `MinToolArgumentChars` 保留头 `ToolArgumentHeadChars`）→ 准入地板。
所有改写文本都以 `[deterministic-prune] ` 开头，便于在会话里反查与统计。

## 测试

`Source/PuddingContextPruningTests/`（独立测试工程，只引用本工程）：29 例 = 行为 17 + 配置校验 9 + 边界 3。
边界断言（S4）：探测器自检 + 进程实际加载程序集 + `*.deps.json` 依赖闭包三重校验，临时加一条反向引用即取红。

## 变更记录

见 `Docs/00_changelog/2026Year/10/2026-10-23-零LLM确定性载荷剪枝接入宿主.md`（修改日志不进 code_map）。
