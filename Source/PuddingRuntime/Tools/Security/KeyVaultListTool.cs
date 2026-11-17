using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Tools.Security;

/// <summary>
/// 只读列出 KeyVault 条目元数据：名称、描述、分类、标签、创建/更新时间。
/// <para>
/// 设计边界（有意为之）：<b>永不返回密钥值</b>。列表接口本身就不返回明文，
/// 本工具沿用同一纪律，并在输出里显式声明 <c>valuesReturned=false</c>，
/// 让调用方不可能误以为拿到了明文。
/// </para>
/// <para>
/// 不提供改值/删除：让对话可以改写或删除密钥，等于让模型能一次切断所有外部服务，
/// 属于需要人类明确授权的边界。描述/分类这类元数据由启动期种子（
/// <c>KeyVaultDescriptionSeeder</c>）和界面维护。
/// </para>
/// </summary>
[Tool(
    id: "keyvault_list",
    name: "List key vault entries",
    description: "只读列出密钥保管箱条目的元数据（名称、描述、分类、标签、创建/更新时间），可按关键词过滤。不返回任何密钥值，也不修改任何条目。Read-only metadata listing for the key vault; never returns secret values.",
    category: ToolCategory.Security,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 28)]
public sealed class KeyVaultListTool : PuddingToolBase<KeyVaultListArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IKeyVaultService _vault;

    public KeyVaultListTool(IKeyVaultService vault)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        KeyVaultListArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var keyword = args.Keyword?.Trim();
        var secrets = await _vault.ListSecretsAsync(ct);

        var items = secrets
            .Where(s => string.IsNullOrEmpty(keyword)
                        || (s.Name?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (s.Description?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (s.Category?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                        || s.Tags.Any(t => t.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new
            {
                name = s.Name,
                description = s.Description,
                category = s.Category,
                tags = s.Tags.ToArray(),
                createdAtUtc = s.CreatedAt.ToUniversalTime(),
                updatedAtUtc = s.UpdatedAt?.ToUniversalTime(),
            })
            .ToArray();

        var payload = new
        {
            total = secrets.Count,
            returned = items.Length,
            keyword = string.IsNullOrEmpty(keyword) ? null : keyword,
            valuesReturned = false,
            items,
        };

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(payload, JsonOptions));
    }
}

/// <summary><see cref="KeyVaultListTool"/> 参数：只读过滤，无必填项。</summary>
public sealed record KeyVaultListArgs
{
    /// <summary>可选关键词，匹配名称 / 描述 / 分类 / 标签。</summary>
    public string? Keyword { get; init; }
}
