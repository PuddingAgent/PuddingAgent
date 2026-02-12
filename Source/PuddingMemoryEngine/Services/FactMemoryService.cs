using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine.Services;

public enum MemoryFactStatus
{
    Pending,
    Accepted,
    Rejected,
    Superseded,
    Archived,
}

public enum FactFreshnessDecayKind
{
    Stable,
    Exponential,
    Linear,
    Step,
    Realtime,
}

public enum FactFreshnessStatus
{
    Fresh,
    Stale,
    Expired,
}

public sealed record MemorySpaceRecord(
    string MemorySpaceId,
    string WorkspaceId,
    string AgentId,
    string Name,
    string Status);

public sealed record MemoryFactRecord(
    string FactId,
    string WorkspaceId,
    string AgentId,
    string MemorySpaceId,
    string Statement,
    string FactType,
    double Confidence,
    MemoryFactStatus Status);

public sealed record FactFreshnessResult(double Score, FactFreshnessStatus Status);

public sealed record FactContextMatchResult(double Score, IReadOnlyList<string> ConflictingKeys);

public sealed class FactFreshnessInput
{
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidTo { get; init; }
    public long? HalfLifeSeconds { get; init; }
    public FactFreshnessDecayKind DecayKind { get; init; } = FactFreshnessDecayKind.Stable;
    public double StaleThreshold { get; init; } = 0.5;
    public double ExpiredThreshold { get; init; } = 0.1;
}

public sealed class FactContextMatchOptions
{
    public IReadOnlyList<string> RequiredKeys { get; init; } = [];
    public IReadOnlyDictionary<string, double> WeightedKeys { get; init; } = new Dictionary<string, double>();
}

public sealed class CreateFactRequest
{
    public string Statement { get; init; } = string.Empty;
    public string? StructuredPayloadJson { get; init; }
    public string FactType { get; init; } = string.Empty;
    public double Confidence { get; init; }
    public MemoryFactStatus Status { get; init; } = MemoryFactStatus.Pending;
    public string? ContextJson { get; init; }
    public CreateFactFreshnessRequest? Freshness { get; init; }
    public IReadOnlyList<CreateFactEvidenceRequest> Evidence { get; init; } = [];
    public IReadOnlyList<CreateFactEntityMentionRequest> EntityMentions { get; init; } = [];
    public IReadOnlyList<CreateFactAssociationRequest> Associations { get; init; } = [];
    public string ActorType { get; init; } = string.Empty;
    public string? ActorId { get; init; }
}

public sealed class CreateFactEvidenceRequest
{
    public string SourceType { get; init; } = string.Empty;
    public string SourceId { get; init; } = string.Empty;
    public string? SourceRange { get; init; }
    public string? QuoteSummary { get; init; }
    public double Confidence { get; init; } = 1.0;
}

public sealed class CreateFactFreshnessRequest
{
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidTo { get; init; }
    public long? HalfLifeSeconds { get; init; }
    public FactFreshnessDecayKind DecayKind { get; init; } = FactFreshnessDecayKind.Stable;
    public double StaleThreshold { get; init; } = 0.5;
    public double ExpiredThreshold { get; init; } = 0.1;
    public string? RefreshHint { get; init; }
    public string? FreshnessReason { get; init; }
}

public sealed class CreateFactEntityMentionRequest
{
    public string EntityKey { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string? AliasesJson { get; init; }
    public string? PropertiesJson { get; init; }
    public double Confidence { get; init; } = 1.0;
}

public sealed class CreateFactAssociationRequest
{
    public string SourceKind { get; init; } = string.Empty;
    public string SourceKey { get; init; } = string.Empty;
    public string TargetKind { get; init; } = string.Empty;
    public string TargetKey { get; init; } = string.Empty;
    public string AssociationType { get; init; } = string.Empty;
    public double Weight { get; init; } = 1.0;
    public double Confidence { get; init; } = 1.0;
    public string? ContextJson { get; init; }
    public string? EvidenceIdsJson { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public long? HalfLifeSeconds { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// 通用事实新鲜度计算器。它只计算调用方提供的半衰期，不决定业务默认值。
/// </summary>
public static class FactFreshnessCalculator
{
    public static FactFreshnessResult Compute(DateTimeOffset now, FactFreshnessInput freshness)
    {
        if (freshness.ValidTo is not null && now > freshness.ValidTo.Value)
            return new FactFreshnessResult(0.0, FactFreshnessStatus.Expired);

        var score = freshness.DecayKind switch
        {
            FactFreshnessDecayKind.Stable => 1.0,
            FactFreshnessDecayKind.Exponential => Math.Pow(0.5, AgeSeconds(now, freshness) / RequiredHalfLife(freshness)),
            FactFreshnessDecayKind.Linear => Math.Max(0.0, 1.0 - AgeSeconds(now, freshness) / (2.0 * RequiredHalfLife(freshness))),
            FactFreshnessDecayKind.Step => ComputeStep(now, freshness),
            FactFreshnessDecayKind.Realtime => ComputeRealtime(now, freshness),
            _ => 1.0,
        };

        var status = score <= freshness.ExpiredThreshold
            ? FactFreshnessStatus.Expired
            : score <= freshness.StaleThreshold
                ? FactFreshnessStatus.Stale
                : FactFreshnessStatus.Fresh;

        return new FactFreshnessResult(score, status);
    }

    private static double ComputeStep(DateTimeOffset now, FactFreshnessInput freshness)
    {
        if (freshness.ValidTo is not null)
            return now <= freshness.ValidTo.Value ? 1.0 : 0.0;

        return AgeSeconds(now, freshness) <= RequiredHalfLife(freshness) ? 1.0 : 0.0;
    }

    private static double ComputeRealtime(DateTimeOffset now, FactFreshnessInput freshness)
    {
        var age = AgeSeconds(now, freshness);
        var halfLife = RequiredHalfLife(freshness);
        if (age > 2.0 * halfLife)
            return 0.0;

        return Math.Pow(0.5, age / halfLife);
    }

    private static double AgeSeconds(DateTimeOffset now, FactFreshnessInput freshness)
    {
        var reference = new[] { freshness.ObservedAt, freshness.LastVerifiedAt }
            .Where(value => value is not null)
            .Max();

        return reference is null ? 0.0 : Math.Max(0.0, (now - reference.Value).TotalSeconds);
    }

    private static long RequiredHalfLife(FactFreshnessInput freshness)
    {
        if (freshness.HalfLifeSeconds is > 0)
            return freshness.HalfLifeSeconds.Value;

        throw new ArgumentException("HalfLifeSeconds must be positive for this decay kind.", nameof(freshness));
    }
}

/// <summary>
/// 通用上下文匹配器。业务维度强弱由调用方通过 options 传入。
/// </summary>
public static class FactContextMatcher
{
    public static FactContextMatchResult Match(
        IReadOnlyDictionary<string, string?> queryContext,
        IReadOnlyDictionary<string, string?> factContext,
        FactContextMatchOptions options)
    {
        var conflicts = options.RequiredKeys
            .Where(key => queryContext.TryGetValue(key, out var queryValue)
                       && factContext.TryGetValue(key, out var factValue)
                       && !StringEquals(queryValue, factValue))
            .ToList();

        if (conflicts.Count > 0)
            return new FactContextMatchResult(0.0, conflicts);

        if (queryContext.Count == 0 && factContext.Count == 0)
            return new FactContextMatchResult(1.0, []);

        if (options.WeightedKeys.Count == 0)
        {
            var matched = factContext.Count(kv => queryContext.TryGetValue(kv.Key, out var value) && StringEquals(value, kv.Value));
            var score = factContext.Count == 0 ? 1.0 : (double)matched / factContext.Count;
            return new FactContextMatchResult(score, []);
        }

        var totalWeight = options.WeightedKeys.Values.Sum();
        if (totalWeight <= 0)
            return new FactContextMatchResult(1.0, []);

        var weightedMatch = options.WeightedKeys.Sum(kv =>
            queryContext.TryGetValue(kv.Key, out var queryValue)
            && factContext.TryGetValue(kv.Key, out var factValue)
            && StringEquals(queryValue, factValue)
                ? kv.Value
                : 0.0);

        var requiredBonus = options.RequiredKeys.All(key =>
            !factContext.ContainsKey(key)
            || (queryContext.TryGetValue(key, out var queryValue) && StringEquals(queryValue, factContext[key])))
                ? 0.8
                : 0.0;

        return new FactContextMatchResult(Math.Max(requiredBonus, weightedMatch / totalWeight), []);
    }

    private static bool StringEquals(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 新版事实记忆服务的最小核心切片。
/// </summary>
public sealed class FactMemoryService
{
    private const string DefaultMemorySpaceName = "默认记忆空间";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<MemoryLibraryDbContext> _dbFactory;

    public FactMemoryService(IDbContextFactory<MemoryLibraryDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<MemorySpaceRecord> EnsureDefaultMemorySpaceAsync(
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        ValidateScope(workspaceId, agentId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.MemorySpaces
            .AsNoTracking()
            .FirstOrDefaultAsync(space =>
                space.WorkspaceId == workspaceId
                && space.AgentId == agentId
                && space.Status == "active", ct);

        if (existing is not null)
            return ToRecord(existing);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var created = new MemorySpaceEntity
        {
            WorkspaceId = workspaceId,
            AgentId = agentId,
            Name = DefaultMemorySpaceName,
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.MemorySpaces.Add(created);
        await db.SaveChangesAsync(ct);

        return ToRecord(created);
    }

    public async Task<MemoryFactRecord> CreateFactAsync(
        string workspaceId,
        string agentId,
        string memorySpaceId,
        CreateFactRequest request,
        CancellationToken ct = default)
    {
        ValidateScope(workspaceId, agentId);
        if (string.IsNullOrWhiteSpace(memorySpaceId))
            throw new ArgumentException("memorySpaceId is required.", nameof(memorySpaceId));
        if (string.IsNullOrWhiteSpace(request.Statement))
            throw new ArgumentException("Statement is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.FactType))
            throw new ArgumentException("FactType is required.", nameof(request));
        if (request.Evidence.Count == 0)
            throw new ArgumentException("At least one evidence record is required.", nameof(request));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var space = await db.MemorySpaces.FirstOrDefaultAsync(s =>
            s.MemorySpaceId == memorySpaceId
            && s.WorkspaceId == workspaceId
            && s.AgentId == agentId, ct);

        if (space is null)
            throw new InvalidOperationException("MemorySpace was not found in the requested workspace and agent scope.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var status = SerializeStatus(request.Status);
        var fact = new GraphMemoryFactEntity
        {
            WorkspaceId = workspaceId,
            AgentId = agentId,
            MemorySpaceId = memorySpaceId,
            Statement = request.Statement.Trim(),
            StructuredPayloadJson = request.StructuredPayloadJson,
            FactType = request.FactType.Trim(),
            Confidence = Clamp01(request.Confidence),
            Status = status,
            CreatedByType = string.IsNullOrWhiteSpace(request.ActorType) ? "unknown" : request.ActorType.Trim(),
            CreatedById = request.ActorId,
            CreatedAt = now,
            UpdatedAt = now,
            AcceptedAt = request.Status == MemoryFactStatus.Accepted ? now : null,
            RejectedAt = request.Status == MemoryFactStatus.Rejected ? now : null,
            ArchivedAt = request.Status == MemoryFactStatus.Archived ? now : null,
        };

        db.MemoryFacts.Add(fact);
        AddEvidence(db, fact, request.Evidence, now);
        AddContext(db, fact, request.ContextJson, now);
        AddFreshness(db, fact, request.Freshness, now);
        AddMentions(db, fact, request.EntityMentions, now);
        AddAssociations(db, fact, request.Associations, now);
        db.MemoryFactRevisions.Add(new MemoryFactRevisionEntity
        {
            WorkspaceId = workspaceId,
            AgentId = agentId,
            MemorySpaceId = memorySpaceId,
            FactId = fact.FactId,
            RevisionType = "create",
            BeforeJson = null,
            AfterJson = JsonSerializer.Serialize(new
            {
                fact.FactId,
                fact.Statement,
                fact.FactType,
                fact.Confidence,
                fact.Status,
            }, JsonOptions),
            ActorType = fact.CreatedByType,
            ActorId = fact.CreatedById,
            CreatedAt = now,
        });

        await db.SaveChangesAsync(ct);

        return ToRecord(fact);
    }

    private static void AddEvidence(
        MemoryLibraryDbContext db,
        GraphMemoryFactEntity fact,
        IReadOnlyList<CreateFactEvidenceRequest> evidence,
        long now)
    {
        foreach (var item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item.SourceType) || string.IsNullOrWhiteSpace(item.SourceId))
                throw new ArgumentException("Evidence SourceType and SourceId are required.", nameof(evidence));

            db.MemoryFactEvidence.Add(new MemoryFactEvidenceEntity
            {
                WorkspaceId = fact.WorkspaceId,
                AgentId = fact.AgentId,
                MemorySpaceId = fact.MemorySpaceId,
                FactId = fact.FactId,
                SourceType = item.SourceType.Trim(),
                SourceId = item.SourceId.Trim(),
                SourceRange = item.SourceRange,
                QuoteSummary = item.QuoteSummary,
                EvidenceHash = ComputeHash($"{item.SourceType}|{item.SourceId}|{item.SourceRange}|{item.QuoteSummary}"),
                Confidence = Clamp01(item.Confidence),
                CreatedAt = now,
            });
        }
    }

    private static void AddContext(MemoryLibraryDbContext db, GraphMemoryFactEntity fact, string? contextJson, long now)
    {
        if (string.IsNullOrWhiteSpace(contextJson))
            return;

        db.MemoryFactContexts.Add(new MemoryFactContextEntity
        {
            WorkspaceId = fact.WorkspaceId,
            AgentId = fact.AgentId,
            MemorySpaceId = fact.MemorySpaceId,
            FactId = fact.FactId,
            ContextJson = contextJson,
            ContextHash = ComputeHash(contextJson),
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static void AddFreshness(
        MemoryLibraryDbContext db,
        GraphMemoryFactEntity fact,
        CreateFactFreshnessRequest? freshness,
        long now)
    {
        if (freshness is null)
            return;

        ValidateFreshness(freshness);
        db.MemoryFactFreshness.Add(new MemoryFactFreshnessEntity
        {
            WorkspaceId = fact.WorkspaceId,
            AgentId = fact.AgentId,
            MemorySpaceId = fact.MemorySpaceId,
            FactId = fact.FactId,
            ObservedAt = freshness.ObservedAt?.ToUnixTimeMilliseconds(),
            LastVerifiedAt = freshness.LastVerifiedAt?.ToUnixTimeMilliseconds(),
            ValidFrom = freshness.ValidFrom?.ToUnixTimeMilliseconds(),
            ValidTo = freshness.ValidTo?.ToUnixTimeMilliseconds(),
            HalfLifeSeconds = freshness.HalfLifeSeconds,
            DecayKind = SerializeDecayKind(freshness.DecayKind),
            StaleThreshold = freshness.StaleThreshold,
            ExpiredThreshold = freshness.ExpiredThreshold,
            RefreshHint = freshness.RefreshHint,
            FreshnessReason = freshness.FreshnessReason,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static void AddMentions(
        MemoryLibraryDbContext db,
        GraphMemoryFactEntity fact,
        IReadOnlyList<CreateFactEntityMentionRequest> mentions,
        long now)
    {
        foreach (var mention in mentions)
        {
            if (string.IsNullOrWhiteSpace(mention.EntityKey))
                throw new ArgumentException("EntityKey is required.", nameof(mentions));

            db.MemoryFactEntityMentions.Add(new MemoryFactEntityMentionEntity
            {
                WorkspaceId = fact.WorkspaceId,
                AgentId = fact.AgentId,
                MemorySpaceId = fact.MemorySpaceId,
                FactId = fact.FactId,
                EntityKey = mention.EntityKey.Trim(),
                EntityType = mention.EntityType.Trim(),
                DisplayName = mention.DisplayName.Trim(),
                Role = mention.Role.Trim(),
                AliasesJson = mention.AliasesJson,
                PropertiesJson = mention.PropertiesJson,
                Confidence = Clamp01(mention.Confidence),
                CreatedAt = now,
            });
        }
    }

    private static void AddAssociations(
        MemoryLibraryDbContext db,
        GraphMemoryFactEntity fact,
        IReadOnlyList<CreateFactAssociationRequest> associations,
        long now)
    {
        foreach (var association in associations)
        {
            if (string.IsNullOrWhiteSpace(association.SourceKind)
                || string.IsNullOrWhiteSpace(association.SourceKey)
                || string.IsNullOrWhiteSpace(association.TargetKind)
                || string.IsNullOrWhiteSpace(association.TargetKey))
            {
                throw new ArgumentException("Association source and target are required.", nameof(associations));
            }

            db.MemoryFactAssociations.Add(new MemoryFactAssociationEntity
            {
                WorkspaceId = fact.WorkspaceId,
                AgentId = fact.AgentId,
                MemorySpaceId = fact.MemorySpaceId,
                FactId = fact.FactId,
                SourceKind = association.SourceKind.Trim(),
                SourceKey = association.SourceKey.Trim(),
                TargetKind = association.TargetKind.Trim(),
                TargetKey = association.TargetKey.Trim(),
                AssociationType = association.AssociationType.Trim(),
                Weight = Clamp01(association.Weight),
                Confidence = Clamp01(association.Confidence),
                ContextJson = association.ContextJson,
                EvidenceIdsJson = association.EvidenceIdsJson,
                ObservedAt = association.ObservedAt?.ToUnixTimeMilliseconds(),
                HalfLifeSeconds = association.HalfLifeSeconds,
                Reason = association.Reason,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    private static void ValidateFreshness(CreateFactFreshnessRequest freshness)
    {
        if (freshness.StaleThreshold <= freshness.ExpiredThreshold)
            throw new ArgumentException("StaleThreshold must be greater than ExpiredThreshold.", nameof(freshness));

        if (freshness.DecayKind is FactFreshnessDecayKind.Exponential or FactFreshnessDecayKind.Linear or FactFreshnessDecayKind.Realtime
            && freshness.HalfLifeSeconds is not > 0)
        {
            throw new ArgumentException("HalfLifeSeconds must be positive for this decay kind.", nameof(freshness));
        }

        if (freshness.DecayKind == FactFreshnessDecayKind.Step
            && freshness.ValidTo is null
            && freshness.HalfLifeSeconds is not > 0)
        {
            throw new ArgumentException("Step freshness requires ValidTo or HalfLifeSeconds.", nameof(freshness));
        }
    }

    private static void ValidateScope(string workspaceId, string agentId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId is required.", nameof(workspaceId));
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId is required.", nameof(agentId));
    }

    private static MemorySpaceRecord ToRecord(MemorySpaceEntity entity)
        => new(entity.MemorySpaceId, entity.WorkspaceId, entity.AgentId, entity.Name, entity.Status);

    private static MemoryFactRecord ToRecord(GraphMemoryFactEntity entity)
        => new(
            entity.FactId,
            entity.WorkspaceId,
            entity.AgentId,
            entity.MemorySpaceId,
            entity.Statement,
            entity.FactType,
            entity.Confidence,
            ParseStatus(entity.Status));

    private static string SerializeStatus(MemoryFactStatus status)
        => status switch
        {
            MemoryFactStatus.Pending => "pending",
            MemoryFactStatus.Accepted => "accepted",
            MemoryFactStatus.Rejected => "rejected",
            MemoryFactStatus.Superseded => "superseded",
            MemoryFactStatus.Archived => "archived",
            _ => "pending",
        };

    private static MemoryFactStatus ParseStatus(string status)
        => status switch
        {
            "accepted" => MemoryFactStatus.Accepted,
            "rejected" => MemoryFactStatus.Rejected,
            "superseded" => MemoryFactStatus.Superseded,
            "archived" => MemoryFactStatus.Archived,
            _ => MemoryFactStatus.Pending,
        };

    private static string SerializeDecayKind(FactFreshnessDecayKind decayKind)
        => decayKind switch
        {
            FactFreshnessDecayKind.Exponential => "exponential",
            FactFreshnessDecayKind.Linear => "linear",
            FactFreshnessDecayKind.Step => "step",
            FactFreshnessDecayKind.Realtime => "realtime",
            _ => "stable",
        };

    private static double Clamp01(double value)
        => Math.Clamp(value, 0.0, 1.0);

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
