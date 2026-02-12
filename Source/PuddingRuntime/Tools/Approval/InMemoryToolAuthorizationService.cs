using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// In-memory runtime authorization store for high-risk tools.
/// It is intentionally scoped behind <see cref="IToolAuthorizationService"/> so permanent
/// grants can later move to a database without changing tool execution code.
/// </summary>
public sealed class InMemoryToolAuthorizationService : IToolAuthorizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<string, ToolGrant> _grants = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly string _permanentGrantFilePath;
    private readonly object _fileLock = new();
    private readonly ILogger<InMemoryToolAuthorizationService> _logger;

    public InMemoryToolAuthorizationService(
        TimeProvider? timeProvider = null,
        PuddingDataPaths? dataPaths = null,
        ILogger<InMemoryToolAuthorizationService>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryToolAuthorizationService>.Instance;
        var runtimeRoot = dataPaths?.RuntimeRoot
            ?? Path.Combine(AppContext.BaseDirectory, "data", "runtime");
        _permanentGrantFilePath = Path.Combine(runtimeRoot, "tool-authorizations.json");
        LoadPermanentGrants();
    }

    public Task<ToolAuthorizationCommandResult> ApplyCommandAsync(
        ToolAuthorizationCommand command,
        ToolAuthorizationContext context,
        CancellationToken ct = default)
    {
        var normalizedContext = context with
        {
            ToolId = ToolAuthorizationDefaults.NormalizeToolId(command.ToolId),
        };

        if (command.Action == ToolAuthorizationAction.Revoke)
        {
            var removed = RemoveMatchingGrants(normalizedContext);
            _logger.LogInformation(
                "[ToolAuth] Revoke tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} user={UserId} removed={Removed}",
                normalizedContext.ToolId,
                normalizedContext.WorkspaceId,
                normalizedContext.SessionId,
                normalizedContext.AgentInstanceId,
                normalizedContext.UserId,
                removed);
            SavePermanentGrants();
            return Task.FromResult(new ToolAuthorizationCommandResult
            {
                Handled = true,
                Message = removed == 0
                    ? $"No active authorization found for tool '{normalizedContext.ToolId}'."
                    : $"Authorization revoked for tool '{normalizedContext.ToolId}'.",
            });
        }

        if (command.Action == ToolAuthorizationAction.Deny)
        {
            RemoveMatchingGrants(normalizedContext);
            _logger.LogInformation(
                "[ToolAuth] Deny tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} user={UserId}",
                normalizedContext.ToolId,
                normalizedContext.WorkspaceId,
                normalizedContext.SessionId,
                normalizedContext.AgentInstanceId,
                normalizedContext.UserId);
            SavePermanentGrants();
            return Task.FromResult(new ToolAuthorizationCommandResult
            {
                Handled = true,
                Message = $"Authorization denied for tool '{normalizedContext.ToolId}'.",
            });
        }

        var now = _timeProvider.GetUtcNow();
        var grant = new ToolGrant
        {
            WorkspaceId = normalizedContext.WorkspaceId,
            AgentInstanceId = normalizedContext.AgentInstanceId,
            SessionId = command.Scope is ToolAuthorizationScope.Once or ToolAuthorizationScope.Session
                ? normalizedContext.SessionId
                : null,
            UserId = normalizedContext.UserId,
            ToolId = normalizedContext.ToolId,
            Scope = command.Scope,
            ExpiresAtUtc = command.Scope == ToolAuthorizationScope.Timed
                ? now.Add(command.Duration)
                : null,
            RemainingUses = command.Scope == ToolAuthorizationScope.Once ? 1 : null,
            CreatedAtUtc = now,
        };

        _grants[BuildGrantKey(grant)] = grant;
        _logger.LogInformation(
            "[ToolAuth] Grant tool={ToolId} scope={Scope} workspace={WorkspaceId} session={SessionId} grantSession={GrantSessionId} agent={AgentInstanceId} user={UserId} expiresAt={ExpiresAtUtc} remainingUses={RemainingUses}",
            grant.ToolId,
            grant.Scope,
            grant.WorkspaceId,
            normalizedContext.SessionId,
            grant.SessionId ?? "*",
            grant.AgentInstanceId,
            grant.UserId,
            grant.ExpiresAtUtc,
            grant.RemainingUses);
        if (grant.Scope == ToolAuthorizationScope.Permanent)
            SavePermanentGrants();

        return Task.FromResult(new ToolAuthorizationCommandResult
        {
            Handled = true,
            Message = BuildGrantMessage(grant, command.Duration),
        });
    }

    public Task<ToolAuthorizationCheckResult> CheckAsync(
        ToolAuthorizationContext context,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        var normalizedContext = context with
        {
            ToolId = ToolAuthorizationDefaults.NormalizeToolId(context.ToolId),
        };

        var now = _timeProvider.GetUtcNow();
        var candidates = EnumerateCandidateGrants(normalizedContext)
            .OrderBy(g => g.Scope)
            .ToArray();

        _logger.LogInformation(
            "[ToolAuth] Check tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} user={UserId} candidates={CandidateCount} argsHash={ArgumentsHash}",
            normalizedContext.ToolId,
            normalizedContext.WorkspaceId,
            normalizedContext.SessionId,
            normalizedContext.AgentInstanceId,
            normalizedContext.UserId,
            candidates.Length,
            normalizedContext.ArgumentsHash ?? "");

        foreach (var grant in candidates)
        {
            var key = BuildGrantKey(grant);
            if (grant.ExpiresAtUtc is not null && grant.ExpiresAtUtc <= now)
            {
                _grants.TryRemove(key, out _);
                _logger.LogInformation(
                    "[ToolAuth] CandidateExpired tool={ToolId} scope={Scope} grantSession={GrantSessionId} expiresAt={ExpiresAtUtc}",
                    grant.ToolId,
                    grant.Scope,
                    grant.SessionId ?? "*",
                    grant.ExpiresAtUtc);
                continue;
            }

            if (grant.Scope == ToolAuthorizationScope.Once)
            {
                if (grant.RemainingUses is null or <= 0)
                {
                    _grants.TryRemove(key, out _);
                    continue;
                }

                _grants.TryRemove(key, out _);
                _logger.LogInformation(
                    "[ToolAuth] AuthorizedOnce tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} user={UserId}",
                    normalizedContext.ToolId,
                    normalizedContext.WorkspaceId,
                    normalizedContext.SessionId,
                    normalizedContext.AgentInstanceId,
                    normalizedContext.UserId);
                return Task.FromResult(new ToolAuthorizationCheckResult
                {
                    IsAuthorized = true,
                    Message = $"One-time authorization consumed for tool '{normalizedContext.ToolId}'.",
                });
            }

            _logger.LogInformation(
                "[ToolAuth] Authorized tool={ToolId} scope={Scope} workspace={WorkspaceId} session={SessionId} grantSession={GrantSessionId} agent={AgentInstanceId} user={UserId}",
                normalizedContext.ToolId,
                grant.Scope,
                normalizedContext.WorkspaceId,
                normalizedContext.SessionId,
                grant.SessionId ?? "*",
                normalizedContext.AgentInstanceId,
                normalizedContext.UserId);
            return Task.FromResult(new ToolAuthorizationCheckResult
            {
                IsAuthorized = true,
                Message = $"Authorization found for tool '{normalizedContext.ToolId}'.",
            });
        }

        _logger.LogWarning(
            "[ToolAuth] Required tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} user={UserId}",
            normalizedContext.ToolId,
            normalizedContext.WorkspaceId,
            normalizedContext.SessionId,
            normalizedContext.AgentInstanceId,
            normalizedContext.UserId);
        return Task.FromResult(new ToolAuthorizationCheckResult
        {
            IsAuthorized = false,
            Message = BuildRequiredMessage(normalizedContext.ToolId, descriptor),
        });
    }

    public string BuildRequiredMessage(string toolId, ToolDescriptor? descriptor = null)
        => ToolAuthorizationDefaults.BuildRequiredMessage(toolId, descriptor);

    private IEnumerable<ToolGrant> EnumerateCandidateGrants(ToolAuthorizationContext context)
    {
        foreach (var grant in _grants.Values)
        {
            if (!string.Equals(grant.WorkspaceId, context.WorkspaceId, StringComparison.Ordinal)
                || !string.Equals(grant.AgentInstanceId, context.AgentInstanceId, StringComparison.Ordinal)
                || !string.Equals(grant.UserId, context.UserId, StringComparison.Ordinal)
                || !string.Equals(grant.ToolId, context.ToolId, StringComparison.Ordinal))
            {
                continue;
            }

            if (grant.SessionId is not null
                && !string.Equals(grant.SessionId, context.SessionId, StringComparison.Ordinal))
            {
                continue;
            }

            yield return grant;
        }
    }

    private int RemoveMatchingGrants(ToolAuthorizationContext context)
    {
        var removed = 0;
        foreach (var grant in EnumerateCandidateGrants(context).ToArray())
        {
            if (_grants.TryRemove(BuildGrantKey(grant), out _))
                removed++;
        }

        return removed;
    }

    private static string BuildGrantMessage(ToolGrant grant, TimeSpan duration)
    {
        return grant.Scope switch
        {
            ToolAuthorizationScope.Once =>
                $"Authorization granted for tool '{grant.ToolId}' once in this session.",
            ToolAuthorizationScope.Session =>
                $"Authorization granted for tool '{grant.ToolId}' for this session.",
            ToolAuthorizationScope.Permanent =>
                $"Authorization granted for tool '{grant.ToolId}' permanently for this user and agent.",
            _ =>
                $"Authorization granted for tool '{grant.ToolId}' for {FormatDuration(duration)}.",
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1 && duration.TotalMinutes % 60 == 0)
            return $"{(int)duration.TotalHours}h";

        return $"{(int)Math.Ceiling(duration.TotalMinutes)}m";
    }

    private static string BuildGrantKey(ToolGrant grant)
        => string.Join('\u001f',
            grant.WorkspaceId,
            grant.AgentInstanceId,
            grant.SessionId ?? "*",
            grant.UserId,
            grant.ToolId,
            grant.Scope.ToString());

    private void LoadPermanentGrants()
    {
        lock (_fileLock)
        {
            if (!File.Exists(_permanentGrantFilePath))
                return;

            try
            {
                var json = File.ReadAllText(_permanentGrantFilePath);
                var file = JsonSerializer.Deserialize<PermanentGrantFile>(json, JsonOptions);
                if (file?.Grants is null)
                    return;

                foreach (var grant in file.Grants.Where(g => g.Scope == ToolAuthorizationScope.Permanent))
                {
                    _grants[BuildGrantKey(grant)] = grant;
                }
            }
            catch
            {
                // Authorization persistence is best-effort; unreadable files are ignored
                // so high-risk tools fall back to requiring explicit authorization again.
            }
        }
    }

    private void SavePermanentGrants()
    {
        lock (_fileLock)
        {
            var grants = _grants.Values
                .Where(g => g.Scope == ToolAuthorizationScope.Permanent)
                .OrderBy(g => g.WorkspaceId, StringComparer.Ordinal)
                .ThenBy(g => g.AgentInstanceId, StringComparer.Ordinal)
                .ThenBy(g => g.UserId, StringComparer.Ordinal)
                .ThenBy(g => g.ToolId, StringComparer.Ordinal)
                .ToArray();

            Directory.CreateDirectory(Path.GetDirectoryName(_permanentGrantFilePath)!);
            var json = JsonSerializer.Serialize(new PermanentGrantFile
            {
                Version = 1,
                Grants = grants,
            }, JsonOptions);
            File.WriteAllText(_permanentGrantFilePath, json);
        }
    }

    private sealed record PermanentGrantFile
    {
        public int Version { get; init; } = 1;
        public IReadOnlyList<ToolGrant> Grants { get; init; } = [];
    }

    private sealed record ToolGrant
    {
        public required string WorkspaceId { get; init; }
        public required string AgentInstanceId { get; init; }
        public string? SessionId { get; init; }
        public required string UserId { get; init; }
        public required string ToolId { get; init; }
        public required ToolAuthorizationScope Scope { get; init; }
        public DateTimeOffset? ExpiresAtUtc { get; init; }
        public int? RemainingUses { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
    }
}
