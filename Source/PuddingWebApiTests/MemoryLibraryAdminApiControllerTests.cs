using Microsoft.AspNetCore.Mvc;
using Moq;
using PuddingCode.Abstractions;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Services;

namespace PuddingWebApiTests;

/// <summary>
/// 记忆图书馆管理 API 合约测试——验证输入校验和错误响应。
/// 全链路集成测试在 Task 8 中通过 CustomWebApplicationFactory 覆盖。
/// </summary>
[TestClass]
public sealed class MemoryLibraryAdminApiControllerTests
{
    private readonly Mock<IMemoryLibrary> _libraryMock = new();
    private readonly Mock<IMemoryLibraryAdminService> _adminMock = new();

    private MemoryLibraryAdminController CreateController()
        => new(_libraryMock.Object, _adminMock.Object);

    // ── Input validation ────────────────────────────────────────────

    [TestMethod]
    public async Task GetOverview_EmptyWorkspaceId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetOverview("  ", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetLibraries_EmptyWorkspaceId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetLibraries("", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetTree_EmptyWorkspaceId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetTree("", "lib-1", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetBook_EmptyWorkspaceId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetBook("", "book-1", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Search_EmptyWorkspaceId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.Search("", "test", 20, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Search_EmptyQuery_Returns400()
    {
        var controller = CreateController();
        var result = await controller.Search("ws-1", "", 20, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Search_TopKLessThan1_Returns400()
    {
        var controller = CreateController();
        var result = await controller.Search("ws-1", "test", 0, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Search_TopKGreaterThan100_Returns400()
    {
        var controller = CreateController();
        var result = await controller.Search("ws-1", "test", 101, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetAgentLibraries_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetAgentLibraries("ws-1", " ", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task AgentSearch_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.AgentSearch("ws-1", " ", "test", 20, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task AgentSearch_EmptyQuery_Returns400()
    {
        var controller = CreateController();
        var result = await controller.AgentSearch("ws-1", "agent-1", "", 20, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetSources_MissingAgentScope_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetSources("book", "book-1", null, null, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }
}
