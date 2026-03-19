using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Utils;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ── CORS（允许 Admin SPA 跨域访问）─────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminSpa", policy =>
        policy.WithOrigins(
                "http://localhost:8000",
                "http://localhost:8001",
                "http://localhost:8004",
                "http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials());
});

builder.Services.AddControllersWithViews();

// ── JWT 认证 ──────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = builder.Configuration["Jwt:Issuer"] ?? "pudding-platform",
            ValidateAudience         = true,
            ValidAudience            = builder.Configuration["Jwt:Audience"] ?? "pudding-admin",
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew                = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();

// ── 注册 PlatformApiClient（通过 Controller API 操作控制面）──
builder.Services.AddHttpClient<PlatformApiClient>(client =>
{
    var endpoint = builder.Configuration["Pudding:ControllerEndpoint"] ?? "http://localhost:5000";
    client.BaseAddress = new Uri(endpoint);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── 注册 Workspace 业务层服务 ──────────────────────────────
builder.Services.AddScoped<WorkspaceBusinessService>();

// ── MinIO 对象存储服务 ─────────────────────────────────────
builder.Services.AddSingleton<PuddingPlatform.Services.MinioStorageService>();

// ── EF Core / 数据库 ──────────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=pudding_platform.db";
builder.Services.AddDbContext<PlatformDbContext>(opt =>
{
    if (connStr.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        opt.UseSqlite(connStr);
    else if (connStr.StartsWith("Host=", StringComparison.OrdinalIgnoreCase)
          || connStr.StartsWith("Server=", StringComparison.OrdinalIgnoreCase)
          || connStr.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        opt.UseNpgsql(connStr);
    else
        opt.UseSqlServer(connStr);
    // 迁移 snapshot 在 SQLite→Npgsql 切换时有轻微 provider 注解差异，逻辑 schema 一致，安全忽略
    opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// ── Session（用于 Auth API 的轻量登录态）──────────────────
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();

// ── 启动时应用迁移（自动建表、不删已有数据，多服务共享库安全）──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
    await db.Database.MigrateAsync();

    // 初始化 Admin 账号（仅首次启动且无 Admin 时创建）
    if (!db.AppUsers.Any(u => u.UserType == UserType.Admin))
    {
        db.AppUsers.Add(new AppUserEntity
        {
            UserId = "admin",
            Username = "admin",
            Email = "admin@pudding.local",
            DisplayName = "平台管理员",
            PasswordHash = PasswordHasher.Hash("pudding.dev"),
            UserType = UserType.Admin,
            IsEnabled = true,
        });
        db.SaveChanges();
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseCors("AdminSpa");
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
