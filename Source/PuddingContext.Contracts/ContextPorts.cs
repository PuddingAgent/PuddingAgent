using System.Collections.Immutable;

namespace PuddingContext.Contracts;

// PA-05D 上下文端口（BCL-only）：有序构建/裁剪/贡献校验；记忆数据库、Tokenizer/压缩模型通过端口注入。
// 原 canonical 当前用户围栏、历史顺序与失败诊断不得因公开 API 被弱化。

public enum ContextRole { System, User, Assistant, Tool }

public enum ContextSourceKind { Canonical, System, Memory, Tool, CallerSupplied }

public sealed record ContextMessage(string MessageId, ContextRole Role, string Content, ContextSourceKind Source,
    int Order, string? ContentHash = null);

/// <summary>An external/tool/memory contribution. It may never impersonate a user or system instruction.</summary>
public sealed record ContextContribution(string ContributionId, ContextSourceKind Source, ContextRole Role,
    string Content, int Priority, int MaxTokens, int LifetimeTurns, string? ContentHash, string? TargetTurnId,
    bool ExternalData);

public sealed record ContextAssemblyRequest(string WorkspaceId, string AgentId, string SessionId, string TurnId,
    string CurrentUserFenceHash, int MaxTokens, ImmutableArray<ContextMessage> CanonicalHistory,
    ImmutableArray<ContextContribution> Contributions)
{
    public bool IsValid => Valid(WorkspaceId) && Valid(AgentId) && Valid(SessionId) && Valid(TurnId)
        && Valid(CurrentUserFenceHash) && MaxTokens is >= 1 and <= 200_000
        && !CanonicalHistory.IsDefault && !Contributions.IsDefault;

    public static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
}

public sealed record ContextAssemblyResult(ImmutableArray<ContextMessage> Messages, int TokenEstimate,
    ImmutableArray<string> DroppedContributionIds, ImmutableArray<string> UsedContributionIds, bool Trimmed);

public interface IContextTokenizer
{
    ValueTask<int> CountAsync(string content, CancellationToken ct = default);
}

public static class ContextErrorCodes
{
    public const string RequestInvalid = "context.request_invalid";
    public const string FenceMissing = "context.fence_missing";
    public const string FenceMismatch = "context.fence_mismatch";
    public const string ContributionInvalid = "context.contribution_invalid";
    public const string ContributionImpersonation = "context.contribution_impersonation";
    public const string ContributionTooLarge = "context.contribution_too_large";
    public const string HashMismatch = "context.hash_mismatch";
    public const string BudgetExceeded = "context.budget_exceeded";
    public const string TokenizerUnavailable = "context.tokenizer_unavailable";
}

public sealed class ContextException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
