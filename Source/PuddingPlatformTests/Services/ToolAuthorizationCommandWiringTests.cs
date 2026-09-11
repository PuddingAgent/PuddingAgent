using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Conversation;
using PuddingRuntime.Services.Tools;

namespace PuddingPlatformTests.Services;

/// <summary>
/// P0-AUTH-A：/authorize 人工批准闭环接线回归。
///
/// 背景：3df7d2a 把 tool approval 的 need_human 票据从 Denied 改成 Pending，
/// 并给出 "Ask the user to approve it with /authorize ... then retry" 引导文案；
/// 但 SystemCommandHandler 没有 SystemCommandKind.Authorization 分支，
/// /authorize 落到兜底 "this system command is not implemented yet."，闭环断裂。
///
/// 放行路径（父级已核实）：AgentFirewall Gate 4 先查 IToolAuthorizationService.CheckAsync
/// （grant 通道，AgentFirewall.cs:192），再回退 IToolApprovalService.CheckAsync（票据通道）。
/// 因此 /authorize 写入 grant 后，重试即可放行。
/// </summary>
[TestClass]
public sealed class ToolAuthorizationCommandWiringTests
{
    private const string WorkspaceId = "workspace-1";
    private const string SessionId = "session-1";
    private const string AgentId = "agent-1";
    private const string UserId = "user-1";
    private const string ToolId = "sample_high";

    [TestMethod]
    public async Task AuthorizeCommand_CreatesRuntimeGrant_AndReleasesBlockedRetry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var authorization = new InMemoryToolAuthorizationService();
        var handler = CreateHandler(db, new RuntimeControlService(), authorization);

        var before = await authorization.CheckAsync(AuthorizationContext(UserId), Descriptor());
        Assert.IsFalse(before.IsAuthorized, "未授权时必须要求人工授权。");

        var result = await handler.HandleAsync(Request("/authorize sample_high once", "request-authorize"));

        StringAssert.Contains(result.Message, "Authorization granted");
        Assert.IsFalse(
            result.Message.Contains("not implemented yet", StringComparison.Ordinal),
            "P0-AUTH-A：/authorize 不得再落到 'not implemented yet' 兜底文案。");

        var after = await authorization.CheckAsync(AuthorizationContext(UserId), Descriptor());
        Assert.IsTrue(after.IsAuthorized, $"人工授权后重试必须放行，实际：{after.Message}");

        // 系统命令边界不变：只写 user/agent transcript 对，不创建执行命令 / Turn。
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }

    [TestMethod]
    public async Task DenyAndRevokeCommands_AreHandledBySystemCommandHandler()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var authorization = new InMemoryToolAuthorizationService();
        var handler = CreateHandler(db, new RuntimeControlService(), authorization);

        await handler.HandleAsync(Request("/authorize sample_high session", "request-grant"));
        Assert.IsTrue(
            (await authorization.CheckAsync(AuthorizationContext(UserId), Descriptor())).IsAuthorized);

        var denied = await handler.HandleAsync(Request("/deny sample_high", "request-deny"));
        StringAssert.Contains(denied.Message, "Authorization denied");
        Assert.IsFalse(
            (await authorization.CheckAsync(AuthorizationContext(UserId), Descriptor())).IsAuthorized,
            "/deny 必须清掉已有 grant。");

        await handler.HandleAsync(Request("/authorize sample_high session", "request-regrant"));
        var revoked = await handler.HandleAsync(Request("/revoke sample_high", "request-revoke"));
        StringAssert.Contains(revoked.Message, "Authorization revoked");
        Assert.IsFalse(
            (await authorization.CheckAsync(AuthorizationContext(UserId), Descriptor())).IsAuthorized,
            "/revoke 必须清掉已有 grant。");
    }

    [TestMethod]
    public async Task AuthorizationCommand_FromNonPrivilegedUser_IsRejected_AndCreatesNoGrant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var authorization = new InMemoryToolAuthorizationService();
        var handler = CreateHandler(db, new RuntimeControlService(), authorization);

        var result = await handler.HandleAsync(new SystemCommandRequest(
            ConversationId: SessionId,
            WorkspaceId: WorkspaceId,
            AgentId: AgentId,
            UserId: "gateway:user-hash",
            ClientRequestId: "request-denied",
            ClientMessageId: "user-message-denied",
            ResponseMessageId: "system-message-denied",
            CommandText: "/authorize sample_high once",
            IsPrivilegedUser: false,
            SourceChannel: "feishu",
            ExternalUserId: "ou_not_allowed"));

        StringAssert.Contains(result.Message, "Permission denied");
        Assert.IsFalse(
            (await authorization.CheckAsync(AuthorizationContext("gateway:user-hash"), Descriptor())).IsAuthorized,
            "非特权用户不得获得任何 grant。");
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
    }

    [TestMethod]
    public async Task NeedHumanPendingTicket_ThenAuthorizeCommand_ClosesHumanApprovalLoop()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var authorization = new InMemoryToolAuthorizationService();
        var handler = CreateHandler(db, new RuntimeControlService(), authorization);

        var ticketStore = new InMemoryToolApprovalTicketStore();
        var approval = new InMemoryToolApprovalService(new NeedHumanToolApprovalReviewer(), ticketStore);

        // 1) 自动审批要求人工决策：NeedHuman → Pending（3df7d2a 语义）。
        var submit = await approval.SubmitAsync(ApprovalTicketRequest(), TicketIdentity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.NeedHuman, submit.Decision, submit.DecisionReason);
        Assert.AreEqual(ToolApprovalTicketStatus.Pending, submit.Status);
        StringAssert.Contains(submit.RecommendedNextStep ?? string.Empty, "/authorize");

        // 2) 防回归：Pending 票据绝不被 CheckAsync 命中（Status == Approved 过滤未放宽）。
        var ticketCheck = await approval.CheckAsync(ExecutionCheck(), Descriptor());
        Assert.AreNotEqual(
            submit.TicketId,
            ticketCheck.TicketId,
            "Pending 票据不得被 CheckAsync 直接命中，人工批准前必须保持阻塞。");

        Assert.IsFalse(
            (await authorization.CheckAsync(AuthorizationContext(UserId), Descriptor())).IsAuthorized,
            "人工授权前重试必须仍然被拒。");

        // 3) 人工批准：/authorize 经 SystemCommandHandler 写入持久 grant。
        var commandResult = await handler.HandleAsync(
            Request("/authorize sample_high once", "request-human-approve"));
        StringAssert.Contains(commandResult.Message, "Authorization granted");

        // 4) AgentFirewall Gate 4 先查的就是这条 grant → 重试放行，闭环成立。
        var after = await authorization.CheckAsync(AuthorizationContext(UserId), Descriptor());
        Assert.IsTrue(after.IsAuthorized, $"人工 /authorize 后重试必须放行，实际：{after.Message}");

        // 5) 票据状态机增强（方案 B：票据改判 Approved + 审计事件）不在本包范围，
        //    人工放行只走 grant 通道；此处锁定当前行为，避免悄悄扩大批准面。
        var stored = await ticketStore.GetAsync(submit.TicketId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(ToolApprovalTicketStatus.Pending, stored!.Status);
    }

    private static SystemCommandRequest Request(string commandText, string requestId) =>
        new(
            ConversationId: SessionId,
            WorkspaceId: WorkspaceId,
            AgentId: AgentId,
            UserId: UserId,
            ClientRequestId: requestId,
            ClientMessageId: $"user-{requestId}",
            ResponseMessageId: $"system-{requestId}",
            CommandText: commandText);

    private static ToolDescriptor Descriptor() => new()
    {
        ToolId = ToolId,
        Name = "Sample high",
        Description = "High-risk sample tool used by the authorization command wiring tests.",
        PermissionLevel = ToolPermissionLevel.High,
        Safety = ToolSafetyFlags.RequiresShell,
    };

    private static ToolAuthorizationContext AuthorizationContext(string userId) => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
        UserId = userId,
        ToolId = ToolId,
    };

    private static ToolApprovalIdentity TicketIdentity() => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
        UserId = UserId,
    };

    private static ToolApprovalExecutionRequest ExecutionCheck() => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
        UserId = UserId,
        ToolId = ToolId,
        ActualArgumentsJson = "{}",
    };

    private static ToolApprovalTicketRequest ApprovalTicketRequest() => new()
    {
        ToolId = ToolId,
        CommandName = "sample high",
        Purpose = "Execute the focused high-risk sample tool in a test.",
        Necessity = "The human approval loop must be verified end to end.",
        FactBasis = ["The test constructed this sample tool descriptor."],
        RequestedArgumentsJson = "{}",
        TargetResources = [ToolId],
        AuthorizedArea = [WorkspaceId],
        OutsideAuthorizedAreaReason = null,
        MayDamageOrDeleteData = false,
        IsIrreversibleOperation = false,
        BackupTaken = false,
        RollbackPlan = "No mutation is expected from this sample tool.",
        OperationContext = "MSTest local runtime test context.",
        OperationPlan = "Call the sample tool once with exact planned arguments.",
        OperationSteps =
        [
            new ToolApprovalOperationStep
            {
                StepNumber = 1,
                Command = "sample_high {}",
                RequestedArgumentsJson = "{}",
                TargetObject = ToolId,
                Purpose = "Verify the human authorization loop.",
                ExpectedEffect = "The tool returns its fixed sample output.",
                Reasonableness = "High-risk sample tool with no side effects.",
                StopCondition = "One invocation is enough to verify the loop.",
                RollbackForStep = "Nothing to roll back.",
            },
        ],
        TemporaryFileEvidence = null,
        MayExposeSecrets = false,
        UserConsentStatus = ToolApprovalUserConsentStatus.Implied,
        AlternativesConsidered = ["No lower-risk call exercises runtime high-risk authorization."],
        RequestedScope = ToolApprovalScope.Once,
        RiskNotes = "No secret or destructive behavior is present in the sample tool.",
    };

    private static SystemCommandHandler CreateHandler(
        PlatformDbContext db,
        IRuntimeControlService runtime,
        IToolAuthorizationService toolAuthorizationService) =>
        new(
            db,
            runtime,
            new UnexpectedRequestCompactionHandler(),
            new UnexpectedSystemStatusSnapshotProvider(),
            new UnexpectedGoalCommandService(),
            toolAuthorizationService,
            NullLogger<SystemCommandHandler>.Instance);

    private sealed class NeedHumanToolApprovalReviewer : IToolApprovalReviewer
    {
        public Task<ToolApprovalReviewResult> ReviewAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            CancellationToken ct = default) =>
            Task.FromResult(new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.NeedHuman,
                DecisionReason = "当前工作空间不具有审计类型的agent",
                RequiresHumanAuthorization = true,
            });
    }

    private sealed class UnexpectedRequestCompactionHandler : IRequestCompactionHandler
    {
        public Task<CompactionResult> HandleAsync(
            RequestCompactionCommand command,
            CancellationToken ct) =>
            throw new AssertFailedException("This test must not request compaction.");
    }

    private sealed class UnexpectedSystemStatusSnapshotProvider : ISystemStatusSnapshotProvider
    {
        public Task<SystemStatusSnapshot> GetAsync(
            SystemStatusSnapshotRequest request,
            CancellationToken ct = default) =>
            throw new AssertFailedException("This test must not request a status snapshot.");
    }

    private sealed class UnexpectedGoalCommandService : IGoalCommandService
    {
        public Task<GoalCommandResult> ExecuteAsync(
            GoalCommandRequest request,
            CancellationToken ct = default) =>
            throw new AssertFailedException("This test must not execute a goal command.");
    }
}
