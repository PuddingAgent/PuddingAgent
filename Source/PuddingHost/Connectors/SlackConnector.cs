using System.Text.Json;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingSlack;

namespace PuddingAgent.Connectors;

/// <summary>Host adapter: owns lifecycle and identity mapping; Slack protocol and journals remain in the leaf component.</summary>
public sealed class SlackConnector(string channelId, string workspaceId, string agentId, SlackChannelSettings settings,
    string storeDirectory, ILogger<SlackConnector> logger) : IPuddingConnector
{
    private CancellationTokenSource? _stop;
    private Task? _run;
    private SlackStore? _store;
    private HttpClient? _http;
    private SlackApiClient? _api;
    private SlackMemberDirectory? _directory;
    private SlackCredentialFile? _credentialStore;
    private SlackAppTokenProvider? _tokens;
    private volatile bool _authenticated;
    private string _status = "stopped";
    private string? _error;
    private long _received, _sent, _errors;
    private DateTimeOffset? _lastReceive, _lastError;
    public ConnectorDescriptor Descriptor { get; } = new()
    {
        ConnectorId = SlackChannelMetadata.ConnectorId(channelId), ConnectorType = "slack", Protocol = "socket_mode",
        Description = "Slack DM and @mention text replies", Capabilities = ["receive", "send", "text", "threads", "delivery_receipt", "lookup_member"]
    };

    public Task StartAsync(ConnectorContext context, CancellationToken ct = default)
    {
        if (_run is not null) return Task.CompletedTask;
        SlackChannelValidation.Normalize(settings);
        try
        {
            ct.ThrowIfCancellationRequested();
            _credentialStore = new SlackCredentialFile(settings.CredentialFile);
            var credentials = _credentialStore.Read().Credentials;
            _http = new HttpClient();
            _tokens = new SlackAppTokenProvider(_http, _credentialStore, settings.AppId);
            _api = new SlackApiClient(_http, credentials, _tokens.GetAsync);
            _directory = new SlackMemberDirectory(_http, credentials.BotToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _http?.Dispose(); _http = null;
            _credentialStore?.Dispose(); _credentialStore = null;
            throw new InvalidOperationException("Slack credential file is missing, unreadable, or invalid; expected appToken and botToken.");
        }
        try { _store = new SlackStore(storeDirectory); }
        catch { _http.Dispose(); _http = null; _credentialStore.Dispose(); _credentialStore = null; throw; }
        _stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, ct);
        _run = RunAsync(context, _stop.Token);
        return Task.CompletedTask;
    }

    private async Task RunAsync(ConnectorContext context, CancellationToken ct)
    {
        try
        {
            // Recover network outages without requiring a Core restart. No event is accepted before auth.test verifies the team.
            SlackIdentity identity;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { identity = await _api!.AuthenticateAsync(ct); break; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { Failure("authentication_failed_retrying"); await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            }
            if (identity.TeamId != settings.TeamId) { Failure("team_id_mismatch"); return; }
            _authenticated = true;
            var admission = new SlackAdmission(settings.TeamId, identity.BotUserId, settings.AllowedUserIds, settings.AllowedChannelIds, settings.AppId, settings.AllowedBotUserIds);
            var socket = new SlackSocketMode(_api!, admission, _store!.Enqueue, ConnectionState);
            using var workers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var receiving = socket.RunAsync(workers.Token);
            var consuming = ConsumeAsync(context, workers.Token);
            var rotating = _tokens!.MaintainAsync(Failure, workers.Token);
            await Task.WhenAny(receiving, consuming, rotating);
            await workers.CancelAsync();
            await Task.WhenAll(receiving, consuming, rotating);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { Failure("connector_worker_failed"); }
        finally { _authenticated = false; }
    }

    private async Task ConsumeAsync(ConnectorContext context, CancellationToken ct)
    {
        var retries = new Dictionary<string, (int Attempts, DateTimeOffset At)>();
        while (!ct.IsCancellationRequested)
        {
            var deferred = retries.Where(item => item.Value.At > DateTimeOffset.UtcNow).Select(item => item.Key);
            foreach (var message in _store!.Pending(deferredMessageKeys: deferred))
            {
                var retry = retries.GetValueOrDefault(message.MessageKey);
                try
                {
                    await context.OnEventReceived(Map(message, channelId, workspaceId, agentId), ct);
                    _store.Complete(message);
                    retries.Remove(message.MessageKey);
                    Interlocked.Increment(ref _received); _lastReceive = DateTimeOffset.UtcNow;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    retries[message.MessageKey] = (retry.Attempts + 1,
                        DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(retry.Attempts, 6)))));
                    Failure("inbox_handoff_failed_pending_retry");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public static PuddingIngressEnvelope Map(SlackInboundMessage message, string channelId, string workspaceId, string agentId)
    {
        var metadata = new Dictionary<string, string>
        {
            [SlackChannelMetadata.TeamId] = message.TeamId,
            [SlackChannelMetadata.UserId] = message.UserId,
            [SlackChannelMetadata.ConversationKey] = message.ConversationKey,
            [SlackChannelMetadata.IsDirectMessage] = message.IsDirectMessage ? "true" : "false",
            [SlackChannelMetadata.IsBot] = message.IsBot ? "true" : "false"
        };
        if (message.ReplyThreadTs is { } thread) metadata[SlackChannelMetadata.ThreadTs] = thread;
        return new PuddingIngressEnvelope
        {
            ConnectorId = SlackChannelMetadata.ConnectorId(channelId), ChannelId = channelId, ChannelType = "slack",
            WorkspaceId = workspaceId, AgentId = agentId, UserExternalId = SlackChannelMetadata.Principal(message.TeamId, message.UserId),
            MessageText = message.Text, MessageType = "chat", ExternalConversationId = message.ChannelId,
            ExternalMessageId = message.MessageKey, CorrelationId = message.ConversationKey, Metadata = metadata
        };
    }

    public async Task SendAsync(ConnectorMessage message, CancellationToken ct = default)
    {
        if (!_authenticated || _store is null || _api is null) throw new InvalidOperationException("Slack connector is not authenticated.");
        if (!message.Metadata.TryGetValue(MessageGatewayMetadata.IdempotencyKey, out var operationId))
            throw new ConnectorDeliveryException(ConnectorDeliveryFailure.Rejected, "missing_idempotency_key");
        if (message.Metadata.GetValueOrDefault(MessageGatewayMetadata.ChannelId) != channelId
            || message.Metadata.GetValueOrDefault(SlackChannelMetadata.TeamId) != settings.TeamId)
            throw new ConnectorDeliveryException(ConnectorDeliveryFailure.Rejected, "route_binding_mismatch");
        var thread = message.Metadata.GetValueOrDefault(SlackChannelMetadata.ThreadTs);
        var receipt = await _store.SendAsync(operationId, new SlackPost(message.Target, thread, message.Content), _api.PostAsync, ct);
        if (receipt.Status != SlackSendStatus.Sent)
            throw new ConnectorDeliveryException(receipt.Status switch
            {
                SlackSendStatus.Retryable => ConnectorDeliveryFailure.Retryable,
                SlackSendStatus.Rejected => ConnectorDeliveryFailure.Rejected,
                _ => ConnectorDeliveryFailure.Unknown
            }, receipt.Code, receipt.RetryAt);
        Interlocked.Increment(ref _sent);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_stop is null) return;
        _authenticated = false;
        await _stop.CancelAsync();
        if (_run is not null) await _run.WaitAsync(ct);
        _store?.Dispose(); _store = null; _http?.Dispose(); _http = null;
        _credentialStore?.Dispose(); _credentialStore = null; _tokens = null; _directory = null;
        _stop.Dispose(); _stop = null; _run = null; _status = "stopped";
    }
    public async Task<ConnectorOperationResult> OperateAsync(string operation, Dictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        if (operation == "lookup_member")
        {
            if (!_authenticated || _directory is not { } directory)
                return new() { Error = _stop?.IsCancellationRequested == true ? "slack_connector_stopping" : "slack_connector_not_ready" };
            if (parameters?.GetValueOrDefault("teamId") != settings.TeamId
                || parameters.GetValueOrDefault("channelId") != channelId
                || parameters.GetValueOrDefault("workspaceId") != workspaceId
                || parameters.GetValueOrDefault("agentId") != agentId)
                return new() { Error = "route_binding_mismatch" };
            try
            {
                var result = await directory.LookupAsync(parameters.GetValueOrDefault("query") ?? "", ct);
                return new() { Success = true, Data = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            }
            catch (SlackProtocolException ex) { return new() { Error = ex.Message }; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new() { Error = "directory_timeout" }; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            { return new() { Error = "directory_unavailable" }; }
        }
        var receipt = operation == "delivery_receipt" && parameters?.GetValueOrDefault("operationId") is { } id ? _store?.Receipt(id) : null;
        return new ConnectorOperationResult { Success = receipt is not null,
            Data = receipt is null ? null : JsonSerializer.Serialize(receipt), Error = receipt is null ? "receipt_not_found_or_unsupported_operation" : null };
    }
    public Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default) => Task.FromResult(new ConnectorDiagnostics
    {
        Status = _status, MessagesReceived = Interlocked.Read(ref _received), MessagesSent = Interlocked.Read(ref _sent),
        MessagesSuppressed = _store?.SuppressedBotMessages ?? 0,
        Errors = Interlocked.Read(ref _errors), LastReceiveTime = _lastReceive, LastErrorTime = _lastError, LastError = _error
    });
    private void Failure(string code)
    {
        _status = code; _error = code; _lastError = DateTimeOffset.UtcNow; Interlocked.Increment(ref _errors);
        logger.LogWarning("[Slack] channel={ChannelId} status={Code}", channelId, code);
    }
    private void ConnectionState(string state)
    {
        if (_status == state) return;
        _status = state;
        logger.LogInformation("[Slack] channel={ChannelId} connection={State}", channelId, state);
    }
}
