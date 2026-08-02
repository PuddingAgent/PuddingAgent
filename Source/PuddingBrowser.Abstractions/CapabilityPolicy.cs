using System.Text.Json;

namespace PuddingBrowser.Abstractions;

public sealed record BrowserOperation
{
    public required string OperationId { get; init; }
    public required string Capability { get; init; }
    public BrowserContextId? ContextId { get; init; }
    public PageId? PageId { get; init; }
    public Uri? TargetUri { get; init; }
    public string? FilePath { get; init; }
    public JsonElement? Metadata { get; init; }
}

public enum BrowserPolicyDecision { Allow, Deny }

public interface IBrowserCapabilityPolicy
{
    ValueTask<BrowserPolicyDecision> AuthorizeAsync(
        BrowserOperation operation, CancellationToken ct);
}

public sealed class AllowAllBrowserCapabilityPolicy : IBrowserCapabilityPolicy
{
    public ValueTask<BrowserPolicyDecision> AuthorizeAsync(
        BrowserOperation operation, CancellationToken ct)
        => ValueTask.FromResult(BrowserPolicyDecision.Allow);
}
