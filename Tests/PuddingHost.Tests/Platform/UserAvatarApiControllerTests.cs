using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using Xunit;

namespace PuddingHost.Tests.Platform;

/// <summary>
/// UserAvatarApiController 直接实例化测试（SQLite 内存库 + 临时 wwwroot）。
/// 覆盖：成功上传、旧文件清理、401 / 403 / 404 / 超限 / 非法 MIME。
/// </summary>
public sealed class UserAvatarApiControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly PlatformDbContext _db;
    private readonly string _webRoot;

    public UserAvatarApiControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new PlatformDbContext(options);
        _db.Database.EnsureCreated();

        _webRoot = Path.Combine(Path.GetTempPath(), "pudding-avatar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);

        _db.AppUsers.Add(new AppUserEntity
        {
            UserId = "alice",
            Username = "Alice",
            Email = "alice@test.local",
            PasswordHash = "x",
            UserType = UserType.SimpleUser,
            IsEnabled = true,
        });
        _db.AppUsers.Add(new AppUserEntity
        {
            UserId = "admin",
            Username = "Admin",
            Email = "admin@test.local",
            PasswordHash = "x",
            UserType = UserType.Admin,
            IsEnabled = true,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_webRoot))
            Directory.Delete(_webRoot, recursive: true);
    }

    private UserAvatarApiController CreateController(string? currentUserId, bool isAdmin = false)
    {
        var controller = new UserAvatarApiController(
            _db,
            new UserAvatarStorageService(new FakeWebHostEnvironment(_webRoot), NullLogger<UserAvatarStorageService>.Instance),
            NullLogger<UserAvatarApiController>.Instance);

        var http = new DefaultHttpContext
        {
            Session = new FakeSession(),
        };
        if (currentUserId is not null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, currentUserId) };
            if (isAdmin)
                claims.Add(new Claim(ClaimTypes.Role, "admin"));
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static IFormFile PngFile(long? forcedLength = null, string contentType = "image/png")
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, forcedLength ?? stream.Length, "file", "a.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
        return file;
    }

    [Fact]
    public async Task Upload_Self_Returns200_UpdatesDb_AndWritesFile()
    {
        var controller = CreateController("alice");

        var result = await controller.Upload("alice", PngFile(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<UserAvatarResponse>(ok.Value!);
        Assert.Equal("alice", body.UserId);
        Assert.StartsWith("/user-avatars/", body.Avatar);
        var entity = await _db.AppUsers.AsNoTracking().SingleAsync(u => u.UserId == "alice");
        Assert.Equal(body.Avatar, entity.Avatar);
        Assert.True(File.Exists(Path.Combine(_webRoot, "user-avatars", body.FileName)));
    }

    [Fact]
    public async Task Upload_Again_ReplacesAvatar_AndDeletesOldFile()
    {
        var controller = CreateController("alice");

        var first = await controller.Upload("alice", PngFile(), CancellationToken.None);
        var firstBody = Assert.IsType<UserAvatarResponse>(
            Assert.IsType<OkObjectResult>(first).Value!);
        var firstFile = Path.Combine(_webRoot, "user-avatars", firstBody.FileName);
        Assert.True(File.Exists(firstFile));

        var second = await controller.Upload("alice", PngFile(), CancellationToken.None);
        var secondBody = Assert.IsType<UserAvatarResponse>(
            Assert.IsType<OkObjectResult>(second).Value!);

        Assert.NotEqual(firstBody.Avatar, secondBody.Avatar);
        Assert.False(File.Exists(firstFile), "旧头像文件应被清理");
        Assert.True(File.Exists(Path.Combine(_webRoot, "user-avatars", secondBody.FileName)));
    }

    [Fact]
    public async Task Upload_UnknownUser_Returns404()
    {
        // 用 Admin 上传，越过 403 检查后命中用户不存在
        var controller = CreateController("admin", isAdmin: true);
        var result = await controller.Upload("ghost", PngFile(), CancellationToken.None);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFound.StatusCode);
    }

    [Fact]
    public async Task Upload_WithoutIdentity_Returns401()
    {
        var controller = CreateController(null);
        var result = await controller.Upload("alice", PngFile(), CancellationToken.None);
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Equal(401, unauthorized.StatusCode);
    }

    [Fact]
    public async Task Upload_OtherUser_AsNonAdmin_Returns403()
    {
        var controller = CreateController("alice");
        var result = await controller.Upload("admin", PngFile(), CancellationToken.None);
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, forbidden.StatusCode);
    }

    [Fact]
    public async Task Upload_OtherUser_AsAdmin_Returns200()
    {
        var controller = CreateController("admin", isAdmin: true);
        var result = await controller.Upload("alice", PngFile(), CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);
    }

    [Fact]
    public async Task Upload_Gif_Returns415()
    {
        var controller = CreateController("alice");
        var result = await controller.Upload("alice", PngFile(contentType: "image/gif"), CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(415, status.StatusCode);
    }

    [Fact]
    public async Task Upload_Oversize_Returns400()
    {
        var controller = CreateController("alice");
        var result = await controller.Upload("alice", PngFile(forcedLength: 5 * 1024 * 1024 + 1), CancellationToken.None);
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, bad.StatusCode);
    }

    private sealed class FakeWebHostEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = webRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = webRoot;
    }

    private sealed class FakeSession : ISession
    {
        private readonly Dictionary<string, byte[]> _store = new();

        public string Id { get; } = Guid.NewGuid().ToString();
        public bool IsAvailable => true;
        public IEnumerable<string> Keys => _store.Keys;

        public void Clear() => _store.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => _store.Remove(key);
        public void Set(string key, byte[] value) => _store[key] = value;
        public bool TryGetValue(string key, out byte[]? value) => _store.TryGetValue(key, out value);
    }
}
