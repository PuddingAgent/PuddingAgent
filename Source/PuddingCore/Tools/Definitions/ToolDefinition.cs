using System.Text.Json;
using PuddingCode.Models;

namespace PuddingCode.Tools.Definitions;

/// <summary>
/// 工具可回放展示意图的 provider-neutral 词汇表。
/// <para>
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §14（Tool-owned presentation 与回放）：
/// <c>generic | terminal | diff | search | read | web | delegation | job</c>。
/// </para>
/// <para>
/// intent 只包含可持久化数据，不包含 React node、CSS 类或本地组件实例；
/// UI 不按 toolName 选择卡片，older/invalid args 只降级到 <see cref="Generic"/>。
/// </para>
/// </summary>
public enum ToolPresentationIntentKind
{
    /// <summary>通用卡片，所有工具的兜底投影。</summary>
    Generic,

    /// <summary>终端命令输出（start/wait/cancel 生命周期）。</summary>
    Terminal,

    /// <summary>文件差异/补丁。</summary>
    Diff,

    /// <summary>搜索结果集合。</summary>
    Search,

    /// <summary>读取/查看内容。</summary>
    Read,

    /// <summary>网页浏览。</summary>
    Web,

    /// <summary>子代理委托。</summary>
    Delegation,

    /// <summary>后台任务/作业。</summary>
    Job,
}

/// <summary>
/// Present 投影产出的可回放意图。Kind 决定卡片族，Meta 携带该卡片族需要的 result-time 持久化元数据。
/// <para>
/// 契约来源：§14 —— 需要 result-time 信息的卡片通过 <c>PresentationMeta</c> 持久化；
/// 实时 SSE 与历史 run archive 复用同一 <see cref="ToolPresentationIntent"/>。
/// </para>
/// </summary>
public sealed record ToolPresentationIntent
{
    /// <summary>卡片族。</summary>
    public required ToolPresentationIntentKind Kind { get; init; }

    /// <summary>卡片族专属的可持久化元数据（如 terminal job id、diff 行范围）。</summary>
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// Present 纯函数的输入：一次调用的 canonical 参数与（可选）canonical 结果。
/// <para>
/// 契约来源：§14 —— <c>PresentCall(args)</c> 与 <c>PresentResult(args, durable result)</c> 必须是纯函数；
/// 需要 result-time 信息的卡片通过 <see cref="ToolPresentationIntent.Meta"/> 持久化。
/// </para>
/// </summary>
public sealed record ToolPresentationInput
{
    /// <summary>本次调用的 canonical 参数 JSON。</summary>
    public required JsonElement Arguments { get; init; }

    /// <summary>本次调用的 canonical 结果 JSON；call 阶段投影时为 null。</summary>
    public JsonElement? Result { get; init; }
}

/// <summary>
/// renderer 产出的模型可见内容。
/// <para>
/// 契约来源：§6 —— 工具主体只返回 canonical JSON value，不返回 Markdown 卡片；
/// renderer 只负责模型可见内容，presentation projector 只负责可回放 UI 元数据。
/// </para>
/// </summary>
public sealed record ToolContent
{
    /// <summary>以结构化 canonical JSON 值作为模型内容。</summary>
    public JsonElement? Canonical { get; init; }

    /// <summary>以文本作为模型内容。</summary>
    public string? Text { get; init; }

    /// <summary>构造纯文本模型内容。</summary>
    public static ToolContent FromText(string text) => new() { Text = text };

    /// <summary>构造结构化 canonical 模型内容。</summary>
    public static ToolContent FromCanonical(JsonElement value) => new() { Canonical = value };
}

/// <summary>
/// Tool 的权限事实源（risk facts）。复用既有枚举，不引入新权限层级，
/// 仅作为 <see cref="ToolDefinition"/> 的权限载荷，与现有 <see cref="ToolDescriptor"/> 语义对齐。
/// </summary>
public sealed record ToolPermissionFacts
{
    /// <summary>权限分级（自动授权 / 需 Agent 配置 / 需用户运行时确认）。</summary>
    public ToolPermissionLevel PermissionLevel { get; init; } = ToolPermissionLevel.Medium;

    /// <summary>安全特征（只读 / 并发安全 / 破坏性 / 需 shell / 需写文件 / 需网络）。</summary>
    public ToolSafetyFlags Safety { get; init; } = ToolSafetyFlags.None;

    /// <summary>子代理暴露策略。</summary>
    public SubAgentExposure SubAgentExposure { get; init; } = SubAgentExposure.Default;

    /// <summary>展示/过滤用分类。</summary>
    public ToolCategory Category { get; init; } = ToolCategory.General;
}

/// <summary>
/// 工具输出合同：output schema + 模型渲染器 + 可选的可回放展示元数据投影器。
/// <para>
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6 ——
/// first-party 工具必须声明 input 和 output schema；renderer 只负责模型可见内容，
/// presentation projector 只负责可回放 UI 元数据。
/// </para>
/// </summary>
public sealed record ToolOutputDefinition
{
    /// <summary>工具输出的 canonical JSON 结构约束。</summary>
    public required JsonSchema Schema { get; init; }

    /// <summary>
    /// 把 canonical 结果渲染为模型可见内容。入参为 (arguments, result) 两个 canonical JSON 值。
    /// </summary>
    public required Func<JsonElement, JsonElement, ToolContent> Render { get; init; }

    /// <summary>
    /// 把 (arguments, result) 投影为可持久化 UI 元数据；null 表示该工具不需要 result-time 元数据。
    /// </summary>
    public Func<JsonElement, JsonElement, JsonElement?>? BuildPresentationMeta { get; init; }
}

/// <summary>
/// 新工具定义合同：声明模型协议和执行策略的事实源。
/// <para>
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6 ——
/// ToolDefinition（Id/Description/InputSchema/Output/权限/超时/IsConcurrencySafe/Present）。
/// </para>
/// <para>
/// 渐进迁移哲学：与现有 <see cref="ToolDescriptor"/> 并存，不原地改造；P0-C 收口时交付迁移清单。
/// 该类型属于工具域合同，落在 Core <c>Tools/Definitions/</c>，与 Schema AST 同级配套。
/// </para>
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>稳定工具 id。注册时校验合法性。</summary>
    public required string Id { get; init; }

    /// <summary>人类可读描述，用于模型/UI 展示。</summary>
    public required string Description { get; init; }

    /// <summary>工具输入的 canonical JSON 结构约束。first-party 工具必须声明。</summary>
    public required JsonSchema InputSchema { get; init; }

    /// <summary>工具输出合同（output schema + renderer + presentation projector）。</summary>
    public required ToolOutputDefinition Output { get; init; }

    /// <summary>权限事实源，默认按保守策略（Medium/None/Default/General）。</summary>
    public ToolPermissionFacts Permission { get; init; } = new();

    /// <summary>工具级超时（host metadata，不发送给模型）。</summary>
    public TimeSpan? DefaultTimeout { get; init; }

    /// <summary>
    /// 并发安全分类器：给定 canonical 参数返回该调用是否可与其他工具并发执行。
    /// 这是 host metadata，不发送给模型；null 表示不提供判定（按保守串行处理）。
    /// </summary>
    public Func<JsonElement, bool>? IsConcurrencySafe { get; init; }

    /// <summary>
    /// 可回放展示投影器：把 <see cref="ToolPresentationInput"/> 投影为 <see cref="ToolPresentationIntent"/>。
    /// null 表示降级到 <see cref="ToolPresentationIntentKind.Generic"/> 卡片。
    /// </summary>
    public Func<ToolPresentationInput, ToolPresentationIntent?>? Present { get; init; }

    /// <summary>返回该定义是否满足 first-party 合同（Id/Description/input+output schema/render 齐全）。</summary>
    public bool IsValid => Validate().Count == 0;

    /// <summary>
    /// 校验定义合法性，返回 <c>$.xxx: message</c> 格式的路径错误列表；空列表表示通过。
    /// <para>
    /// 对应 §6 约束 1：first-party 工具必须声明 input 和 output schema，注册时即校验定义合法性。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add("$.id: required property is missing");
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            errors.Add("$.description: required property is missing");
        }

        if (InputSchema is null)
        {
            errors.Add("$.input_schema: required property is missing");
        }

        if (Output is null)
        {
            errors.Add("$.output: required property is missing");
            return errors;
        }

        if (Output.Schema is null)
        {
            errors.Add("$.output.schema: required property is missing");
        }

        if (Output.Render is null)
        {
            errors.Add("$.output.render: required property is missing");
        }

        return errors;
    }
}
