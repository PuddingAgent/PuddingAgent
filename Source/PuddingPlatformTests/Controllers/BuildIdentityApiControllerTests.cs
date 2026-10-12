using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PuddingPlatform.Controllers.Api;

namespace PuddingPlatformTests.Controllers;

/// <summary>
/// 复核 R5：运行实例构建身份端点必须给出**可核验的加载身份**（PID/启动标识/关键程序集 MVID 与文件 SHA256），
/// 供部署预检把在线实例与构件清单逐项比对；不得返回密钥/配置/完整路径。
/// </summary>
[TestClass]
public sealed class BuildIdentityApiControllerTests
{
    private static JsonElement InvokeAndSerialize()
    {
        var controller = new BuildIdentityApiController();
        var result = controller.Get();
        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok, "端点必须返回 200 与 JSON 负载");
        var json = JsonSerializer.Serialize(ok!.Value);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [TestMethod]
    public void Get_ReportsProcessAndStartIdentity()
    {
        var payload = InvokeAndSerialize();

        Assert.IsTrue(payload.TryGetProperty("processId", out var pid));
        Assert.AreEqual(Environment.ProcessId, pid.GetInt32(), "PID 必须是当前进程（预检据此绑定端口→进程）");

        Assert.IsTrue(payload.TryGetProperty("processStartedAtUtc", out var started));
        Assert.IsTrue(
            started.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(started.GetString(), out _),
            "必须给出可解析的进程启动标识（无则应为 null，预检按 unknown 处理）");
    }

    [TestMethod]
    public void Get_ReportsTrackedAssembliesWithMvidAndFileHash()
    {
        var payload = InvokeAndSerialize();
        Assert.IsTrue(payload.TryGetProperty("assemblies", out var assemblies));
        Assert.AreEqual(JsonValueKind.Array, assemblies.ValueKind);

        var names = new List<string>();
        foreach (var row in assemblies.EnumerateArray())
        {
            names.Add(row.GetProperty("name").GetString()!);
        }

        foreach (var expected in new[] { "PuddingAgent", "PuddingCore", "PuddingRuntime", "PuddingMemoryEngine", "PuddingPlatform" })
        {
            CollectionAssert.Contains(names, expected, $"关键程序集 {expected} 必须出现在身份清单里");
        }

        // 本测试进程已加载 PuddingCore/PuddingPlatform（引用链）⇒ 必须给出 MVID 与非空 SHA256。
        var runtime = assemblies.EnumerateArray().First(r => r.GetProperty("name").GetString() == "PuddingPlatform");
        Assert.IsTrue(runtime.GetProperty("loaded").GetBoolean(), "PuddingPlatform 必须已加载");
        Assert.IsFalse(string.IsNullOrWhiteSpace(runtime.GetProperty("mvid").GetString()), "必须报告 MVID");
        Assert.IsFalse(string.IsNullOrWhiteSpace(runtime.GetProperty("fileSha256").GetString()), "必须报告文件 SHA256");
        Assert.AreEqual(64, runtime.GetProperty("fileSha256").GetString()!.Length, "SHA256 应为 64 位十六进制");
    }

    [TestMethod]
    public void Get_DoesNotLeakSecretsOrFullPaths()
    {
        var payload = InvokeAndSerialize();
        var json = payload.GetRawText().ToLowerInvariant();

        foreach (var forbidden in new[] { "apikey", "api_key", "token", "secret", "password", "controltoken" })
        {
            Assert.IsFalse(json.Contains(forbidden, StringComparison.Ordinal), $"身份负载不得包含 {forbidden}");
        }

        foreach (var row in payload.GetProperty("assemblies").EnumerateArray())
        {
            Assert.IsTrue(row.TryGetProperty("fileName", out var fileName));
            if (fileName.ValueKind == JsonValueKind.String)
            {
                var value = fileName.GetString()!;
                Assert.IsFalse(value.Contains('\\') || value.Contains('/'), "程序集只回文件名，不回完整路径");
            }
        }
    }
}
