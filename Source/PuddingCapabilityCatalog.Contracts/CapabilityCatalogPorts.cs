using System.Collections.Immutable;

namespace PuddingCapabilityCatalog.Contracts;

// PA-05 只读目录端口（BCL-only）：按 kind 读取受权能力页。缺权限以 Partial/MissingReason 表达，
// 不能被折叠成"空目录 = 没有能力"；generation 由目录源提供，用于执行前复核。

public sealed record CapabilityRecord(string CapabilityId, string Kind, string DisplayName, string Revision,
    string Generation, string Availability, string? MissingReason, bool RequiresApproval,
    ImmutableArray<string> RequiredCapabilities);

public sealed record CapabilityPage(ImmutableArray<CapabilityRecord> Capabilities, string? Cursor, bool Complete,
    bool Partial, string Generation, string? MissingReason);

public interface ICapabilitySource
{
    ValueTask<CapabilityPage> ListAsync(string workspaceId, string kind, string? cursor, int maxCount,
        CancellationToken ct = default);
}

public static class CapabilityErrorCodes
{
    public const string RequestInvalid = "capability.request_invalid";
    public const string KindUnknown = "capability.kind_unknown";
    public const string Forbidden = "capability.forbidden";
    public const string SourceUnavailable = "capability.source_unavailable";
}

public sealed class CapabilityException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
