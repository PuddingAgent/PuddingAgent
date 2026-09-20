using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingWebApiTests;

/// <summary>
/// PuddingController.ApprovalController 的功能回归（死接口修复）。
/// </summary>
/// <remarks>
/// 背景：四个 <c>/api/approval/*</c> 端点此前在**已认证**请求下恒 500 ——
/// 组合根 <c>AddPuddingController()</c> 从未注册 <c>InMemoryApprovalService</c>，
/// 而它又硬依赖同样从未注册的 <c>IConnectionMultiplexer</c>，构造期即抛错。
/// <see cref="ApprovalControllerAuthTests"/> 只锁住「匿名必须 401」，并显式把
/// 函数性缺口留作单独立项；本文件补上那部分的端到端回归。
///
/// 关键前提：<c>InMemoryApprovalService</c> 注册为**单例**，因此
/// <c>_factory.Services</c> 取到的实例与 HTTP 请求内注入的实例是同一个，
/// 测试可以直接造单并观察端点对它的作用。
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ApprovalControllerFunctionalTests
{
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new CustomWebApplicationFactory();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _factory.Dispose();
    }

    [TestInitialize]
    public void TestInit()
    {
        _client = _factory.CreateClient();
        JwtHelper.SetBearerToken(_client);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _client?.Dispose();
    }

    private InMemoryApprovalService Service => _factory.Services.GetRequiredService<InMemoryApprovalService>();

    [TestMethod]
    public async Task Pending_Is_Reachable_For_Authenticated_Caller()
    {
        var response = await _client.GetAsync("/api/approval/pending");

        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            "已认证读取待审单不得再落 500：那是 DI 无法构造服务的死接口症状。");
    }

    [TestMethod]
    public async Task Confirm_With_Correct_Code_Succeeds_And_Resolves_The_Record()
    {
        var record = await Service.RequestApprovalAsync("session-confirm-ok", "workspace-1", "delete file");

        var response = await _client.PostAsJsonAsync(
            $"/api/approval/{record.ApprovalId}/confirm",
            new { confirmationCode = record.ConfirmationCode!, confirmedBy = "tester" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var fetched = await Service.GetAsync(record.ApprovalId);
        Assert.IsNotNull(fetched, "已确认的审批单必须仍可读取。");
        Assert.AreEqual(ApprovalStatus.Confirmed, fetched!.Status);
        Assert.AreEqual(
            "admin",
            fetched.ResolvedBy,
            "审计主体必须来自认证上下文（JwtHelper 默认主体 admin），而不是请求体自填的 confirmedBy。");
        Assert.IsNotNull(fetched.ResolvedAt);
    }

    [TestMethod]
    public async Task Confirm_With_Wrong_Code_Is_Rejected_And_Leaves_Record_Pending()
    {
        var record = await Service.RequestApprovalAsync("session-confirm-bad", "workspace-1", "delete file");
        var wrong = record.ConfirmationCode == "00000000" ? "11111111" : "00000000";

        var response = await _client.PostAsJsonAsync(
            $"/api/approval/{record.ApprovalId}/confirm",
            new { confirmationCode = wrong, confirmedBy = "attacker" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var fetched = await Service.GetAsync(record.ApprovalId);
        Assert.IsNotNull(fetched);
        Assert.AreEqual(
            ApprovalStatus.Pending,
            fetched!.Status,
            "错误确认码不得改变审批单状态。");
        Assert.IsNull(fetched.ResolvedBy, "失败确认不得写入确认人。");
    }

    [TestMethod]
    public async Task Confirm_With_Right_Code_On_Unknown_Approval_Is_Rejected()
    {
        // 不存在的 ApprovalId：不得因为确认码格式正确就放行。
        var response = await _client.PostAsJsonAsync(
            $"/api/approval/{Guid.NewGuid():N}/confirm",
            new { confirmationCode = "01234567", confirmedBy = "tester" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Reject_Resolves_The_Record_And_Pending_No_Longer_Lists_It()
    {
        var record = await Service.RequestApprovalAsync("session-reject", "workspace-1", "delete file");

        var reject = await _client.PostAsJsonAsync(
            $"/api/approval/{record.ApprovalId}/reject",
            new { rejectedBy = "tester" });
        Assert.AreEqual(HttpStatusCode.OK, reject.StatusCode);

        var fetched = await Service.GetAsync(record.ApprovalId);
        Assert.AreEqual(ApprovalStatus.Rejected, fetched!.Status);
        Assert.AreEqual(
            "admin",
            fetched.ResolvedBy,
            "审计主体必须来自认证上下文，而不是请求体自填的 rejectedBy。");

        var pending = await Service.QueryPendingAsync();
        Assert.IsFalse(
            pending.Any(p => p.ApprovalId == record.ApprovalId),
            "已拒绝的审批单不得再出现在待审列表中。");
    }

    [TestMethod]
    public async Task Failed_Attempts_Exhaust_And_Void_The_Approval()
    {
        var record = await Service.RequestApprovalAsync("session-exhaust", "workspace-1", "delete file");
        var wrong = record.ConfirmationCode == "00000000" ? "11111111" : "00000000";

        // 前 MaxFailedAttempts - 1 次错误只计数，不作废。
        for (var i = 0; i < ApprovalCode.MaxFailedAttempts - 1; i++)
        {
            var attempt = await _client.PostAsJsonAsync(
                $"/api/approval/{record.ApprovalId}/confirm",
                new { confirmationCode = wrong, confirmedBy = "attacker" });
            Assert.AreEqual(
                HttpStatusCode.BadRequest,
                attempt.StatusCode,
                $"第 {i + 1} 次错误提交应返回 400。");
        }

        var midway = await Service.GetAsync(record.ApprovalId);
        Assert.IsNotNull(midway);
        Assert.AreEqual(ApprovalStatus.Pending, midway!.Status, "未达上限前不得作废。");
        Assert.AreEqual(ApprovalCode.MaxFailedAttempts - 1, midway.FailedAttempts);

        // 第 MaxFailedAttempts 次错误本身即触发作废。
        var last = await _client.PostAsJsonAsync(
            $"/api/approval/{record.ApprovalId}/confirm",
            new { confirmationCode = wrong, confirmedBy = "attacker" });
        Assert.AreEqual(HttpStatusCode.BadRequest, last.StatusCode);

        var exhausted = await Service.GetAsync(record.ApprovalId);
        Assert.IsNotNull(exhausted);
        Assert.AreEqual(
            ApprovalStatus.Expired,
            exhausted!.Status,
            "达到失败上限后审批单必须作废，而不是继续接受尝试。");
        Assert.IsFalse(
            (await Service.QueryPendingAsync()).Any(p => p.ApprovalId == record.ApprovalId),
            "已作废的审批单不得再出现在待审列表中。");

        // 关键：作废后即使提交正确确认码也不得放行，否则阈值形同虚设。
        var correctAfterExhaustion = await _client.PostAsJsonAsync(
            $"/api/approval/{record.ApprovalId}/confirm",
            new { confirmationCode = record.ConfirmationCode!, confirmedBy = "attacker" });
        Assert.AreEqual(
            HttpStatusCode.BadRequest,
            correctAfterExhaustion.StatusCode,
            "作废后即使确认码正确也必须被拒——否则攻击者只需继续爆破。");
    }

    [TestMethod]
    public async Task Confirm_Twice_Is_Rejected_The_Second_Time()
    {
        var record = await Service.RequestApprovalAsync("session-twice", "workspace-1", "delete file");
        var body = new { confirmationCode = record.ConfirmationCode!, confirmedBy = "tester" };

        var first = await _client.PostAsJsonAsync($"/api/approval/{record.ApprovalId}/confirm", body);
        var second = await _client.PostAsJsonAsync($"/api/approval/{record.ApprovalId}/confirm", body);

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(
            HttpStatusCode.BadRequest,
            second.StatusCode,
            "已解析的审批单不得被二次确认。");
    }

    [TestMethod]
    public async Task Confirm_Does_Not_Trust_Caller_Supplied_ConfirmedBy()
    {
        // 请求体蓄意填一个假名：审计字段必须仍然记录**认证主体**，
        // 否则任何调用者都能伪造「是谁批准的」。
        var record = await Service.RequestApprovalAsync("session-forged-by", "workspace-1", "delete file");

        var response = await _client.PostAsJsonAsync(
            $"/api/approval/{record.ApprovalId}/confirm",
            new { confirmationCode = record.ConfirmationCode!, confirmedBy = "somebody-else" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var fetched = await Service.GetAsync(record.ApprovalId);
        Assert.IsNotNull(fetched);
        Assert.AreEqual(
            "admin",
            fetched!.ResolvedBy,
            "ResolvedBy 必须是认证主体（JWT 的 NameIdentifier），绝不能是请求体自填值。");
        Assert.AreNotEqual("somebody-else", fetched.ResolvedBy, "自填姓名一律不得进入审计字段。");
    }

    [TestMethod]
    public async Task Reject_Does_Not_Trust_Caller_Supplied_RejectedBy()
    {
        var record = await Service.RequestApprovalAsync("session-forged-reject", "workspace-1", "delete file");

        var response = await _client.PostAsJsonAsync(
            $"/api/approval/{record.ApprovalId}/reject",
            new { rejectedBy = "somebody-else" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var fetched = await Service.GetAsync(record.ApprovalId);
        Assert.IsNotNull(fetched);
        Assert.AreEqual(
            "admin",
            fetched!.ResolvedBy,
            "ResolvedBy 必须是认证主体，绝不能是请求体自填值。");
        Assert.AreNotEqual("somebody-else", fetched.ResolvedBy);
    }
}
