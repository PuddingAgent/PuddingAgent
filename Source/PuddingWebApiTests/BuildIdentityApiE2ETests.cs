using System.Net;
using System.Text.Json;

namespace PuddingWebApiTests;

/// <summary>
/// 复核 R5 的端到端验证：运行实例身份端点在**真实 ASP.NET Core 宿主**里匿名可读，
/// 并如实报告已加载的关键程序集身份（PID/启动标识/MVID/文件 SHA256）——
/// 这正是外部部署预检从「在线实例」能拿到的唯一权威凭据。
/// <para>
/// 说明：本用例在进程内宿主上验证**端点机制**（路由、匿名可达、载荷形状）；
/// 与真实端口的绑定、以及"端口→进程"判定，由 TestScripts/verify-external-deploy-identity.ps1
/// 在真实部署后用完整模式核对，两者互补。
/// </para>
/// </summary>
[TestClass]
public sealed class BuildIdentityApiE2ETests
{
    [TestMethod]
    public async Task BuildIdentity_IsAnonymousAndReportsLoadedCoreAssemblies()
    {
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/system/build-identity");
        Assert.AreEqual(
            HttpStatusCode.OK,
            response.StatusCode,
            "身份端点必须匿名可读（部署预检不携带登录态，否则 live 阶段永远 unknown）");

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.AreEqual(
            Environment.ProcessId,
            root.GetProperty("processId").GetInt32(),
            "必须报告宿主进程 PID（预检据此绑定端口 → 进程）");

        var startedAt = root.GetProperty("processStartedAtUtc");
        Assert.IsTrue(
            startedAt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(startedAt.GetString(), out _),
            "必须给出可解析的进程启动标识");

        var assemblies = root.GetProperty("assemblies").EnumerateArray().ToList();
        var runtime = assemblies.FirstOrDefault(a =>
            string.Equals(a.GetProperty("name").GetString(), "PuddingRuntime", StringComparison.Ordinal));
        Assert.AreNotEqual(default, runtime, "关键程序集 PuddingRuntime 必须在身份清单里");
        Assert.IsTrue(runtime.GetProperty("loaded").GetBoolean(), "宿主必须已加载 PuddingRuntime");

        var mvid = runtime.GetProperty("mvid").GetString();
        var sha256 = runtime.GetProperty("fileSha256").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(mvid), "必须报告 MVID（证明实际加载的程序集身份）");
        Assert.IsFalse(string.IsNullOrWhiteSpace(sha256), "必须报告文件 SHA256（供与构件清单逐字节比对）");
        Assert.AreEqual(64, sha256!.Length, "SHA256 应为 64 位十六进制");

        // 载荷只含构建身份事实：不得回显密钥/配置。
        Assert.IsFalse(json.Contains("keyVault", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("controlToken", StringComparison.OrdinalIgnoreCase));
    }
}
