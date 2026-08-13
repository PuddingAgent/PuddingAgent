using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class VisionArtifactApiControllerTests
{
    [TestMethod]
    public async Task Upload_UnsupportedMediaType_Returns415InsteadOf500()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        Assert.IsTrue(await db.Workspaces.AnyAsync(w => w.WorkspaceId == "default"));

        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-vision-controller-{Guid.NewGuid():N}");
        var storage = new VisionArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<VisionArtifactStorageService>.Instance);
        var controller = new VisionArtifactApiController(db, storage, NullLogger<VisionArtifactApiController>.Instance);
        await using var bytes = new MemoryStream([0x42, 0x4D, 0x00, 0x00]);
        var file = new FormFile(bytes, 0, bytes.Length, "file", "sample.bmp")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/bmp",
        };

        var response = await controller.Upload(
            "default",
            file,
            width: 1,
            height: 1,
            capturedAt: 1,
            CancellationToken.None);

        var result = Assert.IsInstanceOfType<ObjectResult>(response.Result);
        Assert.AreEqual(StatusCodes.Status415UnsupportedMediaType, result.StatusCode);
    }
}
