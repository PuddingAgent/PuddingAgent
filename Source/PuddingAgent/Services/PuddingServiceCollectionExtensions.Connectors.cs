using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Core;
using PuddingCode.Diagnostics;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Conversation;
using PuddingPlatform.Services.Execution;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Services.Diagnostics;
using PuddingPlatform.Services.Snapshot;
using PuddingCodeIntelligence;
using PuddingCodeIntelligence.Contracts;
using PuddingCodeIntelligence.Storage;
using PuddingPlatform.Services.MessageFabric;
using PuddingPlatform.Services.MessageGateway;
using PuddingPlatform.Services.Mcp;
using PuddingPlatform.Services.TaskPlanning;
using PuddingController;
using PuddingController.Data;
using PuddingController.Services;
using PuddingRuntime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using PuddingRuntime.Services.Events;
using PuddingRuntime.Services.Hooks;
using PuddingRuntime.Services.Messaging;
using PuddingRuntime.Services.Observability;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.SubAgents;
using PuddingRuntime.Services.Tools;
using PuddingRuntime.Services.TaskPlanning;
using PuddingMemoryEngine;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Services;
using PuddingAgent.P2P;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Text;
using PuddingAgent.Connectors;
using PuddingAgent.Services.Events;
using System.Threading.Channels;

namespace PuddingAgent.Services;

public static partial class PuddingServiceCollectionExtensions
{
    private static void AddConnectorServices(WebApplicationBuilder builder)
    {
        // ── P2P 发现（局域网 UDP 广播 + HTTP 探活）────────────────
        builder.Services.AddSingleton<IP2pDiscoveryService, MdnsDiscoveryService>();

        // ── Webhook 连接器 ─────────────────────────────────
        builder.Services.AddSingleton<WebhookConnector>();
        builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<WebhookConnector>());

        // ── HTTP 连接器（最小入站协议）──────────────────────
        builder.Services.AddSingleton<HttpConnector>();
        builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<HttpConnector>());

        // ── WebSocket 连接器 ───────────────────────────────
        builder.Services.AddSingleton<WebSocketConnector>();
        builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<WebSocketConnector>());

        // ── MQTT 连接器（最小协议）──────────────────────────
        builder.Services.AddSingleton<MqttConnector>();
        builder.Services.AddSingleton<IPuddingConnector>(sp => sp.GetRequiredService<MqttConnector>());

        // ── 飞书连接器（从 Agent manifest 动态创建；一个 Agent 一个机器人）──────
        builder.Services.AddSingleton<FeishuConnectorFactory>();

        // ── 网关鉴权（SM2 + 白名单）────────────────────────
        builder.Services.AddSingleton<GatewayAuthService>();

        // ── ConnectorHost（统一管理所有连接器）────────────
        builder.Services.AddSingleton<ConnectorHost>(sp =>
        {
            var host = new ConnectorHost(
                onEventReceived: async (envelope, ct) =>
                {
                    if (string.Equals(
                            envelope.ChannelType,
                            "feishu",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        var gateway = sp.GetRequiredService<IMessageGatewayIngress>();
                        await gateway.AcceptAsync(envelope, ct);
                        return;
                    }

                    // 将连接器事件推入 IInternalEventBus → EventIngressBridge → AgentEventHandler
                    var bus = sp.GetRequiredService<PuddingCode.Abstractions.IInternalEventBus>();
                    var sessionId = envelope.CorrelationId ?? $"connector-session-{Guid.NewGuid():N}"[..26];
                    var messageType = string.IsNullOrWhiteSpace(envelope.MessageType) ? "message" : envelope.MessageType;
                    var eventType = ConnectorGatewayContracts.BuildEventType(envelope.ChannelType, messageType);
                    var payload = new ConnectorInboundPayload
                    {
                        ChannelId = envelope.ChannelId,
                        ChannelType = envelope.ChannelType,
                        UserExternalId = envelope.UserExternalId,
                        MessageText = envelope.MessageText,
                        MessageType = envelope.MessageType,
                        CorrelationId = sessionId,
                        Metadata = envelope.Metadata,
                    };

                    var traceId = envelope.Metadata.TryGetValue("trace_id", out var t) && !string.IsNullOrWhiteSpace(t)
                        ? t
                        : envelope.EnvelopeId;

                    var ie = new PuddingCode.Models.InternalEvent
                    {
                        EventId = envelope.EnvelopeId,
                        Type = eventType,
                        Source = new PuddingCode.Models.EventSource { SourceType = envelope.ChannelType, SourceId = envelope.ChannelId },
                        SessionId = sessionId,
                        WorkspaceId = envelope.WorkspaceId ?? "default",
                        Payload = System.Text.Json.JsonSerializer.SerializeToElement(payload),
                        TimestampUtc = DateTime.UtcNow,
                        Priority = PuddingCode.Models.EventPriorityLevel.Normal,
                        TraceId = traceId,
                        CorrelationId = sessionId,
                        CausationId = envelope.ExternalMessageId,
                    };

                    var spLogger = sp.GetRequiredService<ILogger<global::Program>>();
                    spLogger.LogInformation(
                        "[Program:ConnectorIngress] eventId={EventId} traceId={TraceId} eventType={EventType} channelType={ChannelType} channelId={ChannelId} sessionId={SessionId} envelopeId={EnvelopeId}",
                        ie.EventId,
                        traceId,
                        eventType,
                        envelope.ChannelType,
                        envelope.ChannelId,
                        sessionId,
                        envelope.EnvelopeId);

                    await bus.PublishAsync(ie, ct);

                    // 仅 websocket 通道启用 SSM→WS 转发；其他协议仅通过会话层观察。
                    if (!string.Equals(envelope.ChannelType, "websocket", StringComparison.OrdinalIgnoreCase))
                        return;

                    // 将 WebSocket 连接 ID 的 session 订阅到 SSM，后续 SSE 帧转发到 WebSocket
                    var ssm2 = sp.GetService<PuddingCode.Abstractions.ISessionStateManager>();
                    if (ssm2 is not null)
                    {
                        var wsConnector = sp.GetRequiredService<WebSocketConnector>();
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                // 等待 Agent 创建 session 并开始推流
                                await Task.Delay(2000, ct);
                                spLogger.LogWarning("[Program:SSM→WS] Subscribing session={Sid} conn={Conn} eventType={EventType}", sessionId, envelope.ChannelId, eventType);
                                var reader = ssm2.Subscribe(sessionId);
                                if (reader is null) { spLogger.LogWarning("[Program:SSM→WS] Subscribe returned null"); return; }
                                try
                                {
                                    spLogger.LogWarning("[Program:SSM→WS] Forward start conn={Conn} session={Sid}", envelope.ChannelId, sessionId);
                                    await foreach (var frame in reader.ReadAllAsync(CancellationToken.None))
                                    {
                                        var wsMsg = new PuddingCode.Platform.ConnectorMessage
                                        {
                                            Target = envelope.ChannelId,
                                            Content = System.Text.Json.JsonSerializer.Serialize(new { type = "sse", @event = frame.Event, data = frame.Data }),
                                        };
                                        try { await wsConnector.SendAsync(wsMsg, CancellationToken.None); }
                                        catch { break; }
                                    }
                                    spLogger.LogWarning("[Program:SSM→WS] Forward end conn={Conn} session={Sid}", envelope.ChannelId, sessionId);
                                }
                                finally
                                {
                                    ssm2.Unsubscribe(sessionId, reader);
                                }
                            }
                            catch (Exception fex) { spLogger.LogWarning(fex, "[Program:SSM→WS] Forward error"); }
                        });
                    }
                },
                sp.GetRequiredService<ILogger<ConnectorHost>>());
            return host;
        });
        builder.Services.AddSingleton<ConnectorDeliveryDispatcher>();
        builder.Services.AddHostedService(
            sp => sp.GetRequiredService<ConnectorDeliveryDispatcher>());
        builder.Services.AddSingleton<FeishuStreamingProjectionWorker>();
        builder.Services.AddHostedService(
            sp => sp.GetRequiredService<FeishuStreamingProjectionWorker>());

        // ── Cron 定时任务调度 ──────────────────────────────
        // HOSTED-DISABLED: builder.Services.AddHostedService<CronSchedulerService>();
        // HOSTED-DISABLED: builder.Services.AddHostedService<AgentDailySummaryHostedService>();
        // HOSTED-DISABLED: builder.Services.AddHostedService<StartupDailySummaryCompensationService>();

        // ILlmConfigService 已注册（见上方），同时注册 ILlmResolver 兼容旧接口。
        // Provider/model 配置仅从 data/config/llm.providers.json 读取，不再回落到 DB。
        builder.Services.AddSingleton<ILlmResolver>(sp =>
        {
            return new FileLlmResolver(
                sp.GetRequiredService<ILlmConfigService>(),
                sp.GetRequiredService<ILogger<FileLlmResolver>>());
        });
        builder.Services.AddSingleton<IVisualArtifactObservationService, VisualArtifactObservationService>();

        builder.Services.AddHttpClient("DirectLlm", client =>
        {
            client.Timeout = TimeSpan.FromHours(2);
        });

        builder.Services.AddHttpClient("HttpFetchSkill", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        builder.Services.AddHttpClient("SkillPackageDL", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
        });

        // ── TTS/ASR 语音 Provider ──
        builder.Services.AddHttpClient("DashScopeTts", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient("DashScopeAsr", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        builder.Services.AddSingleton<PuddingCode.Abstractions.IVoiceProviderFactory, PuddingRuntime.Services.VoiceProviderFactory>();

    }

}
