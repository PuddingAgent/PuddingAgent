using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingHost.Controllers;
using PuddingHost.Hosting;
namespace PuddingHost.Tests.Hosting;

/// <summary>
/// Slice S-A：**真实 HTTP 层**证据（进程内 <see cref="TestServer"/>，**不碰运行中的生产宿主**）。
/// <para>
/// 为什么需要它：控制器级的 <c>OkObjectResult</c> 断言只能证明「动作返回 200 的对象」，
/// 证明不了「路由真的可达、鉴权闸门真的生效、JSON 真的是文档里那套 camelCase 形状」。
/// 本类把这三件事一起钉住，并带**活对照**（不给 admin 角色 ⇒ 403），
/// 于是「200」不是一条永远为真的空断言。
/// </para>
/// <para>
/// 该测试宿主是**最小 MVC 管线**：`AddControllers()` 走框架默认序列化策略
/// （<c>JsonSerializerDefaults.Web</c> ⇒ camelCase）——生产宿主未调用 <c>AddJsonOptions</c>，
/// 因此两者策略一致（已实测确认）。
/// </para>
/// </summary>
public sealed class SAIndexStatusHttpTests
{
    /// <summary>路由与响应形状（含 D5 红线：**不得**出现 <c>codeIndex</c>）。</summary>
    [Fact]
    public async Task Get_Status_Returns_200_With_The_Documented_Shape_And_No_CodeIndex_Block()
    {
        using var fixture = new S5Fixture();
        var scope = fixture.NewCorpus("http-shape", ("a.txt", "aaa"));

        var supplyOptions = new FullTextIndexSupplyOptions
        {
            Enabled = true,
            Scopes = [scope],
            WorkspaceRoot = fixture.Root,
            MaxIndexBytes = 1_073_741_824,
            MinRebuildInterval = TimeSpan.FromHours(12),
        };

        var accessor = new FullTextIndexSupplyAccessor(
            new StubCompositionFactory(FixedComposition()));

        var engine = new CountingRootedEngine(fixture.IndexRoot);

        var probe = new FullTextIndexStatusProbe(
            BuildConfiguration(FullTextIndexSupplyOptions.SectionName),
            new StubOptionsMonitor<FullTextIndexSupplyOptions>(supplyOptions),
            fixture.Options,
            engine,
            accessor,
            NullLogger<FullTextIndexStatusProbe>.Instance);

        await using var app = await StartTestAppAsync(probe, isAdmin: true);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/api/admin/index/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("generatedAtUtc", out var generatedAt));
        Assert.Equal(JsonValueKind.String, generatedAt.ValueKind);
        Assert.True(root.TryGetProperty("fullText", out var fullText));

        // 契约字段逐项存在（camelCase 由框架默认策略决定）。
        foreach (var name in new[]
                 {
                     "configured", "enabled", "indexRoot", "indexRootExists", "workspaceRoot",
                     "maxIndexBytes", "minRebuildInterval", "acceptedScopes", "rejectedReasons",
                     "compositionCreated", "maintenance", "scopes", "jobs", "jobsReason",
                 })
        {
            Assert.True(fullText.TryGetProperty(name, out _), $"响应缺少字段 fullText.{name}");
        }

        // D5 红线：本切片不得出现符号索引块或占位。
        Assert.False(root.TryGetProperty("codeIndex", out _), "S-A 不得出现 codeIndex（属下一刀 S-A2）");
        Assert.False(fullText.TryGetProperty("codeIndex", out _), "S-A 不得出现 codeIndex（属下一刀 S-A2）");

        // 配置真值与受理结果。
        Assert.True(fullText.GetProperty("enabled").GetBoolean());
        Assert.Equal(fixture.Options.IndexRootDirectory, fullText.GetProperty("indexRoot").GetString());
        Assert.Equal(1_073_741_824L, fullText.GetProperty("maxIndexBytes").GetInt64());
        Assert.Equal(TimeSpan.FromHours(12), TimeSpan.Parse(fullText.GetProperty("minRebuildInterval").GetString()!));
        Assert.Equal(JsonValueKind.Array, fullText.GetProperty("acceptedScopes").ValueKind);
        Assert.Empty(Describe(fullText.GetProperty("rejectedReasons")));
        Assert.Equal(scope, fullText.GetProperty("acceptedScopes")[0].GetString());

        // 未构造组合 ⇒ 如实上报（不伪造空台账）。
        Assert.False(fullText.GetProperty("compositionCreated").GetBoolean());
        Assert.Empty(Describe(fullText.GetProperty("jobs")));
        Assert.Equal("composition-not-created", fullText.GetProperty("jobsReason").GetString());

        // 维护循环开关（D4）：配置节未提供 ⇒ 走绑定类型默认值 false。
        var maintenance = fullText.GetProperty("maintenance");
        Assert.False(maintenance.GetProperty("configured").GetBoolean());
        Assert.False(maintenance.GetProperty("enabled").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(maintenance.GetProperty("note").GetString()));

        // 逐 scope 观测：条目存在；索引目录路径取自引擎单一真源（尚未建索引 ⇒ 计数类如实为 null）。
        var scopes = Describe(fullText.GetProperty("scopes"));
        var single = Assert.Single(scopes);
        Assert.Equal(scope, single.GetProperty("scopePath").GetString());
        Assert.Equal(engine.ResolveIndexDirectory(scope), single.GetProperty("indexDirectory").GetString());
        Assert.Equal(JsonValueKind.False, single.GetProperty("hasIndex").ValueKind);
        Assert.Equal(JsonValueKind.False, single.GetProperty("indexDirectoryExists").ValueKind);
        Assert.Equal(JsonValueKind.Null, single.GetProperty("indexEntryCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, single.GetProperty("indexBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, single.GetProperty("indexDirectoryLastWriteUtc").ValueKind);

        // 只读：查询前后索引根仍不存在。
        Assert.False(Directory.Exists(fixture.IndexRoot));
        Assert.Null(accessor.Current);
    }

    /// <summary>
    /// 活对照：**不给 admin 角色** ⇒ 403（<c>[Authorize(Roles = "admin")]</c> 真的在生效）。
    /// 没有这一条，「200」可能是因为闸门根本没接上。
    /// </summary>
    [Fact]
    public async Task Get_Status_Without_Admin_Role_Is_Forbidden()
    {
        using var fixture = new S5Fixture();
        var probe = new FullTextIndexStatusProbe(
            BuildConfiguration(FullTextIndexSupplyOptions.SectionName),
            new StubOptionsMonitor<FullTextIndexSupplyOptions>(
                new FullTextIndexSupplyOptions { Enabled = false }),
            fixture.Options,
            new CountingRootedEngine(fixture.IndexRoot),
            new FullTextIndexSupplyAccessor(new StubCompositionFactory(FixedComposition())),
            NullLogger<FullTextIndexStatusProbe>.Instance);

        await using var app = await StartTestAppAsync(probe, isAdmin: false);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/api/admin/index/status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── 局部工具 ────────────────────────────────────────────────────────

    private static IFullTextIndexSupplyComposition FixedComposition() =>
        new TestSupplyComposition(
            new ScriptedSupplyCoordinator(),
            new SupplyCoordinatorOptions(),
            _ => DateTimeOffset.MinValue);

    private static IConfigurationRoot BuildConfiguration(params string[] presentSections)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in presentSections)
            values[$"{section}:Enabled"] = "true";

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IReadOnlyList<JsonElement> Describe(JsonElement array) =>
        array.EnumerateArray().ToList();

    /// <summary>可变 <see cref="IOptionsMonitor{T}"/> 替身（本文件的私有副本，避免与其它测试类耦合）。</summary>
    private sealed class StubOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private static async Task<WebApplication> StartTestAppAsync(FullTextIndexStatusProbe probe, bool isAdmin)
    {
        // 用 WebApplication + UseTestServer()（而非已弃用的 WebHostBuilder/TestServer(IWebHostBuilder)）：
        // 前者是官方推荐路径，且不会新增编译器弃用警告（门禁要求警告数不高于基线）。
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddControllers().AddApplicationPart(typeof(IndexAdminController).Assembly);
        builder.Services.AddSingleton(probe);

        // 显式注入受控配置：不受测试输出目录里 appsettings.json 的干扰。
        builder.Services.AddSingleton<IConfiguration>(
            BuildConfiguration(FullTextIndexSupplyOptions.SectionName));

        // 与生产同名（"Bearer"）的测试认证 scheme：控制器显式指定了该 scheme，
        // 因此替身必须用同一个名字才可能被选中。
        builder.Services
            .AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                options => options.ClaimsIssuer = isAdmin ? "admin" : "plain");
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    /// <summary>测试认证 handler：<c>"admin"</c> issuer 才发放 <c>Role=admin</c>（用于活对照）。</summary>
    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        internal const string SchemeName = "Bearer";

        public TestAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>();
            if (string.Equals(Options.ClaimsIssuer, "admin", StringComparison.Ordinal))
                claims.Add(new Claim(ClaimTypes.Role, "admin"));

            var identity = new ClaimsIdentity(claims, SchemeName);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
