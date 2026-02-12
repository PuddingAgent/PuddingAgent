using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PuddingCode.Observability;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// In-memory automatic approval service for high-risk tool calls.
/// Stores approval tickets and delegates review decisions to an approval reviewer.
/// </summary>
public sealed class InMemoryToolApprovalService : IToolApprovalService
{
    private const int OnceTicketAllowedUses = 2;

    private readonly IToolApprovalReviewer _reviewer;
    private readonly IToolApprovalTicketStore _ticketStore;
    private readonly IToolApprovalAllowlistStore _allowlistStore;
    private readonly IToolApprovalAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InMemoryToolApprovalService>? _logger;
    private readonly ITelemetryMetricSink? _telemetryMetricSink;

    public InMemoryToolApprovalService(
        TimeProvider? timeProvider = null,
        ILogger<InMemoryToolApprovalService>? logger = null,
        ITelemetryMetricSink? telemetryMetricSink = null)
        : this(
            new FakeToolApprovalReviewer(),
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            new InMemoryToolApprovalAuditStore(),
            timeProvider,
            logger,
            telemetryMetricSink)
    {
    }

    public InMemoryToolApprovalService(
        IToolApprovalReviewer reviewer,
        TimeProvider? timeProvider = null,
        ILogger<InMemoryToolApprovalService>? logger = null,
        ITelemetryMetricSink? telemetryMetricSink = null)
        : this(
            reviewer,
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            new InMemoryToolApprovalAuditStore(),
            timeProvider,
            logger,
            telemetryMetricSink)
    {
    }

    public InMemoryToolApprovalService(
        IToolApprovalReviewer reviewer,
        IToolApprovalTicketStore ticketStore,
        TimeProvider? timeProvider = null,
        ILogger<InMemoryToolApprovalService>? logger = null,
        ITelemetryMetricSink? telemetryMetricSink = null)
        : this(
            reviewer,
            ticketStore,
            new InMemoryToolApprovalAllowlistStore(),
            new InMemoryToolApprovalAuditStore(),
            timeProvider,
            logger,
            telemetryMetricSink)
    {
    }

    public InMemoryToolApprovalService(
        IToolApprovalReviewer reviewer,
        IToolApprovalTicketStore ticketStore,
        IToolApprovalAllowlistStore allowlistStore,
        IToolApprovalAuditStore auditStore,
        TimeProvider? timeProvider = null,
        ILogger<InMemoryToolApprovalService>? logger = null,
        ITelemetryMetricSink? telemetryMetricSink = null)
    {
        _reviewer = reviewer;
        _ticketStore = ticketStore;
        _allowlistStore = allowlistStore;
        _auditStore = auditStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _telemetryMetricSink = telemetryMetricSink;
    }

    public async Task<ToolApprovalTicketResult> SubmitAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var startedAt = now;
        var ticketId = "tap_" + Guid.NewGuid().ToString("N");
        var normalizedToolId = ToolAuthorizationDefaults.NormalizeToolId(request.ToolId);
        var requestedDuration = request.RequestedDuration;
        _logger?.LogInformation(
            "[ToolApproval] submit started ticket={TicketId} kind={TicketKind} tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} scope={Scope} steps={StepCount} allowlistRequested={AllowlistRequested}",
            ticketId,
            request.TicketKind,
            normalizedToolId,
            identity.WorkspaceId,
            identity.SessionId,
            identity.AgentInstanceId,
            request.RequestedScope,
            request.OperationSteps.Count,
            request.RequestAllowlistRule);
        await SaveAuditAsync(new ToolApprovalAuditEvent
        {
            EventId = NewAuditEventId(),
            EventType = ToolApprovalAuditEventType.TicketSubmitted,
            WorkspaceId = identity.WorkspaceId,
            SessionId = identity.SessionId,
            AgentInstanceId = identity.AgentInstanceId,
            UserId = identity.UserId,
            ToolId = normalizedToolId,
            Command = ExtractCommand(request.RequestedArgumentsJson),
            ArgumentsJson = request.RequestedArgumentsJson,
            TicketId = ticketId,
            Reason = request.Purpose,
            CreatedAtUtc = now,
        }, ct);

        var hardDenial = BuildHardDenialReason(request);
        ToolApprovalReviewResult review;
        if (!string.IsNullOrWhiteSpace(hardDenial))
        {
            _logger?.LogWarning(
                "[ToolApproval] submit hard denied ticket={TicketId} tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} reason={Reason}",
                ticketId,
                normalizedToolId,
                identity.WorkspaceId,
                identity.SessionId,
                identity.AgentInstanceId,
                hardDenial);
            review = new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = hardDenial,
                RequiresHumanAuthorization = true,
                MissingRequirements = ["software hard safety rule"],
                RecommendedFix = "Reduce the operation risk, add backup and rollback evidence, or use explicit human authorization outside automatic approval.",
                ReviewerModel = "software-hard-safety-rules",
            };
        }
        else
        {
            try
            {
                review = await _reviewer.ReviewAsync(request, identity, descriptor, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await RecordMetricAsync(
                    "tool_approval.submit",
                    TelemetryMetricStatuses.Cancelled,
                    startedAt,
                    "Tool approval submission was cancelled.",
                    identity.WorkspaceId,
                    identity.SessionId,
                    identity.AgentInstanceId,
                    identity.UserId,
                    new Dictionary<string, string>
                    {
                        ["ticket_id"] = ticketId,
                        ["tool_id"] = normalizedToolId,
                        ["ticket_kind"] = request.TicketKind.ToString(),
                        ["requested_scope"] = request.RequestedScope.ToString(),
                        ["tool_stage"] = "review",
                    },
                    ct: CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "[ToolApproval] submit failed during review ticket={TicketId} tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId}",
                    ticketId,
                    normalizedToolId,
                    identity.WorkspaceId,
                    identity.SessionId,
                    identity.AgentInstanceId);
                await RecordMetricAsync(
                    "tool_approval.submit",
                    TelemetryMetricStatuses.Failed,
                    startedAt,
                    "Tool approval submission failed during review.",
                    identity.WorkspaceId,
                    identity.SessionId,
                    identity.AgentInstanceId,
                    identity.UserId,
                    new Dictionary<string, string>
                    {
                        ["ticket_id"] = ticketId,
                        ["tool_id"] = normalizedToolId,
                        ["ticket_kind"] = request.TicketKind.ToString(),
                        ["requested_scope"] = request.RequestedScope.ToString(),
                        ["tool_stage"] = "review",
                    },
                    errorCode: ex.GetType().Name,
                    errorMessage: ex.Message,
                    ct: CancellationToken.None);
                throw;
            }
        }
        var isApproved = review.Decision == ToolApprovalDecision.Approved;
        var grantedScope = isApproved
            ? review.AllowedScope ?? request.RequestedScope
            : request.RequestedScope;
        var grantedDuration = review.AllowedDuration ?? requestedDuration;
        var ticket = new ToolApprovalTicketRecord
        {
            TicketId = ticketId,
            Identity = identity,
            ToolId = normalizedToolId,
            Request = request,
            ArgumentsHash = ToolAuthorizationDefaults.ComputeArgumentsHash(request.RequestedArgumentsJson),
            Scope = grantedScope,
            Status = isApproved ? ToolApprovalTicketStatus.Approved : ToolApprovalTicketStatus.Denied,
            DecisionReason = review.DecisionReason,
            CreatedAtUtc = now,
            DecidedAtUtc = now,
            ExpiresAtUtc = isApproved
                           && grantedScope == ToolApprovalScope.Timed
                           && grantedDuration.HasValue
                           && grantedDuration.Value > TimeSpan.Zero
                ? now.Add(grantedDuration.Value)
                : null,
            RemainingUses = isApproved && grantedScope == ToolApprovalScope.Once ? OnceTicketAllowedUses : null,
        };
        await _ticketStore.SaveAsync(ticket, ct);
        var allowlistRuleIds = isApproved
            ? await CreateAllowlistRulesFromApprovedTicketAsync(ticket, request, identity, normalizedToolId, review, now, ct)
            : [];
        var allowlistRuleId = allowlistRuleIds.FirstOrDefault();
        await SaveAuditAsync(new ToolApprovalAuditEvent
        {
            EventId = NewAuditEventId(),
            EventType = review.Decision switch
            {
                ToolApprovalDecision.Approved => ToolApprovalAuditEventType.TicketApproved,
                ToolApprovalDecision.Denied => ToolApprovalAuditEventType.TicketDenied,
                _ => ToolApprovalAuditEventType.TicketNeedHuman,
            },
            WorkspaceId = identity.WorkspaceId,
            SessionId = identity.SessionId,
            AgentInstanceId = identity.AgentInstanceId,
            UserId = identity.UserId,
            ToolId = normalizedToolId,
            Command = ExtractCommand(request.RequestedArgumentsJson),
            ArgumentsJson = request.RequestedArgumentsJson,
            TicketId = ticketId,
            Decision = review.Decision,
            ReviewerModel = review.ReviewerModel,
            Reason = review.DecisionReason,
            CreatedAtUtc = now,
        }, ct);

        _logger?.Log(
            isApproved ? LogLevel.Information : LogLevel.Warning,
            "[ToolApproval] submit decision ticket={TicketId} decision={Decision} status={Status} tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} reviewerModel={ReviewerModel} grantedScope={GrantedScope} allowlistRule={AllowlistRuleId} durationMs={DurationMs}",
            ticketId,
            review.Decision,
            ticket.Status,
            normalizedToolId,
            identity.WorkspaceId,
            identity.SessionId,
            identity.AgentInstanceId,
            review.ReviewerModel,
            grantedScope,
            allowlistRuleId,
            DurationMs(startedAt));
        await RecordMetricAsync(
            "tool_approval.submit",
            review.Decision switch
            {
                ToolApprovalDecision.Approved => TelemetryMetricStatuses.Succeeded,
                ToolApprovalDecision.Denied => TelemetryMetricStatuses.Failed,
                _ => TelemetryMetricStatuses.Recorded,
            },
            startedAt,
            $"Tool approval submission completed with decision {review.Decision}.",
            identity.WorkspaceId,
            identity.SessionId,
            identity.AgentInstanceId,
            identity.UserId,
            new Dictionary<string, string>
            {
                ["ticket_id"] = ticketId,
                ["tool_id"] = normalizedToolId,
                ["ticket_kind"] = request.TicketKind.ToString(),
                ["requested_scope"] = request.RequestedScope.ToString(),
                ["granted_scope"] = grantedScope.ToString(),
                ["decision"] = review.Decision.ToString(),
                ["status"] = ticket.Status.ToString(),
                ["reviewer_model"] = review.ReviewerModel ?? "",
                ["operation_step_count"] = request.OperationSteps.Count.ToString(),
                ["request_allowlist_rule"] = request.RequestAllowlistRule.ToString(),
                ["allowlist_rule_id"] = allowlistRuleId ?? "",
                ["allowlist_rule_count"] = allowlistRuleIds.Count.ToString(),
            },
            ct: ct);

        return ToResult(ticket, review.Decision, allowlistRuleId);
    }

    public async Task<ToolApprovalCheckResult> CheckAsync(
        ToolApprovalExecutionRequest request,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        var normalizedToolId = ToolAuthorizationDefaults.NormalizeToolId(request.ToolId);
        var now = _timeProvider.GetUtcNow();
        var startedAt = now;
        var allowlist = await FindMatchingAllowlistRuleAsync(request, normalizedToolId, now, ct);
        if (allowlist is not null)
        {
            _logger?.LogInformation(
                "[ToolApproval] check allowed source=allowlist rule={AllowlistRuleId} tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} durationMs={DurationMs}",
                allowlist.RuleId,
                normalizedToolId,
                request.WorkspaceId,
                request.SessionId,
                request.AgentInstanceId,
                DurationMs(startedAt));
            await RecordCheckMetricAsync(
                request,
                normalizedToolId,
                TelemetryMetricStatuses.Succeeded,
                startedAt,
                "Tool approval check allowed by allowlist.",
                new Dictionary<string, string>
                {
                    ["approval_source"] = "allowlist",
                    ["allowlist_rule_id"] = allowlist.RuleId,
                    ["allowlist_source"] = allowlist.Source.ToString(),
                    ["allowlist_rule_hit_count"] = allowlist.HitCount.ToString(),
                    ["allowlist_rule_command"] = Truncate(allowlist.Command, 220),
                    ["actual_command"] = Truncate(ExtractCommand(request.ActualArgumentsJson), 220),
                    ["decision"] = "allowed",
                },
                ct);
            return new ToolApprovalCheckResult
            {
                IsApproved = true,
                AllowlistRuleId = allowlist.RuleId,
                ApprovalSource = allowlist.Source.ToString(),
                Message = $"Automatic approval allowlist rule '{allowlist.RuleId}' allows tool '{normalizedToolId}'.",
            };
        }

        var builtInPolicyApproval = TryGetBuiltInPolicyApproval(request, descriptor, normalizedToolId);
        if (builtInPolicyApproval is not null)
        {
            var policyRule = await RecordBuiltInPolicyAllowlistHitAsync(
                request,
                normalizedToolId,
                builtInPolicyApproval,
                now,
                ct);

            _logger?.LogInformation(
                "[ToolApproval] check allowed source=built_in_policy rule={RuleId} tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} durationMs={DurationMs}",
                builtInPolicyApproval.RuleId,
                normalizedToolId,
                request.WorkspaceId,
                request.SessionId,
                request.AgentInstanceId,
                DurationMs(startedAt));
            await RecordCheckMetricAsync(
                request,
                normalizedToolId,
                TelemetryMetricStatuses.Succeeded,
                startedAt,
                "Tool approval check allowed by built-in tool policy.",
                new Dictionary<string, string>
                {
                    ["approval_source"] = "built_in_policy",
                    ["allowlist_rule_id"] = builtInPolicyApproval.RuleId,
                    ["allowlist_rule_hit_count"] = policyRule.HitCount.ToString(),
                    ["actual_command"] = Truncate(ExtractCommand(request.ActualArgumentsJson), 220),
                    ["decision"] = "allowed",
                },
                ct);
            return new ToolApprovalCheckResult
            {
                IsApproved = true,
                AllowlistRuleId = builtInPolicyApproval.RuleId,
                ApprovalSource = "BuiltInPolicy",
                Message = builtInPolicyApproval.Reason,
            };
        }

        var tickets = await _ticketStore.ListAsync(ct);
        foreach (var ticket in tickets.Where(t => t.Status == ToolApprovalTicketStatus.Approved))
        {
            if (!MatchesBaseIdentity(ticket, request, normalizedToolId))
                continue;

            if (ticket.Scope == ToolApprovalScope.Session
                && !string.Equals(ticket.Identity.SessionId, request.SessionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (ticket.ExpiresAtUtc is not null && ticket.ExpiresAtUtc <= now)
            {
                await _ticketStore.SaveAsync(ticket with { Status = ToolApprovalTicketStatus.Expired }, ct);
                continue;
            }

            if (!MatchesApprovedOperation(ticket, request))
                continue;

            var eventType = ToolApprovalAuditEventType.TicketMatched;
            var isVerificationReplay = false;
            if (ticket.Scope == ToolApprovalScope.Once)
            {
                if (ticket.RemainingUses is null or <= 0)
                {
                    await _ticketStore.SaveAsync(ticket with { Status = ToolApprovalTicketStatus.Consumed }, ct);
                    continue;
                }

                isVerificationReplay = ticket.RemainingUses < OnceTicketAllowedUses;
                var remainingUses = Math.Max(0, ticket.RemainingUses.Value - 1);
                var updatedTicket = ticket with
                {
                    Status = remainingUses == 0 ? ToolApprovalTicketStatus.Consumed : ToolApprovalTicketStatus.Approved,
                    RemainingUses = remainingUses,
                    ConsumedAtUtc = remainingUses == 0 ? now : ticket.ConsumedAtUtc,
                };
                await _ticketStore.SaveAsync(updatedTicket, ct);
                eventType = remainingUses == 0
                    ? ToolApprovalAuditEventType.TicketConsumed
                    : ToolApprovalAuditEventType.TicketMatched;
            }

            await SaveAuditAsync(new ToolApprovalAuditEvent
            {
                EventId = NewAuditEventId(),
                EventType = eventType,
                WorkspaceId = request.WorkspaceId,
                SessionId = request.SessionId,
                AgentInstanceId = request.AgentInstanceId,
                UserId = request.UserId,
                ToolId = normalizedToolId,
                Command = ExtractCommand(request.ActualArgumentsJson),
                ArgumentsJson = request.ActualArgumentsJson,
                TicketId = ticket.TicketId,
                Reason = eventType == ToolApprovalAuditEventType.TicketConsumed
                    ? "Automatic approval once ticket matched and was consumed."
                    : isVerificationReplay
                        ? "Automatic approval once ticket matched as a verification replay."
                    : "Automatic approval ticket matched the actual tool call.",
                CreatedAtUtc = now,
            }, ct);

            _logger?.LogInformation(
                "[ToolApproval] check allowed source=ticket ticket={TicketId} event={EventType} tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} durationMs={DurationMs}",
                ticket.TicketId,
                eventType,
                normalizedToolId,
                request.WorkspaceId,
                request.SessionId,
                request.AgentInstanceId,
                DurationMs(startedAt));
            await RecordCheckMetricAsync(
                request,
                normalizedToolId,
                TelemetryMetricStatuses.Succeeded,
                startedAt,
                "Tool approval check allowed by ticket.",
                new Dictionary<string, string>
                {
                    ["approval_source"] = "ticket",
                    ["ticket_id"] = ticket.TicketId,
                    ["ticket_scope"] = ticket.Scope.ToString(),
                    ["audit_event_type"] = eventType.ToString(),
                    ["decision"] = "allowed",
                },
                ct);

            return new ToolApprovalCheckResult
            {
                IsApproved = true,
                TicketId = ticket.TicketId,
                ApprovalSource = "Ticket",
                Message = isVerificationReplay
                    ? $"Automatic approval ticket '{ticket.TicketId}' allows verification replay for tool '{normalizedToolId}'."
                    : $"Automatic approval ticket '{ticket.TicketId}' allows tool '{normalizedToolId}'.",
            };
        }

        var denialFacts = BuildDenialFacts(normalizedToolId, request, tickets, now);
        return await ReviewImplicitApprovalAsync(
            request,
            descriptor,
            normalizedToolId,
            denialFacts,
            startedAt,
            now,
            ct);
    }

    private static ToolApprovalBuiltInPolicyApproval? TryGetBuiltInPolicyApproval(
        ToolApprovalExecutionRequest request,
        ToolDescriptor descriptor,
        string normalizedToolId)
    {
        if (descriptor.Safety.HasFlag(ToolSafetyFlags.ReadOnly))
        {
            return new ToolApprovalBuiltInPolicyApproval(
                "builtin_read_only_tool",
                $"Built-in policy allows read-only tool '{normalizedToolId}'.");
        }

        if ((string.Equals(normalizedToolId, "file_write", StringComparison.Ordinal)
             || string.Equals(normalizedToolId, "file_patch", StringComparison.Ordinal))
            && TryResolveAllWorkspaceFileTargets(normalizedToolId, request.ActualArgumentsJson, out var targets)
            && targets.Count > 0)
        {
            return new ToolApprovalBuiltInPolicyApproval(
                "builtin_workspace_file_write",
                $"Built-in policy allows workspace-scoped file write for {targets.Count} target(s).");
        }

        return null;
    }

    private static bool TryResolveAllWorkspaceFileTargets(
        string normalizedToolId,
        string? argumentsJson,
        out IReadOnlyList<string> targets)
    {
        targets = [];
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var paths = normalizedToolId switch
            {
                "file_write" => TryGetStringProperty(document.RootElement, "path", out var path)
                    ? [path]
                    : [],
                "file_patch" => ExtractFilePatchPaths(document.RootElement),
                _ => [],
            };

            var resolved = new List<string>();
            foreach (var path in paths)
            {
                if (!HostFileToolPaths.TryResolveInsideWorkspace(path, out var fullPath, out _))
                    return false;
                resolved.Add(fullPath);
            }

            targets = resolved;
            return targets.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ExtractFilePatchPaths(JsonElement root)
    {
        var paths = new List<string>();
        if (TryGetStringProperty(root, "path", out var singlePath))
            paths.Add(singlePath);

        if (TryGetProperty(root, "patches", out var patches)
            && patches.ValueKind == JsonValueKind.Array)
        {
            foreach (var patch in patches.EnumerateArray())
            {
                if (patch.ValueKind == JsonValueKind.Object
                    && TryGetStringProperty(patch, "path", out var path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = "";
        if (!TryGetProperty(element, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private async Task<ToolApprovalCheckResult> ReviewImplicitApprovalAsync(
        ToolApprovalExecutionRequest request,
        ToolDescriptor descriptor,
        string normalizedToolId,
        ToolApprovalDenialFacts denialFacts,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var identity = new ToolApprovalIdentity
        {
            WorkspaceId = request.WorkspaceId,
            SessionId = request.SessionId,
            AgentInstanceId = request.AgentInstanceId,
            UserId = request.UserId,
        };
        var implicitRequest = BuildImplicitApprovalRequest(request, descriptor, normalizedToolId);
        var hardDenial = BuildHardDenialReason(implicitRequest);
        ToolApprovalReviewResult review;
        if (!string.IsNullOrWhiteSpace(hardDenial))
        {
            review = new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = hardDenial,
                RequiresHumanAuthorization = true,
                MissingRequirements = ["software hard safety rule"],
                ReviewerModel = "software-hard-safety-rules",
            };
        }
        else
        {
            try
            {
                review = await _reviewer.ReviewAsync(implicitRequest, identity, descriptor, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "[ToolApproval] implicit audit failed tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId}",
                    normalizedToolId,
                    request.WorkspaceId,
                    request.SessionId,
                    request.AgentInstanceId);
                await RecordCheckMetricAsync(
                    request,
                    normalizedToolId,
                    TelemetryMetricStatuses.Failed,
                    startedAt,
                    "Tool approval implicit audit reviewer failed.",
                    new Dictionary<string, string>
                    {
                        ["approval_source"] = "implicit_audit",
                        ["decision"] = "review_failed",
                        ["failure_type"] = "approval_implicit_review_failed",
                        ["previous_failure_type"] = denialFacts.FailureType,
                        ["approved_ticket_id"] = denialFacts.ApprovedTicketId ?? "",
                        ["approved_command"] = Truncate(denialFacts.ApprovedCommand, 220),
                        ["actual_command"] = Truncate(denialFacts.ActualCommand, 220),
                    },
                    ct);
                var priorFailure = BuildPriorApprovalFailureSummary(denialFacts);
                return new ToolApprovalCheckResult
                {
                    IsApproved = false,
                    Message =
                        $"High-risk tool runtime approval required. Implicit audit failed for tool '{normalizedToolId}': {ex.Message}. " +
                        priorFailure +
                        $"Recommended next step: call request_tool_approval with tool_id='{normalizedToolId}' and exact planned arguments.",
                };
            }
        }

        var isApproved = review.Decision == ToolApprovalDecision.Approved;
        await SaveAuditAsync(new ToolApprovalAuditEvent
        {
            EventId = NewAuditEventId(),
            EventType = isApproved
                ? ToolApprovalAuditEventType.ImplicitApproved
                : ToolApprovalAuditEventType.ImplicitDenied,
            WorkspaceId = request.WorkspaceId,
            SessionId = request.SessionId,
            AgentInstanceId = request.AgentInstanceId,
            UserId = request.UserId,
            ToolId = normalizedToolId,
            Command = denialFacts.ActualCommand,
            ArgumentsJson = request.ActualArgumentsJson,
            Decision = review.Decision,
            ReviewerModel = review.ReviewerModel,
            Reason = review.DecisionReason,
            CreatedAtUtc = now,
        }, ct);

        _logger?.Log(
            isApproved ? LogLevel.Information : LogLevel.Warning,
            "[ToolApproval] implicit audit decision decision={Decision} tool={ToolId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} reviewerModel={ReviewerModel} durationMs={DurationMs}",
            review.Decision,
            normalizedToolId,
            request.WorkspaceId,
            request.SessionId,
            request.AgentInstanceId,
            review.ReviewerModel,
            DurationMs(startedAt));

        if (isApproved)
        {
            await RecordCheckMetricAsync(
                request,
                normalizedToolId,
                TelemetryMetricStatuses.Succeeded,
                startedAt,
                "Tool approval check allowed by implicit audit.",
                new Dictionary<string, string>
                {
                    ["approval_source"] = "implicit_audit",
                    ["decision"] = "allowed",
                    ["reviewer_model"] = review.ReviewerModel ?? "",
                    ["previous_failure_type"] = denialFacts.FailureType,
                    ["approved_ticket_id"] = denialFacts.ApprovedTicketId ?? "",
                    ["approved_command"] = Truncate(denialFacts.ApprovedCommand, 220),
                    ["actual_command"] = Truncate(denialFacts.ActualCommand, 220),
                },
                ct);
            return new ToolApprovalCheckResult
            {
                IsApproved = true,
                ApprovalSource = "ImplicitAudit",
                Message = $"Implicit audit approved tool '{normalizedToolId}' without a pre-existing ticket.",
            };
        }

        await RecordCheckMetricAsync(
            request,
            normalizedToolId,
            TelemetryMetricStatuses.Failed,
            startedAt,
            "Tool approval implicit audit denied.",
            new Dictionary<string, string>
            {
                ["approval_source"] = "implicit_audit",
                ["decision"] = "denied",
                ["failure_type"] = "approval_implicit_denied",
                ["previous_failure_type"] = denialFacts.FailureType,
                ["approved_ticket_id"] = denialFacts.ApprovedTicketId ?? "",
                ["approved_command"] = Truncate(denialFacts.ApprovedCommand, 220),
                ["reviewer_model"] = review.ReviewerModel ?? "",
                ["actual_command"] = Truncate(denialFacts.ActualCommand, 220),
            },
            ct);
        var previousFailure = BuildPriorApprovalFailureSummary(denialFacts);
        return new ToolApprovalCheckResult
        {
            IsApproved = false,
            Message =
                $"High-risk tool runtime approval required. Implicit audit denied tool '{normalizedToolId}': {review.DecisionReason}. " +
                previousFailure +
                $"Recommended next step: call request_tool_approval with tool_id='{normalizedToolId}' and exact planned arguments. " +
                $"Manual fallback only: ask the user to send {ToolAuthorizationDefaults.BuildAuthorizeCommand(normalizedToolId)}.",
        };
    }

    private static string BuildPriorApprovalFailureSummary(ToolApprovalDenialFacts denialFacts)
    {
        if (string.Equals(denialFacts.FailureType, "approval_missing", StringComparison.Ordinal))
            return "";

        var approvedCommand = string.IsNullOrWhiteSpace(denialFacts.ApprovedCommand)
            ? ""
            : $" approved_command='{Truncate(denialFacts.ApprovedCommand, 220)}';";
        var actualCommand = string.IsNullOrWhiteSpace(denialFacts.ActualCommand)
            ? ""
            : $" actual_command='{Truncate(denialFacts.ActualCommand, 220)}';";
        return $"Previous approval check failed with {denialFacts.FailureType}.{approvedCommand}{actualCommand} ";
    }

    private static bool MatchesBaseIdentity(
        ToolApprovalTicketRecord ticket,
        ToolApprovalExecutionRequest request,
        string normalizedToolId)
        => string.Equals(ticket.Identity.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
           && string.Equals(ticket.Identity.AgentInstanceId, request.AgentInstanceId, StringComparison.Ordinal)
           && string.Equals(ticket.Identity.UserId, request.UserId, StringComparison.Ordinal)
           && string.Equals(ticket.ToolId, normalizedToolId, StringComparison.Ordinal);

    private static string BuildDeniedMessage(
        string normalizedToolId,
        ToolApprovalExecutionRequest request,
        IReadOnlyList<ToolApprovalTicketRecord> tickets,
        DateTimeOffset now)
    {
        var relevant = tickets
            .Where(t => MatchesBaseIdentity(t, request, normalizedToolId))
            .OrderByDescending(t => t.DecidedAtUtc ?? t.CreatedAtUtc)
            .ToArray();
        var exactOperation = relevant.FirstOrDefault(t => MatchesApprovedOperation(t, request));
        if (exactOperation is not null)
        {
            if (exactOperation.Status == ToolApprovalTicketStatus.Consumed)
            {
                return
                    $"High-risk tool runtime approval required. Matching once ticket '{exactOperation.TicketId}' for tool '{normalizedToolId}' was already consumed. " +
                    $"Recommended next step: call request_tool_approval again with tool_id='{normalizedToolId}' and the exact planned arguments.";
            }

            if (exactOperation.Status == ToolApprovalTicketStatus.Expired
                || (exactOperation.ExpiresAtUtc is not null && exactOperation.ExpiresAtUtc <= now))
            {
                return
                    $"High-risk tool runtime approval required. Matching ticket '{exactOperation.TicketId}' for tool '{normalizedToolId}' is expired. " +
                    $"Recommended next step: call request_tool_approval again with current facts and exact planned arguments.";
            }

            if (exactOperation.Status == ToolApprovalTicketStatus.Denied)
            {
                return
                    $"High-risk tool runtime approval required. Matching ticket '{exactOperation.TicketId}' for tool '{normalizedToolId}' was denied: {exactOperation.DecisionReason}. " +
                    "Add facts, narrow scope, or choose a safer alternative before retrying request_tool_approval.";
            }

            if (exactOperation.Scope == ToolApprovalScope.Session
                && !string.Equals(exactOperation.Identity.SessionId, request.SessionId, StringComparison.Ordinal))
            {
                return
                    $"High-risk tool runtime approval required. Matching session ticket '{exactOperation.TicketId}' belongs to session '{exactOperation.Identity.SessionId}', but the actual call is in session '{request.SessionId}'. " +
                    "Session scope limits where the ticket is valid; submit a new request_tool_approval ticket for this session.";
            }
        }

        var approvedInSession = relevant.FirstOrDefault(t =>
            t.Status == ToolApprovalTicketStatus.Approved
            && (t.ExpiresAtUtc is null || t.ExpiresAtUtc > now)
            && (t.Scope != ToolApprovalScope.Session || string.Equals(t.Identity.SessionId, request.SessionId, StringComparison.Ordinal))
            && IsRelatedApprovedOperation(t, request));
        if (approvedInSession is not null)
        {
            var approvedCommand = ExtractCommand(approvedInSession.Request?.RequestedArgumentsJson);
            var actualCommand = ExtractCommand(request.ActualArgumentsJson);
            return
                $"High-risk tool runtime approval required. Approved ticket '{approvedInSession.TicketId}' for tool '{normalizedToolId}' exists, but it does not match the actual arguments. " +
                "Use the exact approved arguments or submit a new request_tool_approval ticket for the changed command/arguments. " +
                $"approved_command='{Truncate(approvedCommand, 220)}'; actual_command='{Truncate(actualCommand, 220)}'.";
        }

        var sessionTicket = relevant.FirstOrDefault(t =>
            t.Status == ToolApprovalTicketStatus.Approved
            && t.Scope == ToolApprovalScope.Session
            && IsRelatedApprovedOperation(t, request));
        if (sessionTicket is not null)
        {
            return
                $"High-risk tool runtime approval required. Approved session ticket '{sessionTicket.TicketId}' exists for session '{sessionTicket.Identity.SessionId}', not current session '{request.SessionId}'. " +
                "Session scope is not a cross-session or wildcard authorization; submit request_tool_approval for the current session and exact arguments.";
        }

        return
            "High-risk tool runtime approval required. No matching automatic approval ticket or manual human authorization was found. " +
            $"Recommended next step: call request_tool_approval with tool_id='{normalizedToolId}' and exact planned arguments. " +
            $"Manual fallback only: ask the user to send {ToolAuthorizationDefaults.BuildAuthorizeCommand(normalizedToolId)}.";
    }

    private static ToolApprovalDenialFacts BuildDenialFacts(
        string normalizedToolId,
        ToolApprovalExecutionRequest request,
        IReadOnlyList<ToolApprovalTicketRecord> tickets,
        DateTimeOffset now)
    {
        var actualCommand = ExtractCommand(request.ActualArgumentsJson);
        var relevant = tickets
            .Where(t => MatchesBaseIdentity(t, request, normalizedToolId))
            .OrderByDescending(t => t.DecidedAtUtc ?? t.CreatedAtUtc)
            .ToArray();

        var exactOperation = relevant.FirstOrDefault(t => MatchesApprovedOperation(t, request));
        if (exactOperation is not null)
        {
            var approvedCommand = ExtractCommand(exactOperation.Request?.RequestedArgumentsJson);
            var failureType = exactOperation.Status switch
            {
                ToolApprovalTicketStatus.Consumed => "approval_consumed",
                ToolApprovalTicketStatus.Expired => "approval_expired",
                ToolApprovalTicketStatus.Denied => "approval_denied",
                _ when exactOperation.ExpiresAtUtc is not null && exactOperation.ExpiresAtUtc <= now => "approval_expired",
                _ when exactOperation.Scope == ToolApprovalScope.Session
                    && !string.Equals(exactOperation.Identity.SessionId, request.SessionId, StringComparison.Ordinal) => "approval_wrong_session",
                _ => "approval_mismatch",
            };
            return new ToolApprovalDenialFacts(failureType, exactOperation.TicketId, approvedCommand, actualCommand);
        }

        var approvedInSession = relevant.FirstOrDefault(t =>
            t.Status == ToolApprovalTicketStatus.Approved
            && (t.ExpiresAtUtc is null || t.ExpiresAtUtc > now)
            && (t.Scope != ToolApprovalScope.Session || string.Equals(t.Identity.SessionId, request.SessionId, StringComparison.Ordinal))
            && IsRelatedApprovedOperation(t, request));
        if (approvedInSession is not null)
        {
            return new ToolApprovalDenialFacts(
                "approval_mismatch",
                approvedInSession.TicketId,
                ExtractCommand(approvedInSession.Request?.RequestedArgumentsJson),
                actualCommand);
        }

        var sessionTicket = relevant.FirstOrDefault(t =>
            t.Status == ToolApprovalTicketStatus.Approved
            && t.Scope == ToolApprovalScope.Session
            && IsRelatedApprovedOperation(t, request));
        if (sessionTicket is not null)
        {
            return new ToolApprovalDenialFacts(
                "approval_wrong_session",
                sessionTicket.TicketId,
                ExtractCommand(sessionTicket.Request?.RequestedArgumentsJson),
                actualCommand);
        }

        return new ToolApprovalDenialFacts("approval_missing", null, null, actualCommand);
    }

    private static ToolApprovalTicketRequest BuildImplicitApprovalRequest(
        ToolApprovalExecutionRequest request,
        ToolDescriptor descriptor,
        string normalizedToolId)
    {
        var actualCommand = ExtractCommand(request.ActualArgumentsJson) ?? normalizedToolId;
        var normalizedCommand = NormalizeCommandText(actualCommand);
        var isDestructive =
            IsDiskWipeOrFormatCommand(normalizedCommand)
            || IsDestructiveFileDeleteCommand(normalizedCommand)
            || IsDestructiveDatabaseCommand(normalizedCommand)
            || IsSystemConfigurationCommand(normalizedCommand);
        var isWorkspaceTemporaryCleanup = IsWorkspaceTemporaryFileCleanupCommand(
            actualCommand,
            request.WorkspaceId,
            out var temporaryFileEvidence);
        var isIrreversible = isDestructive && !isWorkspaceTemporaryCleanup;

        return new ToolApprovalTicketRequest
        {
            ToolId = normalizedToolId,
            CommandName = actualCommand,
            Purpose = "Implicitly review an actual high-risk tool call before execution.",
            Necessity = "The agent attempted the tool call without a matching automatic approval ticket.",
            FactBasis =
            [
                $"tool_id={normalizedToolId}",
                $"tool_name={descriptor.Name}",
                $"permission={descriptor.PermissionLevel}",
                $"safety={descriptor.Safety}",
            ],
            RequestedArgumentsJson = request.ActualArgumentsJson,
            TargetResources = [actualCommand],
            AuthorizedArea = [request.WorkspaceId],
            MayDamageOrDeleteData = isDestructive,
            IsIrreversibleOperation = isIrreversible,
            BackupTaken = false,
            RollbackPlan = isWorkspaceTemporaryCleanup
                ? "Targets are temporary/generated workspace files; no rollback is required."
                : isDestructive
                    ? ""
                    : "No destructive operation is evident from the implicit approval facts.",
            OperationContext = $"Implicit runtime approval check for workspace '{request.WorkspaceId}', session '{request.SessionId}'.",
            OperationPlan = "Review the exact actual arguments and decide whether this single invocation may proceed.",
            OperationSteps =
            [
                new ToolApprovalOperationStep
                {
                    StepNumber = 1,
                    ToolId = normalizedToolId,
                    Command = actualCommand,
                    RequestedArgumentsJson = request.ActualArgumentsJson,
                    TargetObject = actualCommand,
                    Purpose = "Execute the exact actual tool call if the implicit audit approves it.",
                    ExpectedEffect = "The tool runs once with the exact arguments under review.",
                    Reasonableness = "Implicit approval is only valid for this exact invocation.",
                    SafetyCheckBefore = "Confirm the actual arguments match the implicit approval request.",
                    StopCondition = "Stop if the implicit audit denies, needs human review, or the arguments change.",
                    RollbackForStep = isWorkspaceTemporaryCleanup
                        ? "No rollback is required for generated temporary cleanup targets."
                        : isDestructive
                            ? ""
                            : "No rollback is required unless the reviewer identifies mutation risk.",
                },
            ],
            TemporaryFileEvidence = temporaryFileEvidence,
            MayExposeSecrets = false,
            UserConsentStatus = ToolApprovalUserConsentStatus.Implied,
            AlternativesConsidered = ["Use explicit request_tool_approval if the implicit audit cannot approve this exact invocation."],
            RequestedScope = ToolApprovalScope.Once,
            RiskNotes = "Implicit approval does not create a reusable ticket or allowlist rule.",
        };
    }

    private async Task<ToolApprovalAllowlistRule?> FindMatchingAllowlistRuleAsync(
        ToolApprovalExecutionRequest request,
        string normalizedToolId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        foreach (var rule in (await _allowlistStore.ListAsync(ct))
                     .Where(r => r.Status == ToolApprovalAllowlistRuleStatus.Enabled))
        {
            if (!string.Equals(ToolAuthorizationDefaults.NormalizeToolId(rule.ToolId), normalizedToolId, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(rule.WorkspaceId)
                && !string.Equals(rule.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal))
            {
                continue;
            }
            if (!MatchesAllowlistOperation(rule, request))
                continue;

            return await RecordAllowlistRuleHitAsync(request, normalizedToolId, rule, now, ct);
        }

        return null;
    }

    private async Task<ToolApprovalAllowlistRule> RecordBuiltInPolicyAllowlistHitAsync(
        ToolApprovalExecutionRequest request,
        string normalizedToolId,
        ToolApprovalBuiltInPolicyApproval approval,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rule = await _allowlistStore.GetAsync(approval.RuleId, ct)
                   ?? new ToolApprovalAllowlistRule
                   {
                       RuleId = approval.RuleId,
                       WorkspaceId = null,
                       ToolId = normalizedToolId,
                       Source = ToolApprovalAllowlistRuleSource.BuiltIn,
                       Status = ToolApprovalAllowlistRuleStatus.Enabled,
                       Reason = approval.Reason,
                       CreatedAtUtc = now,
                   };

        return await RecordAllowlistRuleHitAsync(
            request,
            normalizedToolId,
            rule with { Reason = approval.Reason },
            now,
            ct);
    }

    private async Task<ToolApprovalAllowlistRule> RecordAllowlistRuleHitAsync(
        ToolApprovalExecutionRequest request,
        string normalizedToolId,
        ToolApprovalAllowlistRule rule,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var originalCommand = ExtractCommand(request.ActualArgumentsJson);
        var updated = rule with
        {
            HitCount = rule.HitCount + 1,
            LastHitAtUtc = now,
            UpdatedAtUtc = now,
        };
        await _allowlistStore.SaveAsync(updated, ct);
        await SaveAuditAsync(new ToolApprovalAuditEvent
        {
            EventId = NewAuditEventId(),
            EventType = ToolApprovalAuditEventType.AllowlistHit,
            WorkspaceId = request.WorkspaceId,
            SessionId = request.SessionId,
            AgentInstanceId = request.AgentInstanceId,
            UserId = request.UserId,
            ToolId = normalizedToolId,
            Command = originalCommand,
            ArgumentsJson = request.ActualArgumentsJson,
            OriginalCommand = originalCommand,
            OriginalArgumentsJson = request.ActualArgumentsJson,
            AllowlistRuleId = updated.RuleId,
            AllowlistRuleCommand = updated.Command,
            AllowlistRuleArgumentsJson = updated.ArgumentsJson,
            AllowlistRuleHitCount = updated.HitCount,
            Source = updated.Source,
            Reason = updated.Reason,
            CreatedAtUtc = now,
        }, ct);

        return updated;
    }

    private static bool MatchesAllowlistOperation(
        ToolApprovalAllowlistRule rule,
        ToolApprovalExecutionRequest request)
    {
        var actualCommand = ExtractCommand(request.ActualArgumentsJson);
        if (string.IsNullOrWhiteSpace(actualCommand) || HasShellControlOperator(actualCommand))
            return false;

        if (rule.Source == ToolApprovalAllowlistRuleSource.BuiltIn)
            return BuiltInCommandAllows(rule.Command, actualCommand);

        if (!string.IsNullOrWhiteSpace(rule.ArgumentsJson))
            return JsonStringEquals(rule.ArgumentsJson, request.ActualArgumentsJson);

        return CommandEquals(rule.Command, actualCommand);
    }

    private static string? BuildHardDenialReason(ToolApprovalTicketRequest request)
    {
        var commands = EnumeratePlannedCommands(request)
            .Select(NormalizeCommandText)
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hasRollbackPlan = !string.IsNullOrWhiteSpace(request.RollbackPlan);
        var hasTemporaryFileEvidence = !string.IsNullOrWhiteSpace(request.TemporaryFileEvidence);

        if (commands.Any(IsDiskWipeOrFormatCommand))
        {
            return "Hard safety denial: disk formatting, partition wiping, or raw device overwrite commands are not eligible for automatic approval.";
        }

        if (request.IsIrreversibleOperation && (!request.BackupTaken || !hasRollbackPlan))
        {
            return "Hard safety denial: irreversible operations require backup_taken=true and a concrete rollback_plan before automatic approval can continue.";
        }

        if (commands.Any(IsDestructiveFileDeleteCommand) && !request.BackupTaken && !hasTemporaryFileEvidence)
        {
            return "Hard safety denial: file deletion commands require either a backup plan or evidence that the targets are temporary/generated files.";
        }

        if (request.MayDamageOrDeleteData && !request.BackupTaken && !hasTemporaryFileEvidence)
        {
            return "Hard safety denial: operations that may damage or delete data require backup evidence or temporary/generated target evidence.";
        }

        if (commands.Any(IsDestructiveDatabaseCommand) && (!request.BackupTaken || !hasRollbackPlan))
        {
            return "Hard safety denial: destructive database operations require backup_taken=true and a concrete rollback_plan.";
        }

        if (commands.Any(IsSystemConfigurationCommand) && (!hasRollbackPlan || request.OperationSteps.Count == 0))
        {
            return "Hard safety denial: system configuration changes require a concrete rollback_plan and detailed operation_steps.";
        }

        return null;
    }

    private static IEnumerable<string> EnumeratePlannedCommands(ToolApprovalTicketRequest request)
    {
        var topLevelCommand = ExtractCommand(request.RequestedArgumentsJson);
        if (!string.IsNullOrWhiteSpace(topLevelCommand))
            yield return topLevelCommand;
        if (!string.IsNullOrWhiteSpace(request.CommandName))
            yield return request.CommandName;

        foreach (var step in request.OperationSteps)
        {
            if (!string.IsNullOrWhiteSpace(step.Command))
                yield return step.Command;

            var stepArgumentsCommand = ExtractCommand(step.RequestedArgumentsJson);
            if (!string.IsNullOrWhiteSpace(stepArgumentsCommand))
                yield return stepArgumentsCommand;
        }
    }

    private static string NormalizeCommandText(string command)
        => command.Trim().Replace('\\', '/').ToLowerInvariant();

    private static bool IsDiskWipeOrFormatCommand(string command)
        => command.StartsWith("format ", StringComparison.Ordinal)
           || command.StartsWith("format.com ", StringComparison.Ordinal)
           || command.StartsWith("mkfs.", StringComparison.Ordinal)
           || command.StartsWith("wipefs ", StringComparison.Ordinal)
           || (command.StartsWith("dd ", StringComparison.Ordinal) && command.Contains(" of=/dev/", StringComparison.Ordinal))
           || (command.Contains("diskpart", StringComparison.Ordinal) && command.Contains(" clean", StringComparison.Ordinal));

    private static bool IsDestructiveFileDeleteCommand(string command)
        => command.StartsWith("rm -rf ", StringComparison.Ordinal)
           || command.StartsWith("rm -fr ", StringComparison.Ordinal)
           || command.StartsWith("rm -r ", StringComparison.Ordinal)
           || command.StartsWith("del ", StringComparison.Ordinal)
           || command.StartsWith("erase ", StringComparison.Ordinal)
           || command.StartsWith("remove-item ", StringComparison.Ordinal)
           || command.StartsWith("rd /s ", StringComparison.Ordinal)
           || command.StartsWith("rmdir /s ", StringComparison.Ordinal);

    private static bool IsDestructiveDatabaseCommand(string command)
        => command.Contains("drop database", StringComparison.Ordinal)
           || command.Contains("drop table", StringComparison.Ordinal)
           || command.Contains("truncate table", StringComparison.Ordinal)
           || command.Contains("delete from", StringComparison.Ordinal);

    private static bool IsSystemConfigurationCommand(string command)
        => command.StartsWith("reg add ", StringComparison.Ordinal)
           || command.StartsWith("reg delete ", StringComparison.Ordinal)
           || command.StartsWith("bcdedit ", StringComparison.Ordinal)
           || command.StartsWith("netsh ", StringComparison.Ordinal)
           || command.StartsWith("sc config ", StringComparison.Ordinal)
           || command.StartsWith("set-service ", StringComparison.Ordinal)
           || command.StartsWith("chmod -r ", StringComparison.Ordinal)
           || command.StartsWith("chown -r ", StringComparison.Ordinal)
           || command.Contains("set-itemproperty hklm", StringComparison.Ordinal)
           || command.Contains("new-itemproperty hklm", StringComparison.Ordinal)
           || command.Contains("remove-itemproperty hklm", StringComparison.Ordinal);

    private static bool IsWorkspaceTemporaryFileCleanupCommand(
        string command,
        string workspaceId,
        out string temporaryFileEvidence)
    {
        temporaryFileEvidence = string.Empty;
        var normalized = NormalizeCommandText(command);
        if (!IsDestructiveFileDeleteCommand(normalized)
            || normalized.Contains("-recurse", StringComparison.Ordinal)
            || normalized.Contains(" -r ", StringComparison.Ordinal)
            || normalized.Contains(" /s ", StringComparison.Ordinal)
            || normalized.Contains("*", StringComparison.Ordinal))
        {
            return false;
        }

        var workspaceMarker = $"/data/workspaces/{workspaceId.Trim().ToLowerInvariant()}/";
        if (!normalized.Contains(workspaceMarker, StringComparison.Ordinal))
            return false;

        var targets = ExtractFileTargets(command)
            .Select(target => target.Replace('\\', '/').Trim())
            .Where(target => target.Contains('.', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length == 0)
            return false;

        foreach (var target in targets)
        {
            var targetNormalized = target.ToLowerInvariant();
            if (targetNormalized.Contains("/data/workspaces/", StringComparison.Ordinal)
                && !targetNormalized.Contains(workspaceMarker, StringComparison.Ordinal))
            {
                return false;
            }

            var fileName = Path.GetFileName(targetNormalized);
            if (!LooksLikeGeneratedTemporaryFileName(fileName))
                return false;
        }

        temporaryFileEvidence =
            "Implicit cleanup targets are non-recursive files under the current workspace and their names look generated/temporary: " +
            string.Join(", ", targets.Select(Path.GetFileName));
        return true;
    }

    private static IEnumerable<string> ExtractFileTargets(string command)
    {
        foreach (Match match in Regex.Matches(command, @"(?<path>[A-Za-z]:[^\s,;|]+|(?:\.{1,2}[\\/])?[^\s,;|]+[\\/][^\s,;|]+|\b[\w.-]+\.[A-Za-z0-9]{1,8}\b)"))
        {
            var value = match.Groups["path"].Value.Trim('\'', '"', ',', ';');
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private static bool LooksLikeGeneratedTemporaryFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        return fileName.StartsWith("test_", StringComparison.Ordinal)
               || fileName.StartsWith("_test_", StringComparison.Ordinal)
               || fileName.StartsWith("tmp_", StringComparison.Ordinal)
               || fileName.StartsWith("temp_", StringComparison.Ordinal)
               || fileName.StartsWith("_tmp_", StringComparison.Ordinal)
               || fileName.StartsWith("_error.", StringComparison.Ordinal)
               || fileName.StartsWith("_error_", StringComparison.Ordinal)
               || fileName.StartsWith("_output.", StringComparison.Ordinal)
               || fileName.StartsWith("_output_", StringComparison.Ordinal)
               || fileName.EndsWith(".tmp", StringComparison.Ordinal);
    }

    private static bool IsRelatedApprovedOperation(
        ToolApprovalTicketRecord ticket,
        ToolApprovalExecutionRequest request)
    {
        if (ticket.Request is null)
            return false;

        var actualCommand = ExtractCommand(request.ActualArgumentsJson);
        if (string.IsNullOrWhiteSpace(actualCommand))
            return false;

        var approvedCommand = ExtractCommand(ticket.Request.RequestedArgumentsJson);
        if (CommandsAppearRelated(approvedCommand, actualCommand))
            return true;

        return ticket.Request.OperationSteps.Any(step =>
            StepToolMatches(step, request.ToolId)
            && (CommandsAppearRelated(step.Command, actualCommand)
                || CommandsAppearRelated(ExtractCommand(step.RequestedArgumentsJson), actualCommand)));
    }

    private static bool MatchesApprovedOperation(
        ToolApprovalTicketRecord ticket,
        ToolApprovalExecutionRequest request)
    {
        if (ticket.Request is null)
            return false;

        // TODO(auto-approval): Replace this deterministic matcher with an audit-agent
        // execution review that compares the approved ticket against the actual call.
        if (JsonStringEquals(ticket.Request.RequestedArgumentsJson, request.ActualArgumentsJson))
            return true;

        var actualCommand = ExtractCommand(request.ActualArgumentsJson);
        if (string.IsNullOrWhiteSpace(actualCommand))
            return false;

        var approvedCommand = ExtractCommand(ticket.Request.RequestedArgumentsJson);
        if (CommandEquals(approvedCommand, actualCommand))
            return true;

        return ticket.Request.OperationSteps.Any(step =>
            StepToolMatches(step, request.ToolId)
            && (JsonStringEquals(step.RequestedArgumentsJson, request.ActualArgumentsJson)
                || CommandEquals(step.Command, actualCommand)
                || CommandContains(step.Command, actualCommand)));
    }

    private static bool StepToolMatches(ToolApprovalOperationStep step, string actualToolId)
        => string.IsNullOrWhiteSpace(step.ToolId)
           || string.Equals(
               ToolAuthorizationDefaults.NormalizeToolId(step.ToolId),
               ToolAuthorizationDefaults.NormalizeToolId(actualToolId),
               StringComparison.Ordinal);

    private static bool JsonStringEquals(string? expectedJson, string? actualJson)
    {
        if (string.IsNullOrWhiteSpace(expectedJson) || string.IsNullOrWhiteSpace(actualJson))
            return false;

        return string.Equals(
            CanonicalizeJson(expectedJson),
            CanonicalizeJson(actualJson),
            StringComparison.Ordinal);
    }

    private static string CanonicalizeJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonicalJson(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json.Trim();
        }
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonicalJson(item, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string? ExtractCommand(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var propertyName in new[] { "command", "cmd", "input" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return argumentsJson;
        }

        return null;
    }

    private static bool CommandEquals(string? expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected)
           && !string.IsNullOrWhiteSpace(actual)
           && string.Equals(expected.Trim(), actual.Trim(), StringComparison.Ordinal);

    private static bool CommandContains(string? approvedCommand, string actualCommand)
        => !string.IsNullOrWhiteSpace(approvedCommand)
           && approvedCommand.Contains(actualCommand.Trim(), StringComparison.Ordinal);

    private static bool CommandsAppearRelated(string? approvedCommand, string? actualCommand)
    {
        if (string.IsNullOrWhiteSpace(approvedCommand) || string.IsNullOrWhiteSpace(actualCommand))
            return false;

        if (CommandEquals(approvedCommand, actualCommand))
            return true;

        var approved = NormalizeCommandForRelation(approvedCommand);
        var actual = NormalizeCommandForRelation(actualCommand);
        if (CommandHasPrefix(approved, actual) || CommandHasPrefix(actual, approved))
            return true;

        var approvedKey = BuildCommandRelationKey(approved);
        var actualKey = BuildCommandRelationKey(actual);
        return !string.IsNullOrWhiteSpace(approvedKey)
               && string.Equals(approvedKey, actualKey, StringComparison.Ordinal);
    }

    private static bool CommandHasPrefix(string command, string possiblePrefix)
    {
        if (command.Length <= possiblePrefix.Length)
            return false;

        return command.StartsWith(possiblePrefix, StringComparison.Ordinal)
               && char.IsWhiteSpace(command[possiblePrefix.Length]);
    }

    private static string NormalizeCommandForRelation(string command)
        => string.Join(
            ' ',
            command.Trim().Replace('\\', '/').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? BuildCommandRelationKey(string command)
    {
        var tokens = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeCommandToken)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        if (tokens.Length == 0)
            return null;

        var executable = Path.GetFileName(tokens[0]);
        if (string.IsNullOrWhiteSpace(executable))
            executable = tokens[0];

        if (IsPowerShellExecutable(executable))
        {
            for (var i = 1; i < tokens.Length - 1; i++)
            {
                if (string.Equals(tokens[i], "-file", StringComparison.Ordinal))
                    return "powershell:file:" + NormalizeScriptTarget(tokens[i + 1]);
            }

            return "powershell:" + executable;
        }

        if (IsScriptRunnerExecutable(executable) && tokens.Length > 1)
            return executable + ":" + NormalizeScriptTarget(tokens[1]);

        return executable;
    }

    private static bool IsPowerShellExecutable(string executable)
        => string.Equals(executable, "powershell", StringComparison.Ordinal)
           || string.Equals(executable, "powershell.exe", StringComparison.Ordinal)
           || string.Equals(executable, "pwsh", StringComparison.Ordinal)
           || string.Equals(executable, "pwsh.exe", StringComparison.Ordinal);

    private static bool IsScriptRunnerExecutable(string executable)
        => string.Equals(executable, "python", StringComparison.Ordinal)
           || string.Equals(executable, "python.exe", StringComparison.Ordinal)
           || string.Equals(executable, "python3", StringComparison.Ordinal)
           || string.Equals(executable, "py", StringComparison.Ordinal)
           || string.Equals(executable, "py.exe", StringComparison.Ordinal)
           || string.Equals(executable, "node", StringComparison.Ordinal)
           || string.Equals(executable, "node.exe", StringComparison.Ordinal);

    private static string NormalizeCommandToken(string token)
        => token.Trim().Trim('"', '\'');

    private static string NormalizeScriptTarget(string token)
        => Path.GetFileName(NormalizeCommandToken(token).Replace('\\', '/'));

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength] + "...";
    }

    private static bool BuiltInCommandAllows(string? allowlistedCommand, string actualCommand)
    {
        if (string.IsNullOrWhiteSpace(allowlistedCommand))
            return false;

        var expected = allowlistedCommand.Trim();
        var actual = actualCommand.Trim();
        if (expected.Contains(' '))
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)
               || actual.StartsWith(expected + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasShellControlOperator(string command)
        => command.Contains(';')
           || command.Contains("&&", StringComparison.Ordinal)
           || command.Contains("||", StringComparison.Ordinal)
           || command.Contains('|')
           || command.Contains('>')
           || command.Contains('<')
           || command.Contains('`');

    private Task SaveAuditAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct)
        => _auditStore.SaveAsync(auditEvent, ct);

    private Task RecordCheckMetricAsync(
        ToolApprovalExecutionRequest request,
        string normalizedToolId,
        string status,
        DateTimeOffset startedAt,
        string summary,
        Dictionary<string, string> dimensions,
        CancellationToken ct)
    {
        dimensions["tool_id"] = normalizedToolId;
        dimensions["tool_stage"] = "check";
        return RecordMetricAsync(
            "tool_approval.check",
            status,
            startedAt,
            summary,
            request.WorkspaceId,
            request.SessionId,
            request.AgentInstanceId,
            request.UserId,
            dimensions,
            ct: ct);
    }

    private async Task RecordMetricAsync(
        string name,
        string status,
        DateTimeOffset startedAt,
        string summary,
        string? workspaceId,
        string? sessionId,
        string? agentInstanceId,
        string? userId,
        IReadOnlyDictionary<string, string> dimensions,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        if (_telemetryMetricSink is null)
            return;

        try
        {
            var metricDimensions = dimensions
                .Concat(new[]
                {
                    new KeyValuePair<string, string>("agent_instance_id", agentInstanceId ?? ""),
                })
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            await _telemetryMetricSink.RecordAsync(new TelemetryMetric
            {
                Trace = RuntimeTraceContext.CreateNew(
                    sessionId: sessionId,
                    workspaceId: workspaceId,
                    userId: userId),
                Source = "tool_approval",
                Category = TelemetryMetricCategories.Tool,
                Name = name,
                Status = status,
                OccurredAtUtc = _timeProvider.GetUtcNow(),
                DurationMs = DurationMs(startedAt),
                Severity = status == TelemetryMetricStatuses.Failed ? "warning" : "info",
                Summary = summary,
                Dimensions = metricDimensions,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "[ToolApproval] failed to record telemetry metric name={MetricName} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId}",
                name,
                workspaceId,
                sessionId,
                agentInstanceId);
        }
    }

    private long DurationMs(DateTimeOffset startedAt)
    {
        var duration = _timeProvider.GetUtcNow() - startedAt;
        return Math.Max(0, (long)duration.TotalMilliseconds);
    }

    private sealed record ToolApprovalDenialFacts(
        string FailureType,
        string? ApprovedTicketId,
        string? ApprovedCommand,
        string? ActualCommand);

    private sealed record ToolApprovalBuiltInPolicyApproval(string RuleId, string Reason);

    private async Task<IReadOnlyList<string>> CreateAllowlistRulesFromApprovedTicketAsync(
        ToolApprovalTicketRecord ticket,
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        string normalizedToolId,
        ToolApprovalReviewResult review,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var ruleIds = new List<string>();
        foreach (var proposal in review.AllowlistProposals.Where(HasReusableAllowlistShape))
        {
            ruleIds.Add(await CreateAllowlistRuleFromProposalAsync(
                ticket,
                request,
                identity,
                normalizedToolId,
                review,
                proposal,
                now,
                ct));
        }

        if (ruleIds.Count == 0 && request.RequestAllowlistRule)
        {
            ruleIds.Add(await CreateAllowlistRuleFromTicketAsync(
                ticket,
                request,
                identity,
                normalizedToolId,
                review,
                now,
                ct));
        }

        return ruleIds;
    }

    private async Task<string> CreateAllowlistRuleFromProposalAsync(
        ToolApprovalTicketRecord ticket,
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        string normalizedToolId,
        ToolApprovalReviewResult review,
        ToolApprovalAllowlistProposal proposal,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var proposalToolId = string.IsNullOrWhiteSpace(proposal.ToolId)
            ? normalizedToolId
            : ToolAuthorizationDefaults.NormalizeToolId(proposal.ToolId);
        var argumentsJson = string.IsNullOrWhiteSpace(proposal.ArgumentsJson) ? null : proposal.ArgumentsJson;
        var command = FirstNonWhiteSpace(
            proposal.Command,
            ExtractCommand(argumentsJson),
            request.CommandName,
            ExtractCommand(request.RequestedArgumentsJson));
        var reason = FirstNonWhiteSpace(proposal.Reason, request.AllowlistReason, review.DecisionReason);

        return await CreateAllowlistRuleAsync(
            ticket,
            identity,
            proposalToolId,
            command,
            argumentsJson,
            reason,
            review,
            now,
            ct);
    }

    private async Task<string> CreateAllowlistRuleFromTicketAsync(
        ToolApprovalTicketRecord ticket,
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        string normalizedToolId,
        ToolApprovalReviewResult review,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var command = ExtractCommand(request.RequestedArgumentsJson) ?? request.CommandName;
        return await CreateAllowlistRuleAsync(
            ticket,
            identity,
            normalizedToolId,
            command,
            request.RequestedArgumentsJson,
            string.IsNullOrWhiteSpace(request.AllowlistReason)
                ? review.DecisionReason
                : request.AllowlistReason,
            review,
            now,
            ct);
    }

    private async Task<string> CreateAllowlistRuleAsync(
        ToolApprovalTicketRecord ticket,
        ToolApprovalIdentity identity,
        string toolId,
        string? command,
        string? argumentsJson,
        string? reason,
        ToolApprovalReviewResult review,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rule = new ToolApprovalAllowlistRule
        {
            RuleId = "tap_allow_" + Guid.NewGuid().ToString("N"),
            WorkspaceId = identity.WorkspaceId,
            ToolId = toolId,
            Command = command,
            ArgumentsJson = argumentsJson,
            Source = ToolApprovalAllowlistRuleSource.AuditAgent,
            Status = ToolApprovalAllowlistRuleStatus.Enabled,
            ApprovedByAgentInstanceId = identity.AgentInstanceId,
            ApprovedByUserId = identity.UserId,
            ApprovalTicketId = ticket.TicketId,
            Reason = reason,
            CreatedAtUtc = now,
        };

        await _allowlistStore.SaveAsync(rule, ct);
        await SaveAuditAsync(new ToolApprovalAuditEvent
        {
            EventId = NewAuditEventId(),
            EventType = ToolApprovalAuditEventType.AllowlistRuleCreated,
            WorkspaceId = identity.WorkspaceId,
            SessionId = identity.SessionId,
            AgentInstanceId = identity.AgentInstanceId,
            UserId = identity.UserId,
            ToolId = toolId,
            Command = command,
            ArgumentsJson = argumentsJson,
            TicketId = ticket.TicketId,
            AllowlistRuleId = rule.RuleId,
            Decision = review.Decision,
            Source = rule.Source,
            ReviewerModel = review.ReviewerModel,
            Reason = rule.Reason,
            CreatedAtUtc = now,
        }, ct);

        return rule.RuleId;
    }

    private static bool HasReusableAllowlistShape(ToolApprovalAllowlistProposal proposal)
        => !string.IsNullOrWhiteSpace(proposal.Command) || !string.IsNullOrWhiteSpace(proposal.ArgumentsJson);

    private static string? FirstNonWhiteSpace(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NewAuditEventId() => "taa_" + Guid.NewGuid().ToString("N");

    private static ToolApprovalTicketResult ToResult(
        ToolApprovalTicketRecord ticket,
        ToolApprovalDecision decision,
        string? allowlistRuleId)
        => new()
        {
            TicketId = ticket.TicketId,
            Decision = decision,
            Status = ticket.Status,
            DecisionReason = ticket.DecisionReason,
            AllowedScope = decision == ToolApprovalDecision.Approved ? ticket.Scope : null,
            ExpiresAtUtc = ticket.ExpiresAtUtc,
            RecommendedNextStep = decision == ToolApprovalDecision.Approved
                ? allowlistRuleId is null
                    ? "Continue with the exact approved tool call."
                    : $"Allowlist rule '{allowlistRuleId}' was created. Future matching calls can use fast approval."
                : "Add facts, narrow scope, create a rollback plan, then retry request_tool_approval. Use /authorize only as a manual human fallback.",
            AllowlistRuleId = allowlistRuleId,
        };
}
