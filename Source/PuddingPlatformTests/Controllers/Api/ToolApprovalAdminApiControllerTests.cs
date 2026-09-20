using Microsoft.AspNetCore.Mvc;
using PuddingCode.Tools;
using PuddingPlatform.Controllers.Api;
using PuddingRuntime.Services.Tools;

namespace PuddingPlatformTests.Controllers.Api;

/// <summary>
/// 管理 API 的来源（source）契约回归网。
/// <para>
/// 事故背景（2026-09-21）：后端 `ToolApprovalAllowlistRuleSource` 追加 `Classifier`（提交 `34e41dfb`）后，
/// 本控制器没有同步，产生两处**真实缺陷**：
/// ① `FormatSource` 用 `_ =&gt; "human"` 兜底 ⇒ **分类器落的规则被当作 `human` 上报**（审计溯源字段报错）；
/// ② `TryParseSource` 不认 `classifier` ⇒ 前端编辑/禁用分类器规则时收到 **400**（管理端无法管理该来源的规则）。
/// 本文件钉住这两条，避免"后端加来源、前端/管理 API 漏同步"再次复发。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolApprovalAdminApiControllerTests
{
    private const string ClassifierSource = "classifier";

    private static ToolApprovalAdminApiController CreateController()
        => new(new InMemoryToolApprovalAllowlistStore(), new InMemoryToolApprovalAuditStore());

    private static ToolApprovalAdminApiController.AllowlistRuleMutationDto Mutation(
        string? source,
        string status = "enabled")
        => new()
        {
            ToolId = "shell",
            Command = "dir",
            Source = source,
            Status = status,
        };

    private static string? ReadString(object? payload, string property)
        => payload?.GetType().GetProperty(property)?.GetValue(payload)?.ToString();

    /// <summary>
    /// 读取 <c>MapRule</c> 返回对象上的 RuleId。
    /// 注意：这里读的是<span>未序列化</span>的匿名对象，属性名与 C# 声明一致（<c>RuleId</c>）——
    /// 只有 <c>MapRule</c> 里显式写成 <c>source = ...</c>/<c>status = ...</c> 的两个属性才是小写。
    /// （线上 JSON 的 camelCase 由 MVC 的序列化策略产生，与前端 DTO 一致。）
    /// </summary>
    private static string? ReadRuleId(object? payload) => ReadString(payload, "RuleId");

    private static async Task<string> CreateRuleAsync(
        ToolApprovalAdminApiController controller,
        string? source)
    {
        var result = await controller.CreateAllowlistRule(Mutation(source), CancellationToken.None);
        var created = result as CreatedAtActionResult;
        Assert.IsNotNull(created, $"预期创建成功，实际为 {result.GetType().Name}。");
        var ruleId = ReadRuleId(created.Value);
        Assert.IsFalse(string.IsNullOrWhiteSpace(ruleId), "创建结果必须带回 RuleId。");
        return ruleId!;
    }

    [TestMethod]
    public async Task CreateAllowlistRule_AcceptsClassifierSource_AndEchoesItBack()
    {
        var controller = CreateController();

        var result = await controller.CreateAllowlistRule(
            Mutation(ClassifierSource),
            CancellationToken.None);

        var created = result as CreatedAtActionResult;
        Assert.IsNotNull(created, $"分类器来源必须可创建，实际为 {result.GetType().Name}。");
        Assert.AreEqual(ClassifierSource, ReadString(created.Value, "source"));
    }

    [TestMethod]
    public async Task UpdateAllowlistRule_ClassifierRule_IsEditable_AndSourceIsPreserved()
    {
        var controller = CreateController();
        var ruleId = await CreateRuleAsync(controller, ClassifierSource);

        var result = await controller.UpdateAllowlistRule(
            ruleId,
            Mutation(ClassifierSource, status: "disabled"),
            CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(
            ok,
            "分类器来源的规则必须可在管理端修改（禁用/编辑）——曾因 TryParseSource 不认 classifier 而返回 400。");
        Assert.AreEqual(ClassifierSource, ReadString(ok.Value, "source"));
        Assert.AreEqual("disabled", ReadString(ok.Value, "status"));
    }

    [TestMethod]
    public async Task GetAllowlistRule_ClassifierRule_ReportsClassifierSource_NotHuman()
    {
        var controller = CreateController();
        var ruleId = await CreateRuleAsync(controller, ClassifierSource);

        var result = await controller.GetAllowlistRule(ruleId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        var source = ReadString(ok.Value, "source");
        Assert.AreNotEqual(
            "human",
            source,
            "分类器落的规则绝不能被上报为 human —— 审计溯源字段报错会让操作者误判规则的来源。");
        Assert.AreEqual(ClassifierSource, source);
    }

    [TestMethod]
    public async Task UnknownSource_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.CreateAllowlistRule(
            Mutation("audit_agent_v2"),
            CancellationToken.None);

        Assert.IsInstanceOfType<BadRequestObjectResult>(
            result,
            "不认识的来源必须 fail-closed，而不是被静默归到某个来源。");
    }

    [TestMethod]
    public async Task NullSource_DefaultsToHuman()
    {
        var controller = CreateController();

        var ruleId = await CreateRuleAsync(controller, source: null);
        var result = await controller.GetAllowlistRule(ruleId, CancellationToken.None);

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreEqual("human", ReadString(ok.Value, "source"), "缺省来源保持既有行为：human。");
    }
}
