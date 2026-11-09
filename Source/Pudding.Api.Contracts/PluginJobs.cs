using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-07A 公开任务 DTO：Submit 立即返回票据（Accepted 仅代表受理），完成结果通过 Get/事件另取；
// 宿主绑定 OwnerWorkId、RootBudgetId、PluginGeneration 与 deadline，公开请求不能自报。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitPluginJob(string Kind, string InputRef, string InputHash, int TimeoutSeconds,
    ImmutableArray<string> RequiredGrants);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginJobReceipt(string JobId, long AcceptedSequence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginJobView(string JobId, string State, long PluginGeneration, string? ReasonCode,
    string? ResultRef, string? ResultHash, DateTimeOffset UpdatedAt, int Attempt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CancelPluginJob(string JobId);

public interface IPluginJobsApi
{
    Task<PluginJobReceipt> SubmitAsync(SubmitPluginJob request, CancellationToken ct = default);
    Task<PluginJobView> GetAsync(string jobId, CancellationToken ct = default);
    Task<PluginJobView> CancelAsync(CancelPluginJob request, CancellationToken ct = default);
}

public sealed class PluginJobException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
