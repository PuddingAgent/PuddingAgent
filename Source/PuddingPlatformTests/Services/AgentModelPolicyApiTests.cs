using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pudding.Api.Contracts;
using PuddingCode.Security;
using PuddingModels.Contracts;
using AgentConfigurationSnapshot = PuddingModels.Contracts.AgentConfigurationSnapshot;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Configuration;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-06 management principal mapping and bound policy API: only an authenticated management subject may read/write;
/// the actor is the subject (never a body field or AgentId); updates carry the CAS revision and map typed errors.
/// </summary>
[TestClass]
public sealed class AgentModelPolicyApiTests
{
    [TestMethod]
    public void ManagementPrincipal_IsBoundToItsOwnActorOnly()
    {
        var authorization = new ManagementModelPolicyAuthorization(Accessor(Principal("user-1")));
        Assert.IsTrue(authorization.Allowed(new("ws-1", "agent-1", "user-1")));
        Assert.IsFalse(authorization.Allowed(new("ws-1", "agent-1", "user-2")));
        Assert.IsFalse(new ManagementModelPolicyAuthorization(Accessor(Principal("ext:abc", external: true)))
            .Allowed(new("ws-1", "agent-1", "ext:abc")));
        Assert.IsFalse(new ManagementModelPolicyAuthorization(Accessor(null)).Allowed(new("ws-1", "agent-1", "user-1")));
    }

    [TestMethod]
    public async Task Factory_RejectsNonManagementCallers()
    {
        var factory = new AgentModelPolicyApiFactory(new FakeStore());
        var failure = Assert.ThrowsExactly<AgentModelPolicyException>(() => factory.ForManagementUser(Principal(null)));
        Assert.AreEqual(ModelErrorCodes.ScopeForbidden, failure.Code);
    }

    [TestMethod]
    public async Task BoundApi_ReadsEffectivePolicy_AndWritesWithCas()
    {
        var store = new FakeStore();
        var api = new AgentModelPolicyApiFactory(store).ForManagementUser(Principal("user-1"));

        var effective = await api.GetEffectiveAsync(new AgentRef("ws-1", "agent-1"));
        Assert.IsNotNull(effective);
        Assert.AreEqual("user-1", store.LastReadActor);
        Assert.AreEqual("model-review", effective.Purposes.Single(p => p.Purpose == "Review").ModelId);

        var receipt = await api.UpdateAsync(new UpdateAgentModelPolicy("op-1", new AgentRef("ws-1", "agent-1"), 0,
            [new ModelPolicyPatchDto("Review", "model-review", false)]));
        Assert.AreEqual(1L, receipt.PersistedRevision);
        Assert.AreEqual("user-1", store.LastWriteActor);
        Assert.AreEqual(0L, store.LastExpectedRevision);
    }

    private static IHttpContextAccessor Accessor(ClaimsPrincipal? principal) => new FakeAccessor
    {
        HttpContext = principal is null ? null : new DefaultHttpContext { User = principal },
    };

    private static ClaimsPrincipal Principal(string? subject, bool external = false)
    {
        var claims = new List<Claim>();
        if (subject is not null) claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        if (external) claims.Add(new Claim(ExternalAccessTokenClaimNames.TokenId, "token-1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: subject is null ? null : "Bearer"));
    }

    private sealed class FakeAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakeStore : IAgentModelPolicyStore
    {
        public string? LastReadActor { get; private set; }
        public string? LastWriteActor { get; private set; }
        public long LastExpectedRevision { get; private set; }

        public ValueTask<AgentConfigurationSnapshot> ReadAsync(AgentProfileScope scope, CancellationToken ct = default)
        {
            LastReadActor = scope.ActorId;
            return ValueTask.FromResult(new AgentConfigurationSnapshot(0, 1,
            [
                new("Review", new PurposeModelLayer("model-review", false, PolicySourceKind.Instance, 0), null, null),
                new("Primary", new PurposeModelLayer("model-primary", false, PolicySourceKind.Instance, 0), null, null),
            ]));
        }

        public ValueTask<ModelPolicyWriteResult> WriteAsync(AgentProfileScope scope, UpdateModelPolicyCommand command,
            CancellationToken ct = default)
        {
            LastWriteActor = scope.ActorId;
            LastExpectedRevision = command.ExpectedRevision;
            return ValueTask.FromResult(new ModelPolicyWriteResult(command.ExpectedRevision + 1,
                command.ExpectedRevision + 1, true, ImmutableArray<string>.Empty, 2));
        }
    }
}
