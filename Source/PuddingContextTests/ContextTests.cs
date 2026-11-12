using System.Collections.Immutable;
using PuddingContext;
using PuddingContext.Contracts;

namespace PuddingContextTests;

[TestClass]
public sealed class ContextTests
{
    private static ContextAssembler Assembler(CountingTokenizer? tokenizer = null) => new(tokenizer ?? new CountingTokenizer());

    [TestMethod]
    public void Validator_RequiresTheCanonicalCurrentUserFence()
    {
        var withoutUser = ImmutableArray.Create(ContextFixtures.Canonical()[0], ContextFixtures.Canonical()[1]);
        Assert.AreEqual(ContextErrorCodes.FenceMissing, Assert.ThrowsExactly<ContextException>(() =>
            ContextContributionValidator.ValidateRequest(ContextFixtures.Request(canonical: withoutUser))).Code);

        Assert.AreEqual(ContextErrorCodes.FenceMismatch, Assert.ThrowsExactly<ContextException>(() =>
            ContextContributionValidator.ValidateRequest(ContextFixtures.Request(fence: "sha256-wrong"))).Code);
    }

    [TestMethod]
    public void Validator_RejectsNonAscendingCanonicalOrder()
    {
        var duplicateOrder = ImmutableArray.Create(ContextFixtures.Canonical()[0], ContextFixtures.Canonical()[1],
            ContextFixtures.Canonical()[2] with { Order = 1 });
        Assert.AreEqual(ContextErrorCodes.RequestInvalid, Assert.ThrowsExactly<ContextException>(() =>
            ContextContributionValidator.ValidateRequest(
                ContextFixtures.Request(canonical: duplicateOrder))).Code);
    }

    [TestMethod]
    public void Validator_RejectsExternalImpersonationOfUserOrSystem()
    {
        foreach (var contribution in new[]
                 {
                     ContextFixtures.Memory(role: ContextRole.User),
                     ContextFixtures.Memory(role: ContextRole.System),
                     new ContextContribution("caller-1", ContextSourceKind.CallerSupplied, ContextRole.Assistant,
                         "caller text", 1, 10, 1, null, null, ExternalData: false),
                 })
        {
            Assert.AreEqual(ContextErrorCodes.ContributionImpersonation, Assert.ThrowsExactly<ContextException>(() =>
                ContextContributionValidator.ValidateContribution(contribution, 10_000)).Code);
        }
    }

    [TestMethod]
    public void Validator_RejectsCanonicalContributionsAndBadFields()
    {
        var canonicalSource = new ContextContribution("dup-1", ContextSourceKind.Canonical, ContextRole.Assistant,
            "x", 1, 10, 1, null, null, false);
        Assert.AreEqual(ContextErrorCodes.ContributionInvalid, Assert.ThrowsExactly<ContextException>(() =>
            ContextContributionValidator.ValidateContribution(canonicalSource, 10_000)).Code);

        foreach (var bad in new[]
                 {
                     ContextFixtures.Memory(priority: 2000),
                     ContextFixtures.Memory(maxTokens: 0),
                     ContextFixtures.Memory(maxTokens: 20_000),
                     ContextFixtures.Memory(content: ""),
                 })
        {
            Assert.AreEqual(ContextErrorCodes.ContributionInvalid, Assert.ThrowsExactly<ContextException>(() =>
                ContextContributionValidator.ValidateContribution(bad, 10_000)).Code);
        }
    }

    [TestMethod]
    public void Validator_RejectsDuplicateIdsAndHashMismatch()
    {
        var duplicate = ImmutableArray.Create(ContextFixtures.Memory("memory-1"), ContextFixtures.Memory("memory-1"));
        Assert.AreEqual(ContextErrorCodes.ContributionInvalid, Assert.ThrowsExactly<ContextException>(() =>
            ContextContributionValidator.ValidateRequest(ContextFixtures.Request(contributions: duplicate))).Code);

        var tampered = ContextFixtures.Memory(hash: ContextContributionValidator.Hash("different"));
        var failure = Assert.ThrowsExactly<ContextException>(() =>
            ContextContributionValidator.VerifyHash(tampered));
        Assert.AreEqual(ContextErrorCodes.HashMismatch, failure.Code);
    }

    [TestMethod]
    public async Task Assemble_PreservesCanonicalOrderAndSortsContributionsDeterministically()
    {
        var contributions = ImmutableArray.Create(
            ContextFixtures.Memory("memory-a", priority: 5),
            ContextFixtures.Memory("memory-b", priority: 50),
            ContextFixtures.Memory("memory-c", priority: 5));
        var result = await Assembler().AssembleAsync(ContextFixtures.Request(contributions: contributions));

        Assert.AreEqual(6, result.Messages.Length);
        CollectionAssert.AreEqual(new[] { "m-system", "m-assistant", "m-user" },
            result.Messages.Take(3).Select(m => m.MessageId).ToArray());
        CollectionAssert.AreEqual(new[] { "memory-b", "memory-a", "memory-c" }, result.UsedContributionIds.ToArray());
        Assert.IsFalse(result.Trimmed);
        Assert.AreEqual(ContextRole.User, result.Messages[2].Role);
        Assert.AreEqual(ContextContributionValidator.Hash(ContextFixtures.UserTurn), result.Messages[2].ContentHash);
        Assert.IsTrue(result.Messages.Skip(3).All(m => m.Role == ContextRole.Tool),
            "External contributions stay tool data and never become user/system instructions.");
    }

    [TestMethod]
    public async Task Assemble_TrimsOnlyLowPriorityContributions()
    {
        var canonical = ContextFixtures.Canonical();
        var canonicalTokens = canonical.Sum(m => m.Content.Length);
        var contributions = ImmutableArray.Create(
            ContextFixtures.Memory("memory-high", priority: 100, content: "high", maxTokens: 10),
            ContextFixtures.Memory("memory-low", priority: 1, content: new string('x', 20), maxTokens: 20));
        var result = await Assembler().AssembleAsync(
            ContextFixtures.Request(maxTokens: canonicalTokens + 4, contributions: contributions));

        CollectionAssert.AreEqual(new[] { "memory-high" }, result.UsedContributionIds.ToArray());
        CollectionAssert.AreEqual(new[] { "memory-low" }, result.DroppedContributionIds.ToArray());
        Assert.IsTrue(result.Trimmed);
        Assert.AreEqual(3 + 1, result.Messages.Length, "The canonical fence and the winning contribution are kept.");
    }

    [TestMethod]
    public async Task Assemble_CanonicalOverBudgetOrOversizedContributionIsTyped()
    {
        var overBudget = Assert.ThrowsExactlyAsync<ContextException>(async () =>
            await Assembler().AssembleAsync(ContextFixtures.Request(maxTokens: 10, contributions: [])));
        Assert.AreEqual(ContextErrorCodes.BudgetExceeded, (await overBudget).Code);

        var oversized = ContextFixtures.Memory(maxTokens: 5, content: "0123456789");
        var failure = Assert.ThrowsExactlyAsync<ContextException>(async () =>
            await Assembler().AssembleAsync(ContextFixtures.Request(contributions: [oversized])));
        Assert.AreEqual(ContextErrorCodes.ContributionTooLarge, (await failure).Code);
    }

    [TestMethod]
    public async Task Assembler_TokenizerFailureAndCancellationAreNotSwallowed()
    {
        var tokenizer = new CountingTokenizer { Fail = true };
        var failure = await Assert.ThrowsExactlyAsync<ContextException>(async () =>
            await Assembler(tokenizer).AssembleAsync(ContextFixtures.Request()));
        Assert.AreEqual(ContextErrorCodes.TokenizerUnavailable, failure.Code);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await Assembler().AssembleAsync(ContextFixtures.Request(), cts.Token));
    }

    [TestMethod]
    public async Task Assembler_ValidatesBeforeCountingTokens()
    {
        var tokenizer = new CountingTokenizer();
        await Assert.ThrowsExactlyAsync<ContextException>(async () =>
            await Assembler(tokenizer).AssembleAsync(ContextFixtures.Request(fence: "wrong")));
        Assert.AreEqual(0, tokenizer.Calls, "Fence validation must happen before tokenizer work.");
    }
}
