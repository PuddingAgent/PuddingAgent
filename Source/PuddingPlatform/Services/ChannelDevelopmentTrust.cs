using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

public sealed record ChannelAccountBinding(string App, string Organization, string ExternalUser, string UserId);
public sealed record ChannelTrustSettings(string Mode, IReadOnlyList<ChannelAccountBinding> Bindings)
{
    public static readonly ChannelTrustSettings Restricted = new("restricted", []);
}

/// <summary>已绑定发送者的内部身份。权限属于 Agent，这里只描述「谁在说话」。</summary>
public sealed record ChannelPrincipal(string UserId, string Role);

// Channel is an instance ID. Organization pins the provider tenant, independently of a display name.
public sealed record ChannelSender(string Channel, string App, string Organization, string ExternalUser, bool Group);
public sealed record DevelopmentTurn(string Workspace, string Agent, string Session, string Message,
    string Actor, string User, ChannelSender Source);

public sealed class ChannelDevelopmentTrust(PuddingDataPaths paths, IDataProtectionProvider protection,
    IDbContextFactory<PlatformDbContext> databases)
{
    public const string Web = "web-chat";
    public const string WebIngress = "pudding_web_ingress";
    public const string DingTalk = "dingtalk-group";
    public const string Receipt = "channel_development_receipt";
    private readonly IDataProtector protector = protection.CreateProtector("Pudding.ChannelDevelopment.Turn.v1");

    private static string Digest(params string[] fields) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));

    private string FileName(string workspace, string agent, string channel) =>
        paths.SystemConfigFile(Path.Combine("channel-trust", Digest(workspace, agent, channel) + ".json"));

    public async Task<ChannelTrustSettings> ReadAsync(string workspace, string agent, string channel, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(FileName(workspace, agent, channel), ct);
            return JsonSerializer.Deserialize<ChannelTrustSettings>(json, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Invalid channel trust settings.");
        }
        catch (FileNotFoundException) { return ChannelTrustSettings.Restricted; }
        catch (DirectoryNotFoundException) { return ChannelTrustSettings.Restricted; }
    }

    public async Task SaveAsync(string workspace, string agent, string channel, ChannelTrustSettings settings, CancellationToken ct)
    {
        if (settings.Mode is not ("restricted" or "verified") || settings.Bindings is null || settings.Bindings.Count > 200 ||
            (channel == Web && settings.Bindings.Count != 0) ||
            settings.Bindings.Any(b => b is null || !Valid(b.App) || !Valid(b.Organization) || !Valid(b.ExternalUser) || !Valid(b.UserId)) ||
            settings.Bindings.DistinctBy(b => (b.App, b.Organization, b.ExternalUser)).Count() != settings.Bindings.Count)
            throw new ArgumentException("渠道信任设置或身份绑定无效。");
        foreach (var id in settings.Bindings.Select(b => b.UserId).Distinct())
            if (!await CanDevelopAsync(workspace, id, ct))
                throw new ArgumentException("绑定账号必须已启用，并拥有当前工作区的写入权限。");
        await AtomicFileWriter.WriteJsonAsync(FileName(workspace, agent, channel), settings, JsonSerializerOptions.Web, ct);
    }

    private static bool Valid(string? value) => value is { Length: > 0 and <= 256 } &&
        value == value.Trim() && !value.Any(char.IsControl);

    public const string AdminRole = "admin";
    public const string MemberRole = "member";

    /// <summary>有权访问该工作区时返回其用户类型；未启用、无权限或不存在返回 null。</summary>
    private async Task<UserType?> AccessAsync(string workspace, string userId, CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        var row = await (from u in db.AppUsers from w in db.Workspaces
            where u.UserId == userId && u.IsEnabled && w.WorkspaceId == workspace && w.IsEnabled
            select new
            {
                u.UserType,
                Allowed = u.UserType == UserType.Admin || (db.WorkspaceMembers
                    .Where(m => m.UserEntityId == u.Id && m.WorkspaceEntityId == w.Id)
                    .Select(m => (WorkspaceAccessPolicy?)m.AccessLevel).FirstOrDefault() ??
                    (db.TeamMembers.Any(m => m.UserEntityId == u.Id && m.TeamEntityId == w.TeamEntityId) &&
                     w.TeamAccessPolicy > w.CompanyAccessPolicy ? w.TeamAccessPolicy : w.CompanyAccessPolicy)) >= WorkspaceAccessPolicy.Write
            })
            .SingleOrDefaultAsync(ct);
        return row is { Allowed: true } ? row.UserType : null;
    }

    private async Task<bool> CanDevelopAsync(string workspace, string userId, CancellationToken ct) =>
        await AccessAsync(workspace, userId, ct) is not null;

    /// <summary>
    /// 只回答「这条消息的发送者是谁」。渠道不决定能力：绑定了哪个 Agent，
    /// 就由该 Agent 自己的工具策略决定能做什么。
    /// </summary>
    public async Task<ChannelPrincipal?> ResolvePrincipalAsync(string workspace, string agent, ChannelSender source, CancellationToken ct)
    {
        if (!Valid(source.ExternalUser) || !Valid(source.Channel) || !Valid(source.App) || !Valid(source.Organization)) return null;
        if (source.Channel == Web && (source.App != "pudding" || source.Organization != workspace || source.Group)) return null;
        var settings = await ReadAsync(workspace, agent, source.Channel, ct);
        if (settings.Mode != "verified") return null;
        var user = source.Channel == Web
            ? source.ExternalUser
            : settings.Bindings.SingleOrDefault(b => b.App == source.App && b.Organization == source.Organization && b.ExternalUser == source.ExternalUser)?.UserId;
        if (user is null || await AccessAsync(workspace, user, ct) is not { } type) return null;
        return new(user, type == UserType.Admin ? AdminRole : MemberRole);
    }

    public async Task<string?> ResolveAsync(string workspace, string agent, ChannelSender source, CancellationToken ct) =>
        (await ResolvePrincipalAsync(workspace, agent, source, ct))?.UserId;

    public string Issue(DevelopmentTurn turn) => protector.Protect(JsonSerializer.Serialize(turn));

    public DevelopmentTurn? Verify(IReadOnlyDictionary<string, string>? metadata, string workspace, string agent,
        string session, string message, string? actor)
    {
        if (metadata?.GetValueOrDefault(Receipt) is not { Length: > 0 and <= 8192 } token) return null;
        try
        {
            var turn = JsonSerializer.Deserialize<DevelopmentTurn>(protector.Unprotect(token));
            return turn is { Source: not null } && turn.Workspace == workspace && turn.Agent == agent &&
                turn.Session == session && turn.Message == message && turn.Actor == actor ? turn : null;
        }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
    }
}
