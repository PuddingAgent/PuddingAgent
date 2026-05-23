using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class WorkspaceAgentFileServiceTests
{
    [TestMethod]
    public async Task CreateAgentAsync_ShouldBeVisibleInListAgentsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-workspace-agent-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new WorkspaceAgentFileService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<WorkspaceAgentFileService>.Instance);

            var created = await service.CreateAgentAsync(
                "default",
                new CreateWorkspaceAgentRequest(
                    Name: "test01",
                    Description: null,
                    DisplayName: null,
                    AvatarId: null,
                    AvatarUrl: null,
                    SourceTemplateId: null,
                    SystemPromptOverride: null,
                    PreferredProviderId: "mimo",
                    PreferredModelId: "mimo-v2.5-pro"));

            var listed = await service.ListAgentsAsync("default");

            Assert.HasCount(1, listed);
            Assert.AreEqual(created.AgentId, listed[0].AgentId);
            Assert.AreEqual("test01", listed[0].Name);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
