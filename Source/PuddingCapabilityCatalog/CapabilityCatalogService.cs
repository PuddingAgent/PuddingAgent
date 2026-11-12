using System.Collections.Immutable;
using Pudding.Api.Contracts;
using PuddingCapabilityCatalog.Contracts;

namespace PuddingCapabilityCatalog;

/// <summary>
/// PA-05 read-only capability catalog: forwards a bounded, kind-validated query to the authorized source and
/// preserves Revision/Generation/Availability/Completeness exactly. A forbidden or partial source page is never
/// collapsed into "no capabilities", and a catalog entry is never turned into an executable authorization.
/// </summary>
public sealed class CapabilityCatalogService
{
    private static readonly ImmutableArray<string> KnownKinds = ["tool", "mcp", "context"];
    private readonly ICapabilitySource _source;
    private readonly string _workspaceId;

    /// <summary>The service instance is scoped to a host-authorized workspace; callers never supply or widen it.</summary>
    public CapabilityCatalogService(ICapabilitySource source, string workspaceId)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!Valid(workspaceId))
            throw Error(CapabilityErrorCodes.RequestInvalid, "A bounded host-bound workspace id is required.");
        _workspaceId = workspaceId;
    }

    public async ValueTask<CapabilityCatalogPage> ListAsync(CapabilityQuery query, CancellationToken ct = default)
    {
        if (query is null) throw Error(CapabilityErrorCodes.RequestInvalid, "A capability query is required.");
        if (!KnownKinds.Contains(query.Kind, StringComparer.Ordinal))
            throw Error(CapabilityErrorCodes.KindUnknown, "The capability kind is not published.");
        if (query.MaxCount is < 1 or > 100) throw Error(CapabilityErrorCodes.RequestInvalid, "The page size is out of bounds.");
        if (query.Cursor is { Length: > 128 }) throw Error(CapabilityErrorCodes.RequestInvalid, "The cursor is out of bounds.");
        CapabilityPage page;
        try
        {
            page = await _source.ListAsync(_workspaceId, query.Kind, query.Cursor, query.MaxCount, ct)
                .ConfigureAwait(false);
        }
        catch (CapabilityException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw Error(CapabilityErrorCodes.SourceUnavailable, "The capability source is unavailable."); }
        if (page is null || !Valid(page.Generation))
            throw Error(CapabilityErrorCodes.SourceUnavailable, "The capability source returned no generation.");
        return new(page.Capabilities.Select(ToPublic).ToImmutableArray(), page.Cursor, page.Complete, page.Partial,
            page.Generation, page.MissingReason);
    }

    private static CapabilityCatalogEntry ToPublic(CapabilityRecord record) => new(record.CapabilityId, record.Kind,
        record.DisplayName, record.Revision, record.Generation, record.Availability, record.MissingReason,
        record.RequiresApproval, record.RequiredCapabilities);

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static CapabilityException Error(string code, string message) => new(code, message);
}
