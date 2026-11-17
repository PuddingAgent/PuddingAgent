using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class VoiceCredentialReferenceTests
{
    [TestMethod]
    public async Task ExecutionResolvesVaultAndDoesNotOverwriteRawConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "voice-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var file = paths.SystemConfigFile("voice/providers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var json = JsonSerializer.Serialize(new PuddingVoiceProvidersConfig { Providers = [new() { ProviderId = "dashscope", ApiKey = "{{vault:voice.dashscope}}" }] }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(file, json);
            var vault = new Mock<IKeyVaultService>(MockBehavior.Strict);
            vault.Setup(v => v.InjectAsync("{{vault:voice.dashscope}}", It.IsAny<CancellationToken>())).ReturnsAsync("synthetic-test-key");
            var service = new VoiceProviderFileService(paths, NullLogger<VoiceProviderFileService>.Instance, vault.Object);
            Assert.AreEqual("synthetic-test-key", (await service.LoadForUseAsync()).Providers.Single().ApiKey);
            Assert.AreEqual("{{vault:voice.dashscope}}", (await service.LoadAsync()).Providers.Single().ApiKey);
            Assert.AreEqual(json, await File.ReadAllTextAsync(file));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UnresolvedVaultCannotReachVoiceProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "voice-vault-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var file = paths.SystemConfigFile("voice/providers.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{\"providers\":[{\"providerId\":\"dashscope\",\"apiKey\":\"{{vault:missing}}\"}]}");
            var service = new VoiceProviderFileService(paths, NullLogger<VoiceProviderFileService>.Instance);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.LoadForUseAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
