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
        Assert.AreEqual("tester", fetched.ResolvedBy);
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
        Assert.AreEqual("tester", fetched.ResolvedBy);

        var pending = await Service.QueryPendingAsync();
        Assert.IsFalse(
            pending.Any(p => p.ApprovalId == record.ApprovalId),
            "已拒绝的审批单不得再出现在待审列表中。");
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
}
