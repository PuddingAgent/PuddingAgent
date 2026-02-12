using Microsoft.AspNetCore.Mvc;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class CapabilityApiControllerTests
{
    [TestMethod]
    public async Task List_ShouldExposeRegisteredRuntimeToolsAsCapabilities()
    {
        var controller = new CapabilityApiController(
            new TestToolCatalog(),
            new TestToolPermissionPolicy());

        var response = await controller.List(enabledOnly: true, CancellationToken.None);
        var ok = response.Result as OkObjectResult;

        Assert.IsNotNull(ok);
        var capabilities = (List<CapabilityDto>)ok!.Value!;
        var patch = capabilities.SingleOrDefault(c => c.CapabilityId == "cap-file-patch");

        Assert.IsNotNull(patch);
        Assert.AreEqual("file_patch", patch!.ToolName);
        Assert.IsTrue(patch.RequiresFileWrite);
        Assert.IsTrue(patch.IsEnabled);
    }

    private sealed class TestToolCatalog : IPuddingToolCatalogService
    {
        public IReadOnlyList<ToolDescriptor> ListTools(bool enabledByDefaultOnly = false) =>
        [
            new ToolDescriptor
            {
                ToolId = "file_read",
                Name = "Read file",
                Description = "Read a file",
                PermissionLevel = ToolPermissionLevel.Low,
                Safety = ToolSafetyFlags.ReadOnly,
                SortOrder = 10,
            },
            new ToolDescriptor
            {
                ToolId = "file_patch",
                Name = "Patch file",
                Description = "Apply text patches to a file",
                PermissionLevel = ToolPermissionLevel.High,
                Safety = ToolSafetyFlags.RequiresFileWrite | ToolSafetyFlags.Destructive,
                Parameters = new ToolParameterSchema(
                    [new ToolParameter("path", "string", "File path")],
                    ["path"]),
                SortOrder = 20,
            },
        ];
    }

    private sealed class TestToolPermissionPolicy : IToolPermissionPolicyService
    {
        public ToolPermissionDecision Classify(ToolDescriptor descriptor) => new()
        {
            ToolId = descriptor.ToolId,
            Tier = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite)
                ? ToolPermissionTier.RuntimeGranted
                : ToolPermissionTier.AutoAllowed,
            IsExposedToAgent = true,
            RequiresRuntimeAuthorization = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite),
            RequiresShellExecution = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell),
            RequiresFileWrite = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite)
                                || descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive),
            RequiresNetworkAccess = descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork),
        };

        public bool RequiresRuntimeAuthorization(ToolDescriptor descriptor)
            => Classify(descriptor).RequiresRuntimeAuthorization;

        public bool CanExposeToAgent(ToolDescriptor descriptor, CapabilityPolicy? policy) => true;

        public CapabilityPolicy BuildCapabilityPolicy(
            IEnumerable<ToolDescriptor> descriptors,
            IEnumerable<string> selectedToolNames,
            bool isTaskRole) => new();
    }
}
