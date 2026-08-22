using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Security;

namespace PuddingPlatformTests.Security;

/// <summary>
/// ADR-075 测试 harness：内存 SQLite PlatformDbContext + Schema bootstrap + Owner/Service 装配。
/// </summary>
internal sealed class ExternalAccessTokenTestHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private ExternalAccessTokenTestHarness(SqliteConnection connection, IDbContextFactory<PlatformDbContext> factory)
    {
        _connection = connection;
        Factory = factory;
        Store = new ExternalAccessTokenStore(factory);
    }

    public IDbContextFactory<PlatformDbContext> Factory { get; }
    public ExternalAccessTokenStore Store { get; }

    public static async Task<ExternalAccessTokenTestHarness> CreateAsync(
        PuddingExternalTaskApiConfig? options = null,
        string dataRoot = "unused://in-memory")
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        IDbContextFactory<PlatformDbContext> factory = new StubDbContextFactory(connection);
        var harness = new ExternalAccessTokenTestHarness(connection, factory);

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            await ExternalAccessTokenSchemaBootstrapper.EnsureCreatedAsync(db);
        }

        if (options is not null)
        {
            Directory.CreateDirectory(dataRoot);
            var configDir = Path.Combine(dataRoot, "config");
            Directory.CreateDirectory(configDir);
            await File.WriteAllTextAsync(
                Path.Combine(configDir, "system.json"),
                $$"""
                {
                  "externalTaskApi": {
                    "enabled": {{options.Enabled.ToString().ToLowerInvariant()}},
                    "publicBaseUrl": {{(options.PublicBaseUrl is null ? "null" : $"\"{options.PublicBaseUrl}\"")}},
                    "requireHttps": {{options.RequireHttps.ToString().ToLowerInvariant()}},
                    "defaultTokenLifetimeDays": {{options.DefaultTokenLifetimeDays}},
                    "maxTokenLifetimeDays": {{options.MaxTokenLifetimeDays}},
                    "maxActiveTokensPerOwner": {{options.MaxActiveTokensPerOwner}}
                  }
                }
                """);
        }

        return harness;
    }

    public ExternalAccessTokenService CreateService(string dataRoot = "unused://in-memory")
        => new(
            Store,
            new ExternalTaskApiOptionsProvider(
                PuddingDataPaths.FromRoot(Path.GetFullPath(dataRoot)),
                NullLogger<ExternalTaskApiOptionsProvider>.Instance),
            Factory,
            NullLogger<ExternalAccessTokenService>.Instance);

    public async Task SeedOwnerAsync(string userId, bool enabled = true)
    {
        await using var db = await Factory.CreateDbContextAsync();
        db.AppUsers.Add(new AppUserEntity
        {
            UserId = userId,
            Username = userId,
            Email = $"{userId}@test.local",
            PasswordHash = "x:y",
            UserType = UserType.Admin,
            IsEnabled = enabled,
        });
        await db.SaveChangesAsync();
    }

    public async Task SetOwnerEnabledAsync(string userId, bool enabled)
    {
        await using var db = await Factory.CreateDbContextAsync();
        var user = await db.AppUsers.SingleAsync(u => u.UserId == userId);
        user.IsEnabled = enabled;
        await db.SaveChangesAsync();
    }

    /// <summary>直接 SQL 改 expires（模拟时间流逝），绕过领域服务。</summary>
    public async Task BackdateExpiryAsync(string tokenId, DateTimeOffset expiresAtUtc)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE external_access_tokens SET expires_at_utc = {0} WHERE token_id = {1};",
            expiresAtUtc.ToString("O"), tokenId);
    }

    /// <summary>原始列值检查：确认持久层没有 canonical token 明文。</summary>
    public async Task<List<string>> DumpRawSecretHashesAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT token_id, secret_hash FROM external_access_tokens;";
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(Convert.ToHexString((byte[])reader[1]));
        return values;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private sealed class StubDbContextFactory(SqliteConnection connection) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            return new PlatformDbContext(options);
        }
    }
}
