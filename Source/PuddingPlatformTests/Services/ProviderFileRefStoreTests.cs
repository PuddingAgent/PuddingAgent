using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Core;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Files;

namespace PuddingPlatformTests.Services;

/// <summary>
/// ADR-077 V3-S2b-1：<see cref="SqliteProviderFileRefStore"/> 单测。
/// 覆盖：就绪复用（含近过期/过期/非 ready 过滤）、幂等 upsert、状态迁移 CAS、
/// 过期枚举过滤、并发写唯一键不重复。全部基于 SQLite 内存库/临时文件库，不依赖真实网络。
/// </summary>
[TestClass]
public sealed class ProviderFileRefStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    // ── TryGetReadyRefAsync ─────────────────────────────────────

    [TestMethod]
    public async Task TryGetReadyRef_ReturnsNull_WhenNoRow()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        var result = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-1", "sha-missing", CancellationToken.None);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryGetReadyRef_ReturnsReadyRef_WhenNotNearExpiry()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        var saved = await harness.Store.SaveAsync(NewRecord(harness, expiresIn: TimeSpan.FromHours(1)), CancellationToken.None);

        var result = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-1", Sha1, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(ProviderFileRefStatus.Ready, result.Status);
        Assert.AreEqual("file-1", result.RemoteFileId);
        Assert.AreEqual("image/png", result.MimeType);
        Assert.AreEqual(saved.ExpiresAt, result.ExpiresAt);
        Assert.AreEqual(saved.ArtifactId, result.ArtifactId);
    }

    [TestMethod]
    public async Task TryGetReadyRef_ReturnsNull_WhenExpired()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        await harness.Store.SaveAsync(NewRecord(harness, expiresIn: TimeSpan.FromMinutes(-1)), CancellationToken.None);

        var result = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-1", Sha1, CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryGetReadyRef_ReturnsNull_WhenNearExpiry()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        // 距过期 4 分钟 < 5 分钟（FileRefNearExpirySkewSeconds=300）：不分配给新 invocation。
        await harness.Store.SaveAsync(NewRecord(harness, expiresIn: TimeSpan.FromMinutes(4)), CancellationToken.None);

        var result = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-1", Sha1, CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryGetReadyRef_ReturnsNull_WhenOnlyUploading()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        var record = NewRecord(harness, expiresIn: TimeSpan.FromHours(1)) with { Status = ProviderFileRefStatus.Uploading };
        await harness.Store.SaveAsync(record, CancellationToken.None);

        var result = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-1", Sha1, CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryGetReadyRef_SkipsOtherKeys()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        await harness.Store.SaveAsync(NewRecord(harness, expiresIn: TimeSpan.FromHours(1)), CancellationToken.None);

        var otherEpoch = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-2", Sha1, CancellationToken.None);
        var otherSha = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-1", "sha-other", CancellationToken.None);
        var otherProvider = await harness.Store.TryGetReadyRefAsync("openai", "epoch-1", Sha1, CancellationToken.None);

        Assert.IsNull(otherEpoch);
        Assert.IsNull(otherSha);
        Assert.IsNull(otherProvider);
    }

    // ── SaveAsync 幂等 upsert ───────────────────────────────────

    [TestMethod]
    public async Task SaveAsync_Upsert_SameUniqueKey_UpdatesFields_KeepsCreatedAt()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        var first = NewRecord(harness, expiresIn: TimeSpan.FromHours(1));
        var saved1 = await harness.Store.SaveAsync(first, CancellationToken.None);

        var second = first with
        {
            RemoteFileId = "file-2",
            Bytes = 2048,
            Status = ProviderFileRefStatus.Ready,
            ExpiresAt = Now.AddHours(2),
            LastUsedAt = Now.AddMinutes(1),
            UpdatedAt = Now.AddMinutes(2),
        };
        var saved2 = await harness.Store.SaveAsync(second, CancellationToken.None);

        Assert.AreEqual("file-2", saved2.RemoteFileId);
        Assert.AreEqual(2048, saved2.Bytes);
        Assert.AreEqual(Now.AddHours(2), saved2.ExpiresAt);
        Assert.AreEqual(Now.AddMinutes(1), saved2.LastUsedAt);
        // created_at 不被 upsert 覆盖。
        Assert.AreEqual(saved1.CreatedAt, saved2.CreatedAt);
        Assert.AreEqual(1, await harness.CountRowsAsync());

        // 幂等复查：再保存同键仍单行。
        await harness.Store.SaveAsync(second with { UpdatedAt = Now.AddMinutes(3) }, CancellationToken.None);
        Assert.AreEqual(1, await harness.CountRowsAsync());
    }

    // ── UpdateExpiryAsync ───────────────────────────────────────

    [TestMethod]
    public async Task UpdateExpiryAsync_RenewsReadyRef()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        await harness.Store.SaveAsync(NewRecord(harness, expiresIn: TimeSpan.FromMinutes(10)), CancellationToken.None);

        var renewed = await harness.Store.UpdateExpiryAsync(
            "deepseek", "epoch-1", Sha1, Now.AddDays(1), Now.AddMinutes(1), CancellationToken.None);

        Assert.IsNotNull(renewed);
        Assert.AreEqual(Now.AddDays(1), renewed.ExpiresAt);
        Assert.AreEqual(ProviderFileRefStatus.Ready, renewed.Status);

        var fetched = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-1", Sha1, CancellationToken.None);
        Assert.IsNotNull(fetched, "续期后应可复用");
    }

    [TestMethod]
    public async Task UpdateExpiryAsync_ReturnsNull_WhenKeyMissing()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        var result = await harness.Store.UpdateExpiryAsync(
            "deepseek", "epoch-1", "sha-missing", Now.AddDays(1), Now, CancellationToken.None);
        Assert.IsNull(result);
    }

    // ── MarkExpiredAsync ────────────────────────────────────────

    [TestMethod]
    public async Task MarkExpiredAsync_TransitionsReadyToExpired()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        await harness.Store.SaveAsync(NewRecord(harness, expiresIn: TimeSpan.FromHours(1)), CancellationToken.None);

        var expired = await harness.Store.MarkExpiredAsync("deepseek", "epoch-1", Sha1, Now.AddMinutes(1), CancellationToken.None);

        Assert.IsNotNull(expired);
        Assert.AreEqual(ProviderFileRefStatus.Expired, expired.Status);
        Assert.AreEqual(Now.AddMinutes(1), expired.UpdatedAt);

        var fetched = await harness.Store.TryGetReadyRefAsync("deepseek", "epoch-1", Sha1, CancellationToken.None);
        Assert.IsNull(fetched, "expired 不参与复用");
    }

    [TestMethod]
    public async Task MarkExpiredAsync_ReturnsNull_WhenAlreadyExpired_Cas()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        await harness.Store.SaveAsync(NewRecord(harness, expiresIn: TimeSpan.FromHours(1)), CancellationToken.None);
        await harness.Store.MarkExpiredAsync("deepseek", "epoch-1", Sha1, Now.AddMinutes(1), CancellationToken.None);

        var again = await harness.Store.MarkExpiredAsync("deepseek", "epoch-1", Sha1, Now.AddMinutes(2), CancellationToken.None);

        Assert.IsNull(again, "expired 是终态，二次 CAS 应失败");
    }

    // ── MarkDeletePendingAsync ──────────────────────────────────

    [TestMethod]
    public async Task MarkDeletePendingAsync_TransitionsReadyAndExpired()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        await harness.Store.SaveAsync(NewRecord(harness, expiresIn: TimeSpan.FromHours(1)), CancellationToken.None);
        await harness.Store.SaveAsync(
            NewRecord(harness, artifactId: "artifact-2", sha: "sha-2", fileId: "file-2", expiresIn: TimeSpan.FromHours(1)),
            CancellationToken.None);
        await harness.Store.MarkExpiredAsync("deepseek", "epoch-1", "sha-2", Now, CancellationToken.None);

        var readyToDelete = await harness.Store.MarkDeletePendingAsync("deepseek", "epoch-1", Sha1, Now, CancellationToken.None);
        var expiredToDelete = await harness.Store.MarkDeletePendingAsync("deepseek", "epoch-1", "sha-2", Now, CancellationToken.None);

        Assert.IsNotNull(readyToDelete);
        Assert.AreEqual(ProviderFileRefStatus.DeletePending, readyToDelete.Status);
        Assert.IsNotNull(expiredToDelete);
        Assert.AreEqual(ProviderFileRefStatus.DeletePending, expiredToDelete.Status);
    }

    // ── ListExpiredAsync ────────────────────────────────────────

    [TestMethod]
    public async Task ListExpiredAsync_FiltersByStatusAndExpiry()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        // sha-1：ready 未过期（不应出现在待清理列表）
        await harness.Store.SaveAsync(NewRecord(harness, artifactId: "a1", sha: "sha-1", fileId: "f1", expiresIn: TimeSpan.FromHours(1)), CancellationToken.None);
        // sha-2：expired（expires_at 已过）
        await harness.Store.SaveAsync(NewRecord(harness, artifactId: "a2", sha: "sha-2", fileId: "f2", expiresIn: TimeSpan.FromMinutes(-5)), CancellationToken.None);
        await harness.Store.MarkExpiredAsync("deepseek", "epoch-1", "sha-2", Now, CancellationToken.None);
        // sha-3：delete_pending（已过期才进待清理列表）
        await harness.Store.SaveAsync(NewRecord(harness, artifactId: "a3", sha: "sha-3", fileId: "f3", expiresIn: TimeSpan.FromMinutes(-5)), CancellationToken.None);
        await harness.Store.MarkDeletePendingAsync("deepseek", "epoch-1", "sha-3", Now, CancellationToken.None);
        // sha-4：failed（不在待清理状态）
        await harness.Store.SaveAsync(
            NewRecord(harness, artifactId: "a4", sha: "sha-4", fileId: "f4", expiresIn: TimeSpan.FromMinutes(-5)) with { Status = ProviderFileRefStatus.Failed },
            CancellationToken.None);

        var expired = await harness.Store.ListExpiredAsync(Now.AddMinutes(-1), limit: 10, CancellationToken.None);

        var shas = expired.Select(r => r.ArtifactSha256).OrderBy(x => x).ToArray();
        Assert.AreEqual(2, shas.Length, "只应枚举 expired/delete_pending 且已过期的行");
        CollectionAssert.AreEqual(new[] { "sha-2", "sha-3" }, shas);
        CollectionAssert.DoesNotContain(shas, "sha-1");
        CollectionAssert.DoesNotContain(shas, "sha-4");
    }

    [TestMethod]
    public async Task ListExpiredAsync_RespectsLimit()
    {
        await using var harness = await Harness.CreateMemoryAsync();
        await harness.Store.SaveAsync(NewRecord(harness, artifactId: "a1", sha: "sha-1", fileId: "f1", expiresIn: TimeSpan.FromMinutes(-10)), CancellationToken.None);
        await harness.Store.SaveAsync(NewRecord(harness, artifactId: "a2", sha: "sha-2", fileId: "f2", expiresIn: TimeSpan.FromMinutes(-5)), CancellationToken.None);
        await harness.Store.MarkExpiredAsync("deepseek", "epoch-1", "sha-1", Now, CancellationToken.None);
        await harness.Store.MarkExpiredAsync("deepseek", "epoch-1", "sha-2", Now, CancellationToken.None);

        var limited = await harness.Store.ListExpiredAsync(Now, limit: 1, CancellationToken.None);

        Assert.AreEqual(1, limited.Count);
    }

    // ── 并发 ────────────────────────────────────────────────────

    [TestMethod]
    public async Task ConcurrentSaveAsync_SameUniqueKey_ProducesSingleRow()
    {
        await using var harness = await Harness.CreateFileAsync();
        var first = NewRecord(harness, expiresIn: TimeSpan.FromHours(1));
        var second = first with
        {
            RemoteFileId = "file-concurrent-2",
            UpdatedAt = Now.AddSeconds(1),
        };

        await Task.WhenAll(
            harness.Store.SaveAsync(first, CancellationToken.None),
            harness.Store.SaveAsync(second, CancellationToken.None));

        Assert.AreEqual(1, await harness.CountRowsAsync(), "同一唯一键并发写入不得产生重复行");
    }

    // ── ToReference（值类型映射）────────────────────────────────

    [TestMethod]
    public void ToReference_MapsFileIdMimeTypeExpiresAt()
    {
        var record = NewRecord(harness: null!, expiresIn: TimeSpan.FromHours(1));
        var reference = record.ToReference();

        Assert.AreEqual(record.RemoteFileId, reference.FileId);
        Assert.AreEqual(record.MimeType, reference.MimeType);
        Assert.AreEqual(record.ExpiresAt, reference.ExpiresAt);
    }

    // ── fixtures ────────────────────────────────────────────────

    private const string Sha1 = "sha-1";

    private static ProviderFileRefRecord NewRecord(
        Harness? harness,
        string artifactId = "artifact-1",
        string sha = Sha1,
        string fileId = "file-1",
        TimeSpan? expiresIn = null)
        => new(
            ProviderId: "deepseek",
            CredentialEpoch: "epoch-1",
            ArtifactId: artifactId,
            ArtifactSha256: sha,
            RemoteFileId: fileId,
            Bytes: 1024,
            MimeType: "image/png",
            ExpiresAt: Now.Add(expiresIn ?? TimeSpan.FromHours(1)),
            LastUsedAt: null,
            Status: ProviderFileRefStatus.Ready,
            CreatedAt: Now,
            UpdatedAt: Now);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection? _memoryConnection;
        private readonly string? _filePath;

        private Harness(
            IDbContextFactory<PlatformDbContext> factory,
            SqliteConnection? memoryConnection = null,
            string? filePath = null)
        {
            Factory = factory;
            _memoryConnection = memoryConnection;
            _filePath = filePath;
            Time = new MutableTimeProvider(Now);
            Store = new SqliteProviderFileRefStore(factory, Time);
        }

        public IDbContextFactory<PlatformDbContext> Factory { get; }
        public MutableTimeProvider Time { get; }
        public SqliteProviderFileRefStore Store { get; }

        public static async Task<Harness> CreateMemoryAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var factory = new SharedConnectionDbContextFactory(connection);
            await EnsureSchemaAsync(factory);
            return new Harness(factory, memoryConnection: connection);
        }

        public static async Task<Harness> CreateFileAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "PuddingAgent", "fileref-store-tests");
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".db");
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={filePath};Default Timeout=10")
                .Options;
            var factory = new FileDbContextFactory(options);
            await EnsureSchemaAsync(factory);
            return new Harness(factory, filePath: filePath);
        }

        private static async Task EnsureSchemaAsync(IDbContextFactory<PlatformDbContext> factory)
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            await ProviderFileRefSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        public async Task<int> CountRowsAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            return await db.ProviderFileRefs.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_memoryConnection is not null)
                await _memoryConnection.DisposeAsync();
            if (_filePath is not null)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    File.Delete(_filePath);
                }
                catch (IOException)
                {
                    // 测试清理 best-effort。
                }
            }
        }
    }

    /// <summary>单一共享连接的内存库 factory（所有 DbContext 复用同一连接，内存库不丢数据）。</summary>
    private sealed class SharedConnectionDbContextFactory(SqliteConnection connection) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            return new PlatformDbContext(options);
        }
    }

    private sealed class FileDbContextFactory(DbContextOptions<PlatformDbContext> options) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
