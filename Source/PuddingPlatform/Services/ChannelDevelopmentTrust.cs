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

// Channel is an instance ID. Organization pins the provider tenant, independently of a display name.
public sealed record ChannelSender(string Channel, string App, string Organization, string ExternalUser, bool Group);
public sealed record DevelopmentTurn(string Workspace, string Agent, string Session, string Message,
    string Actor, string User, ChannelSender Source);

public sealed class ChannelDevelopmentTrust(PuddingDataPaths paths, IDataProtectionProvider protection,
    IDbContextFactory<PlatformDbContext> databases)
{
    public const string Web = "web-chat";
    public const string DingTalk = "dingtalk-group";
    public const string Receipt = "channel_development_receipt";
    public const string PrivatePrefix = "bound-channel-";
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

    private async Task<bool> CanDevelopAsync(string workspace, string userId, CancellationToken ct)
    {
        await using var db = await databases.CreateDbContextAsync(ct);
        return await (from u in db.AppUsers from w in db.Workspaces
            where u.UserId == userId && u.IsEnabled && w.WorkspaceId == workspace && w.IsEnabled
            select u.UserType == UserType.Admin || (db.WorkspaceMembers
                .Where(m => m.UserEntityId == u.Id && m.WorkspaceEntityId == w.Id)
                .Select(m => (WorkspaceAccessPolicy?)m.AccessLevel).FirstOrDefault() ??
                (db.TeamMembers.Any(m => m.UserEntityId == u.Id && m.TeamEntityId == w.TeamEntityId) &&
                 w.TeamAccessPolicy > w.CompanyAccessPolicy ? w.TeamAccessPolicy : w.CompanyAccessPolicy)) >= WorkspaceAccessPolicy.Write)
            .SingleOrDefaultAsync(ct);
    }

    public async Task<string?> ResolveAsync(string workspace, string agent, ChannelSender source, CancellationToken ct)
    {
        if (!Valid(source.ExternalUser) || !Valid(source.Channel) || !Valid(source.App) || !Valid(source.Organization)) return null;
        if (source.Channel == Web && (source.App != "pudding" || source.Organization != workspace || source.Group)) return null;
        var settings = await ReadAsync(workspace, agent, source.Channel, ct);
        if (settings.Mode != "verified") return null;
        var user = source.Channel == Web
            ? source.ExternalUser
            : settings.Bindings.SingleOrDefault(b => b.App == source.App && b.Organization == source.Organization && b.ExternalUser == source.ExternalUser)?.UserId;
        return user != null && await CanDevelopAsync(workspace, user, ct) ? user : null;
    }

    public static string PrivateSession(string workspace, string agent, ChannelSender source, string user, string room) =>
        PrivatePrefix + Digest(workspace, agent, source.Channel, source.App, source.Organization, source.ExternalUser, user, room)[..32];

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

    public static CapabilityPolicy Apply(CapabilityPolicy policy, bool verified)
    {
        var shell = verified && policy.RequiresGrantToolNames.Any(t => t is "shell" or "terminal_start" or "terminal_execute");
        return policy with { AllowShellExecution = shell };
    }

    public static CapabilityPolicy ExternalGuest(CapabilityPolicy policy)
    {
        var selected = policy.AllowedToolNames.Count > 0 ? policy.AllowedToolNames : policy.GetAllEffectiveToolNames().ToArray();
        var allowed = selected.Where(t => t is "slack_lookup_member" or "get_current_time").ToArray();
        return new CapabilityPolicy { EnforceToolAllowlist = true, AllowedToolNames = allowed, DefaultToolNames = allowed,
            AllowNetworkAccess = policy.AllowNetworkAccess && allowed.Contains("slack_lookup_member") };
    }
}
