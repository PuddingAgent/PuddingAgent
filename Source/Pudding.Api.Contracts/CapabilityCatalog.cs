using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-05 只读能力目录 DTO：只暴露受权能力身份、Revision/Generation、可用性与所需能力；
// 目录项不是可执行授权，List 不意味着能 Execute；缺权限与"确实没有对象"必须分开。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CapabilityCatalogEntry(string CapabilityId, string Kind, string DisplayName, string Revision,
    string Generation, string Availability, string? MissingReason, bool RequiresApproval,
    ImmutableArray<string> RequiredCapabilities);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CapabilityQuery(string Kind, string? Cursor = null, int MaxCount = 50);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CapabilityCatalogPage(ImmutableArray<CapabilityCatalogEntry> Capabilities, string? Cursor,
    bool Complete, bool Partial, string Generation, string? MissingReason);

public interface ICapabilityCatalogApi
{
    Task<CapabilityCatalogPage> ListAsync(CapabilityQuery query, CancellationToken ct = default);
}

public sealed class CapabilityCatalogException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
