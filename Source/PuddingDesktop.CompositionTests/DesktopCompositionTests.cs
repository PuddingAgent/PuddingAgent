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

    [Fact]
    public async Task LlmSettingsAdapter_WritesThroughRealHost_KeepsSecretsAndPrices()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var settings = factory.CreateLlmSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            // Before Core is ready the adapter must refuse instead of pretending the save worked.
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => settings.ListProvidersAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var provider = new LlmProviderEdit("pool", "Pool", "https://example.invalid/v1", "notes", true,
                ApiKeyChange.Replace, "fixture-only-secret", new LlmProviderLimits(4, 1_000, 9));
            await settings.SaveProviderAsync(provider, timeout.Token);
            var listed = SinglePool(await settings.ListProvidersAsync(timeout.Token));
            Assert.Equal("pool", listed.ProviderId);
            Assert.True(listed.HasApiKey);
            Assert.Equal(new LlmProviderLimits(4, 1_000, 9), listed.Limits);

            var first = new LlmModelEdit("pool", "m1", "M1", "responses", ["tools", "vision"],
                32768, 16384, 4096, null, 1.5m, 2.5m, 0.25m, true, false, false, 3);
            await settings.SaveModelAsync(first, timeout.Token);
            var model = Assert.Single(await settings.ListModelsAsync("pool", timeout.Token));
            Assert.Equal(1.5m, model.InputPricePer1MTokens);
            Assert.Equal(0.25m, model.CacheHitPricePer1MTokens);
            Assert.True(model.IsDefault);

            // Editing the definition alone must not wipe prices, and adding a model must not drop the first.
            await settings.SaveModelAsync(first with { Name = "M1 renamed" }, timeout.Token);
            await settings.SaveModelAsync(first with { ModelId = "m2", Name = "M2", IsDefault = true, SortOrder = 1 }, timeout.Token);
            var models = await settings.ListModelsAsync("pool", timeout.Token);
            Assert.Equal(2, models.Count);
            var renamed = Assert.Single(models, candidate => candidate.ModelId == "m1");
            Assert.Equal("M1 renamed", renamed.Name);
            Assert.Equal(2.5m, renamed.OutputPricePer1MTokens);
            Assert.False(renamed.IsDefault, "promoting another model must clear the previous default");
            Assert.True(Assert.Single(models, candidate => candidate.ModelId == "m2").IsDefault);

            // Keep preserves the stored secret; the adapter never returns plaintext.
            await settings.SaveProviderAsync(provider with { Name = "Pool renamed", KeyChange = ApiKeyChange.Keep, NewKey = null }, timeout.Token);
            listed = SinglePool(await settings.ListProvidersAsync(timeout.Token));
            Assert.Equal("Pool renamed", listed.Name);
            Assert.True(listed.HasApiKey);
            Assert.Equal(new LlmProviderLimits(4, 1_000, 9), listed.Limits);

            await settings.SaveProviderAsync(provider with { KeyChange = ApiKeyChange.Clear, NewKey = null }, timeout.Token);
            Assert.False(SinglePool(await settings.ListProvidersAsync(timeout.Token)).HasApiKey);

            await settings.DeleteModelAsync("pool", "m2", timeout.Token);
            Assert.Single(await settings.ListModelsAsync("pool", timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => settings.ListProvidersAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // The isolated data root is seeded with the shipped default providers, so target ours explicitly.
    private static PuddingDesktop.Foundation.LlmProviderSummary SinglePool(IReadOnlyList<PuddingDesktop.Foundation.LlmProviderSummary> providers)
        => Assert.Single(providers, provider => provider.ProviderId == "pool");

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
