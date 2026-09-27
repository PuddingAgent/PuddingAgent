namespace PuddingDesktop.Foundation;

/// <summary>
/// A provider/model pair. Empty means "not set"; Core requires both halves or neither, so a half-filled
/// pair is a form error rather than something to guess at.
/// </summary>
public sealed record AgentModelChoice(string ProviderId, string ModelId)
{
    public static AgentModelChoice None { get; } = new("", "");
    public bool IsSet => ProviderId.Length > 0 && ModelId.Length > 0;
    public bool IsPartial => ProviderId.Length > 0 != ModelId.Length > 0;
}

public sealed record AgentModelCatalogEntry(
    string ProviderId, string ProviderName, string ModelId, string ModelName,
    bool IsEmbedding, bool IsEnabled, bool IsDeprecated);

public sealed record AgentModelPolicy(
    AgentModelChoice Chat,
    AgentModelChoice Memory,
    AgentModelChoice Embedding,
    string MemorySearchMode,
    string ReasoningEffort)
{
    public static AgentModelPolicy Default { get; } =
        new(AgentModelChoice.None, AgentModelChoice.None, AgentModelChoice.None,
            AgentModelPolicyText.DefaultMemorySearchMode, "");
}

public static class AgentModelPolicyText
{
    public const string DefaultMemorySearchMode = "deep";

    /// <summary>The documented memory-search modes. Core stores the string verbatim, so an unknown stored
    /// value is preserved and shown rather than silently rewritten to the default.</summary>
    public static IReadOnlyList<string> MemorySearchModes { get; } = ["off", "instant", "deep"];

    /// <summary>reasoningEffort is passed to the provider verbatim (low/medium/high, max, …), so it is
    /// free text, not a closed list this app gets to invent.</summary>
    public const int ReasoningEffortMaxLength = 64;

    public static string NormalizeMemorySearchMode(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DefaultMemorySearchMode : value.Trim();

    public static string NormalizeReasoningEffort(string? value) => (value ?? "").Trim();

    public static bool IsKnownMemorySearchMode(string? value) =>
        value is not null && MemorySearchModes.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Form-level checks against the live provider/model catalogue. Core re-validates when it resolves
    /// the profile, so passing here is not a guarantee.
    /// </summary>
    public static IReadOnlyList<string> Validate(AgentModelPolicy policy, IReadOnlyList<AgentModelCatalogEntry> catalog)
    {
        var errors = new List<string>();
        CheckChoice(policy.Chat, "默认对话模型", embedding: false, catalog, errors);
        CheckChoice(policy.Memory, "记忆模型", embedding: false, catalog, errors);
        CheckChoice(policy.Embedding, "Embedding 模型", embedding: true, catalog, errors);
        if (string.IsNullOrWhiteSpace(policy.MemorySearchMode)) errors.Add("记忆检索模式不能为空。");
        var effort = NormalizeReasoningEffort(policy.ReasoningEffort);
        if (effort.Length > ReasoningEffortMaxLength) errors.Add($"推理强度不能超过 {ReasoningEffortMaxLength} 个字符。");
        else if (effort.Any(char.IsWhiteSpace)) errors.Add("推理强度不能包含空白字符。");
        return errors;
    }

    public static string Describe(AgentModelChoice choice, IReadOnlyList<AgentModelCatalogEntry> catalog)
    {
        if (choice.IsPartial) return "未完成（服务商与模型必须同时指定）";
        if (!choice.IsSet) return "未设置（继承模板/默认）";
        var entry = Find(catalog, choice.ProviderId, choice.ModelId);
        return entry is null
            ? $"{choice.ProviderId} / {choice.ModelId}（目录中不存在）"
            : $"{entry.ProviderName} / {entry.ModelName}";
    }

    public static AgentModelCatalogEntry? Find(IReadOnlyList<AgentModelCatalogEntry> catalog, string providerId, string modelId) =>
        catalog.FirstOrDefault(entry =>
            string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<AgentModelChoice> ChoicesForProvider(
        IReadOnlyList<AgentModelCatalogEntry> catalog, string providerId, bool embedding) =>
        catalog.Where(entry => string.Equals(entry.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                               && entry.IsEmbedding == embedding)
            .Select(entry => new AgentModelChoice(entry.ProviderId, entry.ModelId))
            .ToArray();

    private static void CheckChoice(AgentModelChoice choice, string label, bool embedding,
        IReadOnlyList<AgentModelCatalogEntry> catalog, List<string> errors)
    {
        if (choice.IsPartial)
        {
            errors.Add($"{label}：服务商与模型必须同时指定，或同时留空表示继承。");
            return;
        }
        if (!choice.IsSet) return;
        var entry = Find(catalog, choice.ProviderId, choice.ModelId);
        if (entry is null)
        {
            errors.Add($"{label}：服务商 {choice.ProviderId} 下不存在模型 {choice.ModelId}。");
            return;
        }
        if (!entry.IsEnabled) errors.Add($"{label}：服务商 {entry.ProviderName} 已停用。");
        if (entry.IsDeprecated) errors.Add($"{label}：模型 {entry.ModelName} 已废弃。");
        if (embedding && !entry.IsEmbedding) errors.Add($"{label}：{entry.ModelName} 不是 embedding 模型。");
        if (!embedding && entry.IsEmbedding) errors.Add($"{label}：{entry.ModelName} 是 embedding 模型，不能用于对话或记忆。");
    }
}
