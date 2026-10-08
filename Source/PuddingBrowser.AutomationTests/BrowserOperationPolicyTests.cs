using Pudding.Contracts.Desktop;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 方案 §3.2 表格的机器可验快照：授权类别、所需范围、是否需要控制租约、
/// 以及接管期间是否被拒绝。<b>这些判据是唯一真源</b>，不允许由能力名再推断一遍。
/// </summary>
[TestClass]
public sealed class BrowserOperationPolicyTests
{
    private sealed record Expected(
        BrowserAutomationOperation Operation,
        BrowserGrantKind GrantKind,
        BrowserPageGrantScope Scope,
        bool RequiresControlLease,
        bool BlockedWhileUserControlling);

    private static readonly Expected[] Table =
    [
        // context/tab 元数据清单：可信身份 + 允许管理的 context；接管期间允许有界元数据读取。
        new(BrowserAutomationOperation.ContextsList, BrowserGrantKind.None, BrowserPageGrantScope.None, false, false),
        // snapshot/locate/wait/screenshot/page_state：page read grant；接管期间允许只读观察。
        new(BrowserAutomationOperation.PageState, BrowserGrantKind.Page, BrowserPageGrantScope.Read, false, false),
        new(BrowserAutomationOperation.Snapshot, BrowserGrantKind.Page, BrowserPageGrantScope.Read, false, false),
        new(BrowserAutomationOperation.Locate, BrowserGrantKind.Page, BrowserPageGrantScope.Read, false, false),
        new(BrowserAutomationOperation.WaitFor, BrowserGrantKind.Page, BrowserPageGrantScope.Read, false, false),
        new(BrowserAutomationOperation.Screenshot, BrowserGrantKind.Page, BrowserPageGrantScope.Read, false, false),
        // navigate/interact/坐标输入/脚本：page write grant + 当前控制租约；接管期间拒绝。
        new(BrowserAutomationOperation.Navigate, BrowserGrantKind.Page, BrowserPageGrantScope.Write, true, true),
        new(BrowserAutomationOperation.Interact, BrowserGrantKind.Page, BrowserPageGrantScope.Write, true, true),
        new(BrowserAutomationOperation.CoordinateInput, BrowserGrantKind.Page, BrowserPageGrantScope.Write, true, true),
        new(BrowserAutomationOperation.Script, BrowserGrantKind.Page, BrowserPageGrantScope.Write, true, true),
        // context/tab 生命周期：任务级 management 授权；接管期间拒绝副作用。
        new(BrowserAutomationOperation.ContextCreate, BrowserGrantKind.Management, BrowserPageGrantScope.Manage, false, true),
        new(BrowserAutomationOperation.ContextClose, BrowserGrantKind.Management, BrowserPageGrantScope.Manage, false, true),
        new(BrowserAutomationOperation.TabNew, BrowserGrantKind.Management, BrowserPageGrantScope.Manage, false, true),
        // 关闭已有页：management 授权 **加上** 该页自己的 read 授权。
        new(BrowserAutomationOperation.TabClose, BrowserGrantKind.Page,
            BrowserPageGrantScope.Manage | BrowserPageGrantScope.Read, false, true),
    ];

    [TestMethod]
    public void Policy_Table_Matches_The_Design()
    {
        foreach (var expected in Table)
        {
            Assert.AreEqual(
                expected.GrantKind,
                BrowserOperationPolicy.RequiredGrantKind(expected.Operation),
                $"grant kind for {expected.Operation}");
            Assert.AreEqual(
                expected.Scope,
                BrowserOperationPolicy.RequiredScope(expected.Operation),
                $"required scope for {expected.Operation}");
            Assert.AreEqual(
                expected.RequiresControlLease,
                BrowserOperationPolicy.RequiresControlLease(expected.Operation),
                $"lease requirement for {expected.Operation}");
            Assert.AreEqual(
                expected.BlockedWhileUserControlling,
                BrowserOperationPolicy.IsBlockedWhileUserControlling(expected.Operation),
                $"takeover visibility for {expected.Operation}");
        }
    }

    [TestMethod]
    public void Every_Operation_Is_Covered_By_The_Table()
    {
        CollectionAssert.AreEquivalent(
            Table.Select(row => row.Operation).ToArray(),
            BrowserOperationPolicy.All.ToArray(),
            "a new operation must be classified in the §3.2 table, not silently defaulted");
    }

    [TestMethod]
    public void Side_Effecting_And_ReadOnly_Partition_Is_Exhaustive()
    {
        foreach (var operation in BrowserOperationPolicy.All)
        {
            var sideEffecting = BrowserOperationPolicy.IsSideEffecting(operation);
            Assert.AreEqual(
                !sideEffecting,
                BrowserOperationPolicy.IsReadOnly(operation),
                $"{operation} must be either side-effecting or read-only");
        }
    }

    [TestMethod]
    public void Page_Target_Requirement_Follows_The_Grant_Kind()
    {
        foreach (var operation in BrowserOperationPolicy.All)
        {
            Assert.AreEqual(
                BrowserOperationPolicy.RequiredGrantKind(operation) == BrowserGrantKind.Page,
                BrowserOperationPolicy.RequiresPageTarget(operation),
                $"{operation}: only page grants need an explicit context/page target");
        }
    }

    [TestMethod]
    public void Contexts_List_Needs_No_Grant_But_Anything_Else_Does()
    {
        Assert.AreEqual(BrowserGrantKind.None, BrowserOperationPolicy.RequiredGrantKind(BrowserAutomationOperation.ContextsList));

        foreach (var operation in BrowserOperationPolicy.All.Where(
                     operation => operation != BrowserAutomationOperation.ContextsList))
        {
            Assert.IsTrue(
                BrowserOperationPolicy.RequiredGrantKind(operation) != BrowserGrantKind.None,
                $"{operation} must require an authorization");
        }
    }

    [TestMethod]
    public void Operation_Must_Be_Registered_To_Be_Classified()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserOperationPolicy.RequiredScope((BrowserAutomationOperation)99));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserOperationPolicy.RequiredGrantKind((BrowserAutomationOperation)99));
    }

    [TestMethod]
    public void Request_Requires_A_Page_Target_For_Page_Operations()
    {
        // 显式页面目标是跨进程调用的确定性要求：缺目标必须构造期失败，而不是隐式用「当前 Tab」。
        Assert.ThrowsExactly<ArgumentException>(
            () => _ = new BrowserAutomationRequest(
                BrowserAutomationOperation.Snapshot,
                B0Fixture.Agent(),
                Pudding.Contracts.OperationId.NewId(),
                B0Fixture.Generation));

        // 浏览器作用域元数据没有页面可指，允许不带目标。
        var metadata = new BrowserAutomationRequest(
            BrowserAutomationOperation.ContextsList,
            B0Fixture.Agent(),
            Pudding.Contracts.OperationId.NewId(),
            B0Fixture.Generation);

        Assert.IsNull(metadata.Target);
    }

    [TestMethod]
    public void Request_Rejects_An_Unregistered_Operation()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => _ = new BrowserAutomationRequest(
                (BrowserAutomationOperation)99,
                B0Fixture.Agent(),
                Pudding.Contracts.OperationId.NewId(),
                B0Fixture.Generation));
    }

    [TestMethod]
    public void Request_Derives_Scope_And_Lease_From_The_Policy_Not_From_The_Caller()
    {
        var write = B0Fixture.Request(BrowserAutomationOperation.Interact);
        var read = B0Fixture.Request(BrowserAutomationOperation.Snapshot);
        var manage = B0Fixture.Request(BrowserAutomationOperation.TabNew);

        Assert.AreEqual(BrowserPageGrantScope.Write, write.RequiredScope);
        Assert.IsTrue(write.RequiresControlLease);
        Assert.AreEqual(BrowserPageGrantScope.Read, read.RequiredScope);
        Assert.IsFalse(read.RequiresControlLease);
        Assert.AreEqual(BrowserPageGrantScope.Manage, manage.RequiredScope);
        Assert.IsFalse(manage.RequiresControlLease);
        Assert.IsNull(manage.Target, "management operations have no page to authorize yet");
    }

    [TestMethod]
    public void Denial_Reason_Wire_Names_Are_Stable()
    {
        var names = BrowserAuthorizationDenialWire.All
            .Select(BrowserAuthorizationDenialWire.NameOf)
            .ToArray();

        CollectionAssert.Contains(names, "user_takeover");
        CollectionAssert.Contains(names, "no_grant");
        CollectionAssert.Contains(names, "no_management_grant");
        CollectionAssert.Contains(names, "scope_insufficient");
        CollectionAssert.Contains(names, "frame_mismatch");
        CollectionAssert.Contains(names, "epoch_stale");
        CollectionAssert.Contains(names, "page_busy");
        CollectionAssert.Contains(names, "not_connected");

        foreach (var denial in BrowserAuthorizationDenialWire.All)
        {
            Assert.IsTrue(BrowserAuthorizationDenialWire.TryParse(
                BrowserAuthorizationDenialWire.NameOf(denial), out var parsed));
            Assert.AreEqual(denial, parsed);
        }

        Assert.IsFalse(BrowserAuthorizationDenialWire.TryParse("brand_new_reason", out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserAuthorizationDenialWire.NameOf((BrowserAuthorizationDenial)1234));
    }
}
