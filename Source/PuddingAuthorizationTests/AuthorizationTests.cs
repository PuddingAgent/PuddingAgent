using PuddingAuthorization;
using PuddingAuthorization.Contracts;

namespace PuddingAuthorizationTests;

[TestClass]
public sealed class AuthorizationTests
{
    private static AuthorizationEvaluator Evaluator(FakePolicySource source, FakeApprovalStore? store = null)
        => new(source, store ?? new FakeApprovalStore(), new FixedClock(AuthorizationFixtures.Now));

    [TestMethod]
    public async Task SystemDeny_WinsOverPluginAllowAndAnApproval()
    {
        var source = new FakePolicySource();
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.Deny));
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.Allow, PolicySourceKind.Plugin));
        var store = new FakeApprovalStore();
        await store.CreateAsync(new ApprovalRecord("approval-1", "ws-1", "agent-1", "files.write",
            "workspace:default/report.md", "sha256-args-abc", 7, "plugin-a", ApprovalState.Approved,
            AuthorizationFixtures.Now.AddMinutes(-1), AuthorizationFixtures.Now.AddMinutes(5), "host-user", 1));
        var decision = await Evaluator(source, store).EvaluateAsync(AuthorizationFixtures.Request(approvalId: "approval-1"));
        Assert.AreEqual(AuthorizationOutcome.Deny, decision.Outcome);
        Assert.IsFalse(decision.AllowsExecution, "An approval can never override a system Deny.");
    }

    [TestMethod]
    public async Task PluginDeny_TightensAndNoRuleDeniesByDefault()
    {
        var source = new FakePolicySource();
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.Allow));
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.Deny, PolicySourceKind.Plugin));
        Assert.AreEqual(AuthorizationOutcome.Deny, (await Evaluator(source).EvaluateAsync(AuthorizationFixtures.Request())).Outcome);

        var empty = await Evaluator(new FakePolicySource()).EvaluateAsync(AuthorizationFixtures.Request());
        Assert.AreEqual(AuthorizationOutcome.Deny, empty.Outcome, "No policy rule means deny, not allow.");
    }

    [TestMethod]
    public async Task AllowRule_AllowsWithoutApproval()
    {
        var source = new FakePolicySource();
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.Allow));
        var decision = await Evaluator(source).EvaluateAsync(AuthorizationFixtures.Request());
        Assert.IsTrue(decision.AllowsExecution);
        Assert.AreEqual(7L, decision.PolicyVersion);
    }

    [TestMethod]
    public async Task NeedsApproval_WithoutApproval_DoesNotAllowExecution()
    {
        var source = new FakePolicySource();
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.NeedsApproval));
        var decision = await Evaluator(source).EvaluateAsync(AuthorizationFixtures.Request());
        Assert.AreEqual(AuthorizationOutcome.NeedsApproval, decision.Outcome);
        Assert.IsFalse(decision.AllowsExecution);
        Assert.AreEqual(AuthorizationErrorCodes.ApprovalRequired, decision.ReasonCode);
    }

    [TestMethod]
    public async Task ApprovedMatchingCall_AllowsExecution()
    {
        var source = new FakePolicySource();
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.NeedsApproval));
        var store = new FakeApprovalStore();
        await store.CreateAsync(new ApprovalRecord("approval-1", "ws-1", "agent-1", "files.write",
            "workspace:default/report.md", "sha256-args-abc", 7, "plugin-a", ApprovalState.Approved,
            AuthorizationFixtures.Now.AddMinutes(-1), AuthorizationFixtures.Now.AddMinutes(5), "host-user", 2));
        var decision = await Evaluator(source, store).EvaluateAsync(AuthorizationFixtures.Request(approvalId: "approval-1"));
        Assert.IsTrue(decision.AllowsExecution);
        Assert.AreEqual("approval-1", decision.ApprovalId);
    }

    [TestMethod]
    public async Task PendingOrExpiredApproval_DoesNotAllowExecution()
    {
        var source = new FakePolicySource();
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.NeedsApproval));
        var store = new FakeApprovalStore();
        await store.CreateAsync(new ApprovalRecord("approval-1", "ws-1", "agent-1", "files.write",
            "workspace:default/report.md", "sha256-args-abc", 7, "plugin-a", ApprovalState.Pending,
            AuthorizationFixtures.Now, AuthorizationFixtures.Now.AddMinutes(5), null, 0));
        var pending = await Evaluator(source, store).EvaluateAsync(AuthorizationFixtures.Request(approvalId: "approval-1"));
        Assert.AreEqual(AuthorizationOutcome.NeedsApproval, pending.Outcome);

        await store.CreateAsync(new ApprovalRecord("approval-expired", "ws-1", "agent-1", "files.write",
            "workspace:default/report.md", "sha256-args-abc", 7, "plugin-a", ApprovalState.Approved,
            AuthorizationFixtures.Now.AddHours(-1), AuthorizationFixtures.Now.AddMinutes(-1), "host-user", 1));
        var expired = await Evaluator(source, store).EvaluateAsync(AuthorizationFixtures.Request(approvalId: "approval-expired"));
        Assert.AreEqual(AuthorizationOutcome.NeedsApproval, expired.Outcome);
        Assert.AreEqual(AuthorizationErrorCodes.ApprovalExpired, expired.ReasonCode);
    }

    [TestMethod]
    public async Task ApprovalBoundToArgumentHashAndPolicyVersion_MismatchDenies()
    {
        var source = new FakePolicySource();
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.NeedsApproval));
        var store = new FakeApprovalStore();
        await store.CreateAsync(new ApprovalRecord("approval-1", "ws-1", "agent-1", "files.write",
            "workspace:default/report.md", "sha256-args-abc", 7, "plugin-a", ApprovalState.Approved,
            AuthorizationFixtures.Now.AddMinutes(-1), AuthorizationFixtures.Now.AddMinutes(5), "host-user", 1));

        var changedArguments = await Evaluator(source, store).EvaluateAsync(
            AuthorizationFixtures.Request(argumentHash: "sha256-args-CHANGED", approvalId: "approval-1"));
        Assert.AreEqual(AuthorizationOutcome.Deny, changedArguments.Outcome);
        Assert.AreEqual(AuthorizationErrorCodes.ApprovalMismatch, changedArguments.ReasonCode);
    }

    [TestMethod]
    public async Task StalePolicyVersion_IsUnavailable_AndExpiredRulesAreIgnored()
    {
        var source = new FakePolicySource();
        source.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.Allow, policyVersion: 8));
        var stale = await Evaluator(source).EvaluateAsync(AuthorizationFixtures.Request(policyVersion: 7));
        Assert.AreEqual(AuthorizationOutcome.Unavailable, stale.Outcome);

        var expiredSource = new FakePolicySource();
        expiredSource.Rules.Add(AuthorizationFixtures.Rule(AuthorizationOutcome.Allow,
            expiresAt: AuthorizationFixtures.Now.AddMinutes(-1)));
        var expired = await Evaluator(expiredSource).EvaluateAsync(AuthorizationFixtures.Request());
        Assert.AreEqual(AuthorizationOutcome.Deny, expired.Outcome);
    }

    [TestMethod]
    public async Task SourceFailure_IsUnavailable_NotAllow()
    {
        var source = new FakePolicySource { Failure = new InvalidOperationException("policy store down") };
        var decision = await Evaluator(source).EvaluateAsync(AuthorizationFixtures.Request());
        Assert.AreEqual(AuthorizationOutcome.Unavailable, decision.Outcome);
        Assert.IsFalse(decision.AllowsExecution, "A failed policy lookup must never fail open.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
