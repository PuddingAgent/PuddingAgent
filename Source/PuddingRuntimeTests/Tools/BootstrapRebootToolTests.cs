using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// bootstrap_reboot 点火工具的纯逻辑测试：
/// system.json 目标解析（token/httpEnabled/port）与 start 请求体构造。
/// 端到端点火（真实 Desktop HTTP）由 dogfooding 演练覆盖，不在单元测试内起 listener。
/// </summary>
[TestClass]
public sealed class BootstrapRebootToolTests
{
    // ── TryExtractBootstrapTarget ──────────────────────────────

    [TestMethod]
    public void TryExtractBootstrapTarget_FullDesktopConfig_ReturnsTokenSwitchAndPort()
    {
        const string json = """
        {
          "desktop": {
            "core": { "controlToken": "abc123" },
            "bootstrap": { "httpEnabled": true, "httpPort": 8199 }
          }
        }
        """;

        var ok = BootstrapRebootTool.TryExtractBootstrapTarget(json, out var token, out var httpEnabled, out var port);

        Assert.IsTrue(ok);
        Assert.AreEqual("abc123", token);
        Assert.IsTrue(httpEnabled);
        Assert.AreEqual(8199, port);
    }

    [TestMethod]
    public void TryExtractBootstrapTarget_EmptyJson_UsesRecordDefaults()
    {
        // desktop/bootstrap 缺省 → record 默认值：httpEnabled=true, httpPort=8199, token=null
        var ok = BootstrapRebootTool.TryExtractBootstrapTarget("{}", out var token, out var httpEnabled, out var port);

        Assert.IsTrue(ok);
        Assert.IsNull(token);
        Assert.IsTrue(httpEnabled);
        Assert.AreEqual(8199, port);
    }

    [TestMethod]
    public void TryExtractBootstrapTarget_HttpDisabled_ReportsFalse()
    {
        const string json = """
        {
          "desktop": {
            "core": { "controlToken": "t" },
            "bootstrap": { "httpEnabled": false }
          }
        }
        """;

        var ok = BootstrapRebootTool.TryExtractBootstrapTarget(json, out _, out var httpEnabled, out _);

        Assert.IsTrue(ok);
        Assert.IsFalse(httpEnabled);
    }

    [TestMethod]
    public void TryExtractBootstrapTarget_InvalidJson_ReturnsFalse()
    {
        var ok = BootstrapRebootTool.TryExtractBootstrapTarget("{ not json", out _, out _, out _);
        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryExtractBootstrapTarget_CustomPort_IsHonored()
    {
        const string json = """
        {
          "desktop": {
            "core": { "controlToken": "t" },
            "bootstrap": { "httpPort": 18199 }
          }
        }
        """;

        var ok = BootstrapRebootTool.TryExtractBootstrapTarget(json, out _, out _, out var port);

        Assert.IsTrue(ok);
        Assert.AreEqual(18199, port);
    }

    // ── BuildStartRequestJson ──────────────────────────────────

    [TestMethod]
    public void BuildStartRequestJson_AllFields_SerializesCamelCase()
    {
        var body = BootstrapRebootTool.BuildStartRequestJson("tok", "agent:x", yolo: true);

        StringAssert.Contains(body, "\"token\":\"tok\"");
        StringAssert.Contains(body, "\"requestedBy\":\"agent:x\"");
        StringAssert.Contains(body, "\"yolo\":true");
        StringAssert.Contains(body, "\"deploymentMode\":\"desktop-build\"");
    }

    [TestMethod]
    public void BuildStartRequestJson_YoloFalse_SerializesFalse()
    {
        var body = BootstrapRebootTool.BuildStartRequestJson("tok", "agent:x", yolo: false);
        StringAssert.Contains(body, "\"yolo\":false");
    }

    [TestMethod]
    public void BuildStartRequestJson_PrebuiltArtifact_SerializesArtifactEvidence()
    {
        var body = BootstrapRebootTool.BuildStartRequestJson(
            "tok",
            "agent:x",
            yolo: false,
            deploymentMode: "prebuilt-artifact",
            artifactDirectory: @"E:\repo\.tmp-build\core",
            artifactAssemblySha256: "abc");

        StringAssert.Contains(body, "\"deploymentMode\":\"prebuilt-artifact\"");
        StringAssert.Contains(body, "\"artifactDirectory\":\"E:\\\\repo\\\\.tmp-build\\\\core\"");
        StringAssert.Contains(body, "\"artifactAssemblySha256\":\"abc\"");
    }

    [DataTestMethod]
    [DataRow(null, "desktop-build")]
    [DataRow("build", "desktop-build")]
    [DataRow("prebuilt_artifact", "prebuilt-artifact")]
    [DataRow("restart", "restart-only")]
    public void NormalizeDeploymentMode_SupportedAliases_ReturnCanonical(string? value, string expected)
        => Assert.AreEqual(expected, BootstrapRebootTool.NormalizeDeploymentMode(value));

    [TestMethod]
    public void NormalizeDeploymentMode_Unknown_ReturnsNull()
        => Assert.IsNull(BootstrapRebootTool.NormalizeDeploymentMode("hot-swap"));
}
