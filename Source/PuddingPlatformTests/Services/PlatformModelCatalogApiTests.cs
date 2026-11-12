using System.Collections.Immutable;
using System.Security.Claims;
using Pudding.Api.Contracts;
using PuddingCode.Security;
using PuddingModels.Contracts;
using AgentConfigurationSnapshot = PuddingModels.Contracts.AgentConfigurationSnapshot;
using PuddingPlatform.Services.Configuration;
using PuddingPlatform.Services.Models;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-06B public catalog API: listing stays the authorized projection; resolving a purpose uses the trusted actor and
/// explicit InheritPrimary; external/unauthenticated callers are rejected by the factory.
/// </summary>
[TestClass]
public sealed class PlatformModelCatalogApiTests
{
    [TestMethod]
    public async Task Resolve_UsesPurposePolicy_AndExplicitInheritance()
    {
        var api = new PlatformModelCatalogApi(new ModelCatalogAdapter(new Source()), new FakePolicies(
            new PurposeModelSetting("Review", new PurposeModelLayer("model-review", false, PolicySourceKind.Instance, 1), null, null)), new Source(),
            "actor-1");
        var selection = await api.ResolveAsync(new(new("ws-1", "agent-1"), "Review", []));
        Assert.AreEqual("model-review", selection.ModelId);
        Assert.AreEqual("rev-42", selection.Revision);
        Assert.IsFalse(selection.InheritedFromPrimary);

        var inherited = new PlatformModelCatalogApi(new ModelCatalogAdapter(new Source()), new FakePolicies(
            new PurposeModelSetting("Review", new PurposeModelLayer(null, true, PolicySourceKind.Instance, 1), null, null),
            new PurposeModelSetting("Primary", new PurposeModelLayer("model-primary", false, PolicySourceKind.Instance, 1), null, null)), new Source(),
            "actor-1");
        var inheritedSelection = await inherited.ResolveAsync(new(new("ws-1", "agent-1"), "Review", []));
        Assert.AreEqual("model-primary", inheritedSelection.ModelId);
        Assert.IsTrue(inheritedSelection.InheritedFromPrimary);
    }

    [TestMethod]
    public async Task Resolve_MissingPurpose_IsTyped()
    {
        var api = new PlatformModelCatalogApi(new ModelCatalogAdapter(new Source()), new FakePolicies(
            new PurposeModelSetting("Primary", new PurposeModelLayer("model-primary", false, PolicySourceKind.Instance, 1), null, null)), new Source(),
            "actor-1");
        var failure = await Assert.ThrowsExactlyAsync<ModelCatalogException>(async () =>
            await api.ResolveAsync(new(new("ws-1", "agent-1"), "Review", [])));
        Assert.AreEqual(ModelErrorCodes.PolicyMissing, failure.Code);
    }

    [TestMethod]
    public void Factory_RejectsNonManagementCallers()
    {
        var factory = new ModelCatalogApiFactory(new ModelCatalogAdapter(new Source()), new FakePolicies(),
            new Source());
        var failure = Assert.ThrowsExactly<ModelCatalogException>(() =>
            factory.ForManagementUser(Principal(null)));
        Assert.AreEqual(ModelErrorCodes.ScopeForbidden, failure.Code);
        var external = Assert.ThrowsExactly<ModelCatalogException>(() =>
            factory.ForManagementUser(Principal("ext:abc", external: true)));
        Assert.AreEqual(ModelErrorCodes.ScopeForbidden, external.Code);
        Assert.IsNotNull(factory.ForManagementUser(Principal("user-1")));
    }

    private static ClaimsPrincipal Principal(string? subject, bool external = false)
    {
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        if (external) claims.Add(new Claim(ExternalAccessTokenClaimNames.TokenId, "token-1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: subject is null ? null : "Bearer"));
    }

    private sealed class FakePolicies(params PurposeModelSetting[] purposes) : IAgentModelPolicyStore
    {
        public ValueTask<AgentConfigurationSnapshot> ReadAsync(AgentProfileScope scope, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentConfigurationSnapshot(1, 1, [.. purposes]));

        public ValueTask<ModelPolicyWriteResult> WriteAsync(AgentProfileScope scope, UpdateModelPolicyCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Source : IModelCatalogSource
    {
        public ValueTask<ModelCapabilityRecord?> GetAsync(string modelId, CancellationToken ct = default)
            => ValueTask.FromResult<ModelCapabilityRecord?>(new(modelId, "rev-42", 128_000, 8_192, ["text"], true,
                true, "available", InputPerMillion: 1m, CacheHitPerMillion: 0.5m, OutputPerMillion: 2m,
                Currency: "usd", PriceSource: "declared", ExplicitFree: false,
                QuoteValidFrom: DateTimeOffset.UtcNow.AddDays(-1), QuoteValidTo: DateTimeOffset.UtcNow.AddDays(1),
                TimeZoneId: "utc", MaxChargeable: 10m, PricingRuleVersion: "pricing-v9"));

        public ValueTask<ImmutableArray<ModelCapabilityRecord>> ListAsync(string workspaceId, string? cursor,
            int maxCount, CancellationToken ct = default) => ValueTask.FromResult(ImmutableArray<ModelCapabilityRecord>.Empty);
    }
}
