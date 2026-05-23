using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentTemplateFileServiceTests
{
    [TestMethod]
    public async Task TemplateRoundTrip_ShouldPreservePreferredAndMemoryLlmSelections()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-agent-template-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AgentTemplateFileService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<AgentTemplateFileService>.Instance);

            var request = CreateRequest(
                preferredProviderId: "mimo",
                preferredModelId: "mimo-v2.5-pro",
                memoryLlmProviderId: "mimo",
                memoryLlmModelId: "mimo-v2.5");

            await service.CreateTemplateAsync(request);

            var saved = await service.GetTemplateAsync("assistant");

            Assert.IsNotNull(saved);
            Assert.AreEqual("mimo", saved.PreferredProviderId);
            Assert.AreEqual("mimo-v2.5-pro", saved.PreferredModelId);
            Assert.AreEqual("mimo", saved.MemoryLlmProviderId);
            Assert.AreEqual("mimo-v2.5", saved.MemoryLlmModelId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static UpsertGlobalAgentTemplateRequest CreateRequest(
        string? preferredProviderId,
        string? preferredModelId,
        string? memoryLlmProviderId,
        string? memoryLlmModelId) =>
        new(
            TemplateId: "assistant",
            Name: "Assistant",
            Description: null,
            Role: "Service",
            SystemPrompt: null,
            UserPromptTemplate: null,
            PreferredProviderId: preferredProviderId,
            PreferredModelId: preferredModelId,
            MaxContextTokens: 131072,
            MaxReplyTokens: 4096,
            ContainerImage: null,
            SelectedCapabilityIds: [],
            SelectedSkillPackageIds: [],
            IsEnabled: true,
            SortOrder: 10,
            MemoryLlmProviderId: memoryLlmProviderId,
            MemoryLlmModelId: memoryLlmModelId,
            MemorySearchMode: "deep");
}
