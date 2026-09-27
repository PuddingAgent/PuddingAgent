using System.Net.Http.Json;
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

            // DS-02 quota: limits round-trip through the provider file, usage comes from the token ledger.
            var quota = await settings.GetQuotaAsync("pool", timeout.Token);
            Assert.NotNull(quota);
            Assert.Null(quota.DailyTokenLimit);
            Assert.Equal(0L, quota!.DailyTokensUsed);
            Assert.False(quota!.IsSuspended);

            var limited = await settings.SaveQuotaAsync("pool", new LlmQuotaLimits(500_000, 5_000_000), timeout.Token);
            Assert.Equal(500_000L, limited.DailyTokenLimit);
            Assert.Equal(5_000_000L, limited.MonthlyTokenLimit);
            Assert.Equal(500_000L, (await settings.GetQuotaAsync("pool", timeout.Token))!.DailyTokenLimit);

            // A limit Core rejects must not be written, and must not look like a successful save.
            await Assert.ThrowsAsync<ArgumentException>(
                () => settings.SaveQuotaAsync("pool", new LlmQuotaLimits(500_000, 1_000), timeout.Token));
            Assert.Equal(5_000_000L, (await settings.GetQuotaAsync("pool", timeout.Token))!.MonthlyTokenLimit);

            Assert.NotNull((await settings.ResetDailyQuotaAsync("pool", timeout.Token)).DailyResetAt);
            Assert.Null(await settings.GetQuotaAsync("no-such-provider", timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => settings.ListProvidersAsync(timeout.Token));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => settings.GetQuotaAsync("pool", timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task VoiceSettingsAdapter_KeepsSecretsAndSeparatesTtsFromAsrDefaults()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var settings = factory.CreateVoiceSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => settings.ListProvidersAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            await settings.SaveProviderAsync(new VoiceProviderEdit("voice-a", "Voice A", "https://a.invalid", "notes", true,
                ApiKeyChange.Replace, "fixture-voice-secret"), timeout.Token);
            var provider = Assert.Single(await settings.ListProvidersAsync(timeout.Token),
                candidate => candidate.ProviderId == "voice-a");
            Assert.True(provider.HasApiKey);
            Assert.Equal("https://a.invalid", provider.Endpoint);

            // Keep must not clear the stored secret.
            await settings.SaveProviderAsync(new VoiceProviderEdit("voice-a", "Voice A renamed", "https://a.invalid", "notes",
                true, ApiKeyChange.Keep, null), timeout.Token);
            var renamed = Assert.Single(await settings.ListProvidersAsync(timeout.Token),
                candidate => candidate.ProviderId == "voice-a");
            Assert.Equal("Voice A renamed", renamed.Name);
            Assert.True(renamed.HasApiKey);

            await settings.SaveTtsModelAsync(new VoiceTtsModel("voice-a", "tts-1", "TTS 1", "/tts",
                ["longanyang"], ["wav", "mp3"], [24000, 48000], true, true, false, false, false, true, 1), timeout.Token);
            await settings.SaveAsrModelAsync(new VoiceAsrModel("voice-a", "asr-1", "ASR 1", "/asr",
                ["zh-CN"], [16000], true, true, false, false, true, 1), timeout.Token);
            var defaults = await settings.GetDefaultsAsync(timeout.Token);
            Assert.Equal("voice-a", defaults.TtsProviderId);
            Assert.Equal("tts-1", defaults.TtsModelId);
            Assert.Equal("voice-a", defaults.AsrProviderId);
            Assert.Equal("asr-1", defaults.AsrModelId);

            // Re-saving the TTS model without the default flag clears only the TTS pointer.
            await settings.SaveTtsModelAsync(new VoiceTtsModel("voice-a", "tts-1", "TTS 1", "/tts",
                ["longanyang"], ["wav", "mp3"], [24000, 48000], true, true, false, false, false, false, 1), timeout.Token);
            defaults = await settings.GetDefaultsAsync(timeout.Token);
            Assert.Null(defaults.TtsModelId);
            Assert.Equal("asr-1", defaults.AsrModelId);

            var tts = Assert.Single(await settings.ListTtsModelsAsync("voice-a", timeout.Token));
            Assert.Equal(["longanyang"], tts.Voices);
            Assert.Equal([24000, 48000], tts.SampleRates);
            Assert.True(tts.SupportsStreaming && tts.SupportsInstructions);
            Assert.False(tts.IsDefault);

            await Assert.ThrowsAsync<ArgumentException>(() => settings.SaveProviderAsync(
                new VoiceProviderEdit("bad id", "X", "https://a.invalid", "", true, ApiKeyChange.Keep, null), timeout.Token));

            await settings.DeleteAsrModelAsync("voice-a", "asr-1", timeout.Token);
            defaults = await settings.GetDefaultsAsync(timeout.Token);
            Assert.Null(defaults.AsrModelId);
            Assert.Null(defaults.AsrProviderId);
            Assert.Empty(await settings.ListAsrModelsAsync("voice-a", timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => settings.GetDefaultsAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AgentDirectoryAdapter_EditsBasicsWithoutClearingTemplates()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var directory = factory.CreateAgentDirectorySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => directory.ListTemplatesAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);

            // Create a template, then edit only its basics.
            await directory.SaveTemplateAsync(new AgentTemplateEdit("ds04-fixture", "Fixture", "Service", "original",
                true, 7, "pudding"), timeout.Token);
            var created = Assert.Single(await directory.ListTemplatesAsync(timeout.Token),
                template => template.TemplateId == "ds04-fixture");
            Assert.Equal("Fixture", created.Name);
            Assert.False(string.IsNullOrWhiteSpace(created.AvatarId), "新建模板必须落一个真实头像 ID");
            Assert.Equal(7, created.SortOrder);

            await directory.SaveTemplateAsync(new AgentTemplateEdit("ds04-fixture", "Fixture renamed", "Coding",
                "updated", false, 9, "pudding"), timeout.Token);
            var updated = Assert.Single(await directory.ListTemplatesAsync(timeout.Token),
                template => template.TemplateId == "ds04-fixture");
            Assert.Equal("Fixture renamed", updated.Name);
            Assert.Equal("Coding", updated.Role);
            Assert.False(updated.IsEnabled);

            // Shipped presets are listed and can be imported into the directory.
            Assert.NotEmpty(await directory.ListAvatarsAsync(timeout.Token));
            var presets = await directory.ListPresetsAsync(timeout.Token);
            Assert.NotEmpty(presets);
            var preset = presets[0];
            if (!(await directory.ListTemplatesAsync(timeout.Token)).Any(template => template.TemplateId == preset.TemplateId))
            {
                await directory.ImportPresetAsync(preset.TemplateId, timeout.Token);
                Assert.Contains(await directory.ListTemplatesAsync(timeout.Token),
                    template => template.TemplateId == preset.TemplateId);
            }

            // Workspaces come from the platform database; the isolated root has exactly the default one.
            var workspaces = await directory.ListWorkspacesAsync(timeout.Token);
            Assert.NotEmpty(workspaces);
            var workspaceId = workspaces[0].WorkspaceId;

            await directory.CreateInstanceAsync(new AgentInstanceCreate(workspaceId, "DS-04 role", "from fixture",
                "ds04-fixture"), timeout.Token);
            var instances = await directory.ListInstancesAsync(workspaceId, timeout.Token);
            var instance = Assert.Single(instances, candidate => candidate.Name == "DS-04 role");
            Assert.Equal("ds04-fixture", instance.SourceTemplateId);
            Assert.True(instance.IsEnabled);
            Assert.False(instance.IsFrozen);

            // Basic edit keeps the template identity; freeze is a separate state from disable.
            await directory.SaveInstanceAsync(new AgentInstanceEdit(workspaceId, instance.AgentId, "DS-04 role renamed",
                "edited", "Coding", true, "pudding"), timeout.Token);
            var edited = Assert.Single(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.AgentId == instance.AgentId);
            Assert.Equal("DS-04 role renamed", edited.Name);
            Assert.Equal("ds04-fixture", edited.SourceTemplateId);

            await directory.SetInstanceFrozenAsync(workspaceId, instance.AgentId, true, timeout.Token);
            var frozen = Assert.Single(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.AgentId == instance.AgentId);
            Assert.True(frozen.IsFrozen);
            Assert.True(frozen.IsEnabled);

            await directory.SetInstanceFrozenAsync(workspaceId, instance.AgentId, false, timeout.Token);
            await directory.DeleteInstanceAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.DoesNotContain(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.AgentId == instance.AgentId);

            await directory.DeleteTemplateAsync("ds04-fixture", timeout.Token);
            Assert.DoesNotContain(await directory.ListTemplatesAsync(timeout.Token),
                template => template.TemplateId == "ds04-fixture");

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => directory.ListTemplatesAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AgentDocumentAdapter_SeparatesTemplateDefaultsFromInstanceOverrides()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var directory = factory.CreateAgentDirectorySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await kernel.StartAsync(root, timeout.Token);

            await directory.SaveTemplateAsync(new AgentTemplateEdit("ds04-docs", "Docs", "Service", "", true, 0, "pudding"),
                timeout.Token);
            var loaded = await directory.ReadTemplateDocumentsAsync("ds04-docs", timeout.Token);
            Assert.Equal(7, loaded.Documents.Count);
            Assert.Equal(64, loaded.Fingerprint.Length);

            var edited = new Dictionary<string, string>(loaded.Documents, StringComparer.Ordinal)
            {
                ["systemPrompt"] = "you are a fixture",
                ["personaPrompt"] = "# SOUL fixture",
                ["userPromptTemplate"] = "{{input}}"
            };
            await directory.SaveTemplateDocumentsAsync(
                new AgentTemplateDocuments("ds04-docs", loaded.Fingerprint, edited), timeout.Token);

            var reread = await directory.ReadTemplateDocumentsAsync("ds04-docs", timeout.Token);
            Assert.Equal("you are a fixture", reread.Documents["systemPrompt"]);
            Assert.Equal("# SOUL fixture", reread.Documents["personaPrompt"]);
            Assert.Equal("{{input}}", reread.Documents["userPromptTemplate"]);
            // 未编辑的文档保持为空，没有被别的内容顶替。
            Assert.Equal("", reread.Documents["memoryPrompt"]);
            Assert.NotEqual(loaded.Fingerprint, reread.Fingerprint);

            // A stale fingerprint must be refused instead of overwriting someone else's edit.
            await Assert.ThrowsAsync<SettingsConflictException>(() => directory.SaveTemplateDocumentsAsync(
                new AgentTemplateDocuments("ds04-docs", loaded.Fingerprint, edited), timeout.Token));
            Assert.Equal("you are a fixture",
                (await directory.ReadTemplateDocumentsAsync("ds04-docs", timeout.Token)).Documents["systemPrompt"]);

            var workspaces = await directory.ListWorkspacesAsync(timeout.Token);
            var workspaceId = workspaces[0].WorkspaceId;
            await directory.CreateInstanceAsync(new AgentInstanceCreate(workspaceId, "Docs role", "", "ds04-docs"), timeout.Token);
            var instance = Assert.Single(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.Name == "Docs role");

            // Instances are seeded from the template, so nothing is an override until it is edited.
            var documents = await directory.ReadInstanceDocumentsAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal(6, documents.Count);
            var soul = Assert.Single(documents, document => document.Key == "soul");
            Assert.Equal("# SOUL fixture", soul.TemplateDefault);
            Assert.Equal("# SOUL fixture", soul.Content);
            Assert.False(soul.OverridesTemplate);
            Assert.NotEmpty(soul.Sha256);
            // AGENTS.md had no template default, so the instance manifest does not reference it yet:
            // the page must say so rather than fail, and a save must create and repair it.
            var agents = Assert.Single(documents, document => document.Key == "agents");
            Assert.False(agents.IsHealthy);
            Assert.Contains("manifest", agents.Issue, StringComparison.Ordinal);
            await directory.SaveInstanceDocumentAsync(workspaceId, instance.AgentId, "agents", "# AGENTS created", "", timeout.Token);
            var repaired = Assert.Single(await directory.ReadInstanceDocumentsAsync(workspaceId, instance.AgentId, timeout.Token),
                document => document.Key == "agents");
            Assert.True(repaired.IsHealthy);
            Assert.Equal("# AGENTS created", repaired.Content);
            Assert.NotEmpty(repaired.Sha256);

            var heartbeat = Assert.Single(documents, document => document.Key == "heartbeat");
            // 心跳提示词只有实例级文档，没有模板默认值。
            Assert.Equal("", heartbeat.TemplateDefault);

            // A stale SHA must be refused; the correct one writes and turns the document into an override.
            await Assert.ThrowsAsync<SettingsConflictException>(() => directory.SaveInstanceDocumentAsync(
                workspaceId, instance.AgentId, "soul", "# edited", "0000000000000000000000000000000000000000000000000000000000000000", timeout.Token));
            await directory.SaveInstanceDocumentAsync(workspaceId, instance.AgentId, "soul", "# edited", soul.Sha256, timeout.Token);
            var updated = Assert.Single(await directory.ReadInstanceDocumentsAsync(workspaceId, instance.AgentId, timeout.Token),
                document => document.Key == "soul");
            Assert.Equal("# edited", updated.Content);
            Assert.True(updated.OverridesTemplate);
            Assert.NotEqual(soul.Sha256, updated.Sha256);

            await Assert.ThrowsAsync<ArgumentException>(() => directory.SaveInstanceDocumentAsync(
                workspaceId, instance.AgentId, "not-a-document", "x", "", timeout.Token));
            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => directory.ReadTemplateDocumentsAsync("ds04-docs", timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task AgentModelPolicyAdapter_WritesPairsAndPreservesEveryOtherField()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var directory = factory.CreateAgentDirectorySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await kernel.StartAsync(root, timeout.Token);

            var catalog = await directory.ListModelCatalogAsync(timeout.Token);
            Assert.NotEmpty(catalog);
            var chatModel = catalog.First(entry => !entry.IsEmbedding && entry.IsEnabled && !entry.IsDeprecated);

            // The shipped pool has no embedding model, so the fixture creates one through the LLM adapter.
            var llm = factory.CreateLlmSettings(kernel);
            await llm.SaveModelAsync(new LlmModelEdit(chatModel.ProviderId, "fixture-embedding", "Fixture Embedding",
                "openai", [], 8192, null, 1024, null, 0m, 0m, 0m, false, false, true, 9), timeout.Token);
            catalog = await directory.ListModelCatalogAsync(timeout.Token);
            var embeddingModel = catalog.First(entry => entry.IsEmbedding && entry.ProviderId == chatModel.ProviderId);

            // A template with a prompt and a document: the model policy save must not disturb either.
            await directory.SaveTemplateAsync(new AgentTemplateEdit("ds04-models", "Models", "Service", "", true, 0, "pudding"), timeout.Token);
            var documents = await directory.ReadTemplateDocumentsAsync("ds04-models", timeout.Token);
            var edited = new Dictionary<string, string>(documents.Documents, StringComparer.Ordinal) { ["systemPrompt"] = "keep me" };
            await directory.SaveTemplateDocumentsAsync(new AgentTemplateDocuments("ds04-models", documents.Fingerprint, edited), timeout.Token);

            var initial = await directory.ReadTemplateModelPolicyAsync("ds04-models", timeout.Token);
            Assert.False(initial.Chat.IsSet);
            Assert.Equal(AgentModelPolicyText.DefaultMemorySearchMode, initial.MemorySearchMode);
            Assert.Equal("", initial.ReasoningEffort);

            var policy = new AgentModelPolicy(
                new AgentModelChoice(chatModel.ProviderId, chatModel.ModelId),
                new AgentModelChoice(embeddingModel.ProviderId, embeddingModel.ModelId),
                new AgentModelChoice(embeddingModel.ProviderId, embeddingModel.ModelId),
                "instant", "max");
            await directory.SaveTemplateModelPolicyAsync("ds04-models", policy, timeout.Token);

            var saved = await directory.ReadTemplateModelPolicyAsync("ds04-models", timeout.Token);
            Assert.Equal(chatModel.ModelId, saved.Chat.ModelId);
            Assert.Equal(embeddingModel.ModelId, saved.Memory.ModelId);
            Assert.Equal(embeddingModel.ModelId, saved.Embedding.ModelId);
            Assert.Equal("instant", saved.MemorySearchMode);
            Assert.Equal("max", saved.ReasoningEffort);
            // The document slice must survive a model-policy save.
            Assert.Equal("keep me", (await directory.ReadTemplateDocumentsAsync("ds04-models", timeout.Token)).Documents["systemPrompt"]);

            // A half-filled pair is refused and nothing is written.
            await Assert.ThrowsAsync<ArgumentException>(() => directory.SaveTemplateModelPolicyAsync("ds04-models",
                policy with { Chat = new AgentModelChoice(chatModel.ProviderId, "") }, timeout.Token));
            Assert.Equal(chatModel.ModelId, (await directory.ReadTemplateModelPolicyAsync("ds04-models", timeout.Token)).Chat.ModelId);

            var workspaces = await directory.ListWorkspacesAsync(timeout.Token);
            var workspaceId = workspaces[0].WorkspaceId;
            await directory.CreateInstanceAsync(new AgentInstanceCreate(workspaceId, "Models role", "", "ds04-models"), timeout.Token);
            var instance = Assert.Single(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.Name == "Models role");

            // A new instance inherits the template's model defaults at creation time.
            var instancePolicy = await directory.ReadInstanceModelPolicyAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal(chatModel.ModelId, instancePolicy.Chat.ModelId);
            Assert.Equal("instant", instancePolicy.MemorySearchMode);
            Assert.Equal("max", instancePolicy.ReasoningEffort);

            await directory.SaveInstanceModelPolicyAsync(workspaceId, instance.AgentId,
                new AgentModelPolicy(new AgentModelChoice(chatModel.ProviderId, chatModel.ModelId), AgentModelChoice.None,
                    AgentModelChoice.None, "off", "low"), timeout.Token);
            var savedInstance = await directory.ReadInstanceModelPolicyAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal(chatModel.ModelId, savedInstance.Chat.ModelId);
            Assert.False(savedInstance.Memory.IsSet);
            Assert.Equal("off", savedInstance.MemorySearchMode);
            Assert.Equal("low", savedInstance.ReasoningEffort);
            // The template was not touched by an instance save.
            Assert.Equal("instant", (await directory.ReadTemplateModelPolicyAsync("ds04-models", timeout.Token)).MemorySearchMode);
            Assert.True(Assert.Single(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.AgentId == instance.AgentId).IsEnabled, "模型策略保存不得改变启用状态");

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => directory.ListModelCatalogAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task AgentSmartRouteAdapter_WritesRoutesWithoutClobberingOtherProfileFields()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var directory = factory.CreateAgentDirectorySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await kernel.StartAsync(root, timeout.Token);

            await directory.SaveTemplateAsync(new AgentTemplateEdit("ds04-smart", "Smart", "Service", "", true, 0, "pudding"), timeout.Token);
            var workspaces = await directory.ListWorkspacesAsync(timeout.Token);
            var workspaceId = workspaces[0].WorkspaceId;
            await directory.CreateInstanceAsync(new AgentInstanceCreate(workspaceId, "Smart role", "keeps this", "ds04-smart"), timeout.Token);
            var instance = Assert.Single(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.Name == "Smart role");

            var initial = await directory.ReadSmartRoutesAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal(7, initial.Count);
            Assert.All(SmartRoleRoutes.Roles, slot => Assert.Equal("", initial[slot.RoleId]));

            var routes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["explorer"] = "deepseek/deepseek-chat",
                ["tester"] = "deepseek/deepseek-reasoner"
            };
            await directory.SaveSmartRoutesAsync(workspaceId, instance.AgentId, routes, timeout.Token);

            var saved = await directory.ReadSmartRoutesAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal("deepseek/deepseek-chat", saved["explorer"]);
            Assert.Equal("deepseek/deepseek-reasoner", saved["tester"]);
            Assert.Equal("", saved["planner"]);
            // A Smart save must not clear the description, nor the rest of the profile.
            var after = Assert.Single(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.AgentId == instance.AgentId);
            Assert.Equal("keeps this", after.Description);
            Assert.True(after.IsEnabled);
            Assert.Equal("ds04-smart", after.SourceTemplateId);

            // Core owns the route format; the boundary refuses a malformed one before writing anything.
            await Assert.ThrowsAsync<ArgumentException>(() => directory.SaveSmartRoutesAsync(workspaceId, instance.AgentId,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["explorer"] = "not-a-route" }, timeout.Token));
            Assert.Equal("deepseek/deepseek-chat",
                (await directory.ReadSmartRoutesAsync(workspaceId, instance.AgentId, timeout.Token))["explorer"]);

            // Clearing a route is an explicit empty string, not a missing key.
            await directory.SaveSmartRoutesAsync(workspaceId, instance.AgentId,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["explorer"] = "", ["tester"] = "deepseek/deepseek-reasoner" },
                timeout.Token);
            var cleared = await directory.ReadSmartRoutesAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal("", cleared["explorer"]);
            Assert.Equal("deepseek/deepseek-reasoner", cleared["tester"]);

            // The basic profile save preserves routing (Core forces the stored Smart fields back).
            await directory.SaveInstanceAsync(new AgentInstanceEdit(workspaceId, instance.AgentId, "Smart role renamed",
                "edited", "Service", true, "pudding"), timeout.Token);
            Assert.Equal("deepseek/deepseek-reasoner",
                (await directory.ReadSmartRoutesAsync(workspaceId, instance.AgentId, timeout.Token))["tester"]);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => directory.ReadSmartRoutesAsync(workspaceId, instance.AgentId, timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task AgentGuardrailAdapter_KeepsBudgetsAndContainerOverrideScoped()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var directory = factory.CreateAgentDirectorySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await kernel.StartAsync(root, timeout.Token);

            await directory.SaveTemplateAsync(new AgentTemplateEdit("ds04-guard", "Guard", "Service", "", true, 0, "pudding"), timeout.Token);
            var initial = await directory.ReadTemplateGuardrailsAsync("ds04-guard", timeout.Token);
            Assert.Equal(200, initial.MaxRounds);
            Assert.Equal(86400, initial.MaxElapsedSeconds);
            Assert.Equal(400, initial.MaxToolCallsTotal);
            Assert.Equal("", initial.ContainerImage);

            var policy = new AgentGuardrailPolicy(300, 7200, 250, "mcr.microsoft.com/dotnet/sdk:10.0");
            await directory.SaveTemplateGuardrailsAsync("ds04-guard", policy, timeout.Token);
            var saved = await directory.ReadTemplateGuardrailsAsync("ds04-guard", timeout.Token);
            Assert.Equal(policy, saved);

            // A budget no loop could honour is refused and nothing is written.
            await Assert.ThrowsAsync<ArgumentException>(() => directory.SaveTemplateGuardrailsAsync("ds04-guard",
                policy with { MaxRounds = 0 }, timeout.Token));
            Assert.Equal(300, (await directory.ReadTemplateGuardrailsAsync("ds04-guard", timeout.Token)).MaxRounds);

            var workspaces = await directory.ListWorkspacesAsync(timeout.Token);
            var workspaceId = workspaces[0].WorkspaceId;
            await directory.CreateInstanceAsync(new AgentInstanceCreate(workspaceId, "Guard role", "", "ds04-guard"), timeout.Token);
            var instance = Assert.Single(await directory.ListInstancesAsync(workspaceId, timeout.Token),
                candidate => candidate.Name == "Guard role");

            // A new instance inherits the template budgets at creation time.
            var inherited = await directory.ReadInstanceGuardrailsAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal(300, inherited.MaxRounds);
            Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", inherited.ContainerImage);

            await directory.SaveInstanceGuardrailsAsync(workspaceId, instance.AgentId,
                new AgentGuardrailPolicy(50, 600, 20, ""), timeout.Token);
            var instanceSaved = await directory.ReadInstanceGuardrailsAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal(50, instanceSaved.MaxRounds);
            Assert.Equal(600, instanceSaved.MaxElapsedSeconds);
            Assert.Equal(20, instanceSaved.MaxToolCallsTotal);
            // 清空镜像覆盖必须留在空值上，而不是回退到模板。
            Assert.Equal("", instanceSaved.ContainerImage);

            // The template is untouched by an instance save, and vice versa.
            Assert.Equal(300, (await directory.ReadTemplateGuardrailsAsync("ds04-guard", timeout.Token)).MaxRounds);
            await directory.SaveTemplateGuardrailsAsync("ds04-guard", policy with { MaxRounds = 400 }, timeout.Token);
            Assert.Equal(50, (await directory.ReadInstanceGuardrailsAsync(workspaceId, instance.AgentId, timeout.Token)).MaxRounds);

            // Smart routing and the description survive a guardrail save.
            await directory.SaveSmartRoutesAsync(workspaceId, instance.AgentId,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["explorer"] = "deepseek/deepseek-chat" }, timeout.Token);
            await directory.SaveInstanceGuardrailsAsync(workspaceId, instance.AgentId,
                new AgentGuardrailPolicy(60, 900, 30, "alpine"), timeout.Token);
            Assert.Equal("deepseek/deepseek-chat",
                (await directory.ReadSmartRoutesAsync(workspaceId, instance.AgentId, timeout.Token))["explorer"]);
            Assert.Equal(60, (await directory.ReadInstanceGuardrailsAsync(workspaceId, instance.AgentId, timeout.Token)).MaxRounds);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => directory.ReadTemplateGuardrailsAsync("ds04-guard", timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task ToolPluginAdapter_ReadsRegistryAndReportsManifestOnlyWithoutClaimingExecution()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var settings = factory.CreateToolPluginSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => settings.ListToolsAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var tools = await settings.ListToolsAsync(timeout.Token);
            Assert.NotEmpty(tools);
            Assert.Equal(tools.Count, tools.Select(tool => tool.ToolId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(tools, tool => Assert.Equal(string.Empty, ToolPluginTextValidateNonExecutableMismatch(tool)));
            // Read-only by construction: the adapter exposes no create/update/delete operation.

            // A manifest-only package written into the data root must be reported as declared, not runnable.
            var pluginRoot = Path.Combine(root, "plugins", "code-search");
            Directory.CreateDirectory(pluginRoot);
            await File.WriteAllTextAsync(Path.Combine(pluginRoot, "plugin.json"), """
                {
                  "schema": "pudding-plugin/v1",
                  "id": "pudding.code-search",
                  "name": "Code Search Plugin",
                  "version": "1.0.0",
                  "entry": { "assembly": "bin/CodeSearch.dll", "type": "CodeSearch.Plugin" },
                  "tools": [
                    { "id": "plugin_code_search", "name": "Plugin Code Search", "description": "searches" }
                  ]
                }
                """);
            // An invalid manifest must be diagnosable rather than silently ignored.
            var brokenRoot = Path.Combine(root, "plugins", "broken-package");
            Directory.CreateDirectory(brokenRoot);
            await File.WriteAllTextAsync(Path.Combine(brokenRoot, "plugin.json"), "{ \"schema\": \"pudding-plugin/v1\" }");

            await settings.ReloadPluginsAsync(timeout.Token);
            var report = await settings.ReadPluginCatalogAsync(timeout.Token);
            var declared = Assert.Single(report.Packages, package => package.PluginId == "pudding.code-search");
            Assert.True(declared.IsManifestOnly);
            Assert.Equal("1.0.0", declared.Version);
            Assert.Equal(1, declared.ToolCount);

            var declaredTool = Assert.Single(report.DeclaredTools, tool => tool.PluginId == "pudding.code-search");
            Assert.Equal("plugin_code_search", declaredTool.ToolId);
            Assert.False(declaredTool.IsExecutable);
            Assert.Equal(1, report.ManifestOnlyToolCount);

            var broken = Assert.Single(report.Packages, package => package.PluginId == "broken-package");
            Assert.True(broken.IsInvalid);
            Assert.False(string.IsNullOrWhiteSpace(broken.StatusReason));
            Assert.Equal(1, report.InvalidManifestCount);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => settings.ReadPluginCatalogAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Every registry entry must agree with the shared executability rule (empty means it does).</summary>
    private static string ToolPluginTextValidateNonExecutableMismatch(PuddingDesktop.Foundation.ToolCatalogEntry tool) =>
        tool.IsExecutable == PuddingDesktop.Foundation.ToolPluginText.IsExecutable(tool.RuntimeStatus)
            ? string.Empty
            : $"{tool.ToolId} disagrees with the executability rule";
    [Fact]
    public async Task SkillHubAdapter_ReportsPublishedSkillsAndTheirAuditEvents()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var hub = factory.CreateSkillHubSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => hub.ReadOverviewAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var empty = await hub.ReadOverviewAsync(timeout.Token);
            Assert.Equal(0, empty.TotalSkills);
            Assert.Empty(await hub.ListSkillsAsync(null, null, null, 1, 50, timeout.Token));
            Assert.Empty(await hub.ListEventsAsync(null, 50, timeout.Token));

            // Seed through the product's own HTTP surface, then read it back through the settings adapter.
            using var client = await CreateAdminClientAsync(root, kernel.Snapshot.WorkbenchAddress!);
            var publish = await client.PostAsJsonAsync("/api/skill-hub/skills", new
            {
                skillId = "pudding-fixture-skill",
                name = "Fixture Skill",
                summary = "summary",
                description = "description",
                tags = new[] { "fixture" },
                keywords = new[] { "fixture" },
                version = "1.0.0",
                skillMarkdown = "# Fixture\n\nbody",
                visibility = "global"
            }, timeout.Token);
            Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync(timeout.Token));
            var version = await client.PostAsJsonAsync("/api/skill-hub/skills/pudding-fixture-skill/versions", new
            {
                skillId = "pudding-fixture-skill",
                name = "Fixture Skill",
                version = "1.1.0",
                skillMarkdown = "# Fixture\n\nsecond",
                evolutionAction = "patch",
                parentVersion = "1.0.0",
                publishNote = "fixture evolution"
            }, timeout.Token);
            Assert.True(version.IsSuccessStatusCode, await version.Content.ReadAsStringAsync(timeout.Token));

            var overview = await hub.ReadOverviewAsync(timeout.Token);
            Assert.Equal(1, overview.TotalSkills);
            Assert.Equal(1, overview.ActiveSkills);
            Assert.Equal(0, overview.RetiredSkills);
            Assert.Equal(2, overview.TotalVersions);
            Assert.Equal(1, overview.EvolvedSkills);
            Assert.Contains(overview.EvolutionActionCounts, count => count.Action == "patch" && count.Count == 1);

            var skills = await hub.ListSkillsAsync(null, null, null, 1, 50, timeout.Token);
            var skill = Assert.Single(skills, candidate => candidate.SkillId == "pudding-fixture-skill");
            Assert.Equal("1.1.0", skill.LatestVersion);
            Assert.Equal(2, skill.VersionCount);
            Assert.Contains("fixture", skill.Tags);

            var events = await hub.ListEventsAsync("pudding-fixture-skill", 50, timeout.Token);
            Assert.NotEmpty(events);
            Assert.All(events, entry => Assert.Equal("pudding-fixture-skill", entry.SkillId));
            Assert.Contains(events, entry => entry.EventType.Length > 0 && entry.ActorKind.Length > 0);
            Assert.Empty(await hub.ListEventsAsync("no-such-skill", 50, timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => hub.ListEventsAsync(null, 50, timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SkillLibraryAdapter_EditsMetaPublishesVersionRegistersInstallAndRetires()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var hub = factory.CreateSkillHubSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await kernel.StartAsync(root, timeout.Token);
            using var client = await CreateAdminClientAsync(root, kernel.Snapshot.WorkbenchAddress!);
            var publish = await client.PostAsJsonAsync("/api/skill-hub/skills", new
            {
                skillId = "pudding-library-skill",
                name = "Library Skill",
                summary = "summary",
                description = "description",
                tags = new[] { "fixture" },
                version = "1.0.0",
                skillMarkdown = "# Library\n\nfirst",
                visibility = "global"
            }, timeout.Token);
            Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync(timeout.Token));

            var detail = await hub.ReadSkillAsync("pudding-library-skill", timeout.Token);
            Assert.NotNull(detail);
            Assert.Equal("1.0.0", detail!.Skill.LatestVersion);
            var version = Assert.Single(detail.Versions);
            Assert.Equal("create", version.EvolutionAction);

            var content = await hub.ReadVersionAsync("pudding-library-skill", "1.0.0", timeout.Token);
            Assert.NotNull(content);
            Assert.Equal("# Library\n\nfirst", content!.SkillMarkdown);
            Assert.NotEmpty(content.ContentHash);
            Assert.Null(await hub.ReadVersionAsync("pudding-library-skill", "9.9.9", timeout.Token));
            Assert.Null(await hub.ReadSkillAsync("no-such-skill", timeout.Token));

            // Metadata edit must not touch the version or the markdown.
            await hub.SaveSkillMetaAsync("pudding-library-skill",
                new SkillHubMetaEdit("Library Skill renamed", "new summary", "new description",
                    ["fixture", "v2"], ["keyword"], "active", "global"), timeout.Token);
            detail = await hub.ReadSkillAsync("pudding-library-skill", timeout.Token);
            Assert.Equal("Library Skill renamed", detail!.Skill.Name);
            Assert.Equal(["fixture", "v2"], detail.Skill.Tags);
            Assert.Single(detail.Versions);
            Assert.Equal("# Library\n\nfirst",
                (await hub.ReadVersionAsync("pudding-library-skill", "1.0.0", timeout.Token))!.SkillMarkdown);

            // Publishing a version appends lineage and keeps the previous version readable.
            await hub.PublishVersionAsync(new SkillHubVersionPublish("pudding-library-skill", "Library Skill renamed",
                "1.1.0", "# Library\n\nsecond", "patch", "1.0.0", "fixture evolution", ["fixture"], "global"), timeout.Token);
            detail = await hub.ReadSkillAsync("pudding-library-skill", timeout.Token);
            Assert.Equal(2, detail!.Versions.Count);
            Assert.Equal("1.1.0", detail.Skill.LatestVersion);
            var evolved = Assert.Single(detail.Versions, item => item.Version == "1.1.0");
            Assert.Equal("patch", evolved.EvolutionAction);
            Assert.Equal("1.0.0", evolved.ParentVersion);
            Assert.Equal("fixture evolution", evolved.PublishNote);

            // A rejected publish is surfaced as a failure rather than a silent success.
            await Assert.ThrowsAsync<InvalidOperationException>(() => hub.PublishVersionAsync(
                new SkillHubVersionPublish("pudding-library-skill", "Library Skill renamed", "1.1.0",
                    "# duplicate", "patch", "1.0.0", "", [], "global"), timeout.Token));

            // Install registration is a ledger row the detail view reports.
            await hub.RegisterInstallAsync(new SkillHubInstallRegistration("pudding-library-skill",
                "default.agent_1", "default", "1.0.0", "", "composition-test"), timeout.Token);
            detail = await hub.ReadSkillAsync("pudding-library-skill", timeout.Token);
            var install = Assert.Single(detail!.RecentInstalls);
            Assert.Equal("default.agent_1", install.AgentInstanceId);
            Assert.Equal("1.0.0", install.InstalledVersion);

            // Retirement is soft: the status changes and versions survive.
            await hub.RetireSkillAsync("pudding-library-skill", timeout.Token);
            detail = await hub.ReadSkillAsync("pudding-library-skill", timeout.Token);
            Assert.Equal("retired", detail!.Skill.Status);
            Assert.False(SkillHubText.IsUsable(detail.Skill.Status));
            Assert.Equal(2, detail.Versions.Count);
            var retired = await hub.ListSkillsAsync(null, null, "retired", 1, 50, timeout.Token);
            Assert.Contains(retired, skill => skill.SkillId == "pudding-library-skill");
            Assert.DoesNotContain(await hub.ListSkillsAsync(null, null, "active", 1, 50, timeout.Token),
                skill => skill.SkillId == "pudding-library-skill");

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => hub.ReadSkillAsync("pudding-library-skill", timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    /// <summary>Builds an admin client against the in-process host using the isolated root's own signing key.</summary>
    private static async Task<HttpClient> CreateAdminClientAsync(string root, Uri address)
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, "config", "system.json")));
        var key = document.RootElement.GetProperty("Jwt").GetProperty("Key").GetString()!;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken("kernel-test", "kernel-test",
            [new(System.Security.Claims.ClaimTypes.Role, "admin")], expires: DateTime.UtcNow.AddMinutes(2),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token));
        return client;
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
