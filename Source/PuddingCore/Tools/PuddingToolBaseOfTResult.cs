using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Tools.Definitions;

namespace PuddingCode.Tools;

/// <summary>
/// 双泛型强类型结果 Tool 基类。派生类实现 <see cref="ExecuteCoreAsync"/> 返回强类型 canonical
/// 结果（<typeparamref name="TResult"/>），基类统一负责参数反序列化、lossless JSON materialize、
/// output schema 校验与 renderer 处理，最终产出 <see cref="ToolExecutionResult"/>。
/// <para>
/// 与单泛型 <see cref="PuddingToolBase{TArgs}"/> 的区别：业务主体不再手写
/// <c>JsonSerializer.Serialize</c> + <c>ToolExecutionResult.Ok</c>，而是返回强类型结果，
/// 由基类按 <see cref="ToolResultMaterializer"/> 的 lossless 语义统一序列化，保证输出契约一致。
/// </para>
/// </summary>
public abstract class PuddingToolBase<TArgs, TResult> : IPuddingTool
    where TArgs : class
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public ToolDescriptor Descriptor { get; }

    protected PuddingToolBase()
    {
        Descriptor = ToolDescriptorFactory.Create(GetType(), typeof(TArgs));
    }

    /// <summary>
    /// P0-B-4 最小化：默认 <c>null</c>，表示跳过 output schema 校验和 renderer，
    /// 直接以 materialize 后的 canonical JSON 作为输出。
    /// 派生类需要 output schema 校验与 renderer 时重写此属性返回完整的 <see cref="ToolDefinition"/>。
    /// </summary>
    protected virtual ToolDefinition? Definition => null;

    protected virtual IDisposable? BeginExecutionScope(ToolExecutionRequest request)
        => null;

    protected abstract Task<TResult> ExecuteCoreAsync(
        TArgs args,
        ToolExecutionContext context,
        CancellationToken ct);

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
    {
        using var scope = BeginExecutionScope(request);
        try
        {
            var args = DeserializeArgs(NormalizeArgumentsJson(request.ArgumentsJson));
            var result = await ExecuteCoreAsync(args, request.Context, ct);
            return MaterializeResult(args, result);
        }
        catch (JsonException ex)
        {
            return ToolExecutionResult.Fail(BuildInvalidArgumentsJsonError(ex, request.ArgumentsJson));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Normalizes a tool-specific compatibility payload before the shared typed deserializer runs.
    /// Implementations must keep the descriptor schema canonical and use this hook only at the
    /// execution boundary.
    /// </summary>
    protected virtual string? NormalizeArgumentsJson(string? argumentsJson)
        => argumentsJson;

    private ToolExecutionResult MaterializeResult(TArgs args, TResult result)
    {
        var canonical = ToolResultMaterializer.Materialize(result);
        var def = Definition;

        if (def?.Output?.Schema is not null)
        {
            var errors = def.Output.Schema.Validate(canonical);
            if (errors.Count > 0)
            {
                return ToolExecutionResult.Fail("Tool output invalid: " + string.Join("; ", errors));
            }
        }

        if (def?.Output?.Render is not null)
        {
            var argsEl = ToolResultMaterializer.Materialize(args);
            var content = def.Output.Render(argsEl, canonical);
            return ToolExecutionResult.Ok(
                content.Text ?? (content.Canonical?.GetRawText() ?? canonical.GetRawText()));
        }

        return ToolExecutionResult.Ok(canonical.GetRawText());
    }

    private static TArgs DeserializeArgs(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return Activator.CreateInstance<TArgs>();

        using var doc = JsonDocument.Parse(argumentsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return JsonSerializer.Deserialize<TArgs>(argumentsJson, s_jsonOptions)
                   ?? Activator.CreateInstance<TArgs>();

        var normalized = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            normalized[prop.Name] = prop.Value.Clone();
            normalized[ToPascalCase(prop.Name)] = prop.Value.Clone();
        }

        var json = JsonSerializer.Serialize(normalized, s_jsonOptions);
        return JsonSerializer.Deserialize<TArgs>(json, s_jsonOptions)
               ?? Activator.CreateInstance<TArgs>();
    }

    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return name;
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    private static string BuildInvalidArgumentsJsonError(JsonException ex, string? argumentsJson)
    {
        var fieldHint = TryInferJsonField(argumentsJson, ex.LineNumber) is { Length: > 0 } field
            ? $" Near field '{field}'."
            : "";
        return "Tool arguments must be valid JSON object. String values must be wrapped in double quotes; " +
               "for example: {\"rollback_plan\": \"No rollback is required.\"}." +
               fieldHint +
               $" JSON parser error: {ex.Message}";
    }

    private static string? TryInferJsonField(string? argumentsJson, long? lineNumber)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || lineNumber is null)
            return null;

        var lines = argumentsJson.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var index = (int)Math.Clamp(lineNumber.Value, 0, lines.Length - 1);
        for (var i = index; i >= 0; i--)
        {
            var match = Regex.Match(lines[i], "\"(?<name>[^\"]+)\"\\s*:");
            if (match.Success)
                return match.Groups["name"].Value;
        }

        return null;
    }
}
