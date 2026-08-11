using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using PuddingBrowser.Protocol;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

public sealed class StorageManagementAuthorizationTests : IDisposable
{
    private readonly string _dataRoot = Path.Combine(
        Path.GetTempPath(),
        "PuddingAgent",
        "storage-auth-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Desktop_Control_Token_Is_Accepted_Only_On_Loopback()
    {
        const string token = "test-control-token";
        Directory.CreateDirectory(Path.Combine(_dataRoot, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(_dataRoot, "config", "system.json"),
            JsonSerializer.Serialize(new
            {
                desktop = new { core = new { controlToken = token } },
            }));
        var handler = new StorageManagementAuthorizationHandler(
            new PuddingHostOptions
            {
                Mode = PuddingHostMode.DesktopChild,
                DataRoot = _dataRoot,
            },
            new DesktopControlTokenValidator(_dataRoot));
        var requirement = new StorageManagementRequirement();
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers[BrowserBridgeProtocol.ControlTokenHeader] = token;
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
            http);

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Authenticated_Admin_Is_Accepted_In_Console_Mode()
    {
        var handler = new StorageManagementAuthorizationHandler(
            new PuddingHostOptions
            {
                Mode = PuddingHostMode.Console,
                DataRoot = _dataRoot,
            },
            new DesktopControlTokenValidator(_dataRoot));
        var requirement = new StorageManagementRequirement();
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "admin")],
            authenticationType: "test");
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(identity),
            new DefaultHttpContext());

        await ((IAuthorizationHandler)handler).HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataRoot))
            Directory.Delete(_dataRoot, recursive: true);
    }
}
