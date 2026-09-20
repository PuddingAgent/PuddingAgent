using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Classification;
using PuddingPlatform.Controllers.Api;

namespace PuddingPlatformTests.Controllers;

/// <summary>
/// S6b-1：分类器健康只读 API（D6，§8.2）契约测试。
/// 离线：全部用假 <see cref="IClassifierHealthReporter"/>，不发网络、不碰 PuddingRuntime 生产代码。
/// wire 断言范式与 GoalSnapshotWireContractTests 一致（JsonSerializerDefaults.Web + JsonDocument）。
/// </summary>
[TestClass]
public sealed class ClassifierHealthApiControllerTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void Get_RegisteredReporter_ReturnsAllContractFields()
    {
        var checkedAt = DateTimeOffset.Parse("2026-09-21T03:00:00Z");
        var reporter = new FakeHealthReporter(new ClassifierStatus
        {
            ClassifierId = "rules.system",
            Health = ClassifierHealth.Healthy,
            Detail = "last verdict ok",
            ConsecutiveFailures = 0,
            LastCheckedAtUtc = checkedAt,
            LastLatencyMs = 123.5,
        });

        var document = Invoke(reporter);

        Assert.AreEqual(true, document.RootElement.GetProperty("configured").GetBoolean());
        var entry = document.RootElement.GetProperty("classifiers")[0];
        Assert.AreEqual("rules.system", entry.GetProperty("classifierId").GetString());
        Assert.AreEqual("healthy", entry.GetProperty("health").GetString());
        Assert.AreEqual("last verdict ok", entry.GetProperty("detail").GetString());
        Assert.AreEqual(0, entry.GetProperty("consecutiveFailures").GetInt32());
        Assert.AreEqual(checkedAt, entry.GetProperty("lastCheckedAtUtc").GetDateTimeOffset());
        Assert.AreEqual(123.5, entry.GetProperty("lastLatencyMs").GetDouble());
    }

    [TestMethod]
    public void Get_DegradedUnavailableUnknown_AreReflectedVerbatim()
    {
        var reporter = new FakeHealthReporter(
            new ClassifierStatus { ClassifierId = "a", Health = ClassifierHealth.Degraded, ConsecutiveFailures = 3 },
            new ClassifierStatus { ClassifierId = "b", Health = ClassifierHealth.Unavailable, ConsecutiveFailures = 5 },
            new ClassifierStatus { ClassifierId = "c", Health = ClassifierHealth.Unknown, ConsecutiveFailures = 0 });

        var document = Invoke(reporter);

        var entries = document.RootElement.GetProperty("classifiers");
        Assert.AreEqual(3, entries.GetArrayLength());
        Assert.AreEqual("degraded", entries[0].GetProperty("health").GetString());
        Assert.AreEqual(3, entries[0].GetProperty("consecutiveFailures").GetInt32());
        Assert.AreEqual("unavailable", entries[1].GetProperty("health").GetString());
        Assert.AreEqual(5, entries[1].GetProperty("consecutiveFailures").GetInt32());
        Assert.AreEqual("unknown", entries[2].GetProperty("health").GetString());
    }

    [TestMethod]
    public void Get_MissingReporter_ReturnsConfiguredFalse_ExplicitUnknownState()
    {
        // 健康面未注册（宿主未接线）：必须明确表达“未知/未配置”，不得 500、不得假装健康。
        using var provider = new ServiceCollection().BuildServiceProvider();
        var controller = CreateController(provider);

        var actionResult = controller.GetHealth();

        var ok = Assert.IsInstanceOfType<OkObjectResult>(actionResult);
        Assert.IsTrue(ok.StatusCode is null or 200, $"expected 200, got {ok.StatusCode}");
        var wireJson = JsonSerializer.Serialize(ok.Value, WireOptions);
        using var document = JsonDocument.Parse(wireJson);
        Assert.AreEqual(false, document.RootElement.GetProperty("configured").GetBoolean());
        Assert.AreEqual(0, document.RootElement.GetProperty("classifiers").GetArrayLength());
    }

    [TestMethod]
    public void Get_ResponseCarriesNoSecretMarkers_AndOnlyWhitelistedFields()
    {
        var reporter = new FakeHealthReporter(new ClassifierStatus
        {
            ClassifierId = "rules.system",
            Health = ClassifierHealth.Healthy,
            Detail = "ok",
            ConsecutiveFailures = 0,
            LastCheckedAtUtc = DateTimeOffset.UtcNow,
            LastLatencyMs = 1.0,
        });

        var document = Invoke(reporter, out var wireJson);

        // 脱敏：输出不得出现密钥/令牌标记字样（端点仅透出健康面白名单字段，不附加配置原文）。
        foreach (var marker in new[] { "apiKey", "api-key", "token", "Bearer", "secret", "password" })
        {
            Assert.IsFalse(
                wireJson.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"response must not contain secret marker '{marker}': {wireJson}");
        }

        // 白名单字段集：顶层与每条记录不得出现契约之外的字段。
        var topLevel = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        CollectionAssert.AreEquivalent(new[] { "classifiers", "configured" }, topLevel);
        var entry = document.RootElement.GetProperty("classifiers")[0];
        var entryFields = entry.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        CollectionAssert.AreEquivalent(new[]
        {
            "classifierId", "health", "detail", "consecutiveFailures", "lastCheckedAtUtc", "lastLatencyMs",
        }, entryFields);
    }

    [TestMethod]
    public void Get_IsReadOnly_NoWriteEndpoints_AndNoSnapshotMutation()
    {
        var reporter = new FakeHealthReporter(new ClassifierStatus
        {
            ClassifierId = "rules.system",
            Health = ClassifierHealth.Healthy,
            ConsecutiveFailures = 0,
        });
        using var provider = new ServiceCollection()
            .AddSingleton<IClassifierHealthReporter>(reporter)
            .BuildServiceProvider();
        var controller = CreateController(provider);

        // 只读：仅允许 GET 动作——反射断言无任何写动词端点。
        var writeMethods = typeof(ClassifierHealthApiController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(ClassifierHealthApiController))
            .Where(m => m.GetCustomAttributes(true)
                .Any(a => a is HttpPostAttribute or HttpPutAttribute or HttpDeleteAttribute
                    or HttpPatchAttribute))
            .ToList();
        Assert.AreEqual(0, writeMethods.Count, "health API must expose no write endpoints");

        // 只读：重复调用不产生副作用，Snapshot 是唯一交互且状态不被改写。
        var before = reporter.Statuses;
        controller.GetHealth();
        controller.GetHealth();
        Assert.AreEqual(2, reporter.SnapshotCalls);
        Assert.AreSame(before, reporter.Statuses);
        Assert.AreEqual(ClassifierHealth.Healthy, reporter.Statuses[0].Health);
        Assert.AreEqual(0, reporter.Statuses[0].ConsecutiveFailures);
    }

    [TestMethod]
    public void Get_VendorNeutral_RouteOutputAndTypesContainNoVendorNames()
    {
        var reporter = new FakeHealthReporter(
            new ClassifierStatus { ClassifierId = "rules.system", Health = ClassifierHealth.Healthy },
            new ClassifierStatus { ClassifierId = "arbiter.mock", Health = ClassifierHealth.Unavailable });

        var document = Invoke(reporter, out var wireJson);

        // 厂商中立：路由模板、响应输出、类型/命名空间均不得出现厂商字样（§8.2 / §14.11.1）。
        var routeTemplate = typeof(ClassifierHealthApiController)
            .GetCustomAttributes(true)
            .OfType<RouteAttribute>()
            .Single().Template;
        foreach (var vendor in new[] { "jev", "noul", "choice" })
        {
            Assert.IsFalse(routeTemplate.Contains(vendor, StringComparison.OrdinalIgnoreCase),
                $"route '{routeTemplate}' must not contain vendor name '{vendor}'");
            Assert.IsFalse(wireJson.Contains(vendor, StringComparison.OrdinalIgnoreCase),
                $"response must not contain vendor name '{vendor}': {wireJson}");
            Assert.IsFalse(typeof(ClassifierHealthApiController).FullName!
                .Contains(vendor, StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public void Get_NeverProbedStatus_NullOptionalFieldsAreExplicitNulls()
    {
        var reporter = new FakeHealthReporter(new ClassifierStatus
        {
            ClassifierId = "rules.system",
            Health = ClassifierHealth.Unknown,
            Detail = null,
            ConsecutiveFailures = 0,
            LastCheckedAtUtc = null,
            LastLatencyMs = null,
        });

        var document = Invoke(reporter);

        // 从未探测：可选字段显式 null（不是缺失、不是 0、不是假装健康）。
        var entry = document.RootElement.GetProperty("classifiers")[0];
        Assert.AreEqual("unknown", entry.GetProperty("health").GetString());
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("detail").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("lastCheckedAtUtc").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, entry.GetProperty("lastLatencyMs").ValueKind);
    }

    // —— 测试脚手架 ——

    /// <summary>注入假健康面 → 调 GET → 返回 wire JSON（已解析）。</summary>
    private static JsonDocument Invoke(FakeHealthReporter reporter)
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IClassifierHealthReporter>(reporter)
            .BuildServiceProvider();
        var controller = CreateController(provider);

        var actionResult = controller.GetHealth();
        var ok = Assert.IsInstanceOfType<OkObjectResult>(actionResult);
        var wireJson = JsonSerializer.Serialize(ok.Value, WireOptions);
        return JsonDocument.Parse(wireJson);
    }

    private static JsonDocument Invoke(FakeHealthReporter reporter, out string wireJson)
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IClassifierHealthReporter>(reporter)
            .BuildServiceProvider();
        var controller = CreateController(provider);

        var actionResult = controller.GetHealth();
        var ok = Assert.IsInstanceOfType<OkObjectResult>(actionResult);
        wireJson = JsonSerializer.Serialize(ok.Value, WireOptions);
        return JsonDocument.Parse(wireJson);
    }

    private static ClassifierHealthApiController CreateController(ServiceProvider provider)
    {
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        return new ClassifierHealthApiController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private sealed class FakeHealthReporter(params ClassifierStatus[] statuses) : IClassifierHealthReporter
    {
        public ClassifierStatus[] Statuses { get; } = statuses;

        public int SnapshotCalls { get; private set; }

        public IReadOnlyList<ClassifierStatus> Snapshot()
        {
            SnapshotCalls++;
            return Statuses;
        }
    }
}
