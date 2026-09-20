using System.Net;
using System.Net.Http.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PuddingWebApiTests;

/// <summary>
/// <c>POST /api/sessions/{sessionId}/decide</c>（Chat 审批卡片决定端点）的契约回归。
/// </summary>
/// <remarks>
/// <para><b>为什么现在补测</b>：该端点此前**零覆盖**。审计发现后端**没有任何
/// <c>approval.requested</c> 事件的写入点**——全仓只见到常量定义
/// （<c>ConversationEventStore.cs:608</c>）与 schema 校验分支（<c>:485</c>），
/// 没有生产者。于是审批卡片永不出现、<c>decide</c> 永远落 404，是一条"半成品"
/// 链路：请求校验、反向查找、幂等判定、过期判定、事件追加**全部就绪**，唯独
/// 缺生产者。另注：<c>Decisions.AlwaysAllow</c>（<c>"always_allow"</c>）在服务端
/// **也没有任何消费者**（同名命中都是无关的任务闸门 <c>ManualAlwaysAllowFence</c>）。</para>
///
/// <para><b>本文件的定位</b>：在不改动生产代码的前提下，把该端点的**可达性与
/// 契约**固定下来，尤其把它在「缺生产者」状态下的可观察行为（404
/// <c>approval_not_found</c>）显式固化。将来补上生产者后，这些断言会 fail loud
/// 地提示需要改写为「先造 approval.requested，再决定」的正向流程。</para>
///
/// <para>依赖可解析性由 <see cref="Decide_Is_Reachable_For_Authenticated_Caller"/>
/// 守护：若 <c>IConversationEventStore</c> 之类依赖哪天没被注册，它会以 500 而非
/// 404 失败（迭代 #15 记录过的死接口症状）。</para>
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class SessionApprovalDecideTests
{
    private static CustomWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    private const string AnySession = "session-without-any-approval";

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
        _client.Dispose();
    }

    private Task<HttpResponseMessage> DecideAsync(string sessionId, string? approvalId, string? decision)
        => _client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/decide",
            new { approvalId, decision });

    [TestMethod]
    public async Task Decide_Is_Reachable_For_Authenticated_Caller()
    {
        var response = await DecideAsync(AnySession, "some-approval", "allow_once");

        Assert.AreNotEqual(
            HttpStatusCode.InternalServerError,
            response.StatusCode,
            "端点必须可达：500 意味着依赖无法构造（迭代 #15 记录过的死接口症状）。");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Decide_Without_Any_Approval_Request_Returns_Approval_Not_Found()
    {
        // 固化「生产者缺失」下的可观察行为：后端从不写 approval.requested，
        // 因此任何 decide 都会走到这里。补上生产者后本用例须改写为正向流程。
        var response = await DecideAsync(AnySession, "unknown-approval-id", "allow_once");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(
            body,
            "approval_not_found",
            "缺生产者时必须明确报 approval_not_found，而不是静默成功。");
    }

    [TestMethod]
    public async Task Decide_With_Unknown_Decision_Value_Is_Rejected()
    {
        var response = await DecideAsync(AnySession, "some-approval", "maybe");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "invalid_decision");
    }

    [TestMethod]
    public async Task Decide_Without_ApprovalId_Is_Rejected()
    {
        var response = await DecideAsync(AnySession, null, "allow_once");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "invalid_decision");
    }

    [TestMethod]
    public async Task Decide_With_Whitespace_ApprovalId_Is_Rejected()
    {
        var response = await DecideAsync(AnySession, "   ", "allow_once");

        Assert.AreEqual(
            HttpStatusCode.BadRequest,
            response.StatusCode,
            "纯空白 approvalId 必须被当作缺失处理。");
    }

    [TestMethod]
    public async Task Decide_Without_Token_Is_Unauthorized()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync(
            $"/api/sessions/{AnySession}/decide",
            new { approvalId = "some-approval", decision = "allow_once" });

        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            response.StatusCode,
            "匿名调用必须被类级 [Authorize] 拦下——且请求校验不得先于授权生效。");
    }

    [TestMethod]
    public async Task Decide_With_Decision_Padded_By_Spaces_Is_Accepted_As_Valid()
    {
        // decision 是 Trim 后比对的；补白后仍属合法值，故应越过校验、
        // 走到「找不到审批请求」这一步（404），而不是被判 400。
        var response = await DecideAsync(AnySession, "some-approval", "  allow_once  ");

        Assert.AreEqual(
            HttpStatusCode.NotFound,
            response.StatusCode,
            "带空白的合法 decision 应被 Trim 后接受，而不是误判为 invalid_decision。");
    }
}
