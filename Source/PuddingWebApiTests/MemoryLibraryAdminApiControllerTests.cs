using Microsoft.AspNetCore.Mvc;
using Moq;
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
    private readonly Mock<IMemoryLibraryAdminService> _adminMock = new();

    private MemoryLibraryAdminController CreateController()
        => new(_adminMock.Object);

    // ── Input validation ────────────────────────────────────────────

    [TestMethod]
    public async Task GetAgentOverview_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetAgentOverview("ws-1", " ", CancellationToken.None);
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
    public async Task EnsureAgentDefaultLibrary_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.EnsureAgentDefaultLibrary("ws-1", " ", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetAgentTree_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetAgentTree("ws-1", " ", "lib-1", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetAgentBook_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetAgentBook("ws-1", " ", "book-1", CancellationToken.None);
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
    public async Task AgentSearch_TopKLessThan1_Returns400()
    {
        var controller = CreateController();
        var result = await controller.AgentSearch("ws-1", "agent-1", "test", 0, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task AgentSearch_TopKGreaterThan100_Returns400()
    {
        var controller = CreateController();
        var result = await controller.AgentSearch("ws-1", "agent-1", "test", 101, CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task CreateAgentTreeNode_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.CreateAgentTreeNode(
            "ws-1",
            " ",
            new CreateMemoryTreeNodeRequest("ws-1", "lib-1", null, "节点", null, "page"),
            CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task CreateAgentBook_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.CreateAgentBook(
            "ws-1",
            " ",
            new CreateMemoryBookRequest("ws-1", "lib-1", null, "Book", null),
            CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task UpdateAgentBook_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.UpdateAgentBook(
            "ws-1",
            " ",
            "book-1",
            new UpdateMemoryBookRequest("Book", null),
            CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task CreateAgentChapter_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.CreateAgentChapter(
            "ws-1",
            " ",
            new CreateMemoryChapterRequest("book-1", "章节", "内容", 0.5),
            CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task UpdateAgentChapter_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.UpdateAgentChapter(
            "ws-1",
            " ",
            "chapter-1",
            new UpdateMemoryChapterRequest("章节", "内容", 0.5),
            CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task ArchiveAgentBook_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.ArchiveAgentBook("ws-1", " ", "book-1", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    [TestMethod]
    public async Task ArchiveAgentChapter_EmptyAgentId_Returns400()
    {
        var controller = CreateController();
        var result = await controller.ArchiveAgentChapter("ws-1", " ", "chapter-1", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }

    [TestMethod]
    public async Task GetAgentSources_EmptyOwnerType_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetAgentSources("ws-1", "agent-1", "", "book-1", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task GetAgentPointers_EmptySourceType_Returns400()
    {
        var controller = CreateController();
        var result = await controller.GetAgentPointers("ws-1", "agent-1", "", "book-1", CancellationToken.None);
        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }
}
