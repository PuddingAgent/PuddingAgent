namespace PuddingCodeIndex.Contracts;

public enum CodeIndexStatus
{
    Unknown,
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Outcome of one indexing request. <c>LanguageOutcomes</c> is the additive, optional per-language detail
/// of an aggregate run: it stays <c>null</c> for an indexer that is not an aggregate, and the trailing
/// default keeps every existing construction site compiling unchanged.
/// </summary>
public sealed record CodeIndexResult(
    bool Success,
    CodeIndexStatus Status,
    string? Message = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    string? WorkspaceId = null,
    string? ProjectId = null,
    IReadOnlyList<LanguageIndexOutcome>? LanguageOutcomes = null);

/// <summary>
/// Per-language outcome of an aggregate (multi-language) run: which languages ran, which of them succeeded
/// and why the others did not — the fact that answers "did TypeScript actually get indexed?".
/// </summary>
/// <param name="Language">Label of the language, e.g. <c>"C#"</c>.</param>
/// <param name="Success">Whether that language completed its share of the run.</param>
/// <param name="Status">Status that language reported.</param>
/// <param name="Message">Diagnostic that language reported, if any.</param>
public sealed record LanguageIndexOutcome(
    string Language,
    bool Success,
    CodeIndexStatus Status,
    string? Message = null);
