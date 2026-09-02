using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Security;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Services.Security;
using PuddingPlatformTests.Security;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class AdminAccessTokenWireContractTests
{
    [TestMethod]
    public void AccessTokenStatus_SerializesAsStableEnumName()
    {
        var dto = new AdminAccessTokenController.AccessTokenDetailResponse
        {
            TokenId = "pat-test",
            Status = "Active",
        };

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StringAssert.Contains(json, "\"status\":\"Active\"");
        Assert.IsFalse(json.Contains("\"status\":0", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task List_ProjectsPersistenceEnumToStableStatusName()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync("admin");
        var service = harness.CreateService();
        var created = await service.CreateAsync(new ExternalAccessTokenCreateCommand
        {
            Name = "wire-contract",
            WorkspaceIds = ["default"],
            Scopes = [ExternalTaskApiScopes.WorkspacesRead],
            LifetimeDays = 30,
            OwnerUserId = "admin",
        });
        Assert.IsTrue(created.IsOk);

        var configRoot = Path.Combine(
            Path.GetTempPath(),
            $"pudding-token-wire-{Guid.NewGuid():N}");
        var controller = new AdminAccessTokenController(
            service,
            new ExternalTaskApiOptionsProvider(
                PuddingDataPaths.FromRoot(configRoot),
                NullLogger<ExternalTaskApiOptionsProvider>.Instance));

        var result = Assert.IsInstanceOfType<OkObjectResult>(
            await controller.List(
                status: null,
                ownerUserId: null,
                workspaceId: null,
                scope: null,
                page: 1,
                pageSize: 20,
                ct: CancellationToken.None));
        var json = JsonSerializer.Serialize(
            result.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        StringAssert.Contains(json, "\"status\":\"Active\"");
        Assert.IsFalse(json.Contains("\"status\":0", StringComparison.Ordinal));
    }
}
