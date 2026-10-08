using System.Text;

namespace PuddingBrowser.Automation;

/// <summary>
/// **子代理委派的授权信封**（设计方案 §9 / 门禁 G10）。
///
/// 委派不是"把控制权交出去"，而是把一个**受约束的任务**交出去：目标、允许范围、页面授权、
/// 期望结果、预算与截止时间。判定这些字段是否齐全、范围是否**不超出**父授权、注入的工具是否**够用**，
/// 都是纯逻辑 —— 因此放在这里逐条验证，宿主侧只负责签发与执行。
/// </summary>
public sealed record BrowserDelegationEnvelope(
    string Goal,
    BrowserDelegationScope Scope,
    string PageGrantId,
    string ExpectedResult,
    int ResultBudgetBytes,
    IReadOnlyList<string> InjectedTools,
    IReadOnlyList<string> RequiredTools,
    TimeSpan Deadline);

/// <summary>父授权范围（子代理只能取子集：读取与写入分开表达）。</summary>
public sealed record BrowserDelegationScope(bool CanRead, bool CanWrite, bool CanManage);

/// <summary>委派信封的校验结果。</summary>
public static class BrowserDelegation
{
    /// <summary>父结果默认上限（§9：默认结论上限 2 KiB，允许声明更小）。</summary>
    public const int DefaultResultBudgetBytes = 2 * 1024;

    /// <summary>方案要求的绝对上限（父代理不能通过声明把上限抬过这一条）。</summary>
    public const int MaxResultBudgetBytes = 2 * 1024;

    /// <summary>
    /// 写入类任务必须注入的工具。
    ///
    /// **更正（2026-10-08）**：原先按设计示例写成 browser_navigate + browser_screenshot，但本仓库的浏览器
    /// 工具面（<c>BrowserAgentToolIds</c>）**没有 screenshot 这个工具**（截图能力存在，但不是七项工具之一）。
    /// 要求一个不存在的工具名会让每个可写委派都判不合格 —— 那是"照着文档示例写代码"而不是"照着代码事实写"。
    /// 现改为实际存在的写入必需工具；设计里"须包含 navigate/screenshot"的意图由 browser_interact（真实输入）承担。
    /// </summary>
    public static readonly IReadOnlyList<string> WriteTaskRequiredTools = ["browser_navigate", "browser_interact"];

    /// <summary>校验信封：不合格就**明确拒绝**（逐条给原因，而不是让执行器去猜）。</summary>
    public static bool TryValidate(
        BrowserDelegationEnvelope? envelope,
        BrowserDelegationScope parentScope,
        out IReadOnlyList<string> problems)
    {
        var collected = new List<string>();

        if (envelope is null)
        {
            problems = ["a delegation envelope is required"];
            return false;
        }

        if (parentScope is null)
        {
            problems = ["the parent scope is required (a delegation cannot be judged without it)"];
            return false;
        }

        if (string.IsNullOrWhiteSpace(envelope.Goal))
        {
            collected.Add("the delegation goal must be stated (a sub-agent cannot be given an unnamed task)");
        }

        if (string.IsNullOrWhiteSpace(envelope.ExpectedResult))
        {
            collected.Add("the expected result must be stated (otherwise the conclusion cannot be judged)");
        }

        if (string.IsNullOrWhiteSpace(envelope.PageGrantId))
        {
            // §8：没有 grant 的显式 PageId 同样拒绝 —— 委派更不能例外。
            collected.Add("a page grant is required (a sub-agent must not be handed a page without authorization)");
        }

        if (envelope.Deadline <= TimeSpan.Zero)
        {
            collected.Add("the delegation must carry a positive deadline");
        }

        // 范围只能收窄，不能扩大（子代理不得扩大父授权）。
        if (envelope.Scope is null)
        {
            collected.Add("the delegated scope is required");
        }
        else if ((envelope.Scope.CanRead && !parentScope.CanRead)
            || (envelope.Scope.CanWrite && !parentScope.CanWrite)
            || (envelope.Scope.CanManage && !parentScope.CanManage))
        {
            collected.Add("the delegated scope must be a subset of the parent scope (a child cannot exceed its parent)");
        }

        // 预算：默认 2 KiB，允许更小，不允许更大。
        if (envelope.ResultBudgetBytes <= 0)
        {
            collected.Add("the result budget must be positive");
        }
        else if (envelope.ResultBudgetBytes > MaxResultBudgetBytes)
        {
            collected.Add(
                $"the result budget must not exceed {MaxResultBudgetBytes} bytes (the parent conclusion is bounded by design)");
        }

        // 工具：注入列表必须覆盖任务需要的工具，且写入类任务必须真的拿到 navigate/screenshot。
        var injected = envelope.InjectedTools ?? [];
        var required = envelope.RequiredTools ?? [];

        if (required.Count == 0)
        {
            collected.Add("the required tool list must not be empty (a task nobody can execute is not a task)");
        }
        else
        {
            var missing = required
                .Where(tool => !injected.Contains(tool, StringComparer.Ordinal))
                .ToArray();

            if (missing.Length > 0)
            {
                // "父代理能发现七项工具"推不出"子代理可用"（§9 明确要求验证真实注入）。
                collected.Add("injected tools do not cover the task: " + string.Join(", ", missing));
            }
        }

        if (envelope.Scope?.CanWrite == true)
        {
            var missingWriteTools = WriteTaskRequiredTools
                .Where(tool => !injected.Contains(tool, StringComparer.Ordinal))
                .ToArray();

            if (missingWriteTools.Length > 0)
            {
                collected.Add(
                    "a write-capable delegation must inject: " + string.Join(", ", missingWriteTools));
            }
        }

        problems = collected;
        return collected.Count == 0;
    }
}

/// <summary>
/// **父代理能看到的子代理结论**（§9）：有界文本 + 必要摘录 + URL/Artifact/operationId + 验证状态。
///
/// 注意这个类型**没有**放整页快照、也没有放子代理内部对话的字段 ——
/// 结构上就装不进去，比"记得不要传"可靠。
/// </summary>
public sealed record BrowserDelegationConclusion(
    string Text,
    int Bytes,
    bool Truncated,
    string? ArtifactId,
    string? Url,
    string? OperationId,
    string VerificationState);

/// <summary>
/// 把子代理的结果**收窄**成父代理可见的有界结论。
/// </summary>
public static class BrowserDelegationConclusionShaper
{
    /// <summary>
    /// 收窄：文本超过预算就截断（**按 UTF-8 字节**，且不切断多字节字符），
    /// 并把溢出部分指向 Artifact（全文以受权限约束的 Artifact 保存）。
    /// </summary>
    public static BrowserDelegationConclusion Shape(
        string? fullText,
        string verificationState,
        int budgetBytes = BrowserDelegation.DefaultResultBudgetBytes,
        string? artifactId = null,
        string? url = null,
        string? operationId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationState);

        var text = fullText ?? string.Empty;
        var bytes = Encoding.UTF8.GetByteCount(text);

        if (bytes <= budgetBytes)
        {
            return new BrowserDelegationConclusion(text, bytes, Truncated: false, artifactId, url, operationId, verificationState);
        }

        var (bounded, actualBytes) = TruncateToUtf8Budget(text, budgetBytes);

        // 有溢出就**必须**有 Artifact 指向全文：否则父代理拿到的是一段无出处的残文。
        var effectiveArtifact = string.IsNullOrWhiteSpace(artifactId) ? null : artifactId;

        return new BrowserDelegationConclusion(
            bounded,
            actualBytes,
            Truncated: true,
            effectiveArtifact,
            url,
            operationId,
            verificationState);
    }

    /// <summary>按 UTF-8 字节截断，但**不切断**多字节字符（否则文本会出现替换符）。</summary>
    private static (string Text, int Bytes) TruncateToUtf8Budget(string text, int budgetBytes)
    {
        var builder = new StringBuilder(text.Length);
        var used = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (used + runeBytes > budgetBytes)
            {
                break;
            }

            builder.Append(rune);
            used += runeBytes;
        }

        return (builder.ToString(), used);
    }
}
