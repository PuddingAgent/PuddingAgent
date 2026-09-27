namespace PuddingDesktop.Foundation;

/// <summary>
/// Execution budgets and the container image override. Core applies these as loop budgets without
/// further clamping, so the form only rejects values that can never be meaningful (≤ 0).
/// </summary>
public sealed record AgentGuardrailPolicy(
    int MaxRounds,
    int MaxElapsedSeconds,
    int MaxToolCallsTotal,
    string ContainerImage)
{
    /// <summary>
    /// Core's own template defaults: 200 rounds / 86400 seconds / 400 tool calls (AgentTemplateFileService).
    /// The DTO-level literal of 100 is a stale placeholder and is not what the file service writes.
    /// </summary>
    public static AgentGuardrailPolicy Default { get; } = new(200, 86400, 400, "");

    public bool HasContainerImage => !string.IsNullOrWhiteSpace(ContainerImage);

    public string Describe() =>
        $"轮次 {MaxRounds} · 时长上限 {FormatDuration(MaxElapsedSeconds)} · 工具调用 {MaxToolCallsTotal} · " +
        (HasContainerImage ? ContainerImage : "无容器镜像覆盖");

    internal static string FormatDuration(int seconds) => seconds switch
    {
        < 60 => $"{seconds} 秒",
        < 3600 => $"{seconds / 60.0:0.#} 分钟",
        < 86400 => $"{seconds / 3600.0:0.#} 小时",
        _ => $"{seconds / 86400.0:0.#} 天"
    };
}

public static class AgentGuardrailText
{
    public const int ContainerImageMaxLength = 512;

    public static IReadOnlyList<string> Validate(AgentGuardrailPolicy policy)
    {
        var errors = new List<string>();
        if (policy.MaxRounds <= 0) errors.Add("最大轮次必须大于 0。");
        if (policy.MaxElapsedSeconds <= 0) errors.Add("最长运行时长必须大于 0 秒。");
        if (policy.MaxToolCallsTotal <= 0) errors.Add("工具调用总上限必须大于 0。");

        var image = policy.ContainerImage.Trim();
        if (image.Length > ContainerImageMaxLength)
            errors.Add($"容器镜像不能超过 {ContainerImageMaxLength} 个字符。");
        else if (image.Any(char.IsWhiteSpace))
            errors.Add("容器镜像不能包含空白字符。");
        return errors;
    }

    /// <summary>Empty text means "no override"; it is never silently turned into a default image.</summary>
    public static string NormalizeContainerImage(string? value) => (value ?? "").Trim();
}
