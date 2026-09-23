using System.Text.Json;
using PuddingCode.Tools.Definitions;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 工具展示投影（presentation）投影器：把一次工具调用的 canonical 参数/结果投影为可回放的
/// UI 元数据 wire JSON（<c>{"kind":"terminal","meta":{...}}</c>）。
/// <para>
/// 契约来源：<c>PuddingCore/Tools/Definitions/ToolDefinition.cs</c> §14 —— tool-owned presentation
/// （<see cref="ToolDefinition.Present"/> / <see cref="ToolPresentationIntent"/>）。本类型只做
/// 「读工具自己声明的 Present + 转 wire JSON」，**不做任何按 toolName 的硬编码分派**；
/// kind 词表与渲染器归属前端 registry（未注册 kind 一律回落 generic）。
/// </para>
/// <para>
/// fail-open 是硬要求：无 presenter / presenter 返回 null / presenter 抛异常 / 参数非法
/// ⇒ 一律降级 <c>generic</c>。展示投影失败**绝不允许**影响工具调用执行或事件产出。
/// </para>
/// </summary>
public interface IToolPresentationProjector
{
    /// <summary>
    /// 投影一次工具调用的展示意图。返回 wire JSON <c>{"kind":"..."}</c> 或
    /// <c>{"kind":"...","meta":{...}}</c>；meta 无事实时整键省略。实现必须 fail-open，永不抛出。
    /// </summary>
    /// <param name="toolId">工具 id（= 事件 payload 的 name）。</param>
    /// <param name="argumentsJson">本次调用的参数 JSON 原文；call 阶段与 result 阶段都应传入。</param>
    /// <param name="resultJson">结果 JSON 原文；call 阶段传 null。</param>
    JsonElement Project(string? toolId, string? argumentsJson, string? resultJson);
}

/// <summary>
/// 默认展示投影器：presenter 由 <see cref="ToolPresentationCatalog"/> 按工具 id 解析（工具侧就近声明）。
/// </summary>
public sealed class ToolPresentationProjector : IToolPresentationProjector
{
    /// <summary>降级意图：无 presenter / presenter 返回 null / 投影失败时的事件 projection。</summary>
    public static readonly JsonElement Generic = SerializeGeneric();

    /// <summary>生产默认实例（组合根注入用；也可由 Runtime 直接静态使用）。</summary>
    public static ToolPresentationProjector Default { get; } = new(ToolPresentationCatalog.TryResolve);

    private readonly Func<string, Func<ToolPresentationInput, ToolPresentationIntent?>?> _resolvePresenter;

    /// <param name="resolvePresenter">按工具 id 解析该工具的 Present 声明；无声明返回 null。</param>
    public ToolPresentationProjector(
        Func<string, Func<ToolPresentationInput, ToolPresentationIntent?>?> resolvePresenter)
    {
        _resolvePresenter = resolvePresenter
            ?? throw new ArgumentNullException(nameof(resolvePresenter));
    }

    /// <inheritdoc />
    public JsonElement Project(string? toolId, string? argumentsJson, string? resultJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(toolId))
                return Generic;

            var presenter = _resolvePresenter(toolId);
            if (presenter is null)
                return Generic;

            var intent = presenter(new ToolPresentationInput
            {
                Arguments = ToolPresentationJson.ParseObjectOrEmpty(argumentsJson),
                Result = ToolPresentationJson.TryParseResult(resultJson),
            });

            return ToWire(intent);
        }
        catch (Exception)
        {
            // fail-open：展示投影失败不得阻断工具执行与事件产出。
            return Generic;
        }
    }

    /// <summary>把 typed intent 写成 wire JSON（kind 小写词表；meta 为 null 时省略该键）。</summary>
    public static JsonElement ToWire(ToolPresentationIntent? intent)
    {
        if (intent is null)
            return Generic;

        var kind = ToWireKind(intent.Kind);
        if (intent.Meta is not { ValueKind: JsonValueKind.Object } meta)
        {
            return kind == "generic"
                ? Generic
                : JsonSerializer.SerializeToElement(new { kind });
        }

        return JsonSerializer.SerializeToElement(new { kind, meta });
    }

    /// <summary>Core 枚举 → 前端八类小写词表（未注册值一律 generic，绝不泄漏 .NET 枚举名）。</summary>
    public static string ToWireKind(ToolPresentationIntentKind kind) => kind switch
    {
        ToolPresentationIntentKind.Terminal => "terminal",
        ToolPresentationIntentKind.Diff => "diff",
        ToolPresentationIntentKind.Search => "search",
        ToolPresentationIntentKind.Read => "read",
        ToolPresentationIntentKind.Web => "web",
        ToolPresentationIntentKind.Delegation => "delegation",
        ToolPresentationIntentKind.Job => "job",
        _ => "generic",
    };

    private static JsonElement SerializeGeneric() =>
        JsonSerializer.SerializeToElement(new { kind = "generic" });
}
