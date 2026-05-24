using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Utils;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// 首次启动 Bootstrap 初始化 API。
/// 当数据库中尚无 Admin 用户时，引导创建首个管理员账号。
/// </summary>
[ApiController]
[Route("api/bootstrap")]
[AllowAnonymous]
public partial class BootstrapApiController(
    IConfiguration config,
    PlatformDbContext db,
    BootstrapStateService stateService,
    Sm2JwtSigner sm2JwtSigner,
    ILogger<BootstrapApiController> logger) : ControllerBase
{
    private IActionResult? CheckInitialized()
    {
        var initialized = config.GetValue<bool>("Bootstrap:Initialized");
        if (initialized)
            return StatusCode(403, new { status = "error", message = "系统已完成初始化" });
        return null;
    }

    /// <summary>GET /api/bootstrap/status — 检查系统是否需要初始化</summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var lockResult = CheckInitialized();
        if (lockResult != null) return lockResult;

        var hasAdmin = await db.AppUsers.AnyAsync(u => u.UserType == UserType.Admin, ct);
        var userCount = await db.AppUsers.CountAsync(ct);

        return Ok(new
        {
            needsSetup = !hasAdmin,
            hasAdmin,
            userCount,
        });
    }

    /// <summary>POST /api/bootstrap/admin — 创建首个管理员账号</summary>
    [HttpPost("admin")]
    public async Task<IActionResult> CreateAdmin([FromBody] BootstrapAdminRequest request, CancellationToken ct)
    {
        var lockResult = CheckInitialized();
        if (lockResult != null) return lockResult;

        // 密码强度校验：≥8位，含大小写+数字（事务外提前校验）
        if (!PasswordStrengthRegex().IsMatch(request.Password))
            return BadRequest(new
            {
                status = "error",
                message = "密码必须至少8位，且包含大写字母、小写字母和数字",
            });

        // 使用 ReadCommitted 事务防止 TOCTOU 竞态条件：
        // SQLite 不支持 Serializable 隔离级别与 EF Core 的可靠交互（事务可能自动完成导致
        // "This SqliteTransaction has completed" 异常），ReadCommitted 在 EnsureCreated 表上已足够。
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // 仅当无 Admin 时允许
            if (await db.AppUsers.AnyAsync(u => u.UserType == UserType.Admin, ct))
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { status = "error", message = "系统已完成初始化" });
            }

            // 校验用户 ID 是否已被占用
            if (await db.AppUsers.AnyAsync(u => u.UserId == request.UserId, ct))
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { status = "error", message = "用户 ID 已被占用" });
            }

            var entity = new AppUserEntity
            {
                UserId = request.UserId,
                Username = request.UserId,
                Email = request.Email,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.UserId : request.DisplayName,
                PasswordHash = PasswordHasher.Hash(request.Password),
                UserType = UserType.Admin,
                IsEnabled = true,
            };

            db.AppUsers.Add(entity);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await stateService.SetInitializedAsync();
            logger.LogInformation("[Bootstrap] Created first admin account userId={UserId}", entity.UserId);

            var token = GenerateJwt(entity.UserId, entity.DisplayName ?? entity.UserId, entity.Email, "admin");

            HttpContext.Session.SetString("username", entity.UserId);
            HttpContext.Session.SetString("authority", "admin");

            return Ok(new { status = "ok", type = "account", currentAuthority = "admin", token });
        }
        catch
        {
            // 事务可能已被 SQLite/EF Core 自动完成，仅在仍可用时回滚
            try { await transaction.RollbackAsync(ct); } catch (InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>POST /api/bootstrap/complete — 一次性提交首次安装向导。</summary>
    [HttpPost("complete")]
    public async Task<IActionResult> Complete([FromBody] BootstrapCompleteRequest request, CancellationToken ct)
    {
        var lockResult = CheckInitialized();
        if (lockResult != null) return lockResult;

        if (request.Admin is null)
            return BadRequest(new { status = "error", message = "缺少管理员账号信息" });

        if (!PasswordStrengthRegex().IsMatch(request.Admin.Password))
            return BadRequest(new
            {
                status = "error",
                message = "密码必须至少8位，且包含大写字母、小写字母和数字",
            });

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (await db.AppUsers.AnyAsync(u => u.UserType == UserType.Admin, ct))
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { status = "error", message = "系统已完成初始化" });
            }

            if (await db.AppUsers.AnyAsync(u => u.UserId == request.Admin.UserId, ct))
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { status = "error", message = "用户 ID 已被占用" });
            }

            if (await db.AppUsers.AnyAsync(u => u.Email == request.Admin.Email, ct))
            {
                await transaction.RollbackAsync(ct);
                return BadRequest(new { status = "error", message = "邮箱已被占用" });
            }

            var admin = new AppUserEntity
            {
                UserId = request.Admin.UserId,
                Username = request.Admin.UserId,
                Email = request.Admin.Email,
                DisplayName = string.IsNullOrWhiteSpace(request.Admin.DisplayName)
                    ? request.Admin.UserId
                    : request.Admin.DisplayName,
                PasswordHash = PasswordHasher.Hash(request.Admin.Password),
                UserType = UserType.Admin,
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            db.AppUsers.Add(admin);
            await db.SaveChangesAsync(ct);

            var workspace = await EnsureDefaultWorkspaceAsync(admin, request.Defaults, ct);
            var providerResult = await ConfigureProviderAsync(request.Provider, ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await stateService.SetInitializedAsync();
            logger.LogInformation(
                "[Bootstrap] Completed setup userId={UserId} providerId={ProviderId} workspaceId={WorkspaceId}",
                admin.UserId,
                providerResult.ProviderId,
                workspace.WorkspaceId);

            var token = GenerateJwt(admin.UserId, admin.DisplayName ?? admin.UserId, admin.Email, "admin");

            HttpContext.Session.SetString("username", admin.UserId);
            HttpContext.Session.SetString("authority", "admin");

            return Ok(new BootstrapCompleteResponse(
                "ok",
                "admin",
                token,
                providerResult.ProviderId,
                providerResult.ChatModelId,
                providerResult.MemoryModelId,
                workspace.WorkspaceId,
                new BootstrapHealthSummary(
                    Database: true,
                    Admin: true,
                    Provider: providerResult.Configured,
                    Workspace: true,
                    Warnings: providerResult.Warnings)));
        }
        catch
        {
            try { await transaction.RollbackAsync(ct); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private async Task<WorkspaceEntity> EnsureDefaultWorkspaceAsync(
        AppUserEntity admin,
        BootstrapDefaultsRequest? defaults,
        CancellationToken ct)
    {
        const string teamId = "platform-team";
        const string workspaceId = "default";

        var team = await db.Teams.FirstOrDefaultAsync(t => t.TeamId == teamId, ct);
        if (team is null)
        {
            team = new TeamEntity
            {
                TeamId = teamId,
                Name = "平台团队",
                Description = "平台默认团队",
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Teams.Add(team);
            await db.SaveChangesAsync(ct);
        }

        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (workspace is null)
        {
            workspace = new WorkspaceEntity
            {
                WorkspaceId = workspaceId,
                Slug = workspaceId,
                TeamEntityId = team.Id,
                Name = string.IsNullOrWhiteSpace(defaults?.WorkspaceName) ? "默认工作空间" : defaults.WorkspaceName,
                Description = "首次初始化创建的默认工作空间",
                TeamAccessPolicy = WorkspaceAccessPolicy.Write,
                CompanyAccessPolicy = WorkspaceAccessPolicy.ReadOnly,
                IsEnabled = true,
                IsFrozen = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync(ct);
        }
        else if (!string.IsNullOrWhiteSpace(defaults?.WorkspaceName))
        {
            workspace.Name = defaults.WorkspaceName;
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (!await db.TeamMembers.AnyAsync(m => m.TeamEntityId == team.Id && m.UserEntityId == admin.Id, ct))
        {
            db.TeamMembers.Add(new TeamMemberEntity
            {
                TeamEntityId = team.Id,
                UserEntityId = admin.Id,
                Role = TeamMemberRole.Admin,
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceEntityId == workspace.Id && m.UserEntityId == admin.Id, ct))
        {
            db.WorkspaceMembers.Add(new WorkspaceMemberEntity
            {
                WorkspaceEntityId = workspace.Id,
                UserEntityId = admin.Id,
                AccessLevel = WorkspaceAccessPolicy.Manage,
                AddedAt = DateTimeOffset.UtcNow,
            });
        }

        return workspace;
    }

    private async Task<BootstrapProviderResult> ConfigureProviderAsync(
        BootstrapProviderRequest? provider,
        CancellationToken ct)
    {
        if (provider is null || string.Equals(provider.Mode, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return new BootstrapProviderResult(null, null, null, false, ["未配置真实模型服务，将继续使用已有或 fake provider。"]);
        }

        if (string.IsNullOrWhiteSpace(provider.ProviderId)
            || string.IsNullOrWhiteSpace(provider.Name)
            || string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new InvalidOperationException("ProviderId、名称和 BaseUrl 不能为空");
        }

        var providerId = provider.ProviderId.Trim();
        var entity = await db.LlmProviders
            .Include(p => p.Models)
            .Include(p => p.Quota)
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);

        if (entity is null)
        {
            entity = new LlmProviderEntity
            {
                ProviderId = providerId,
                Name = provider.Name.Trim(),
                Protocol = string.IsNullOrWhiteSpace(provider.Protocol) ? "openai" : provider.Protocol.Trim(),
                BaseUrl = provider.BaseUrl.Trim(),
                ApiKey = string.IsNullOrWhiteSpace(provider.ApiKey) ? null : provider.ApiKey,
                Description = "首次初始化创建",
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Quota = new LlmProviderQuotaEntity { UpdatedAt = DateTimeOffset.UtcNow },
            };
            db.LlmProviders.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            entity.Name = provider.Name.Trim();
            entity.Protocol = string.IsNullOrWhiteSpace(provider.Protocol) ? "openai" : provider.Protocol.Trim();
            entity.BaseUrl = provider.BaseUrl.Trim();
            if (!string.IsNullOrWhiteSpace(provider.ApiKey))
                entity.ApiKey = provider.ApiKey;
            entity.IsEnabled = true;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            entity.Quota ??= new LlmProviderQuotaEntity { ProviderId = entity.Id, UpdatedAt = DateTimeOffset.UtcNow };
        }

        var chatModelId = AddOrUpdateModel(entity, provider.ChatModelId, isDefault: true);
        var memoryModelId = AddOrUpdateModel(entity, provider.MemoryModelId, isDefault: false);
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            warnings.Add("未填写 API Key，后续调用真实模型可能失败。");
        if (chatModelId is null)
            warnings.Add("未填写默认聊天模型。");

        return new BootstrapProviderResult(providerId, chatModelId, memoryModelId, true, warnings);
    }

    private static string? AddOrUpdateModel(LlmProviderEntity provider, string? modelId, bool isDefault)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return null;

        var normalized = modelId.Trim();
        var model = provider.Models.FirstOrDefault(m => m.ModelId == normalized);
        if (model is null)
        {
            provider.Models.Add(new LlmModelEntity
            {
                ProviderId = provider.Id,
                ModelId = normalized,
                Name = normalized,
                Description = isDefault ? "首次初始化默认聊天模型" : "首次初始化记忆/总结模型",
                MaxContextTokens = 8192,
                MaxOutputTokens = 2048,
                CapabilityTagsJson = "[]",
                IsDefault = isDefault,
                SortOrder = isDefault ? 0 : 10,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            model.IsDefault = model.IsDefault || isDefault;
            model.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (isDefault)
        {
            foreach (var other in provider.Models.Where(m => m.ModelId != normalized))
                other.IsDefault = false;
        }

        return normalized;
    }

    private string GenerateJwt(string userId, string displayName, string email, string authority)
    {
        var key = config["Jwt:Key"] ?? "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!";
        var issuer = config["Jwt:Issuer"] ?? "pudding-platform";
        var audience = config["Jwt:Audience"] ?? "pudding-admin";
        var expiryHours = int.TryParse(config["Jwt:ExpiryHours"], out var h) ? h : 8;
        var utcNow = DateTime.UtcNow;
        var expiresAt = utcNow.AddHours(expiryHours);
        var jti = Guid.NewGuid().ToString();

        var secKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var creds = new SigningCredentials(secKey, SecurityAlgorithms.HmacSha256);

        var sm2Payload = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["aud"] = audience,
            ["email"] = email,
            ["exp"] = new DateTimeOffset(expiresAt).ToUnixTimeSeconds().ToString(),
            ["iss"] = issuer,
            ["jti"] = jti,
            ["name"] = displayName,
            ["nameid"] = userId,
            ["role"] = authority,
            ["sub"] = userId,
        };
        var sm2Signature = sm2JwtSigner.SignPayload(JsonSerializer.Serialize(sm2Payload));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, displayName),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, authority),
            new Claim("sm2_sig", sm2Signature),
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).{8,}$")]
    private static partial Regex PasswordStrengthRegex();
}

public sealed record BootstrapAdminRequest(
    string UserId,
    string Email,
    string Password,
    string? DisplayName = null
);

public sealed record BootstrapCompleteRequest(
    BootstrapAdminRequest Admin,
    BootstrapProviderRequest? Provider,
    BootstrapDefaultsRequest? Defaults
);

public sealed record BootstrapProviderRequest(
    string Mode,
    string? ProviderId,
    string? Name,
    string? Protocol,
    string? BaseUrl,
    string? ApiKey,
    string? ChatModelId,
    string? MemoryModelId
);

public sealed record BootstrapDefaultsRequest(
    string? WorkspaceName,
    string? AgentName
);

public sealed record BootstrapCompleteResponse(
    string Status,
    string CurrentAuthority,
    string Token,
    string? ProviderId,
    string? ChatModelId,
    string? MemoryModelId,
    string WorkspaceId,
    BootstrapHealthSummary Health
);

public sealed record BootstrapHealthSummary(
    bool Database,
    bool Admin,
    bool Provider,
    bool Workspace,
    IReadOnlyList<string> Warnings
);

internal sealed record BootstrapProviderResult(
    string? ProviderId,
    string? ChatModelId,
    string? MemoryModelId,
    bool Configured,
    IReadOnlyList<string> Warnings
);
