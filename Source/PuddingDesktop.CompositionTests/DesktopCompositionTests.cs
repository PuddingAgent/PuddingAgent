using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Composition;
using PuddingDesktop.Foundation;

namespace PuddingDesktop.CompositionTests;

public sealed class DesktopCompositionTests
{
    [Fact]
    public async Task RealHostStartsInProcess_HealthReady_RootExclusive_StopsAndRestarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "Pudding-kernel-test-" + Guid.NewGuid().ToString("N"));
        var desktop = new Desktop();
        var factory = new DesktopKernelFactory(desktop);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(root, "config", "system.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Jwt = new { Key = key, Issuer = "kernel-test", Audience = "kernel-test" } }));
        await using var kernel = new InProcessKernel(factory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await kernel.StartAsync(root, timeout.Token);
            Assert.Equal(DesktopKernelState.Ready, kernel.Snapshot.State);
            using var http = new HttpClient();
            var address = kernel.Snapshot.WorkbenchAddress!;
            Assert.True((await http.GetAsync(new Uri(address, "/health/ready"), timeout.Token)).IsSuccessStatusCode);
            var html = await http.GetStringAsync(address, timeout.Token);
            Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<IOException>(() => new PuddingHost.Hosting.PuddingDataRootLease(root));
            var show = new Uri(address, "/api/desktop/show/Settings");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await http.PostAsync(show, null, timeout.Token)).StatusCode);
            var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken("kernel-test", "kernel-test",
                [new(System.Security.Claims.ClaimTypes.Role, "admin")], expires: DateTime.UtcNow.AddMinutes(2),
                signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
            http.DefaultRequestHeaders.Authorization = new("Bearer", new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token));
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await http.PostAsync(show, null, timeout.Token)).StatusCode);
            Assert.Equal(ShellPage.Settings, desktop.Page);
            Assert.Equal(Environment.ProcessId, desktop.ProcessId);
            await Assert.ThrowsAsync<IOException>(() => factory.StartAsync(root, timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => factory.StartAsync(Path.Combine(root, "other-data"), timeout.Token));
            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(new Uri(address, "/health/ready"), timeout.Token));
            await kernel.StartAsync(root, timeout.Token);
            Assert.Equal(DesktopKernelState.Ready, kernel.Snapshot.State);
            await kernel.StopAsync(timeout.Token);
        }
        finally { await kernel.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task SettingsOperationsRunInsideRealHost_AndStopInvalidatesThem()
    {
        var root = await CreateIsolatedDataRootAsync();
        await using var kernel = new InProcessKernel(new DesktopKernelFactory(new Desktop()));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await kernel.StartAsync(root, timeout.Token);
            Assert.Equal(DesktopKernelState.Ready, kernel.Snapshot.State);

            kernel.Settings.SetSelection(new SettingsSelection("default", "general-assistant-001"));
            var stamp = kernel.Settings.Capture();
            Assert.True(stamp.Selection.IsSpecified);

            // Direct call into the scoped Core service: no HttpClient, no controller, no JSON hop.
            var providers = await kernel.RunSettingsAsync("llm.providers.read", async (scope, _) =>
                (await scope.Services.GetRequiredService<PuddingPlatform.Services.LlmProviderFileService>()
                    .ListProvidersAsync(CancellationToken.None)).Count);
            Assert.True(providers >= 0);
            kernel.Settings.EnsureCurrent(stamp);
            Assert.Equal(0, kernel.Settings.OutstandingOperations);

            await kernel.StopAsync(timeout.Token);
            // The previous generation and its selection can no longer be written into.
            Assert.False(kernel.Settings.IsCurrent(stamp));
            var refused = await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => kernel.RunSettingsAsync("llm.providers.read", (_, _) => Task.FromResult(0)));
            Assert.Equal(SettingsUnavailable.KernelStopped, refused.Reason);
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task<string> CreateIsolatedDataRootAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Pudding-kernel-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(root, "config", "system.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Jwt = new { Key = key, Issuer = "kernel-test", Audience = "kernel-test" } }));
        return root;
    }

    private sealed class Desktop : IDesktopServices
    {
        public ShellPage? Page; public int ProcessId;
        public Task ShowAsync(ShellPage page, CancellationToken cancellationToken = default)
        { Page = page; ProcessId = Environment.ProcessId; return Task.CompletedTask; }
        public Task OpenDocumentAsync(WorkspaceDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
