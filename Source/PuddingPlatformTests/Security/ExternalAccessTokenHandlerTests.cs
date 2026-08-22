using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Security;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Security;

namespace PuddingPlatformTests.Security;

/// <summary>
/// ADR-075 §15.1：认证 Handler 矩阵 — Header 缺失 NoResult、
/// 成功构造 ClaimsPrincipal（无 admin role）、JWT 冒充 External Token 失败、
/// last-used 投递不阻断认证。
/// </summary>
[TestClass]
public sealed class ExternalAccessTokenHandlerTests
{
    private const string Owner = "admin";

    [TestMethod]
    public async Task NoAuthorizationHeader_NoResult()
    {
        var setup = await CreateSetupWithTokenAsync();
        await using (setup.Harness)
        {
            var handler = CreateHandler(setup.Context);

            var result = await handler.AuthenticateAsync();

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.None);
        }
    }

    [TestMethod]
    public async Task ValidToken_Succeeds_WithExpectedClaims_NoAdminRole()
    {
        var setup = await CreateSetupWithTokenAsync(
            scopes: [ExternalTaskApiScopes.TasksRead, ExternalTaskApiScopes.TasksWrite],
            workspaces: ["default"]);
        await using (setup.Harness)
        {
            setup.Context.Request.Headers.Authorization = $"Bearer {setup.CanonicalToken}";
            var handler = CreateHandler(setup.Context);

            var result = await handler.AuthenticateAsync();

            Assert.IsTrue(result.Succeeded, $"failure: {result.Failure?.Message}");
            var principal = result.Ticket!.Principal;
            Assert.AreEqual("access-token:" + setup.TokenId, principal.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.AreEqual("handler-test", principal.FindFirstValue(ClaimTypes.Name));
            Assert.AreEqual(
                ExternalAccessTokenDefaults.ActorType,
                principal.FindFirstValue(ExternalAccessTokenClaimNames.ActorType));
            var scopes = principal.FindAll(ExternalAccessTokenClaimNames.Scope).Select(c => c.Value).ToList();
            CollectionAssert.AreEquivalent(new[] { "tasks.read", "tasks.write" }, scopes);
            var workspaces = principal.FindAll(ExternalAccessTokenClaimNames.Workspace).Select(c => c.Value).ToList();
            CollectionAssert.AreEquivalent(new[] { "default" }, workspaces);

            // 绝不注入 admin role。
            Assert.IsFalse(principal.IsInRole("admin"));
            Assert.IsNull(principal.FindFirst(ClaimTypes.Role));
        }
    }

    [TestMethod]
    public async Task AdminJwtPresentedToExternalScheme_Fails()
    {
        var setup = await CreateSetupWithTokenAsync();
        await using (setup.Harness)
        {
            setup.Context.Request.Headers.Authorization = "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.fakesig";
            var handler = CreateHandler(setup.Context);

            var result = await handler.AuthenticateAsync();

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Failure?.Message.Contains("invalid_token") == true);
        }
    }

    [TestMethod]
    public async Task WrongSecret_Fails()
    {
        var setup = await CreateSetupWithTokenAsync();
        await using (setup.Harness)
        {
            var tampered = setup.CanonicalToken[..^3] + "xyz";
            setup.Context.Request.Headers.Authorization = $"Bearer {tampered}";
            var handler = CreateHandler(setup.Context);

            var result = await handler.AuthenticateAsync();

            Assert.IsFalse(result.Succeeded);
        }
    }

    [TestMethod]
    public async Task MultipleAuthorizationHeaders_Fail()
    {
        var setup = await CreateSetupWithTokenAsync();
        await using (setup.Harness)
        {
            setup.Context.Request.Headers.Authorization =
                new Microsoft.Extensions.Primitives.StringValues(
                    [$"Bearer {setup.CanonicalToken}", "Bearer pdt_v1_x.y"]);
            var handler = CreateHandler(setup.Context);

            var result = await handler.AuthenticateAsync();

            Assert.IsFalse(result.Succeeded);
        }
    }

    [TestMethod]
    public async Task RecordsUsageInCoalescer_WhenRegistered()
    {
        var setup = await CreateSetupWithTokenAsync();
        await using (setup.Harness)
        {
            var coalescer = new ExternalAccessTokenUsageCoalescer(setup.Harness.Store);
            setup.Context.RequestServices = BuildServiceProvider(setup.Harness, coalescer);
            setup.Context.Request.Headers.Authorization = $"Bearer {setup.CanonicalToken}";
            var handler = CreateHandler(setup.Context);

            var result = await handler.AuthenticateAsync();

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(1, coalescer.PendingCount);
        }
    }

    // ── harness ──────────────────────────────────────────────

    private sealed record HandlerSetup(
        ExternalAccessTokenTestHarness Harness,
        DefaultHttpContext Context,
        string CanonicalToken,
        string TokenId);

    private static async Task<HandlerSetup> CreateSetupWithTokenAsync(
        IReadOnlyList<string>? scopes = null,
        IReadOnlyList<string>? workspaces = null)
    {
        var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();
        var created = await service.CreateAsync(new ExternalAccessTokenCreateCommand
        {
            Name = "handler-test",
            Scopes = scopes ?? [ExternalTaskApiScopes.TasksRead],
            WorkspaceIds = workspaces ?? ["default"],
            OwnerUserId = Owner,
        });
        Assert.IsTrue(created.IsOk, $"setup create failed: {created.Error}");

        var context = new DefaultHttpContext
        {
            RequestServices = BuildServiceProvider(harness),
        };
        return new HandlerSetup(harness, context, created.Value!.AccessToken, created.Value.Item.TokenId);
    }

    private static IServiceProvider BuildServiceProvider(
        ExternalAccessTokenTestHarness harness,
        ExternalAccessTokenUsageCoalescer? coalescer = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(harness.CreateService());
        if (coalescer is not null)
            services.AddSingleton(coalescer);
        return services.BuildServiceProvider();
    }

    private static ExternalAccessTokenHandler CreateHandler(DefaultHttpContext context)
    {
        var handler = new ExternalAccessTokenHandler(
            new StubOptionsMonitor<ExternalAccessTokenOptions>(new ExternalAccessTokenOptions()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);
        var scheme = new AuthenticationScheme(
            ExternalAccessTokenAuthentication.Scheme,
            displayName: null,
            typeof(ExternalAccessTokenHandler));
        handler.InitializeAsync(scheme, context).GetAwaiter().GetResult();
        return handler;
    }

    private sealed class StubOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
