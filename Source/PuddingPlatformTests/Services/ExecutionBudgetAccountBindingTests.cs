using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingExecutionResources.Contracts;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.ExecutionBudgets;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-01 P2 account binding on real SQLite: one transaction reads the account and its parent chain, then creates or
/// reuses it. Covers idempotent creation, ancestor paths, missing/conflicting definitions, cross-workspace/currency/mode
/// rejection, corrupt paths, id boundaries, two independent Store instances racing on one database file, ledger-state
/// survival after recreating a Store, and cancellation. No production database, no provider.
/// </summary>
[TestClass]
public sealed class ExecutionBudgetAccountBindingTests
{
    [TestMethod]
    public async Task A01_SameRootDefinition_IsCreatedOnce_ThenReusedWithIdenticalFields()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var definition = GoalDef("goal-1");

        var first = await host.Store.EnsureAccountBindingAsync(definition);
        var second = await host.Store.EnsureAccountBindingAsync(definition);

        Assert.IsTrue(first.Created);
        Assert.IsFalse(second.Created, "The second identical resolution must reuse the account.");
        Assert.AreEqual(first.Definition.AccountId, second.Definition.AccountId);
        Assert.IsFalse(second.Ancestors.IsDefault);
        Assert.AreEqual(0, second.Ancestors.Length);
        Assert.AreEqual(definition.Identity, second.Definition.Identity);
        Assert.AreEqual(definition.Limits, second.Definition.Limits);
        Assert.AreEqual("usd", second.Definition.CostUnit);
        Assert.AreEqual(BudgetEnforcementMode.Enforced, second.Definition.Mode);
        Assert.AreEqual(1, (await host.Store.ListAccountIdsAsync()).Length);

        var snapshot = await host.Store.ReadAsync(first.Definition.AccountId);
        Assert.AreEqual(10m, snapshot.Limits.MaxCost);
        Assert.AreEqual(100_000L, snapshot.Limits.MaxOutputTokens);
        Assert.AreEqual(BudgetEnforcementMode.Enforced,
            await host.Store.GetEnforcementModeAsync(first.Definition.AccountId));
    }

    [TestMethod]
    public async Task A02_GoalThenWorkThenChildWork_ReturnRootToParentAncestors_WithoutTheLeaf()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var goal = await host.Store.EnsureAccountBindingAsync(GoalDef("goal-1"));
        var work = await host.Store.EnsureAccountBindingAsync(WorkDef("work-1", goal.Definition.AccountId));
        var child = await host.Store.EnsureAccountBindingAsync(WorkDef("work-2", work.Definition.AccountId,
            agent: "agent-3", session: "session-3"));

        Assert.IsTrue(goal.Created);
        Assert.IsTrue(work.Created);
        Assert.IsTrue(child.Created);
        Assert.AreEqual(0, goal.Ancestors.Length);
        CollectionAssert.AreEqual(new[] { new BudgetAccountId("goal:goal-1") }, work.Ancestors.ToArray());
        CollectionAssert.AreEqual(
            new[] { new BudgetAccountId("goal:goal-1"), new BudgetAccountId("work:work-1") }, child.Ancestors.ToArray());
        Assert.AreEqual(3, (await host.Store.ListAccountIdsAsync()).Length,
            "Creating descendants must not create extra accounts.");
    }

    [TestMethod]
    public async Task A03_WorkRootAndTaskRootWithWork_HaveCorrectTypes_AndCreateNoGoalAccount()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var workRoot = await host.Store.EnsureAccountBindingAsync(WorkDef("work-root", null));
        var task = await host.Store.EnsureAccountBindingAsync(TaskDef("task-1"));
        var workUnderTask = await host.Store.EnsureAccountBindingAsync(WorkDef("work-task-1", task.Definition.AccountId));

        Assert.AreEqual("work:work-root", workRoot.Definition.AccountId.Value);
        Assert.AreEqual(BudgetAccountKind.Work, workRoot.Definition.Identity.RootKind);
        Assert.AreEqual(0, workRoot.Ancestors.Length);

        Assert.AreEqual("task:task-1", task.Definition.AccountId.Value);
        Assert.AreEqual(BudgetAccountKind.Task, task.Definition.Identity.RootKind);

        CollectionAssert.AreEqual(new[] { task.Definition.AccountId }, workUnderTask.Ancestors.ToArray());
        var ids = await host.Store.ListAccountIdsAsync();
        Assert.AreEqual(3, ids.Length);
        Assert.IsFalse(ids.Any(id => id.Value.StartsWith("goal:", StringComparison.Ordinal)),
            "An independent Work/Task root must not create a Goal account.");
    }

    [TestMethod]
    public async Task A04_MissingDeclaredParent_IsAccountMissing_AndCreatesNoRows()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("work-1", new BudgetAccountId("goal:missing"))));
        Assert.AreEqual(BudgetErrorCodes.AccountMissing, failure.Code);
        Assert.AreEqual(0, (await host.Store.ListAccountIdsAsync()).Length,
            "Neither the parent nor the child may be created.");
    }

    [TestMethod]
    public async Task A05_ConflictingExistingDefinition_IsConflict_AndIdKindMismatchIsAccountInvalid()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var original = GoalDef("goal-1");
        var created = await host.Store.EnsureAccountBindingAsync(original);
        var before = await host.Store.ReadAsync(created.Definition.AccountId);

        foreach (var conflicting in new[]
                 {
                     original with { Identity = original.Identity with { WorkspaceId = "ws-2" } },
                     original with { Identity = original.Identity with { AgentId = "agent-2" } },
                     original with { Identity = original.Identity with { SessionId = "session-2" } },
                     original with { Identity = original.Identity with { RootKind = BudgetAccountKind.Task } },
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
                await host.Store.EnsureAccountBindingAsync(conflicting));
            Assert.IsTrue(failure.Code is BudgetErrorCodes.Conflict or BudgetErrorCodes.AccountInvalid,
                $"Unexpected code {failure.Code}.");
        }

        // Explicit AccountInvalid: the account id no longer matches its identity kind/root id.
        foreach (var invalid in new[]
                 {
                     original with { AccountId = new BudgetAccountId("task:goal-1") },
                     original with { Identity = original.Identity with { RootKind = BudgetAccountKind.Task } },
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
                await host.Store.EnsureAccountBindingAsync(invalid));
            Assert.AreEqual(BudgetErrorCodes.AccountInvalid, failure.Code);
        }

        Assert.AreEqual(1, (await host.Store.ListAccountIdsAsync()).Length);
        var after = await host.Store.ReadAsync(created.Definition.AccountId);
        Assert.AreEqual(before.Limits, after.Limits);
        Assert.AreEqual(before.Revision, after.Revision);
        Assert.AreEqual(before.Calls, after.Calls);
        Assert.AreEqual(BudgetEnforcementMode.Enforced, await host.Store.GetEnforcementModeAsync(created.Definition.AccountId));
    }

    [TestMethod]
    public async Task A06_ChangingTheParentReference_IsConflict_AndKeepsTheOriginalChain()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var goalA = await host.Store.EnsureAccountBindingAsync(GoalDef("goal-a"));
        var goalB = await host.Store.EnsureAccountBindingAsync(GoalDef("goal-b"));
        var work = await host.Store.EnsureAccountBindingAsync(WorkDef("work-1", goalA.Definition.AccountId));

        var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("work-1", goalB.Definition.AccountId)));
        Assert.AreEqual(BudgetErrorCodes.Conflict, failure.Code,
            "A different declared parent is a conflict even when the new parent really exists.");

        var rebound = await host.Store.EnsureAccountBindingAsync(WorkDef("work-1", goalA.Definition.AccountId));
        Assert.IsFalse(rebound.Created);
        CollectionAssert.AreEqual(new[] { goalA.Definition.AccountId }, rebound.Ancestors.ToArray(),
            "The persisted parent chain must be unchanged.");
        Assert.AreEqual(3, (await host.Store.ListAccountIdsAsync()).Length);
    }

    [TestMethod]
    public async Task A07_ChangingAnyLimitCostUnitOrMode_IsConflict_AndNeverSilentlyKeepsTheOldValue()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var original = GoalDef("goal-1",
            limits: new(MaxOutputTokens: 100, MaxCost: 1m, MaxCalls: 5, MaxConcurrentAttempts: 3,
                MaxInputTokensPerRequest: 1_000));
        var created = await host.Store.EnsureAccountBindingAsync(original);

        foreach (var conflicting in new[]
                 {
                     original with { Limits = original.Limits with { MaxOutputTokens = null } },
                     original with { Limits = original.Limits with { MaxOutputTokens = 101 } },
                     original with { Limits = original.Limits with { MaxCost = 2m } },
                     original with { Limits = original.Limits with { MaxCost = 0m } },
                     original with { Limits = original.Limits with { MaxCalls = 6 } },
                     original with { Limits = original.Limits with { MaxConcurrentAttempts = 4 } },
                     original with { Limits = original.Limits with { MaxInputTokensPerRequest = null } },
                     original with { CostUnit = "eur" },
                     original with { Mode = BudgetEnforcementMode.Legacy },
                     original with { Mode = BudgetEnforcementMode.Shadow },
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
                await host.Store.EnsureAccountBindingAsync(conflicting));
            Assert.AreEqual(BudgetErrorCodes.Conflict, failure.Code);
        }

        var snapshot = await host.Store.ReadAsync(created.Definition.AccountId);
        Assert.AreEqual(original.Limits, snapshot.Limits, "The frozen limits must not change.");
        Assert.AreEqual("usd", snapshot.CostUnit);
        Assert.AreEqual(BudgetEnforcementMode.Enforced,
            await host.Store.GetEnforcementModeAsync(created.Definition.AccountId));
        Assert.AreEqual(1, (await host.Store.ListAccountIdsAsync()).Length);
    }

    [TestMethod]
    public async Task A08_CrossWorkspaceCurrencyOrModeChild_IsRejected_ButDelegationIsAllowed()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var goal = await host.Store.EnsureAccountBindingAsync(GoalDef("goal-1"));

        var workspace = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("work-ws", goal.Definition.AccountId, workspace: "ws-2")));
        Assert.AreEqual(BudgetErrorCodes.Conflict, workspace.Code);

        var currency = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("work-ccy", goal.Definition.AccountId, costUnit: "eur")));
        Assert.AreEqual(BudgetErrorCodes.CurrencyMismatch, currency.Code);

        var mode = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("work-mode", goal.Definition.AccountId,
                mode: BudgetEnforcementMode.Legacy)));
        Assert.AreEqual(BudgetErrorCodes.Conflict, mode.Code);
        Assert.AreEqual(1, (await host.Store.ListAccountIdsAsync()).Length, "No rejected child may be persisted.");

        // Delegation: a different Agent/Session under the same Workspace/CostUnit/Mode is legal.
        var delegated = await host.Store.EnsureAccountBindingAsync(WorkDef("work-delegated", goal.Definition.AccountId,
            agent: "agent-9", session: "session-9"));
        Assert.IsTrue(delegated.Created);
        CollectionAssert.AreEqual(new[] { goal.Definition.AccountId }, delegated.Ancestors.ToArray());
        var identity = await host.Store.GetAccountIdentityAsync(delegated.Definition.AccountId);
        Assert.AreEqual("agent-9", identity.Identity.AgentId);
        Assert.AreEqual("session-9", identity.Identity.SessionId);
        Assert.AreEqual(2, (await host.Store.ListAccountIdsAsync()).Length);
    }

    [TestMethod]
    public async Task A09_SelfReferenceCycleBrokenAncestorAndTooDeepPath_ArePathInvalid()
    {
        await using var host = await InMemoryHost.CreateAsync();

        // Self reference for a new account.
        var self = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("self-1", new BudgetAccountId("work:self-1"))));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, self.Code);

        // A stored cycle (only constructible directly in an isolated test database).
        await using (var db = host.CreateDbContext())
        {
            db.ExecutionBudgetAccounts.Add(Entity(new BudgetAccountId("work:cycle-a"), "work:cycle-b", "cycle-a"));
            db.ExecutionBudgetAccounts.Add(Entity(new BudgetAccountId("work:cycle-b"), "work:cycle-a", "cycle-b"));
            await db.SaveChangesAsync();
        }
        var cycle = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("cycle-a", new BudgetAccountId("work:cycle-b"))));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, cycle.Code);

        // A deeper ancestor disappearing must not turn the node into a new root.
        var brokenRoot = await host.Store.EnsureAccountBindingAsync(WorkDef("broken-a", null));
        var brokenMiddle = await host.Store.EnsureAccountBindingAsync(WorkDef("broken-b", brokenRoot.Definition.AccountId));
        var brokenLeaf = await host.Store.EnsureAccountBindingAsync(WorkDef("broken-c", brokenMiddle.Definition.AccountId));
        await using (var db = host.CreateDbContext())
        {
            var row = await db.ExecutionBudgetAccounts.SingleAsync(account => account.AccountId == "work:broken-a");
            db.ExecutionBudgetAccounts.Remove(row);
            await db.SaveChangesAsync();
        }
        var broken = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("broken-c", brokenMiddle.Definition.AccountId)));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, broken.Code);

        // A 65-deep persisted path exceeds the 64-level limit (including the account itself).
        await using (var db = host.CreateDbContext())
        {
            for (var level = 1; level <= 65; level++)
            {
                var accountId = "work:deep-" + level;
                db.ExecutionBudgetAccounts.Add(Entity(new BudgetAccountId(accountId),
                    level == 1 ? null : "work:deep-" + (level - 1), "deep-" + level));
            }
            await db.SaveChangesAsync();
        }
        var tooDeep = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("deep-65", new BudgetAccountId("work:deep-64"))));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, tooDeep.Code);
        Assert.IsTrue(brokenLeaf.Created);
    }

    [TestMethod]
    public async Task A10_SixtyFourLevelPath_AndPrefixedIdBoundaries_AreAcceptedOrRejectedWithoutTrim()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var parent = await host.Store.EnsureAccountBindingAsync(GoalDef("level-1", limits: new(MaxOutputTokens: 1_000_000)));
        for (var level = 2; level <= 64; level++)
        {
            var created = await host.Store.EnsureAccountBindingAsync(WorkDef("level-" + level, parent.Definition.AccountId));
            Assert.IsTrue(created.Created);
            parent = created;
        }
        Assert.AreEqual(64, (await host.Store.ListAccountIdsAsync()).Length);
        Assert.AreEqual(63, parent.Ancestors.Length, "A 64-level path has 63 ancestors and excludes the account itself.");

        // 64-level creation is the boundary; one more level is rejected.
        var tooDeep = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("level-65", parent.Definition.AccountId)));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, tooDeep.Code);

        // Prefixed id length boundary: "work:" + 123 chars == 128 is accepted, 124 chars is not.
        var maxRootId = new string('a', 123);
        var maxCreated = await host.Store.EnsureAccountBindingAsync(WorkDef(maxRootId, null));
        Assert.AreEqual(128, maxCreated.Definition.AccountId.Value.Length);
        var overLength = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef(new string('a', 124), null)));
        Assert.AreEqual(BudgetErrorCodes.AccountInvalid, overLength.Code);

        // Commas, control characters and surrounding whitespace are invalid and never trimmed/truncated.
        foreach (var badId in new[] { "bad,id", "bad\tid", " bad", "bad " })
        {
            var failure = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
                await host.Store.EnsureAccountBindingAsync(WorkDef(badId, null)));
            Assert.AreEqual(BudgetErrorCodes.AccountInvalid, failure.Code);
        }
        var ids = await host.Store.ListAccountIdsAsync();
        Assert.AreEqual(65, ids.Length, "Invalid ids must not create rows.");
    }

    [TestMethod]
    public async Task A11_TwoIndependentStoresOnOneDatabase_ConcurrentSameDefinition_CreateExactlyOneAccount()
    {
        await using var database = await FileDatabase.CreateAsync();
        var storeOne = database.NewStore();
        var storeTwo = database.NewStore();
        var definition = GoalDef("goal-1");

        var (first, second) = await RaceAsync(
            () => storeOne.EnsureAccountBindingAsync(definition),
            () => storeTwo.EnsureAccountBindingAsync(definition));

        var bindings = new[] { first, second }.OfType<BudgetAccountBindingResult>().ToArray();
        Assert.AreEqual(2, bindings.Length,
            $"Both concurrent resolutions must return a binding, not a failure: {string.Join(",", new[] { first, second }.OfType<string>())}");
        Assert.AreEqual(1, bindings.Count(binding => binding.Created), "Exactly one caller may create the row.");
        Assert.AreEqual(bindings[0].Definition.AccountId, bindings[1].Definition.AccountId);
        CollectionAssert.AreEqual(bindings[0].Ancestors.ToArray(), bindings[1].Ancestors.ToArray());
        Assert.AreEqual(1, (await storeOne.ListAccountIdsAsync()).Length);
        Assert.AreEqual(1, (await storeTwo.ListAccountIdsAsync()).Length);
    }

    [TestMethod]
    public async Task A12_TwoIndependentStoresOnOneDatabase_CompetingDefinitions_OneCreatedOneConflict()
    {
        await using var database = await FileDatabase.CreateAsync();
        var storeOne = database.NewStore();
        var storeTwo = database.NewStore();
        var firstDefinition = GoalDef("goal-1", limits: new(MaxOutputTokens: 100, MaxCost: 1m));
        var secondDefinition = GoalDef("goal-1", limits: new(MaxOutputTokens: 200, MaxCost: 2m));

        var (first, second) = await RaceAsync(
            () => storeOne.EnsureAccountBindingAsync(firstDefinition),
            () => storeTwo.EnsureAccountBindingAsync(secondDefinition));

        var created = new[] { first, second }.OfType<BudgetAccountBindingResult>().SingleOrDefault();
        Assert.IsNotNull(created, "Exactly one competing definition must win.");
        Assert.IsTrue(created.Created);
        Assert.AreEqual(1, new[] { first, second }.OfType<string>().Count(code => code == BudgetErrorCodes.Conflict),
            "Exactly one competing definition must lose with budget.conflict.");
        Assert.AreEqual(1, (await storeOne.ListAccountIdsAsync()).Length);

        // The winner is complete: no field is mixed from the losing definition.
        var snapshot = await storeOne.ReadAsync(created.Definition.AccountId);
        Assert.AreEqual(created.Definition.Limits, snapshot.Limits);
        Assert.AreEqual(created.Definition.CostUnit, snapshot.CostUnit);
        Assert.AreEqual(created.Definition.Mode, await storeOne.GetEnforcementModeAsync(created.Definition.AccountId));
        Assert.AreEqual(created.Definition.Identity, (await storeOne.GetAccountIdentityAsync(created.Definition.AccountId)).Identity);
    }

    [TestMethod]
    public async Task A13_RecreatingTheStore_ReusesTheAccountAndKeepsLedgerState()
    {
        await using var database = await FileDatabase.CreateAsync();
        var store = database.NewStore();
        var rootDefinition = GoalDef("goal-1", limits: new(MaxOutputTokens: 1_000, MaxCost: 5m));
        var root = await store.EnsureAccountBindingAsync(rootDefinition);
        var work = await store.EnsureAccountBindingAsync(WorkDef("work-1", root.Definition.AccountId,
            limits: new(MaxOutputTokens: 500)));

        var path = BudgetAccountPaths.RootToLeaf(work.Definition.AccountId, work.Ancestors);
        var reservation = await store.ReserveAsync(new ReserveBudget("op-1", "attempt-1", work.Definition.AccountId,
            path, work.Definition.Identity, "model-a", "rev-1", "pricing-v1", 100, 50, 0.05m, "usd",
            DateTimeOffset.UtcNow.AddMinutes(5)));
        await store.MarkDispatchedAsync(new DispatchBudget(reservation.ReservationId, "attempt-1", 1,
            reservation.CancellationRevision));
        await store.SettleAsync(new SettleBudget(reservation.ReservationId, "attempt-1", 1, new ModelUsage(10, 20),
            0.02m, "usd", "receipt-1", DateTimeOffset.UtcNow));
        var rootBefore = await store.ReadAsync(root.Definition.AccountId);
        var workBefore = await store.ReadAsync(work.Definition.AccountId);
        Assert.AreEqual(1, rootBefore.Calls);
        Assert.IsTrue(rootBefore.Cost > 0m);
        Assert.IsTrue(rootBefore.Revision > 0);

        // Discard the first Store instance and re-resolve through a brand-new one on the same database file.
        var recreated = database.NewStore();
        var rootAfterBinding = await recreated.EnsureAccountBindingAsync(rootDefinition);
        var workAfterBinding = await recreated.EnsureAccountBindingAsync(WorkDef("work-1",
            rootAfterBinding.Definition.AccountId, limits: new(MaxOutputTokens: 500)));
        Assert.IsFalse(rootAfterBinding.Created);
        Assert.IsFalse(workAfterBinding.Created);
        CollectionAssert.AreEqual(new[] { root.Definition.AccountId }, workAfterBinding.Ancestors.ToArray());

        var rootAfter = await recreated.ReadAsync(root.Definition.AccountId);
        var workAfter = await recreated.ReadAsync(work.Definition.AccountId);
        foreach (var (before, after) in new[] { (rootBefore, rootAfter), (workBefore, workAfter) })
        {
            Assert.AreEqual(before.Calls, after.Calls, "Calls must survive a new Store instance.");
            Assert.AreEqual(before.Cost, after.Cost, "Cost must survive a new Store instance.");
            Assert.AreEqual(before.UnknownCost, after.UnknownCost);
            Assert.AreEqual(before.Revision, after.Revision, "Re-resolution must not touch the ledger revision.");
            Assert.AreEqual(before.OutputTokens, after.OutputTokens);
        }
    }

    [TestMethod]
    public async Task A14_CancelledLeafAncestorOrToken_LeavesNoNewRowsAndDoesNotResurrect()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var root = await host.Store.EnsureAccountBindingAsync(GoalDef("goal-1"));
        var work = await host.Store.EnsureAccountBindingAsync(WorkDef("work-1", root.Definition.AccountId));
        await host.Store.CancelAsync(new CancelBudget(root.Definition.AccountId, "user.cancelled", 1));

        var cancelled = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("work-1", root.Definition.AccountId)));
        Assert.AreEqual(BudgetErrorCodes.Cancelled, cancelled.Code);
        var underCancelledAncestor = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("work-2", work.Definition.AccountId)));
        Assert.AreEqual(BudgetErrorCodes.Cancelled, underCancelledAncestor.Code);
        Assert.AreEqual(2, (await host.Store.ListAccountIdsAsync()).Length);
        Assert.IsTrue((await host.Store.ReadAsync(work.Definition.AccountId)).Cancelled,
            "Cancellation must not be cleared by re-resolution.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // The cancelled token is observed as an OperationCanceledException (the runtime may surface it as
        // TaskCanceledException); no row may be written in either case.
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await host.Store.EnsureAccountBindingAsync(GoalDef("goal-2"), cancellation.Token));
        Assert.AreEqual(2, (await host.Store.ListAccountIdsAsync()).Length,
            "A pre-cancelled call must observe cancellation before any row is written.");
    }

    [TestMethod]
    public async Task O1_1_ExistingHealthyChild_ChangingCostUnit_ReturnsConflict_AndLeavesStateUntouched()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var root = await host.Store.EnsureAccountBindingAsync(GoalDef("goal-1"));
        var childDefinition = WorkDef("work-1", root.Definition.AccountId);
        var child = await host.Store.EnsureAccountBindingAsync(childDefinition);
        var rootBefore = await host.Store.ReadAsync(root.Definition.AccountId);
        var childBefore = await host.Store.ReadAsync(child.Definition.AccountId);
        var countBefore = (await host.Store.ListAccountIdsAsync()).Length;

        // A changed request cost unit on an already persisted account is a definition conflict, even though the
        // persisted leaf and its healthy ancestors share one currency.
        var conflict = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(childDefinition with { CostUnit = "eur" }));
        Assert.AreEqual(BudgetErrorCodes.Conflict, conflict.Code,
            "The persisted definition wins: a changed request cost unit must be budget.conflict.");

        Assert.AreEqual(countBefore, (await host.Store.ListAccountIdsAsync()).Length);
        var childAfter = await host.Store.ReadAsync(child.Definition.AccountId);
        var rootAfter = await host.Store.ReadAsync(root.Definition.AccountId);
        Assert.AreEqual("usd", childAfter.CostUnit);
        Assert.AreEqual(childBefore.Revision, childAfter.Revision);
        Assert.AreEqual(rootBefore.Revision, rootAfter.Revision);
        Assert.AreEqual(childBefore.Calls, childAfter.Calls);
        Assert.AreEqual(childBefore.InputTokens, childAfter.InputTokens);
        Assert.AreEqual(childBefore.OutputTokens, childAfter.OutputTokens);
        Assert.AreEqual(childBefore.Cost, childAfter.Cost);
        Assert.AreEqual(childBefore.ReservedCost, childAfter.ReservedCost);
        Assert.AreEqual(childBefore.UnknownCost, childAfter.UnknownCost);

        // The original definition is still readable and unchanged, including its declared parent.
        var rebound = await host.Store.EnsureAccountBindingAsync(childDefinition);
        Assert.IsFalse(rebound.Created);
        Assert.AreEqual(childDefinition.ParentAccountId, rebound.Definition.ParentAccountId);
        Assert.AreEqual("usd", rebound.Definition.CostUnit);
        CollectionAssert.AreEqual(new[] { root.Definition.AccountId }, rebound.Ancestors.ToArray());
    }

    [TestMethod]
    public async Task O1_2_NewChildWithCrossCurrencyRequest_IsCurrencyMismatch_AndIsNotInserted()
    {
        await using var host = await InMemoryHost.CreateAsync();
        var root = await host.Store.EnsureAccountBindingAsync(GoalDef("goal-1"));
        var rootBefore = await host.Store.ReadAsync(root.Definition.AccountId);

        var mismatch = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("work-2", root.Definition.AccountId, costUnit: "eur")));
        Assert.AreEqual(BudgetErrorCodes.CurrencyMismatch, mismatch.Code);
        var ids = await host.Store.ListAccountIdsAsync();
        Assert.AreEqual(1, ids.Length, "The rejected cross-currency child must not be inserted.");
        Assert.IsFalse(ids.Any(id => id.Value == "work:work-2"));
        var rootAfter = await host.Store.ReadAsync(root.Definition.AccountId);
        Assert.AreEqual("usd", rootAfter.CostUnit);
        Assert.AreEqual(rootBefore.Revision, rootAfter.Revision);
    }

    [TestMethod]
    public async Task O1_3_ExistingLeafAndStoredAncestorDisagreeOnCurrency_IsCurrencyMismatch_WithoutRepair()
    {
        await using var host = await InMemoryHost.CreateAsync();
        // Only a directly constructed isolated test database may contain a persisted currency inconsistency.
        await using (var db = host.CreateDbContext())
        {
            db.ExecutionBudgetAccounts.Add(Entity(new BudgetAccountId("work:o1-root"), null, "o1-root", "usd"));
            db.ExecutionBudgetAccounts.Add(Entity(new BudgetAccountId("work:o1-leaf"), "work:o1-root", "o1-leaf", "eur"));
            await db.SaveChangesAsync();
        }
        var leafDefinition = WorkDef("o1-leaf", new BudgetAccountId("work:o1-root"), costUnit: "eur");
        var leafBefore = await host.Store.ReadAsync(leafDefinition.AccountId);
        var rootBefore = await host.Store.ReadAsync(new BudgetAccountId("work:o1-root"));

        // The request reuses the leaf's persisted definition; the stored ancestor disagrees, so the ancestor check
        // must fail with currency_mismatch and must not rewrite either row into a repaired currency.
        var mismatch = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(leafDefinition));
        Assert.AreEqual(BudgetErrorCodes.CurrencyMismatch, mismatch.Code);
        Assert.AreEqual(2, (await host.Store.ListAccountIdsAsync()).Length);
        var leafAfter = await host.Store.ReadAsync(leafDefinition.AccountId);
        var rootAfter = await host.Store.ReadAsync(new BudgetAccountId("work:o1-root"));
        Assert.AreEqual("eur", leafAfter.CostUnit, "The stored leaf currency must not be repaired in place.");
        Assert.AreEqual("usd", rootAfter.CostUnit);
        Assert.AreEqual(leafBefore.Revision, leafAfter.Revision);
        Assert.AreEqual(rootBefore.Revision, rootAfter.Revision);
    }

    [TestMethod]
    public async Task O1_4_OriginalCoverage_RootCostUnitConflictPathInvalidAndCancelled_AllStillHold()
    {
        await using var host = await InMemoryHost.CreateAsync();
        // A07: an existing root changing its cost unit is a conflict, not a mismatch.
        var rootDefinition = GoalDef("goal-1", limits: new(MaxCost: 1m));
        var root = await host.Store.EnsureAccountBindingAsync(rootDefinition);
        var rootBefore = await host.Store.ReadAsync(root.Definition.AccountId);
        var rootConflict = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(rootDefinition with { CostUnit = "eur" }));
        Assert.AreEqual(BudgetErrorCodes.Conflict, rootConflict.Code);
        var rootAfter = await host.Store.ReadAsync(root.Definition.AccountId);
        Assert.AreEqual("usd", rootAfter.CostUnit);
        Assert.AreEqual(rootBefore.Revision, rootAfter.Revision);

        // A09: path corruption keeps its own error.
        var self = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(WorkDef("o1-self", new BudgetAccountId("work:o1-self"))));
        Assert.AreEqual(BudgetErrorCodes.PathInvalid, self.Code);

        // A14: persistent cancellation keeps its own error.
        var childDefinition = WorkDef("work-1", root.Definition.AccountId);
        await host.Store.EnsureAccountBindingAsync(childDefinition);
        await host.Store.CancelAsync(new CancelBudget(root.Definition.AccountId, "user.cancelled", 1));
        var cancelled = await Assert.ThrowsExactlyAsync<ExecutionResourceException>(async () =>
            await host.Store.EnsureAccountBindingAsync(childDefinition));
        Assert.AreEqual(BudgetErrorCodes.Cancelled, cancelled.Code);
        Assert.AreEqual(2, (await host.Store.ListAccountIdsAsync()).Length);
    }

    private static BudgetAccountDefinition GoalDef(string goalId, string workspace = "ws-1", string agent = "agent-1",
        string session = "session-1", BudgetLimits? limits = null, string costUnit = "usd",
        BudgetEnforcementMode mode = BudgetEnforcementMode.Enforced)
        => Define(BudgetAccountKind.GoalRun, goalId, null, workspace, agent, session, limits, costUnit, mode);

    private static BudgetAccountDefinition TaskDef(string taskId, string workspace = "ws-1", string agent = "agent-1",
        string session = "session-1", BudgetLimits? limits = null, string costUnit = "usd",
        BudgetEnforcementMode mode = BudgetEnforcementMode.Enforced)
        => Define(BudgetAccountKind.Task, taskId, null, workspace, agent, session, limits, costUnit, mode);

    private static BudgetAccountDefinition WorkDef(string workId, BudgetAccountId? parent, string workspace = "ws-1",
        string agent = "agent-2", string session = "session-2", BudgetLimits? limits = null, string costUnit = "usd",
        BudgetEnforcementMode mode = BudgetEnforcementMode.Enforced)
        => Define(BudgetAccountKind.Work, workId, parent, workspace, agent, session, limits, costUnit, mode);

    private static BudgetAccountDefinition Define(BudgetAccountKind kind, string rootId, BudgetAccountId? parent,
        string workspace, string agent, string session, BudgetLimits? limits, string costUnit,
        BudgetEnforcementMode mode)
        => new(new BudgetAccountId(BudgetAccountDescriptor.Prefix(kind)! + rootId), parent,
            new BudgetIdentity(kind, rootId, workspace, agent, session),
            limits ?? new(MaxOutputTokens: 100_000, MaxCost: 10m), costUnit, mode);

    private static ExecutionBudgetAccountEntity Entity(BudgetAccountId accountId, string? parentId, string rootId,
        string costUnit = "usd")
        => new()
        {
            AccountId = accountId.Value,
            ParentAccountId = parentId,
            RootKind = (int)BudgetAccountKind.Work,
            RootId = rootId,
            WorkspaceId = "ws-1",
            AgentId = "agent-2",
            SessionId = "session-2",
            CostUnit = costUnit,
            MaxOutputTokens = 100_000,
            MaxCost = "10",
            MaxConcurrentAttempts = 2,
            EnforcementMode = (int)BudgetEnforcementMode.Enforced,
        };

    private static async Task<object> CaptureAsync(Func<Task<BudgetAccountBindingResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ExecutionResourceException exception)
        {
            return exception.Code;
        }
    }

    /// <summary>Deterministic barrier: both calls enter the store at the same time, no sleeps and no probability.</summary>
    private static async Task<(object First, object Second)> RaceAsync(
        Func<Task<BudgetAccountBindingResult>> first, Func<Task<BudgetAccountBindingResult>> second)
    {
        using var barrier = new Barrier(2);
        var one = Task.Run(async () =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(15)))
                throw new AssertFailedException("The concurrency barrier timed out.");
            return await CaptureAsync(first);
        });
        var two = Task.Run(async () =>
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(15)))
                throw new AssertFailedException("The concurrency barrier timed out.");
            return await CaptureAsync(second);
        });
        var results = await Task.WhenAll(one, two);
        return (results[0], results[1]);
    }

    private sealed class Factory(Func<PlatformDbContext> create) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => create();
    }

    private sealed class InMemoryHost : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<PlatformDbContext> _options;
        public ExecutionBudgetStore Store { get; }

        private InMemoryHost(SqliteConnection connection, DbContextOptions<PlatformDbContext> options,
            ExecutionBudgetStore store)
        {
            _connection = connection;
            _options = options;
            Store = store;
        }

        public PlatformDbContext CreateDbContext() => new(_options);

        public static async Task<InMemoryHost> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(() => new PlatformDbContext(options));
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            return new(connection, options, new ExecutionBudgetStore(factory));
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }

    /// <summary>One real SQLite file shared by independently constructed Store instances with their own connections.</summary>
    private sealed class FileDatabase : IAsyncDisposable
    {
        private readonly DbContextOptions<PlatformDbContext> _options;

        private FileDatabase(string path, DbContextOptions<PlatformDbContext> options)
        {
            Path = path;
            _options = options;
        }

        private string Path { get; }

        public ExecutionBudgetStore NewStore() => new(new Factory(() => new PlatformDbContext(_options)));

        public static async Task<FileDatabase> CreateAsync()
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pudding-ds-i1-04");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options;
            var factory = new Factory(() => new PlatformDbContext(options));
            using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await ExecutionBudgetSchemaBootstrapper.EnsureCreatedAsync(db, null);
            }
            return new(path, options);
        }

        public async ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            await Task.CompletedTask;
            try
            {
                if (File.Exists(Path)) File.Delete(Path);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the isolated test database only.
            }
        }
    }
}
