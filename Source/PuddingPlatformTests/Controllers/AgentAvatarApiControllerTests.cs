using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class AgentAvatarApiControllerTests
{
    private static readonly string[] ExpectedAvatarIds =
    [
        "neutral",
        "smile",
        "sleepy",
        "thinking",
        "angry",
        "silver",
        "amber",
        "mint",
    ];

    [TestMethod]
    public void List_ShouldReturnAllAvatars_FromJsonCatalog()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = (IAgentAvatarCatalog)fixture.Catalog;
        var controller = new AgentAvatarApiController(catalog);

        var result = controller.List(CancellationToken.None);

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var avatars = Assert.IsInstanceOfType<List<AgentAvatarDto>>(ok.Value);
        Assert.IsTrue(avatars.Count >= 8, $"Expected at least 8 avatars, got {avatars.Count}");

        foreach (var expectedId in ExpectedAvatarIds)
        {
            Assert.IsTrue(
                avatars.Any(a => a.AvatarId == expectedId),
                $"Missing avatarId: {expectedId}");
        }
    }

    [TestMethod]
    public void GetDefault_ShouldReturnNeutral()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = fixture.Catalog;

        var def = catalog.GetDefault();

        Assert.IsNotNull(def);
        Assert.AreEqual("neutral", def.AvatarId);
    }

    [TestMethod]
    public void Find_ShouldReturnNull_ForUnknownAvatarId()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = fixture.Catalog;

        var result = catalog.Find("non-existent");

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Find_ShouldReturnAvatar_ForKnownId()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = fixture.Catalog;

        var result = catalog.Find("smile");

        Assert.IsNotNull(result);
        Assert.AreEqual("Support Agent", result.Name);
        Assert.AreEqual("/assets/agent-avatars/agent-avatar-smile.png", result.UrlPath);
    }

    [TestMethod]
    public void ResolveUrl_ShouldReturnUrl_ForKnownId()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = fixture.Catalog;

        var url = catalog.ResolveUrl("thinking");

        Assert.AreEqual("/assets/agent-avatars/agent-avatar-thinking.png", url);
    }

    [TestMethod]
    public void ResolveUrl_ShouldReturnNull_ForUnknownId()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = fixture.Catalog;

        var url = catalog.ResolveUrl("bogus");

        Assert.IsNull(url);
    }

    [TestMethod]
    public void ControllerGet_ShouldReturnAvatar_ForKnownId()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = (IAgentAvatarCatalog)fixture.Catalog;
        var controller = new AgentAvatarApiController(catalog);

        var result = controller.Get("amber");

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var avatar = Assert.IsInstanceOfType<AgentAvatarDto>(ok.Value);
        Assert.AreEqual("amber", avatar.AvatarId);
        Assert.AreEqual("/assets/agent-avatars/agent-avatar-amber.png", avatar.Url);
        Assert.IsFalse(avatar.IsDefault);
    }

    [TestMethod]
    public void ControllerGet_ShouldReturnNotFound_ForUnknownId()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = (IAgentAvatarCatalog)fixture.Catalog;
        var controller = new AgentAvatarApiController(catalog);

        var result = controller.Get("bogus");

        Assert.IsInstanceOfType<NotFoundResult>(result.Result);
    }

    [TestMethod]
    public void List_ShouldMarkDefaultAvatar()
    {
        using var fixture = new AvatarCatalogTestFixture();
        var catalog = (IAgentAvatarCatalog)fixture.Catalog;
        var controller = new AgentAvatarApiController(catalog);

        var result = controller.List(CancellationToken.None);

        var ok = Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var avatars = Assert.IsInstanceOfType<List<AgentAvatarDto>>(ok.Value);
        Assert.HasCount(1, avatars.Where(a => a.IsDefault).ToList());
        Assert.AreEqual("neutral", avatars.First(a => a.IsDefault).AvatarId);
    }
}
