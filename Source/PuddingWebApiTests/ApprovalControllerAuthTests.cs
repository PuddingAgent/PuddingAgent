using System.Net;
using System.Net.Http.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PuddingWebApiTests;

/// <summary>
/// PuddingController.ApprovalController 的授权边界回归（发现 X）。
///
/// 背景：该控制器类上原本没有任何授权特性，而它在生产组合根里**确实被注册了**
/// （PuddingServiceCollectionExtensions.Platform.cs:405 调 AddPuddingController()），
/// MVC 默认应用部件发现使它的路由可达且匿名。实测匿名 GET /api/approval/pending
/// 返回 500（而非 404），证明请求已进入控制器管道，只是 InMemoryApprovalService
/// 依赖的 IConnectionMultiplexer 未注册而在构造期抛错。
///
/// 本用例锁死"匿名必须 401"这一条不变量：它不依赖 service 能否构造，
/// 因此即使将来注册了 Redis，匿名读也仍然被挡住。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ApprovalControllerAuthTests
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

    /// <summary>
    /// 三个匿名端点都必须 401。注意断言的是 401 而**不是**"非 200"：
    /// 修复前它们是 500（DI 缺件），断言 401 才能区分"授权挡住了"与"实现崩了"。
    /// </summary>
    [TestMethod]
    public async Task ApprovalEndpoints_RequireAuthentication()
    {
        using var anonymous = _factory.CreateClient();

        var pending = await anonymous.GetAsync("/api/approval/pending");
        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            pending.StatusCode,
            "匿名读取待审单必须 401：返回体含 ConfirmationCode，泄露即等于确认机制失效。");

        var single = await anonymous.GetAsync($"/api/approval/{Guid.NewGuid():N}");
        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            single.StatusCode,
            "匿名读取单条审批必须 401。");

        var reject = await anonymous.PostAsJsonAsync(
            $"/api/approval/{Guid.NewGuid():N}/reject",
            new { rejectedBy = "anonymous" });
        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            reject.StatusCode,
            "匿名拒绝待审单必须 401：RejectRequest 不含确认码，此前任意可达者都能拒绝任意单。");
    }

    /// <summary>
    /// 已认证主体不得被 401 拦截。当前组合根未注册 IApprovalService ⇒ 预期 500；
    /// 该函数性缺口（死接口）单独立项，不在本用例的断言范围内——
    /// 这里只守住"授权层没有误伤已认证调用者"。
    /// </summary>
    [TestMethod]
    public async Task ApprovalEndpoints_DoNotRejectAuthenticatedCallers()
    {
        var response = await _client.GetAsync("/api/approval/pending");
        Assert.AreNotEqual(
            HttpStatusCode.Unauthorized,
            response.StatusCode,
            "已认证主体不得被 401 拦截。");
    }
}
