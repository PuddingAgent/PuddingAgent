using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Configuration;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingDingTalk;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Conversation;

namespace PuddingAgent.Services;

/// <summary>One public channel session per Agent and corporate room. The normal Core worker owns execution.</summary>
internal sealed class DingTalkConversationIngress(AgentManifestCatalog agents, string agentId, string appId,
    ISessionRepository sessions, IServiceScopeFactory scopes, VisionArtifactStorageService artifacts,
    DingTalkMediaClient media, string? reviewedRoot) : IPublicGroupAgent
{
    internal const string Provenance = "gateway_dingtalk_group_source";
    internal sealed record Sender(string App, string Agent, GroupSource Source, string Message)
    {
        internal string Input => "ding-in-" + Hash(App, Source.CorporationId, Source.ConversationId, Message);
        internal string Actor => "ding-user-" + Hash(Source.CorporationId, Source.UserId);
    }

    internal static string Hash(params string[] parts) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(parts)))[..32];

    /// <summary>
    /// 主会话不存在时的创建路径。参数形态与
    /// <c>MessageGatewayIngress.EnsureMainConversationAsync</c> 保持一致：
    /// <c>SessionRole.Main</c> + <c>PrincipalKind="agent"</c>。
    /// </summary>
    private async Task<SessionRecord> CreateMainSessionAsync(
        AgentInstanceManifest owner, string workspace, CancellationToken ct)
    {
        var created = new SessionRecord
        {
            SessionId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspace,
            AgentInstanceId = agentId,
            AgentTemplateId = owner.TemplateId,
            ChannelId = "admin",
            OwnerUserId = "admin",
            SessionType = SessionType.ServiceSession,
            SessionRole = SessionRole.Main,
            PrincipalKind = "agent",
            PrincipalId = agentId,
            Status = SessionStatus.Active,
            Title = string.IsNullOrWhiteSpace(owner.DisplayName) ? "主线" : $"{owner.DisplayName} 主线",
        };
        await sessions.CreateAsync(created, ct);
        return created;
    }

    public async Task<GroupReply> ReplyAsync(PublicQuestion q, CancellationToken ct)
    {
        var owner = await agents.GetAsync(agentId, ct);
        if (owner is not { IsEnabled: true, IsFrozen: false } || q.Source.PuddingGroup != "guest")
            throw new UnauthorizedAccessException();
        var sender = new Sender(appId, agentId, q.Source, q.MessageId);
        var workspace = owner.WorkspaceId;
        using var scope = scopes.CreateScope();
        var trust = scope.ServiceProvider.GetService<ChannelDevelopmentTrust>();
        var source = new ChannelSender(ChannelDevelopmentTrust.DingTalk, appId, q.Source.CorporationId, q.Source.UserId, true);
        var trustUser = trust == null ? null : await trust.ResolveAsync(workspace, agentId, source, ct);
        // 单一会话：钉钉群消息同样汇入该 Agent 的唯一主会话，渠道不决定会话。
        var mainSession = await sessions.FindMainAsync(workspace, "agent", agentId, ct)
            ?? await CreateMainSessionAsync(owner, workspace, ct);
        var session = mainSession.SessionId;
        var actor = sender.Actor;
        DevelopmentTurn? development = trustUser == null
            ? null
            : new DevelopmentTurn(workspace, agentId, session, sender.Input, actor, trustUser, source);

        var images = new Dictionary<string, GroupImage>(StringComparer.Ordinal);
        var parts = new List<ContentPart>();
        if (q.Text.Length > 0) parts.Add(new() { Type = "text", Text = q.Text });
        foreach (var code in q.ImageDownloadCodes)
        {
            var bytes = await media.DownloadAsync(code, ct);
            var info = VisionImageInspector.InspectPrefix(bytes);
            if (info is null || info.MimeType is not ("image/jpeg" or "image/png") ||
                (long)info.Width * info.Height > 32_000_000 || Math.Max(info.Width, info.Height) > VisionImageInspector.MaxImageEdgePixels)
                return new("请使用每张不超过 4 MiB 的脱敏 PNG 或 JPEG 截图。本次图片未提交到会话。");
            var id = "vision-" + Hash(session, sender.Input, Convert.ToHexString(SHA256.HashData(bytes)));
            if (!images.TryAdd(id, new(bytes, info.MimeType))) continue;
            using var stream = new MemoryStream(bytes, writable: false);
            await artifacts.SaveIdempotentAsync(workspace, id, stream, info.MimeType, info.Width, info.Height,
                q.Source.SentAt.ToUnixTimeMilliseconds(), ct);
            parts.Add(new() { Type = "image", ArtifactId = id, Detail = "original" });
        }

        var submit = scope.ServiceProvider.GetRequiredService<ISubmitTurnHandler>();
        var metadata = development == null
            ? new Dictionary<string, string> { [Provenance] = JsonSerializer.Serialize(sender),
                [MessageGatewayMetadata.ChannelType] = "dingtalk", [MessageGatewayMetadata.ExternalUserId] = q.Source.UserId }
            : new Dictionary<string, string> { [ChannelDevelopmentTrust.Receipt] =
                scope.ServiceProvider.GetRequiredService<ChannelDevelopmentTrust>().Issue(development) };
        var result = await submit.HandleAsync(new SubmitTurnCommand(session, workspace, sender.Actor, sender.Input,
            sender.Input, new() { Type = "agent", AgentIds = [agentId] }, parts, metadata)
            { IsTrustedGatewayIngress = true }, ct);
        var turn = result.TurnIds.Single();
        // 单一会话：不再返回「请去工作台查看」的状态提示，正文由回复投影直接回到渠道。
        await sessions.UpdateAsync(mainSession with { LastActiveAt = q.Source.ReceivedAt }, ct);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            var terminal = await db.ConversationEvents.AsNoTracking().Where(e => e.ConversationId == session &&
                e.TurnId == turn && (e.Type == ConversationEventTypes.TurnCompleted ||
                e.Type == ConversationEventTypes.TurnCancelled || e.Type == ConversationEventTypes.TurnFailed))
                .Select(e => new { e.Type, e.Payload }).SingleOrDefaultAsync(deadline.Token);
            if (terminal is not null)
            {
                if (terminal.Type != ConversationEventTypes.TurnCompleted) return new("鲸鲸本轮未完成。可在工作台的钉钉群会话中查看状态。");
                using var payload = JsonDocument.Parse(terminal.Payload);
                var text = payload.RootElement.GetProperty("reply").GetString() ?? "";
                if (AgentReplyImageDirective.Parse(text).Items.Any(i => i.Reference == DingTalkPublicMedia.LogoId)
                    && await DingTalkPublicMedia.ReadLogoAsync(reviewedRoot, ct) is { } logo)
                {
                    using var file = new MemoryStream(logo.Bytes, writable: false);
                    await artifacts.SaveIdempotentAsync(workspace, DingTalkPublicMedia.LogoId, file, logo.MimeType, ct: ct);
                    images[DingTalkPublicMedia.LogoId] = logo;
                }
                return ProjectReply(text, images);
            }
            await Task.Delay(350, deadline.Token);
        }
    }

    internal static GroupReply ProjectReply(string text, IReadOnlyDictionary<string, GroupImage> images)
    {
        var projected = AgentReplyImageDirective.Parse(text);
        if (!projected.HasImages) return new(text);
        var outgoing = new List<GroupImage>();
        foreach (var directive in projected.Items.Reverse())
        {
            // The model cannot nominate another conversation's artifact, a URL, or a local file.
            if (!images.TryGetValue(directive.Reference, out var image)) return new("这张图片不在本轮允许发送的附件中。");
            outgoing.Insert(0, image);
            text = text.Remove(directive.MatchIndex, directive.MatchLength);
        }
        return new(text.Trim(), outgoing);
    }
}

internal sealed class DingTalkSubmitGuard(SubmitTurnHandler next) : ISubmitTurnHandler
{
    public Task<AcceptanceResult> HandleAsync(SubmitTurnCommand command, CancellationToken ct)
    {
        Validate(command);
        return next.HandleAsync(command, ct);
    }

    internal static void Validate(SubmitTurnCommand command)
    {
        // 单一会话后，钉钉消息不再进入渠道自有会话（旧 ding-group-* 前缀），
        // 因此不再用 ConversationId 反推房间归属；只校验消息来源的完整性。
        var source = command.Metadata?.GetValueOrDefault(DingTalkConversationIngress.Provenance);
        // 非可信网关提交且无钉钉来源：与钉钉无关，放行。
        if (!command.IsTrustedGatewayIngress && source is null) return;
        // 可信网关提交必须携带可验证的钉钉来源（缺失即拒绝，不能靠会话前缀兜底）。
        if (command.IsTrustedGatewayIngress && source is not null &&
            JsonSerializer.Deserialize<DingTalkConversationIngress.Sender>(source) is { Source: not null } sender &&
            sender.Input == command.ClientRequestId &&
            sender.Input == command.ClientMessageId &&
            sender.Actor == command.UserId &&
            command.Recipients.AgentIds is [var recipient] && recipient == sender.Agent) return;
        throw new UnauthorizedAccessException("DingTalk group turns require verified channel ingress.");
    }
}
