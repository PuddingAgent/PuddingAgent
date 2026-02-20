using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Models;

namespace PuddingCode.Skills;

/// <summary>
/// Discovers [PuddingSkill]-annotated methods via reflection, generates JSON Function Schemas,
/// enforces role-based access, and dispatches invocations.
/// </summary>
public sealed partial class SkillRegistry : ISkillRegistry
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<string, SkillEntry> _skills = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public void Register(object skillInstance)
    {
        ArgumentNullException.ThrowIfNull(skillInstance);

        var type = skillInstance.GetType();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = method.GetCustomAttribute<PuddingSkillAttribute>();
            if (attr is null) continue;

            var name = ToSnakeCase(method.Name);
            var parameters = BuildParameterSchema(method);

            var entry = new SkillEntry
            {
                Name = name,
                Description = attr.Description,
                Group = attr.Group,
                AllowedRoles = attr.AllowedRoles,
                Parameters = parameters,
                Method = method,
                Instance = skillInstance
            };

            _skills[name] = entry;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<SkillEntry> GetSkills(AgentRole role)
        => _skills.Values.Where(s => s.IsAllowed(role)).ToList();

    /// <inheritdoc/>
    public IReadOnlyList<SkillEntry> GetAllSkills()
        => [.. _skills.Values];

    /// <inheritdoc/>
    public SkillEntry? FindSkill(string name)
        => _skills.GetValueOrDefault(name);

    /// <inheritdoc/>
    public IReadOnlyList<SkillEntry> SearchSkills(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return GetAllSkills();

        return _skills.Values
            .Where(s => s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                     || s.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                     || s.Group.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<SkillResult> ExecuteAsync(
        string skillName, string argumentsJson, AgentRole callerRole, CancellationToken ct = default)
    {
        if (!_skills.TryGetValue(skillName, out var entry))
            return SkillResult.Error($"Unknown skill: '{skillName}'.");

        if (!entry.IsAllowed(callerRole))
            return SkillResult.PermissionDenied(skillName, callerRole);

        try
        {
            var args = BuildInvocationArgs(entry.Method, argumentsJson, ct);
            var result = entry.Method.Invoke(entry.Instance, args);

            // Handle async methods
            var output = result switch
            {
                Task<string> taskStr => await taskStr,
                Task task => await AwaitAndReturnAsync(task),
                string str => str,
                null => "OK",
                _ => result.ToString() ?? "OK"
            };

            return SkillResult.Ok(output ?? "OK");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return SkillResult.Error($"Skill '{skillName}' failed: {ex.InnerException.Message}");
        }
        catch (Exception ex)
        {
            return SkillResult.Error($"Skill '{skillName}' failed: {ex.Message}");
        }
    }

    // ── Schema generation ──

    /// <summary>Builds a ToolParameterSchema from the method's parameters.</summary>
    private static ToolParameterSchema BuildParameterSchema(MethodInfo method)
    {
        var properties = new List<ToolParameter>();
        var required = new List<string>();

        foreach (var param in method.GetParameters())
        {
            // Skip CancellationToken — injected at invocation time, not by LLM
            if (param.ParameterType == typeof(CancellationToken))
                continue;

            var desc = param.GetCustomAttribute<SkillParamAttribute>()?.Description
                       ?? param.Name ?? "parameter";
            var jsonType = MapClrTypeToJsonType(param.ParameterType);
            var name = ToSnakeCase(param.Name ?? "arg");

            properties.Add(new ToolParameter(name, jsonType, desc));

            // Nullable or optional params are not required
            if (!IsOptionalParam(param))
                required.Add(name);
        }

        return new ToolParameterSchema(properties, required);
    }

    /// <summary>Maps CLR types to JSON Schema type strings.</summary>
    private static string MapClrTypeToJsonType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string)) return "string";
        if (underlying == typeof(int) || underlying == typeof(long)
            || underlying == typeof(short) || underlying == typeof(byte)) return "integer";
        if (underlying == typeof(double) || underlying == typeof(float)
            || underlying == typeof(decimal)) return "number";
        if (underlying == typeof(bool)) return "boolean";

        return "string"; // Fallback: serialize as string
    }

    private static bool IsOptionalParam(ParameterInfo param)
        => param.HasDefaultValue
           || Nullable.GetUnderlyingType(param.ParameterType) is not null
           || (param.ParameterType.IsClass && param.GetCustomAttribute<System.Runtime.CompilerServices.NullableAttribute>() is not null);

    // ── Invocation ──

    /// <summary>Deserializes JSON args and maps them to the method's parameter array.</summary>
    private static object?[] BuildInvocationArgs(MethodInfo method, string argumentsJson, CancellationToken ct)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];

        Dictionary<string, JsonElement>? jsonArgs = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            jsonArgs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson, s_jsonOptions);
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];

            if (param.ParameterType == typeof(CancellationToken))
            {
                args[i] = ct;
                continue;
            }

            var snakeName = ToSnakeCase(param.Name ?? "arg");

            if (jsonArgs is not null && TryGetJsonValue(jsonArgs, snakeName, param.Name, out var element))
            {
                args[i] = DeserializeElement(element, param.ParameterType);
            }
            else if (param.HasDefaultValue)
            {
                args[i] = param.DefaultValue;
            }
            else
            {
                args[i] = param.ParameterType.IsValueType
                    ? Activator.CreateInstance(param.ParameterType)
                    : null;
            }
        }

        return args;
    }

    private static bool TryGetJsonValue(
        Dictionary<string, JsonElement> dict, string snakeName, string? originalName, out JsonElement value)
    {
        // Try snake_case first, then original camelCase name
        if (dict.TryGetValue(snakeName, out value)) return true;
        if (originalName is not null && dict.TryGetValue(originalName, out value)) return true;
        return false;
    }

    private static object? DeserializeElement(JsonElement element, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (element.ValueKind == JsonValueKind.Null) return null;
        if (underlying == typeof(string)) return element.GetString();
        if (underlying == typeof(int)) return element.GetInt32();
        if (underlying == typeof(long)) return element.GetInt64();
        if (underlying == typeof(double)) return element.GetDouble();
        if (underlying == typeof(bool)) return element.GetBoolean();

        // Fallback: deserialize via JSON
        return JsonSerializer.Deserialize(element.GetRawText(), targetType, s_jsonOptions);
    }

    private static async Task<string> AwaitAndReturnAsync(Task task)
    {
        await task;

        // If Task<T>, extract the result via reflection
        var taskType = task.GetType();
        if (taskType.IsGenericType)
        {
            var resultProp = taskType.GetProperty("Result");
            var result = resultProp?.GetValue(task);
            return result?.ToString() ?? "OK";
        }

        return "OK";
    }

    // ── Naming ──

    /// <summary>Converts PascalCase method names to snake_case for LLM function names.</summary>
    private static string ToSnakeCase(string name)
        => SnakeCaseRegex().Replace(name, "_$1").TrimStart('_').ToLowerInvariant();

    [GeneratedRegex("([A-Z])")]
    private static partial Regex SnakeCaseRegex();
}
