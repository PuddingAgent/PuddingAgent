using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-07 legacy skill package slice: the application operation extracted out of SkillPackageApiController.
/// The object store is a port, so validation, object-key construction and old-object cleanup are tested
/// without a live MinIO server.
/// </summary>
[TestClass]
public sealed class SkillPackageServiceTests
{
    [TestMethod]
    public async Task UploadValidatesBeforeWritingAnything()
    {
        await using var harness = await Harness.CreateAsync();
        var stream = new MemoryStream("package"u8.ToArray());

        var badId = await harness.Service.UploadAsync(
            new SkillPackageUpload("Bad Id", "N", null, null, null), harness.File(stream));
        Assert.AreEqual(SkillHubStatus.BadRequest, badId.Status);
        var badName = await harness.Service.UploadAsync(
            new SkillPackageUpload("pkg", " ", null, null, null), harness.File(stream));
        Assert.AreEqual(SkillHubStatus.BadRequest, badName.Status);
        var badExtension = await harness.Service.UploadAsync(
            new SkillPackageUpload("pkg", "N", null, null, null), harness.File(stream, "malware.exe"));
        Assert.AreEqual(SkillHubStatus.BadRequest, badExtension.Status);
        Assert.AreEqual(0, harness.Store.Uploads.Count, "被拒绝的上传不得触碰对象存储");

        var ok = await harness.Service.UploadAsync(
            new SkillPackageUpload("my-package", "My Package", "notes", "2.0.0", 7), harness.File(stream));
        Assert.AreEqual(SkillHubStatus.Ok, ok.Status);
        Assert.AreEqual("my-package", ok.Value!.SkillPackageId);
        Assert.AreEqual("2.0.0", ok.Value.Version);
        Assert.AreEqual(7, ok.Value.SortOrder);
        Assert.AreEqual(1, harness.Store.Uploads.Count);
        Assert.AreEqual("skill-packages/my-package/2.0.0/package.zip", harness.Store.Uploads[0]);

        // A duplicate id is a conflict, never an overwrite.
        var duplicate = await harness.Service.UploadAsync(
            new SkillPackageUpload("my-package", "Again", null, null, null), harness.File(stream));
        Assert.AreEqual(SkillHubStatus.Conflict, duplicate.Status);
        Assert.AreEqual(1, harness.Store.Uploads.Count);

        // Space and traversal segments are removed from the object key.
        await harness.Service.UploadAsync(new SkillPackageUpload("second", "Second", null, null, null),
            harness.File(new MemoryStream([1]), "../evil name.zip"));
        Assert.AreEqual("skill-packages/second/1.0.0/evil_name.zip", harness.Store.Uploads[1]);
    }

    [TestMethod]
    public async Task UpdateMetaTouchesMetadataOnly()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.UploadAsync(new SkillPackageUpload("pkg", "Original", "old", "1.0.0", 100),
            harness.File(new MemoryStream([1, 2, 3])));

        var result = await harness.Service.UpdateMetaAsync("pkg", new UpdateSkillPackageRequest("Renamed", "new", false, 5));
        Assert.AreEqual(SkillHubStatus.Ok, result.Status);
        Assert.AreEqual("Renamed", result.Value!.Name);
        Assert.IsFalse(result.Value.IsEnabled);
        Assert.AreEqual(5, result.Value.SortOrder);
        Assert.AreEqual("1.0.0", result.Value.Version, "元数据更新不得改变版本");
        Assert.AreEqual("package.zip", result.Value.FileName);
        Assert.AreEqual(0, harness.Store.Deletes.Count, "元数据更新不得删除对象");

        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.UpdateMetaAsync(
            "missing", new UpdateSkillPackageRequest("N", null, true, 1))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.UpdateMetaAsync(
            "pkg", new UpdateSkillPackageRequest("  ", null, true, 1))).Status);
    }

    [TestMethod]
    public async Task UpdateFileReplacesTheObjectAndKeepsThePackage()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.UploadAsync(new SkillPackageUpload("pkg", "P", null, "1.0.0", 100),
            harness.File(new MemoryStream([1, 2, 3])));

        // A rejected replacement must leave the existing file and object untouched.
        var rejected = await harness.Service.UpdateFileAsync("pkg", "2.0.0", harness.File(new MemoryStream([9]), "bad.rar"));
        Assert.AreEqual(SkillHubStatus.BadRequest, rejected.Status);
        Assert.AreEqual(1, harness.Store.Uploads.Count);
        Assert.AreEqual(0, harness.Store.Deletes.Count);

        var result = await harness.Service.UpdateFileAsync("pkg", "2.0.0", harness.File(new MemoryStream([4, 5, 6, 7]), "v2.tar.gz"));
        Assert.AreEqual(SkillHubStatus.Ok, result.Status);
        Assert.AreEqual("2.0.0", result.Value!.Version);
        Assert.AreEqual("v2.tar.gz", result.Value.FileName);
        Assert.AreEqual(4, result.Value.FileSizeBytes);
        Assert.AreEqual("skill-packages/pkg/2.0.0/v2.tar.gz", harness.Store.Uploads[1]);
        CollectionAssert.AreEqual(new[] { "skill-packages/pkg/1.0.0/package.zip" }, harness.Store.Deletes.ToArray());

        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.UpdateFileAsync(
            "missing", "1.0.0", harness.File(new MemoryStream([1])))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.UpdateFileAsync(
            "pkg", "  ", harness.File(new MemoryStream([1])))).Status);
    }

    [TestMethod]
    public async Task DeleteRemovesRowAndObjectAndDownloadUrlComesFromTheStore()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.UploadAsync(new SkillPackageUpload("pkg", "P", null, null, null),
            harness.File(new MemoryStream([1])));

        var url = await harness.Service.GetDownloadUrlAsync("pkg");
        Assert.AreEqual(SkillHubStatus.Ok, url.Status);
        Assert.AreEqual("https://store.invalid/skill-packages/pkg/1.0.0/package.zip", url.Value);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.GetDownloadUrlAsync("missing")).Status);

        var deleted = await harness.Service.DeleteAsync("pkg");
        Assert.AreEqual(SkillHubStatus.Ok, deleted.Status);
        Assert.AreEqual(0, (await harness.Service.ListAsync()).Count);
        Assert.IsNull(await harness.Service.GetAsync("pkg"));
        CollectionAssert.AreEqual(new[] { "skill-packages/pkg/1.0.0/package.zip" }, harness.Store.Deletes.ToArray());
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.DeleteAsync("pkg")).Status);
    }

    [TestMethod]
    public void FileRulesMatchTheWebContractExactly()
    {
        Assert.IsTrue(SkillPackageService.IsAllowedFile("a.zip"));
        Assert.IsTrue(SkillPackageService.IsAllowedFile("A.TAR.GZ"));
        Assert.IsFalse(SkillPackageService.IsAllowedFile("a.tgz"), "Web 只接受 .zip 与 .tar.gz，桌面端不擅自放宽");
        Assert.IsFalse(SkillPackageService.IsAllowedFile("a.rar"));
        Assert.IsFalse(SkillPackageService.IsAllowedFile(null));
        Assert.AreEqual("evil_name.zip", SkillPackageService.SanitizeFileName("../evil name.zip"));
        Assert.IsTrue(SkillPackageService.IsValidId("my-package-2"));
        Assert.IsFalse(SkillPackageService.IsValidId("My-Package"));
        Assert.IsFalse(SkillPackageService.IsValidId("with_underscore"));
        Assert.IsFalse(SkillPackageService.IsValidId(""));
    }

    private sealed class Harness(
        PlatformDbContext db, SqliteConnection connection, SkillPackageService service, FakeStore store)
        : IAsyncDisposable
    {
        public SkillPackageService Service => service;
        public FakeStore Store => store;

        public SkillPackageFile File(Stream content, string fileName = "package.zip") =>
            new(fileName, content.Length, "application/zip", content);

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var factory = new TestDbContextFactory(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            var store = new FakeStore();
            var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return new Harness(db, connection, new SkillPackageService(db, store, NullLogger<SkillPackageService>.Instance), store);
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }
    private sealed class FakeStore : ISkillPackageObjectStore
    {
        public List<string> Uploads { get; } = [];
        public List<string> Deletes { get; } = [];

        public Task<string> UploadAsync(string objectKey, Stream stream, long size, string contentType, CancellationToken ct = default)
        {
            Uploads.Add(objectKey);
            return Task.FromResult(objectKey);
        }

        public Task<string> GetPresignedDownloadUrlAsync(string objectKey, int expirySeconds = 86400, CancellationToken ct = default) =>
            Task.FromResult($"https://store.invalid/{objectKey}");

        public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        {
            Deletes.Add(objectKey);
            return Task.CompletedTask;
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
