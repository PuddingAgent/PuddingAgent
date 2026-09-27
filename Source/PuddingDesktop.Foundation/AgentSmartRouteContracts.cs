namespace PuddingDesktop.Foundation;

/// <summary>One Smart sub-agent route slot. Core stores a route as the literal "{providerId}/{modelId}".</summary>
public sealed record SmartRoleSlot(string RoleId, string Title, string Description);

/// <summary>
/// Smart sub-agent model routing. Only role instances have this configuration in the product, and Core
/// validates the route format itself (<c>{providerId}/{modelId}</c>), so the opposite of "empty" is a
/// strictly formatted route, not a free label.
/// </summary>
public static class SmartRoleRoutes
{
    public const string Format = "{providerId}/{modelId}";

    public static IReadOnlyList<SmartRoleSlot> Roles { get; } =
    [
        new("explorer", "探索者 Explorer", "代码库检索与定位"),
        new("researcher", "研究员 Researcher", "资料研究与外部信息"),
        new("planner", "规划者 Planner", "任务拆解与计划"),
        new("reviewer", "审阅者 Reviewer", "变更审查与风险"),
        new("developer", "开发者 Developer", "编码实现"),
        new("deployer", "部署者 Deployer", "构建与发布"),
        new("tester", "测试者 Tester", "测试与验证"),
    ];

    public static SmartRoleSlot? Find(string roleId) =>
        Roles.FirstOrDefault(slot => string.Equals(slot.RoleId, roleId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Empty means "not routed". Splits on the first slash, matching Core's own parser.</summary>
    public static (string ProviderId, string ModelId)? Parse(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return null;
        var trimmed = route.Trim();
        var separator = trimmed.IndexOf('/');
        if (separator <= 0 || separator == trimmed.Length - 1) return null;
        var providerId = trimmed[..separator].Trim();
        var modelId = trimmed[(separator + 1)..].Trim();
        return providerId.Length == 0 || modelId.Length == 0 ? null : (providerId, modelId);
    }

    public static AgentModelChoice ToChoice(string? route)
    {
        var parsed = Parse(route);
        return parsed is null ? AgentModelChoice.None : new AgentModelChoice(parsed.Value.ProviderId, parsed.Value.ModelId);
    }

    public static string FromChoice(AgentModelChoice choice) =>
        choice.IsSet ? $"{choice.ProviderId}/{choice.ModelId}" : "";

    public static string Describe(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "未设置（使用该角色的默认模型）";
        var parsed = Parse(route);
        return parsed is null ? $"{route}（格式不是 {Format}）" : $"{parsed.Value.ProviderId} / {parsed.Value.ModelId}";
    }

    /// <summary>Form-level check mirroring Core's NormalizeSmartRoleModel.</summary>
    public static IReadOnlyList<string> Validate(string? route, string label)
    {
        if (string.IsNullOrWhiteSpace(route)) return [];
        var trimmed = route.Trim();
        var separator = trimmed.IndexOf('/');
        if (separator <= 0) return [$"{label}：必须使用 {Format} 格式。"];
        if (separator == trimmed.Length - 1) return [$"{label}：'/' 之后必须填写模型 ID。"];
        return [];
    }
}
