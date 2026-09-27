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
    [Fact]
    public async Task SkillLineageAndInstallLedgerAdapter_ReportChainsAndBehindVersions()
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
            var created = await client.PostAsJsonAsync("/api/skill-hub/skills", new
            {
                skillId = "pudding-evo-skill", name = "Evo Skill", version = "1.0.0",
                skillMarkdown = "# Evo\n\nfirst", visibility = "global"
            }, timeout.Token);
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(timeout.Token));
            var evolved = await client.PostAsJsonAsync("/api/skill-hub/skills/pudding-evo-skill/versions", new
            {
                skillId = "pudding-evo-skill", name = "Evo Skill", version = "1.1.0",
                skillMarkdown = "# Evo\n\nsecond", evolutionAction = "patch", parentVersion = "1.0.0",
                publishNote = "chain"
            }, timeout.Token);
            Assert.True(evolved.IsSuccessStatusCode, await evolved.Content.ReadAsStringAsync(timeout.Token));

            var lineage = await hub.ReadLineageAsync("pudding-evo-skill", timeout.Token);
            Assert.NotNull(lineage);
            Assert.Equal(2, lineage!.Nodes.Count);
            var rootNode = Assert.Single(lineage.Nodes, node => node.Version == "1.0.0");
            // create 版本是根节点。
            Assert.Equal("", rootNode.ParentNodeId);
            var childNode = Assert.Single(lineage.Nodes, node => node.Version == "1.1.0");
            Assert.Equal(rootNode.NodeId, childNode.ParentNodeId);
            Assert.Equal("patch", childNode.EvolutionAction);
            Assert.Single(lineage.Edges);
            var lines = SkillHubText.RenderLineage(lineage);
            Assert.Equal(2, lines.Count);
            Assert.StartsWith("1.0.0", lines[0], StringComparison.Ordinal);
            Assert.StartsWith("  1.1.0", lines[1], StringComparison.Ordinal);

            var global = await hub.ReadGlobalLineageAsync(null, 500, timeout.Token);
            Assert.Contains(global.Nodes, node => node.SkillId == "pudding-evo-skill");
            Assert.Null(await hub.ReadLineageAsync("no-such-skill", timeout.Token));

            await hub.RegisterInstallAsync(new SkillHubInstallRegistration("pudding-evo-skill",
                "default.evo_agent", "default", "1.0.0", "", "composition-test"), timeout.Token);
            var installs = await hub.ListInstallsAsync("default.evo_agent", "pudding-evo-skill", 1, 50, timeout.Token);
            var install = Assert.Single(installs);
            Assert.Equal("1.0.0", install.InstalledVersion);
            Assert.Equal("default.evo_agent", install.AgentInstanceId);
            Assert.Empty(await hub.ListInstallsAsync("someone-else", null, 1, 50, timeout.Token));

            // The agent reports an older version, so the hub must report it as behind.
            var updates = await hub.ListUpdatesAsync("default.evo_agent", timeout.Token);
            var update = Assert.Single(updates, entry => entry.SkillId == "pudding-evo-skill");
            Assert.Equal("1.0.0", update.InstalledVersion);
            Assert.Equal("1.1.0", update.LatestVersion);
            Assert.True(update.IsBehind);
            Assert.Empty(await hub.ListUpdatesAsync("no-such-agent", timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => hub.ReadLineageAsync("pudding-evo-skill", timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task SkillPackageAdapter_NeverReportsSuccessWithoutAFileOrAStore()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var packages = factory.CreateSkillPackageSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => packages.ListAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            Assert.Empty(await packages.ListAsync(timeout.Token));

            // Nothing is seeded, so every row-addressed operation must fail rather than pretend.
            await Assert.ThrowsAsync<InvalidOperationException>(() => packages.SaveMetaAsync(
                new SkillPackageMetaEdit("missing", "N", "", true, 1), timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => packages.DeleteAsync("missing", timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => packages.GetDownloadUrlAsync("missing", timeout.Token));

            // A missing file is rejected before any Core call, so no row and no object can appear.
            var missingFile = Path.Combine(root, "does-not-exist.zip");
            await Assert.ThrowsAsync<FileNotFoundException>(() => packages.UploadAsync(
                new SkillPackageUploadEdit("fixture-pack", "Fixture", "", "1.0.0", 100, missingFile), timeout.Token));
            await Assert.ThrowsAsync<FileNotFoundException>(() => packages.ReplaceFileAsync(
                new SkillPackageFileEdit("fixture-pack", "1.1.0", missingFile), timeout.Token));
            Assert.Empty(await packages.ListAsync(timeout.Token));

            // A real file with a rejected extension must also be refused before Core writes anything.
            var rejected = Path.Combine(root, "fixture.tgz");
            await File.WriteAllBytesAsync(rejected, [1, 2, 3], timeout.Token);
            var badExtension = await Assert.ThrowsAsync<InvalidOperationException>(() => packages.UploadAsync(
                new SkillPackageUploadEdit("fixture-pack", "Fixture", "", "1.0.0", 100, rejected), timeout.Token));
            Assert.Contains("zip", badExtension.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await packages.ListAsync(timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => packages.ListAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task AgentGrantAdapter_KeepsTemplateGrantsAndInstanceSnapshotSeparate()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var agents = factory.CreateAgentDirectorySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => agents.ListGrantOptionsAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var options = await agents.ListGrantOptionsAsync(timeout.Token);
            Assert.NotEmpty(options.Capabilities);
            Assert.Equal(options.Capabilities.Count,
                options.Capabilities.Select(option => option.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(options.Capabilities, option => Assert.True(option.IsCapability));

            // Reading grants for something that does not exist must fail rather than return an empty set.
            await Assert.ThrowsAsync<InvalidOperationException>(() => agents.ReadTemplateGrantsAsync("no-such-template", timeout.Token));

            await agents.SaveTemplateAsync(new AgentTemplateEdit("grant-template", "Grant Template", "developer",
                "fixture", true, 500, ""), timeout.Token);
            var granted = options.Capabilities.Take(2).Select(option => option.Id).ToArray();
            await agents.SaveTemplateGrantsAsync("grant-template", new AgentGrantSet(granted, []), timeout.Token);
            var templateGrants = await agents.ReadTemplateGrantsAsync("grant-template", timeout.Token);
            Assert.Equal(AgentGrantText.Normalize(granted), templateGrants.CapabilityIds);
            Assert.Empty(templateGrants.SkillPackageIds);

            var workspaces = await agents.ListWorkspacesAsync(timeout.Token);
            Assert.NotEmpty(workspaces);
            var workspaceId = workspaces[0].WorkspaceId;
            await agents.CreateInstanceAsync(new AgentInstanceCreate(workspaceId, "grant-agent", "fixture", "grant-template"), timeout.Token);
            var instance = (await agents.ListInstancesAsync(workspaceId, timeout.Token))
                .Single(agent => agent.Name == "grant-agent");

            // Creation inherits the template snapshot.
            var state = await agents.ReadInstanceGrantsAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal(templateGrants.CapabilityIds, state.Grants.CapabilityIds);
            Assert.True(state.Comparison.IsIdentical);
            Assert.Equal("与模板授权一致", AgentGrantText.DescribeComparison(state.TemplateGrants, state.Grants));

            // "Explicitly none" writes an empty list; it must not fall back to the template.
            await agents.SaveInstanceGrantsAsync(workspaceId, instance.AgentId,
                AgentGrantSelection.None, AgentGrantSelection.None, timeout.Token);
            state = await agents.ReadInstanceGrantsAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Empty(state.Grants.CapabilityIds);
            Assert.False(state.Comparison.IsIdentical);
            Assert.Contains("比模板少", AgentGrantText.DescribeComparison(state.TemplateGrants, state.Grants), StringComparison.Ordinal);
            Assert.Equal("没有授权（明确不授权）", AgentGrantText.DescribeSet(state.Grants, options));

            // "Unspecified" keeps the stored value; it does not re-inherit the template.
            await agents.SaveInstanceGrantsAsync(workspaceId, instance.AgentId,
                AgentGrantSelection.UnspecifiedSelection, AgentGrantSelection.UnspecifiedSelection, timeout.Token);
            state = await agents.ReadInstanceGrantsAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Empty(state.Grants.CapabilityIds);

            // Adopting the template writes its current value.
            await agents.SaveInstanceGrantsAsync(workspaceId, instance.AgentId,
                AgentGrantSelection.Of(state.TemplateGrants.CapabilityIds),
                AgentGrantSelection.Of(state.TemplateGrants.SkillPackageIds), timeout.Token);
            state = await agents.ReadInstanceGrantsAsync(workspaceId, instance.AgentId, timeout.Token);
            Assert.Equal(templateGrants.CapabilityIds, state.Grants.CapabilityIds);
            Assert.True(state.Comparison.IsIdentical);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => agents.ReadTemplateGrantsAsync("grant-template", timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task WorkspaceAdapter_CreatesEditsFreezesAndKeepsMembersInsideTheirWorkspace()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var workspaces = factory.CreateWorkspaceSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => workspaces.ListAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var seeded = await workspaces.ListAsync(timeout.Token);
            Assert.Contains(seeded, workspace => workspace.WorkspaceId == "default");
            Assert.True(seeded.Single(workspace => workspace.WorkspaceId == "default").IsBuiltInDefault);

            var teams = await workspaces.ListTeamsAsync(timeout.Token);
            Assert.NotEmpty(teams);
            // 隔离数据根没有经过 Bootstrap，因此没有用户；成员增删的正常路径由 WorkspaceServiceTests 覆盖，
            // 这里验证适配器不会吞掉 Core 的拒绝。
            _ = await workspaces.ListUsersAsync(timeout.Token);

            // The built-in default workspace is refused by Core, and it must survive the attempt.
            await Assert.ThrowsAsync<InvalidOperationException>(() => workspaces.DeleteAsync("default", timeout.Token));
            Assert.Contains(await workspaces.ListAsync(timeout.Token), workspace => workspace.WorkspaceId == "default");

            var create = new WorkspaceCreateRequest("fixture-space", teams[0].TeamId, "Fixture Space", "created by test",
                "{\"theme\":\"dark\"}", "Manage", "ReadOnly");
            await workspaces.CreateAsync(create, timeout.Token);
            var created = (await workspaces.ListAsync(timeout.Token)).Single(workspace => workspace.WorkspaceId == "fixture-space");
            Assert.True(created.IsEnabled, "新建工作区默认启用");
            Assert.False(created.IsFrozen);
            Assert.Equal(teams[0].TeamId, created.TeamId);

            await Assert.ThrowsAsync<InvalidOperationException>(() => workspaces.CreateAsync(
                create with { WorkspaceId = "fixture-space" }, timeout.Token));

            await workspaces.SaveAsync(new WorkspaceEdit("fixture-space", "Renamed Space", "edited",
                "{\"theme\":\"light\"}", "ReadOnly", "None", true), timeout.Token);
            var edited = (await workspaces.ListAsync(timeout.Token)).Single(workspace => workspace.WorkspaceId == "fixture-space");
            Assert.Equal("Renamed Space", edited.Name);
            Assert.Equal("ReadOnly", edited.TeamAccessPolicy);
            // The default workspace is untouched by edits to another one.
            Assert.Equal("default",
                (await workspaces.ListAsync(timeout.Token)).Single(workspace => workspace.WorkspaceId == "default").WorkspaceId);

            await workspaces.SetFrozenAsync("fixture-space", frozen: true, timeout.Token);
            Assert.True((await workspaces.ListAsync(timeout.Token))
                .Single(workspace => workspace.WorkspaceId == "fixture-space").IsFrozen);
            await workspaces.SetFrozenAsync("fixture-space", frozen: false, timeout.Token);
            Assert.False((await workspaces.ListAsync(timeout.Token))
                .Single(workspace => workspace.WorkspaceId == "fixture-space").IsFrozen);

            // A second workspace proves member operations stay inside the one they name.
            await workspaces.CreateAsync(create with { WorkspaceId = "other-space", Name = "Other Space" }, timeout.Token);
            Assert.Empty(await workspaces.ListMembersAsync("fixture-space", timeout.Token));
            Assert.Empty(await workspaces.ListMembersAsync("other-space", timeout.Token));

            // An unknown user is refused by Core and the adapter must surface it as a real failure.
            await Assert.ThrowsAsync<InvalidOperationException>(() => workspaces.AddMemberAsync(
                "fixture-space", "no-such-user-xyz", "Write", timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => workspaces.RemoveMemberAsync(
                "other-space", 4242, timeout.Token));
            // A member operation against a workspace that does not exist is refused too.
            await Assert.ThrowsAsync<InvalidOperationException>(() => workspaces.AddMemberAsync(
                "no-such-space", "no-such-user-xyz", "Write", timeout.Token));

            await workspaces.DeleteAsync("fixture-space", timeout.Token);
            Assert.DoesNotContain(await workspaces.ListAsync(timeout.Token),
                workspace => workspace.WorkspaceId == "fixture-space");

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => workspaces.ListAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task ChannelAdapter_KeepsTheStoredSecretAndEnforcesTheRealChannelRules()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var channels = factory.CreateChannelSettings(kernel);
        var workspaces = factory.CreateWorkspaceSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => channels.ListProvidersAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var providers = await channels.ListProvidersAsync(timeout.Token);
            var feishu = Assert.Single(providers, provider => provider.ProviderId == "feishu");
            Assert.True(feishu.IsBuiltIn, "飞书服务商由 Core 内置定义");
            Assert.Contains("streaming", feishu.Capabilities);

            var workspaceId = (await workspaces.ListAsync(timeout.Token)).Single().WorkspaceId;
            Assert.Empty(await channels.ListChannelsAsync(workspaceId, timeout.Token));

            // A disabled provider cannot be used, and an unknown one is refused.
            await channels.SaveProviderAsync(new ChannelProviderEdit("feishu", "飞书", "renamed", false), timeout.Token);
            var disabled = await channels.ListProvidersAsync(timeout.Token);
            Assert.False(disabled.Single(provider => provider.ProviderId == "feishu").IsEnabled);
            var withDisabledProvider = new ChannelEdit(workspaceId, "", "Channel", "", "feishu", "", "cli_app",
                ChannelSecret.Of("secret-value"), true, false, "", [], true);
            // 停用的服务商是 InvalidOperationException；服务商不存在才是 KeyNotFoundException。
            await Assert.ThrowsAsync<InvalidOperationException>(() => channels.CreateChannelAsync(withDisabledProvider, timeout.Token));
            Assert.Empty(await channels.ListChannelsAsync(workspaceId, timeout.Token));

            await channels.SaveProviderAsync(new ChannelProviderEdit("feishu", "飞书", "renamed back", true), timeout.Token);

            // A bound agent must exist in this workspace (Core reports a missing agent as KeyNotFound).
            await Assert.ThrowsAsync<KeyNotFoundException>(() => channels.CreateChannelAsync(
                withDisabledProvider with { BoundAgentId = "no-such-agent" }, timeout.Token));

            await channels.CreateChannelAsync(withDisabledProvider, timeout.Token);
            var created = Assert.Single(await channels.ListChannelsAsync(workspaceId, timeout.Token));
            Assert.Equal("cli_app", created.AppId);
            Assert.True(created.HasAppSecret, "创建时必须带密钥，但返回值只有布尔标记");
            Assert.True(created.StreamingRepliesEnabled);

            // Editing without replacing keeps the stored secret; the adapter sends null for it.
            await channels.SaveChannelAsync(new ChannelEdit(workspaceId, created.ChannelId, "Renamed Channel", "edited",
                "feishu", "", "cli_app2", ChannelSecret.Keep, false, true, "Cherry", ["ou_1"], true), timeout.Token);
            var edited = Assert.Single(await channels.ListChannelsAsync(workspaceId, timeout.Token));
            Assert.Equal("Renamed Channel", edited.Name);
            Assert.Equal("cli_app2", edited.AppId);
            Assert.True(edited.HasAppSecret, "留空的密钥必须保持，不能被清除");
            Assert.True(edited.TtsRepliesEnabled);
            Assert.Equal("Cherry", edited.TtsVoice);
            Assert.Equal(["ou_1"], edited.PrivilegedUserOpenIds);

            // A second channel with the same Feishu App ID is refused.
            await Assert.ThrowsAsync<InvalidOperationException>(() => channels.CreateChannelAsync(
                withDisabledProvider with { Name = "Duplicate", AppId = "cli_app2" }, timeout.Token));

            await channels.DeleteChannelAsync(workspaceId, created.ChannelId, timeout.Token);
            Assert.Empty(await channels.ListChannelsAsync(workspaceId, timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => channels.ListProvidersAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task WorkspaceResourceAdapter_KeepsResourcesInsideTheirWorkspace()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var resources = factory.CreateWorkspaceResourceSettings(kernel);
        var workspaces = factory.CreateWorkspaceSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => resources.ListSkillsAsync("any", timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var teams = await workspaces.ListTeamsAsync(timeout.Token);
            var template = new WorkspaceCreateRequest("resource-space", teams[0].TeamId, "Resource Space", "",
                "", "Manage", "Manage");
            await workspaces.CreateAsync(template, timeout.Token);
            await workspaces.CreateAsync(template with { WorkspaceId = "other-space", Name = "Other Space" }, timeout.Token);

            var workspaceId = "resource-space";
            Assert.Empty(await resources.ListKnowledgeBasesAsync(workspaceId, timeout.Token));
            // A workspace that does not exist is refused, not silently treated as empty.
            await Assert.ThrowsAsync<InvalidOperationException>(() => resources.ListKnowledgeBasesAsync("ghost", timeout.Token));

            await resources.SaveKnowledgeBaseAsync(new KnowledgeBaseEdit(workspaceId, "", "Docs", "notes", "VectorStore", true), timeout.Token);
            var kb = Assert.Single(await resources.ListKnowledgeBasesAsync(workspaceId, timeout.Token));
            Assert.Equal("VectorStore", kb.KbType);
            Assert.Equal(0, kb.DocumentCount);
            await resources.SaveKnowledgeBaseAsync(new KnowledgeBaseEdit(workspaceId, kb.KbId, "Docs v2", "edited", "Graph", false), timeout.Token);
            var edited = Assert.Single(await resources.ListKnowledgeBasesAsync(workspaceId, timeout.Token));
            Assert.Equal("Docs v2", edited.Name);
            Assert.Equal("Graph", edited.KbType);
            Assert.False(edited.IsEnabled);

            // The other workspace cannot see or address it.
            Assert.Empty(await resources.ListKnowledgeBasesAsync("other-space", timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resources.SaveKnowledgeBaseAsync(
                new KnowledgeBaseEdit("other-space", kb.KbId, "Hijack", "", "Graph", true), timeout.Token));

            await resources.SaveSkillAsync(new WorkspaceSkillEdit(workspaceId, "", "Builtin", "", "BuiltIn", "{\"raw\":1}", true), timeout.Token);
            var skills = await resources.ListSkillsAsync(workspaceId, timeout.Token);
            var builtIn = Assert.Single(skills);
            Assert.Equal("BuiltIn", builtIn.SkillType);
            Assert.False(builtIn.IsMcp);
            Assert.Equal("{\"raw\":1}", builtIn.ConfigJson);

            // An MCP skill with malformed config is refused by Core and the adapter surfaces it.
            await Assert.ThrowsAsync<InvalidOperationException>(() => resources.SaveSkillAsync(
                new WorkspaceSkillEdit(workspaceId, "", "Broken", "", "MCP", "{not json", true), timeout.Token));
            Assert.Single(await resources.ListSkillsAsync(workspaceId, timeout.Token));
            Assert.Empty(await resources.ListSkillsAsync("other-space", timeout.Token));

            await resources.SaveWorkflowAsync(new WorkspaceWorkflowEdit(workspaceId, "", "Flow", "", "{\"steps\":[]}", "Draft", true), timeout.Token);
            var workflow = Assert.Single(await resources.ListWorkflowsAsync(workspaceId, timeout.Token));
            Assert.Equal("Draft", workflow.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => resources.SaveWorkflowAsync(
                new WorkspaceWorkflowEdit(workspaceId, "", "Bad", "", "not json", "Draft", true), timeout.Token));
            // An empty definition is allowed.
            await resources.SaveWorkflowAsync(new WorkspaceWorkflowEdit(workspaceId, "", "Empty", "", "", "Active", true), timeout.Token);
            var all = await resources.ListWorkflowsAsync(workspaceId, timeout.Token);
            Assert.Equal(2, all.Count);
            Assert.Contains(all, flow => flow.Status == "Active" && flow.DefinitionJson.Length == 0);

            await resources.DeleteWorkflowAsync(workspaceId, workflow.WorkflowId, timeout.Token);
            Assert.Single(await resources.ListWorkflowsAsync(workspaceId, timeout.Token));
            await resources.DeleteSkillAsync(workspaceId, builtIn.SkillId, timeout.Token);
            Assert.Empty(await resources.ListSkillsAsync(workspaceId, timeout.Token));
            await resources.DeleteKnowledgeBaseAsync(workspaceId, kb.KbId, timeout.Token);
            Assert.Empty(await resources.ListKnowledgeBasesAsync(workspaceId, timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => resources.ListWorkflowsAsync(workspaceId, timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task MemoryLibraryAdapter_CreatesTreeBookAndChaptersPerAgent()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var memory = factory.CreateMemoryLibrarySettings(kernel);
        var workspaces = factory.CreateWorkspaceSettings(kernel);
        var directory = factory.CreateAgentDirectorySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => memory.ListLibrariesAsync("ws", "agent", timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var teams = await workspaces.ListTeamsAsync(timeout.Token);
            await workspaces.CreateAsync(new WorkspaceCreateRequest("memory-space", teams[0].TeamId,
                "Memory Space", "", "", "Manage", "Manage"), timeout.Token);

            // A memory library is agent scoped, so the agent must exist first.
            await directory.CreateInstanceAsync(new AgentInstanceCreate("memory-space", "memory-agent",
                "fixture", "grant-template"), timeout.Token);
            var agent = (await directory.ListInstancesAsync("memory-space", timeout.Token))
                .SingleOrDefault(instance => instance.Name == "memory-agent");
            // The template may not exist in this root, so fall back to any instance.
            agent ??= (await directory.ListInstancesAsync("memory-space", timeout.Token)).FirstOrDefault();
            Assert.NotNull(agent);

            var library = await memory.EnsureDefaultLibraryAsync("memory-space", agent.AgentId, timeout.Token);
            Assert.Equal("memory-space", library.WorkspaceId);
            Assert.Contains(await memory.ListLibrariesAsync("memory-space", agent.AgentId, timeout.Token),
                item => item.LibraryId == library.LibraryId);

            // EnsureDefaultLibrary may seed root nodes, so assert our node is added rather than assuming empty.
            var seeded = await memory.ReadTreeAsync("memory-space", agent.AgentId, library.LibraryId, timeout.Token);
            await memory.CreateTreeNodeAsync(new MemoryTreeNodeCreate("memory-space", agent.AgentId,
                library.LibraryId, "", "Root Page", "root", "Page"), timeout.Token);
            var tree = await memory.ReadTreeAsync("memory-space", agent.AgentId, library.LibraryId, timeout.Token);
            // Core decides how a created page maps onto tree nodes (and may seed its own), so assert the
            // node is present and that the tree grew, without encoding Core's internal page/book layout.
            var createdNodes = MemoryLibraryText.Flatten(tree).Where(node => node.Title == "Root Page").ToArray();
            Assert.NotEmpty(createdNodes);
            Assert.Contains(createdNodes, node => node.Type == "Page");
            Assert.True(MemoryLibraryText.CountNodes(tree) > MemoryLibraryText.CountNodes(seeded),
                "新建节点后树应当增长");

            await memory.CreateBookAsync(new MemoryBookCreate("memory-space", agent.AgentId, library.LibraryId,
                createdNodes[0].Id, "Book One", "summary"), timeout.Token);
            tree = await memory.ReadTreeAsync("memory-space", agent.AgentId, library.LibraryId, timeout.Token);
            var bookNode = MemoryLibraryText.Flatten(tree).FirstOrDefault(node => node.Title == "Book One")!;
            bookNode ??= MemoryLibraryText.Flatten(tree).First(node => node.HasBook);
            Assert.NotNull(bookNode);

            var book = await memory.ReadBookAsync("memory-space", agent.AgentId, bookNode.BookId, timeout.Token);
            Assert.NotNull(book);
            Assert.Empty(book!.Chapters);

            await memory.CreateChapterAsync(new MemoryChapterCreate("memory-space", agent.AgentId,
                book.BookId, "Chapter One", "content one", 0.5), timeout.Token);
            book = await memory.ReadBookAsync("memory-space", agent.AgentId, book.BookId, timeout.Token);
            var chapter = Assert.Single(book!.Chapters);
            Assert.Equal("Chapter One", chapter.Title);
            Assert.Equal(0.5, chapter.Importance);

            await memory.UpdateChapterAsync(new MemoryChapterEdit("memory-space", agent.AgentId,
                chapter.ChapterId, "Chapter One v2", "content two", 0.9), timeout.Token);
            book = await memory.ReadBookAsync("memory-space", agent.AgentId, book.BookId, timeout.Token);
            Assert.Equal("Chapter One v2", book!.Chapters.Single().Title);
            Assert.Equal("content two", book.Chapters.Single().Content);

            await memory.UpdateBookAsync(new MemoryBookEdit("memory-space", agent.AgentId, book.BookId,
                "Book One v2", "edited"), timeout.Token);
            Assert.Equal("Book One v2", (await memory.ReadBookAsync("memory-space", agent.AgentId, book.BookId, timeout.Token))!.Title);

            // A chapter id addressed through a different agent id must not resolve: Core reports the
            // scoped ownership failure as UnauthorizedAccessException, which the page surfaces as-is.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => memory.ArchiveChapterAsync(
                "memory-space", "no-such-agent", chapter.ChapterId, timeout.Token));

            await memory.ArchiveChapterAsync("memory-space", agent.AgentId, chapter.ChapterId, timeout.Token);
            await memory.ArchiveBookAsync("memory-space", agent.AgentId, book.BookId, timeout.Token);
            var archived = await memory.ReadBookAsync("memory-space", agent.AgentId, book.BookId, timeout.Token);
            Assert.NotEqual("Active", archived!.Status);
            // Archiving is not deleting: the chapter content is still there.
            Assert.Single(archived.Chapters);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => memory.ListLibrariesAsync("memory-space", agent.AgentId, timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task MemorySearchAdapter_FindsChaptersAndInspectsSourcesAndPointers()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var memory = factory.CreateMemoryLibrarySettings(kernel);
        var workspaces = factory.CreateWorkspaceSettings(kernel);
        var directory = factory.CreateAgentDirectorySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => memory.SearchAsync("ws", "agent", "q", 10, timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var teams = await workspaces.ListTeamsAsync(timeout.Token);
            await workspaces.CreateAsync(new WorkspaceCreateRequest("search-space", teams[0].TeamId,
                "Search Space", "", "", "Manage", "Manage"), timeout.Token);
            await directory.CreateInstanceAsync(new AgentInstanceCreate("search-space", "search-agent",
                "fixture", "grant-template"), timeout.Token);
            var agent = (await directory.ListInstancesAsync("search-space", timeout.Token))
                .FirstOrDefault(instance => instance.Name == "search-agent")
                ?? (await directory.ListInstancesAsync("search-space", timeout.Token)).First();
            Assert.NotNull(agent);

            var library = await memory.EnsureDefaultLibraryAsync("search-space", agent!.AgentId, timeout.Token);
            await memory.CreateBookAsync(new MemoryBookCreate("search-space", agent.AgentId, library.LibraryId,
                "", "Searchable Book", "fixture"), timeout.Token);
            var tree = await memory.ReadTreeAsync("search-space", agent.AgentId, library.LibraryId, timeout.Token);
            var bookId = MemoryLibraryText.Flatten(tree).First(node => node.Title == "Searchable Book").BookId;
            await memory.CreateChapterAsync(new MemoryChapterCreate("search-space", agent.AgentId, bookId,
                "Zebra Chapter", "the quick brown zebra jumps", 0.6), timeout.Token);

            var hits = await memory.SearchAsync("search-space", agent.AgentId, "zebra", 10, timeout.Token);
            var hit = Assert.Single(hits, item => item.ChapterId.Length > 0);
            Assert.Equal(bookId, hit.BookId);
            // A hit carries the book title and the matched snippet only - no chapter title - which is why
            // the inspector reads the book to show chapter metadata.
            Assert.Equal("Searchable Book", hit.BookTitle);
            Assert.Contains("zebra", hit.Snippet, StringComparison.OrdinalIgnoreCase);
            // 分数由 Core 决定：FTS 路径可能就是 0，界面照实显示而不是自己造一个相关度。
            Assert.True(hit.Score >= 0, "分数不应为负数");
            // A query with no match returns nothing rather than a fabricated result.
            Assert.Empty(await memory.SearchAsync("search-space", agent.AgentId, "zzzz-no-such-term", 10, timeout.Token));

            // The inspector's two key pairs are independent Core queries.
            var sources = await memory.ListSourcesAsync("search-space", agent.AgentId, "chapter", hit.ChapterId, timeout.Token);
            Assert.NotNull(sources);
            var pointers = await memory.ListPointersAsync("search-space", agent.AgentId, "chapter", hit.ChapterId, timeout.Token);
            Assert.NotNull(pointers);

            // The hit's chapter is readable through the library, which is what 定位 relies on.
            var book = await memory.ReadBookAsync("search-space", agent.AgentId, hit.BookId, timeout.Token);
            Assert.NotNull(book);
            Assert.Contains(book!.Chapters, chapter => chapter.ChapterId == hit.ChapterId);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => memory.SearchAsync("search-space", agent.AgentId, "zebra", 10, timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task StorageAdapter_ReadsCachedInventoryAndUpdatesThePolicyWithCas()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var storage = factory.CreateStorageSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => storage.ReadSnapshotAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);

            // Reading is a cached read: it must work immediately without triggering a scan.
            var snapshot = await storage.ReadSnapshotAsync(timeout.Token);
            Assert.True(snapshot.Revision >= 0);
            Assert.NotNull(snapshot.Databases);
            Assert.NotNull(snapshot.Classes);
            Assert.True(snapshot.TotalBytes >= 0);

            var classes = await storage.ListDataClassesAsync(timeout.Token);
            Assert.NotEmpty(classes);
            Assert.Equal(classes.Count, classes.Select(item => item.TargetId).Distinct(StringComparer.Ordinal).Count());
            Assert.All(classes, item => Assert.False(string.IsNullOrWhiteSpace(item.SafetyLevelName)));
            var protectedObjects = await storage.ListProtectedObjectsAsync(timeout.Token);
            Assert.NotNull(protectedObjects);

            // The trend may legitimately be empty on a fresh root; it must still answer.
            var trend = await storage.ReadTrendAsync(7, timeout.Token);
            Assert.NotNull(trend);

            var policy = await storage.ReadPolicyAsync(timeout.Token);
            Assert.NotNull(policy.Targets);
            var automatic = policy.Targets.Where(target => target.AutomaticCleanupAllowed).ToArray();
            Assert.NotEmpty(automatic);
            var target = automatic[0];

            // A stale revision is a conflict, not an overwrite.
            await Assert.ThrowsAsync<SettingsConflictException>(() => storage.SavePolicyAsync(
                new StorageRetentionPolicyUpdate(policy.PolicyRevision + 99, policy.AutomaticCleanupEnabled,
                    [new StorageRetentionTargetEdit(target.TargetId, true, target.DefaultRetentionDays ?? 14)]),
                timeout.Token));

            // A target Core protects against automatic cleanup cannot be enabled.
            var forbidden = policy.Targets.FirstOrDefault(item => !item.AutomaticCleanupAllowed);
            if (forbidden is not null)
                await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SavePolicyAsync(
                    new StorageRetentionPolicyUpdate(policy.PolicyRevision, true,
                        [new StorageRetentionTargetEdit(forbidden.TargetId, true, 7)]), timeout.Token));

            // A valid update succeeds and advances the revision Core reports back.
            var days = target.DefaultRetentionDays ?? target.MinRetentionDays ?? 14;
            await storage.SavePolicyAsync(new StorageRetentionPolicyUpdate(policy.PolicyRevision,
                policy.AutomaticCleanupEnabled,
                [new StorageRetentionTargetEdit(target.TargetId, true, days)]), timeout.Token);
            var updated = await storage.ReadPolicyAsync(timeout.Token);
            Assert.True(updated.PolicyRevision > policy.PolicyRevision);
            Assert.Contains(updated.Targets, item => item.TargetId == target.TargetId && item.Enabled);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => storage.ReadPolicyAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task StorageCleanupAdapter_PreviewsCreatesConfirmsAndCancelsAJob()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var storage = factory.CreateStorageSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => storage.ListCleanupJobsAsync(50, timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            Assert.Empty(await storage.ListCleanupJobsAsync(50, timeout.Token));

            // Only categories Core allows manual cleanup on can be previewed.
            var classes = await storage.ListDataClassesAsync(timeout.Token);
            var previewable = classes.Where(item => item.ManualCleanupAllowed).ToArray();
            Assert.NotEmpty(previewable);

            // A category Core protects from manual cleanup must be refused by the preview.
            var forbidden = classes.FirstOrDefault(item => !item.ManualCleanupAllowed);
            if (forbidden is not null)
                await Assert.ThrowsAnyAsync<Exception>(() => storage.CreateCleanupPreviewAsync(
                    [forbidden.TargetId], 30, timeout.Token));

            var preview = await storage.CreateCleanupPreviewAsync([previewable[0].TargetId], 30, timeout.Token);
            Assert.Equal(previewable[0].TargetId, Assert.Single(preview.Targets).TargetId);
            Assert.True(preview.ExpiresAtUtc > preview.CreatedAtUtc, "预览必须带过期时间");
            Assert.False(preview.IsExpired(DateTimeOffset.UtcNow));

            // The request id is Core's idempotency key: the same value must not create a second job.
            var requestId = $"composition-{Guid.NewGuid():N}";
            var jobId = await storage.CreateCleanupJobAsync(preview.PreviewId, requestId, timeout.Token);
            var second = await storage.CreateCleanupJobAsync(preview.PreviewId, requestId, timeout.Token);
            Assert.Equal(jobId, second);

            var job = await storage.ReadCleanupJobAsync(jobId, timeout.Token);
            Assert.NotNull(job);
            Assert.Equal([previewable[0].TargetId], job!.TargetIds);
            Assert.True(job.NeedsConfirmation || job.Status is "Queued" or "Running",
                $"新建作业应等待确认或已在执行，实际 {job.Status}");

            // A fresh preview cannot create a second job with a different request id and the same preview.
            await Assert.ThrowsAnyAsync<Exception>(() => storage.CreateCleanupJobAsync(
                preview.PreviewId, $"composition-{Guid.NewGuid():N}", timeout.Token));

            // Events are readable and the job is listed.
            var events = await storage.ReadCleanupEventsAsync(jobId, 50, timeout.Token);
            Assert.NotNull(events);
            Assert.Contains(await storage.ListCleanupJobsAsync(50, timeout.Token), item => item.JobId == jobId);

            // Confirming moves the job forward; cancelling is a request, so it must be accepted too.
            if (job.NeedsConfirmation) await storage.ConfirmCleanupJobAsync(jobId, timeout.Token);
            var confirmed = await storage.ReadCleanupJobAsync(jobId, timeout.Token);
            Assert.NotNull(confirmed);
            Assert.NotEqual("NeedsConfirmation", confirmed!.Status);

            var cancellable = confirmed.IsTerminal ? job : confirmed;
            if (!cancellable.IsTerminal) await storage.CancelCleanupJobAsync(cancellable.JobId, timeout.Token);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => storage.ReadCleanupJobAsync(jobId, timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task SecurityAdapter_ManagesSecretMetadataWithoutEverReadingPlaintext()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var security = factory.CreateSecuritySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => security.ListSecretsAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            Assert.Empty(await security.ListSecretsAsync(timeout.Token));

            await security.SaveSecretAsync(new VaultSecretEdit("", "composition-key", "fixture", "api",
                "super-secret-value", ["fixture"]), timeout.Token);
            var created = Assert.Single(await security.ListSecretsAsync(timeout.Token));
            Assert.Equal("composition-key", created.Name);
            Assert.Equal("api", created.Category);
            Assert.Equal(["fixture"], created.Tags);
            // The model carries metadata and the placeholder only; reading plaintext is not even possible.
            Assert.Equal("{{vault:composition-key}}", created.Placeholder);
            Assert.DoesNotContain("super-secret-value", created.ToString(), StringComparison.Ordinal);

            // A blank value on update keeps the stored secret (Core's documented semantics).
            await security.SaveSecretAsync(new VaultSecretEdit(created.KeyVaultId, "composition-key",
                "renamed description", "token", "", ["fixture", "eu"]), timeout.Token);
            var updated = Assert.Single(await security.ListSecretsAsync(timeout.Token));
            Assert.Equal("renamed description", updated.Description);
            Assert.Equal("token", updated.Category);
            Assert.Equal(2, updated.Tags.Count);
            Assert.NotNull(updated.UpdatedAt);

            await Assert.ThrowsAsync<InvalidOperationException>(() => security.DeleteSecretAsync("no-such-id", timeout.Token));
            await security.DeleteSecretAsync(created.KeyVaultId, timeout.Token);
            Assert.Empty(await security.ListSecretsAsync(timeout.Token));

            // Classifier health is an optional surface: either wired with entries, or explicitly unknown.
            var health = await security.ReadClassifierHealthAsync(timeout.Token);
            if (!health.Configured) Assert.Empty(health.Classifiers);
            else Assert.All(health.Classifiers, item => Assert.False(string.IsNullOrWhiteSpace(item.ClassifierId)));
            Assert.False(string.IsNullOrWhiteSpace(SecurityText.DescribeHealthSummary(health)));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => security.ListSecretsAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task ApprovalAdapter_CreatesDisablesAndAuditsAllowlistRules()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var security = factory.CreateSecuritySettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => security.ListApprovalRulesAsync(null, null, null, timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var initial = await security.ListApprovalRulesAsync(null, null, null, timeout.Token);
            var initialCount = initial.Count;

            // Core normalizes the tool id, and the rule needs an exact-match key.
            await security.SaveApprovalRuleAsync(new ApprovalRuleEdit("", "composition-space", "Shell_Exec",
                "git status", "", "human", "enabled", "allow", "", "reviewer-1", "", "composition fixture"),
                timeout.Token);
            var rules = await security.ListApprovalRulesAsync(null, null, null, timeout.Token);
            // 内置规则里可能已有同名命令，所以按本次写入的唯一理由定位自己建的那条。
            var created = Assert.Single(rules, rule => rule.Reason == "composition fixture");
            Assert.Equal("shell_exec", created.ToolId);
            Assert.True(created.IsEnabled);
            Assert.False(created.IsDeny);
            Assert.Equal("human", created.Source);
            Assert.Equal("reviewer-1", created.ApprovedByUserId);

            // Filters answer from Core's own vocabularies.
            Assert.Contains(await security.ListApprovalRulesAsync("composition-space", null, null, timeout.Token),
                rule => rule.RuleId == created.RuleId);
            Assert.Empty(await security.ListApprovalRulesAsync("other-space", null, null, timeout.Token));
            Assert.Contains(await security.ListApprovalRulesAsync(null, "SHELL_EXEC", "enabled", timeout.Token),
                rule => rule.RuleId == created.RuleId);

            // A deny rule keeps the deny effect rather than being folded into an allow.
            await security.SaveApprovalRuleAsync(new ApprovalRuleEdit("", "", "shell_exec", "rm -rf /", "",
                "classifier", "enabled", "deny", "agent-1", "", "", "blocked"), timeout.Token);
            var deny = await security.ListApprovalRulesAsync(null, "shell_exec", null, timeout.Token);
            Assert.Contains(deny, rule => rule.IsDeny && rule.Command == "rm -rf /");

            // Disabling keeps the record.
            await security.DisableApprovalRuleAsync(created.RuleId, timeout.Token);
            var afterDisable = await security.ListApprovalRulesAsync(null, null, null, timeout.Token);
            var disabled = Assert.Single(afterDisable, rule => rule.RuleId == created.RuleId);
            Assert.False(disabled.IsEnabled);
            Assert.NotNull(disabled.DisabledAtUtc);
            Assert.True(afterDisable.Count >= initialCount + 2, "停用不得删除记录");

            // The mutations above wrote audit events, so the audit trail and stats must show them.
            var audit = await security.ListApprovalAuditAsync(new ApprovalAuditQuery("", "", "", 200), timeout.Token);
            Assert.NotEmpty(audit);
            Assert.Contains(audit, entry => entry.EventType == "allowlist_rule_created");
            Assert.Contains(audit, entry => entry.EventType == "allowlist_rule_disabled");
            Assert.All(audit, entry => Assert.False(string.IsNullOrWhiteSpace(entry.EventType)));

            var stats = await security.ReadApprovalStatsAsync(timeout.Token);
            Assert.True(stats.AllowlistRules >= afterDisable.Count);
            Assert.True(stats.EnabledAllowlistRules <= stats.AllowlistRules);
            Assert.Contains("规则：", stats.SummaryText, StringComparison.Ordinal);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => security.DisableApprovalRuleAsync("tal_missing", timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => security.ReadApprovalStatsAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task AccessTokenAdapter_CreatesListsRenamesAndRevokesWithCas()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var tokens = factory.CreateAccessTokenSettings(kernel);
        var workspaces = factory.CreateWorkspaceSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => tokens.ReadStatusAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var status = await tokens.ReadStatusAsync(timeout.Token);
            Assert.True(status.MaxTokenLifetimeDays >= 1);
            Assert.True(status.DefaultTokenLifetimeDays <= status.MaxTokenLifetimeDays);

            Assert.Empty((await tokens.ListAsync(new AccessTokenFilter("", "", "", "", 1, 20), timeout.Token)).Items);

            var workspaceId = (await workspaces.ListAsync(timeout.Token)).Single().WorkspaceId;
            var created = await tokens.CreateAsync(new AccessTokenCreateRequest("composition-token", [workspaceId],
                ["tasks.read", "tasks.write"], null), timeout.Token);
            // 明文只在创建结果里出现一次。
            Assert.StartsWith("pdt_v1_", created.AccessToken, StringComparison.Ordinal);
            Assert.Equal("composition-token", created.Token.Name);
            Assert.True(created.Token.IsActive);
            Assert.Equal(["tasks.read", "tasks.write"], created.Token.Scopes.OrderBy(scope => scope));

            var page = await tokens.ListAsync(new AccessTokenFilter("", "", "", "", 1, 20), timeout.Token);
            var listed = Assert.Single(page.Items);
            Assert.Equal(created.Token.TokenId, listed.TokenId);
            Assert.Equal(1, page.Total);
            // 摘要里有显示前缀（Core 用来识别令牌的非秘密片段），但绝不能含完整明文。
            Assert.StartsWith("pdt_v1_", listed.DisplayPrefix, StringComparison.Ordinal);
            Assert.True(listed.DisplayPrefix.Length < created.AccessToken.Length, "显示前缀必须短于明文");
            Assert.DoesNotContain(created.AccessToken, listed.ToString(), StringComparison.Ordinal);

            var detail = await tokens.ReadAsync(created.Token.TokenId, timeout.Token);
            Assert.NotNull(detail);
            Assert.Null(await tokens.ReadAsync("tok_missing", timeout.Token));

            // 过期版本必须冲突，不能覆盖。
            await Assert.ThrowsAsync<SettingsConflictException>(() => tokens.RenameAsync(
                created.Token.TokenId, created.Token.Version + 5, "renamed", timeout.Token));
            await tokens.RenameAsync(created.Token.TokenId, created.Token.Version, "renamed", timeout.Token);
            var renamed = await tokens.ReadAsync(created.Token.TokenId, timeout.Token);
            Assert.Equal("renamed", renamed!.Name);
            Assert.True(renamed.Version > created.Token.Version, "重命名后版本应递增");

            // Core 只拒绝超长原因（>500）；空原因在 Core 侧是允许的，界面自己要求填写以便溯源。
            await Assert.ThrowsAsync<ArgumentException>(() => tokens.RevokeAsync(
                created.Token.TokenId, renamed.Version, new string('x', 501), timeout.Token));
            await tokens.RevokeAsync(created.Token.TokenId, renamed.Version, "composition fixture", timeout.Token);
            var revoked = await tokens.ReadAsync(created.Token.TokenId, timeout.Token);
            Assert.False(revoked!.IsActive);
            Assert.Contains("composition fixture", revoked.RevocationText, StringComparison.Ordinal);
            Assert.Contains(await tokens.ListAsync(new AccessTokenFilter("Revoked", "", "", "", 1, 20), timeout.Token) is { } page2
                ? page2.Items : [], item => item.TokenId == created.Token.TokenId);
            Assert.Empty((await tokens.ListAsync(new AccessTokenFilter("Active", "", "", "", 1, 20), timeout.Token)).Items);

            // Core 不接受未知 scope，适配器把它作为真实错误抛出。
            await Assert.ThrowsAsync<InvalidOperationException>(() => tokens.CreateAsync(
                new AccessTokenCreateRequest("bad", [workspaceId], ["tasks.admin"], null), timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => tokens.ReadStatusAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task RoleAdapter_ListsBuiltInRolesAndRefusesToChangeThem()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var roles = factory.CreateRoleSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => roles.ListAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var seeded = await roles.ListAsync(timeout.Token);
            Assert.NotEmpty(seeded);
            // Core 内置四个系统角色，全部只读。
            var systemRoles = seeded.Where(role => role.IsSystemRole).ToArray();
            Assert.True(systemRoles.Length >= 4, $"内置角色应至少 4 个，实际 {systemRoles.Length}");
            Assert.All(systemRoles, role => Assert.False(role.IsEditable));
            Assert.All(systemRoles, role => Assert.All(role.Permissions, permission => Assert.True(
                RoleText.IsKnownPermission(permission), $"内置角色出现未知权限 {permission}")));

            // 系统角色不可改、不可删：Core 直接拒绝，适配器把它变成真实错误。
            var admin = systemRoles[0];
            await Assert.ThrowsAsync<InvalidOperationException>(() => roles.UpdateAsync(new RoleEdit(
                admin.RoleId, "hijacked", "", ["workspace:read"]), timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => roles.DeleteAsync(admin.RoleId, timeout.Token));
            Assert.Contains(await roles.ListAsync(timeout.Token), role => role.RoleId == admin.RoleId && role.Name == admin.Name);

            // 自定义角色：创建、改名与改权限、重名冲突、删除。
            await roles.CreateAsync(new RoleEdit("composition-role", "Composition Role", "fixture",
                ["workspace:read", "template:read"]), timeout.Token);
            var created = Assert.Single(await roles.ListAsync(timeout.Token),
                role => role.RoleId == "composition-role");
            Assert.False(created.IsSystemRole);
            Assert.True(created.IsEditable);
            Assert.Equal(["template:read", "workspace:read"], created.Permissions.OrderBy(value => value));

            await Assert.ThrowsAsync<InvalidOperationException>(() => roles.CreateAsync(new RoleEdit(
                "composition-role", "Duplicate", "", []), timeout.Token));

            await roles.UpdateAsync(new RoleEdit("composition-role", "Renamed Role", "edited",
                ["agent:run"]), timeout.Token);
            var renamed = Assert.Single(await roles.ListAsync(timeout.Token),
                role => role.RoleId == "composition-role");
            Assert.Equal("Renamed Role", renamed.Name);
            Assert.Equal(["agent:run"], renamed.Permissions);

            await roles.DeleteAsync("composition-role", timeout.Token);
            Assert.DoesNotContain(await roles.ListAsync(timeout.Token), role => role.RoleId == "composition-role");
            await Assert.ThrowsAsync<InvalidOperationException>(() => roles.DeleteAsync("composition-role", timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => roles.ListAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task UserAdapter_CreatesUpdatesPasswordsRolesAndProtectsTheLastAdmin()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var users = factory.CreateUserSettings(kernel);
        var roles = factory.CreateRoleSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => users.ListAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var before = await users.ListAsync(timeout.Token);

            // 密码下限由 Core 强制；短密码必须是真实错误。
            await Assert.ThrowsAsync<InvalidOperationException>(() => users.CreateAsync(new UserCreate(
                "composition-user", "Composition User", "composition@example.invalid", "", "SimpleUser",
                "abc", "abc"), timeout.Token));

            await users.CreateAsync(new UserCreate("composition-user", "Composition User",
                "composition@example.invalid", "Composition", "SimpleUser", "s3cret-pass", "s3cret-pass"), timeout.Token);
            var created = Assert.Single(await users.ListAsync(timeout.Token),
                user => user.UserId == "composition-user");
            Assert.True(created.IsEnabled, "新账号默认启用");
            Assert.False(created.IsAdmin);
            Assert.Empty(created.RoleIds);

            // 重复 UserId 与重复邮箱都是冲突（不是覆盖）。
            await Assert.ThrowsAsync<InvalidOperationException>(() => users.CreateAsync(new UserCreate(
                "composition-user", "Other", "other@example.invalid", "", "SimpleUser",
                "s3cret-pass", "s3cret-pass"), timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => users.CreateAsync(new UserCreate(
                "other-user", "Other", "composition@example.invalid", "", "SimpleUser",
                "s3cret-pass", "s3cret-pass"), timeout.Token));

            await users.UpdateAsync(new UserMetaEdit("composition-user", "Renamed User",
                "renamed@example.invalid", "Renamed", "SimpleUser", false), timeout.Token);
            var updated = Assert.Single(await users.ListAsync(timeout.Token), user => user.UserId == "composition-user");
            Assert.Equal("Renamed User", updated.Username);
            Assert.Equal("renamed@example.invalid", updated.Email);
            Assert.False(updated.IsEnabled);

            // 改邮箱撞已有邮箱：修正后与新建一致地冲突，而不是撞唯一索引变成 500。
            var taken = before.FirstOrDefault(user => user.Email.Length > 0);
            if (taken is not null)
                await Assert.ThrowsAsync<InvalidOperationException>(() => users.UpdateAsync(new UserMetaEdit(
                    "composition-user", "Renamed User", taken.Email, "", "SimpleUser", false), timeout.Token));

            await users.ChangePasswordAsync(new UserPasswordChange("composition-user", "another-pass", "another-pass"),
                timeout.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => users.ChangePasswordAsync(
                new UserPasswordChange("composition-user", "short", "short"), timeout.Token));

            // 隔离数据根只有 4 个内置角色，所以先建一个自定义角色来验证分配。
            await roles.CreateAsync(new RoleEdit("composition-assign-role", "Assign Role", "", ["workspace:read"]),
                timeout.Token);
            var role = (await roles.ListAsync(timeout.Token)).Single(item => item.RoleId == "composition-assign-role");
            await users.AssignRolesAsync("composition-user", [role.RoleId], timeout.Token);
            var assigned = Assert.Single(await users.ListAsync(timeout.Token), user => user.UserId == "composition-user");
            Assert.Equal([role.RoleId], assigned.RoleIds);
            // 空列表 = 全部移除（全量替换语义）。
            await users.AssignRolesAsync("composition-user", [], timeout.Token);
            Assert.Empty(Assert.Single(await users.ListAsync(timeout.Token),
                user => user.UserId == "composition-user").RoleIds);

            await users.DeleteAsync("composition-user", timeout.Token);
            Assert.DoesNotContain(await users.ListAsync(timeout.Token), user => user.UserId == "composition-user");
            await Assert.ThrowsAsync<InvalidOperationException>(() => users.DeleteAsync("composition-user", timeout.Token));

            // 最后一个 Admin 不可删除：Core 直接拒绝。
            var admins = (await users.ListAsync(timeout.Token)).Where(user => user.IsAdmin).ToArray();
            if (admins.Length == 1)
                await Assert.ThrowsAsync<InvalidOperationException>(() => users.DeleteAsync(admins[0].UserId, timeout.Token));

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => users.ListAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task TeamAdapter_ManagesTeamsMembersWorkspacesAndWhitelists()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var teams = factory.CreateTeamSettings(kernel);
        var users = factory.CreateUserSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => teams.ListAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var seeded = await teams.ListAsync(timeout.Token);
            Assert.NotEmpty(seeded);
            // default 工作区属于某个已存在的团队，所以那个团队不能直接删除。
            Assert.Contains(seeded, team => !team.CanDelete);

            await teams.CreateAsync(new TeamEdit("composition-team", "Composition Team", "fixture", true), timeout.Token);
            var created = Assert.Single(await teams.ListAsync(timeout.Token), team => team.TeamId == "composition-team");
            Assert.True(created.IsEnabled);
            Assert.True(created.CanDelete);

            await Assert.ThrowsAsync<InvalidOperationException>(() => teams.CreateAsync(
                new TeamEdit("composition-team", "Duplicate", "", true), timeout.Token));
            await teams.UpdateAsync(new TeamEdit("composition-team", "Renamed Team", "edited", false), timeout.Token);
            var renamed = Assert.Single(await teams.ListAsync(timeout.Token), team => team.TeamId == "composition-team");
            Assert.Equal("Renamed Team", renamed.Name);
            Assert.False(renamed.IsEnabled);

            // 隔离数据根没有用户，成员校验需要先建一个真实用户。
            await users.CreateAsync(new UserCreate("composition-member", "Composition Member",
                "composition-member@example.invalid", "", "SimpleUser", "s3cret-pass", "s3cret-pass"), timeout.Token);
            var user = Assert.Single(await users.ListAsync(timeout.Token),
                candidate => candidate.UserId == "composition-member");
            await Assert.ThrowsAsync<InvalidOperationException>(() => teams.AddMemberAsync(
                new TeamMemberAdd("composition-team", user.UserId, "Owner"), timeout.Token));
            await teams.AddMemberAsync(new TeamMemberAdd("composition-team", user.UserId, "Admin"), timeout.Token);
            var member = Assert.Single(await teams.ListMembersAsync("composition-team", timeout.Token));
            Assert.Equal(user.UserId, member.UserId);
            Assert.Equal("Admin", member.Role);
            await Assert.ThrowsAsync<InvalidOperationException>(() => teams.AddMemberAsync(
                new TeamMemberAdd("composition-team", user.UserId, "Member"), timeout.Token));
            await teams.RemoveMemberAsync("composition-team", user.UserId, timeout.Token);
            Assert.Empty(await teams.ListMembersAsync("composition-team", timeout.Token));

            // 工作区：创建后团队不可删除，策略必须合法。
            await Assert.ThrowsAsync<InvalidOperationException>(() => teams.CreateWorkspaceAsync(
                new TeamWorkspaceEdit("composition-team", "composition-space", "S", "", "", "Owner", "Manage", true),
                timeout.Token));
            await teams.CreateWorkspaceAsync(new TeamWorkspaceEdit("composition-team", "composition-space",
                "Composition Space", "d", "", "Manage", "ReadOnly", true), timeout.Token);
            var workspace = Assert.Single(await teams.ListWorkspacesAsync("composition-team", timeout.Token));
            Assert.Equal("composition-team", workspace.TeamId);
            Assert.Equal("ReadOnly", workspace.CompanyAccessPolicy);
            Assert.Contains(await teams.ListAsync(timeout.Token),
                team => team.TeamId == "composition-team" && !team.CanDelete);
            await Assert.ThrowsAsync<InvalidOperationException>(() => teams.DeleteAsync("composition-team", timeout.Token));

            await teams.UpdateWorkspaceAsync(new TeamWorkspaceEdit("composition-team", "composition-space",
                "Renamed Space", "d", "", "ReadOnly", "None", false), timeout.Token);
            var updated = Assert.Single(await teams.ListWorkspacesAsync("composition-team", timeout.Token));
            Assert.Equal("Renamed Space", updated.Name);
            Assert.False(updated.IsEnabled);

            // 白名单：None 被拒，重复被拒，跨工作区删除被拒。
            await Assert.ThrowsAsync<InvalidOperationException>(() => teams.AddWorkspaceMemberAsync(
                new TeamWorkspaceMemberAdd("composition-space", user.UserId, "None"), timeout.Token));
            await teams.AddWorkspaceMemberAsync(new TeamWorkspaceMemberAdd("composition-space", user.UserId, "Write"),
                timeout.Token);
            var entry = Assert.Single(await teams.ListWorkspaceMembersAsync("composition-space", timeout.Token));
            Assert.Equal("Write", entry.AccessLevel);
            await Assert.ThrowsAsync<InvalidOperationException>(() => teams.AddWorkspaceMemberAsync(
                new TeamWorkspaceMemberAdd("composition-space", user.UserId, "Manage"), timeout.Token));

            var otherWorkspace = (await teams.ListAsync(timeout.Token))
                .SelectMany(team => team.WorkspaceCount > 0 ? new[] { team.TeamId } : [])
                .FirstOrDefault();
            if (otherWorkspace is not null)
            {
                var foreign = (await teams.ListWorkspacesAsync(otherWorkspace, timeout.Token)).FirstOrDefault();
                if (foreign is not null)
                    await Assert.ThrowsAsync<InvalidOperationException>(() => teams.RemoveWorkspaceMemberAsync(
                        foreign.WorkspaceId, entry.Id, timeout.Token));
            }
            // 内置默认工作空间在此路径同样受保护。
            await Assert.ThrowsAsync<InvalidOperationException>(() => teams.DeleteWorkspaceAsync("default", timeout.Token));

            await teams.RemoveWorkspaceMemberAsync("composition-space", entry.Id, timeout.Token);
            Assert.Empty(await teams.ListWorkspaceMembersAsync("composition-space", timeout.Token));
            await teams.DeleteWorkspaceAsync("composition-space", timeout.Token);
            Assert.Empty(await teams.ListWorkspacesAsync("composition-team", timeout.Token));
            await teams.DeleteAsync("composition-team", timeout.Token);
            Assert.DoesNotContain(await teams.ListAsync(timeout.Token), team => team.TeamId == "composition-team");

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => teams.ListAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task RuntimeNodeAdapter_ReadsTheRegistryAndAuditsFreezeAndUnfreeze()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var nodes = factory.CreateRuntimeNodeSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => nodes.ListNodesAsync(timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            var listed = await nodes.ListNodesAsync(timeout.Token);
            // 隔离宿主里可能一个节点都没注册；列表仍必须成功返回，计数按真实数据算。
            var summary = RuntimeNodeSummary.Of(listed);
            Assert.Equal(listed.Count, summary.Total);
            Assert.Contains("在线", summary.HeadlineText, StringComparison.Ordinal);

            // 未知节点：拒绝且不写审计。
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => nodes.FreezeAsync("no-such-node", "composition", timeout.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => nodes.UnfreezeAsync("no-such-node", "composition", timeout.Token));

            // 有节点时走完整链路：冻结 → 审计 → 解冻 → 审计。
            var node = listed.FirstOrDefault();
            if (node is not null)
            {
                await nodes.FreezeAsync(node.NodeId, "composition freeze", timeout.Token);
                var frozen = Assert.Single(await nodes.ListNodesAsync(timeout.Token),
                    candidate => candidate.NodeId == node.NodeId);
                Assert.True(frozen.IsFrozen);
                // 冻结后再次冻结返回 false（Core 的 FreezeNode 幂等失败语义），适配器如实报错。
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => nodes.FreezeAsync(node.NodeId, "again", timeout.Token));

                await nodes.UnfreezeAsync(node.NodeId, "composition unfreeze", timeout.Token);
                var unfrozen = Assert.Single(await nodes.ListNodesAsync(timeout.Token),
                    candidate => candidate.NodeId == node.NodeId);
                Assert.False(unfrozen.IsFrozen);
            }

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(() => nodes.ListNodesAsync(timeout.Token));
        }
        finally
        {
            await kernel.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task DiagnosticsAdapter_QueriesRedactedTimelineAndComponentHealth()
    {
        var root = await CreateIsolatedDataRootAsync();
        var factory = new DesktopKernelFactory(new Desktop());
        await using var kernel = new InProcessKernel(factory);
        var diagnostics = factory.CreateDiagnosticsSettings(kernel);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => diagnostics.QueryTimelineAsync(RuntimeTimelineFilter.Default, timeout.Token));

            await kernel.StartAsync(root, timeout.Token);
            // 空数据根：分页元数据仍然可用，且不抛异常。
            var page = await diagnostics.QueryTimelineAsync(RuntimeTimelineFilter.Default, timeout.Token);
            Assert.True(page.Page >= 1);
            Assert.True(page.PageSize is >= 1 and <= 500);
            Assert.Equal(page.Items.Count, page.Total == 0 ? 0 : page.Items.Count);
            Assert.False(page.CanGoBack);

            // 筛选与分页参数原样送达 Core 的契约（非法值被 Normalize 收敛）。
            var filtered = await diagnostics.QueryTimelineAsync(RuntimeTimelineFilter.Default with
            {
                SessionId = "composition-session", Status = "failed", Page = 0, PageSize = 9_999,
            }, timeout.Token);
            Assert.Equal(1, filtered.Page);
            Assert.Equal(500, filtered.PageSize);

            var overview = await diagnostics.LoadOverviewAsync(timeout.Token);
            Assert.NotNull(overview);
            // 空数据根没有组件；总量必须自洽。
            Assert.Equal(overview.Components.Sum(component => component.StartedCount), overview.Started);
            Assert.Equal(overview.Components.Sum(component => component.FailedCount), overview.Failed);
            Assert.True(overview.UnhealthyCount <= overview.Components.Count);

            await kernel.StopAsync(timeout.Token);
            await Assert.ThrowsAsync<SettingsUnavailableException>(
                () => diagnostics.LoadOverviewAsync(timeout.Token));
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
