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
    private static void AddBootstrapServices(
        WebApplicationBuilder builder,
        PuddingDataPaths dataPaths)
    {
        // ── Bootstrap 初始化 ─────────────────────────────────
        var stateFilePath = Path.Combine(dataPaths.RuntimeRoot, "bootstrap-state.json");

        if (!File.Exists(stateFilePath))
        {
            var secretBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            var secret = Convert.ToBase64String(secretBytes);
            var initialState = System.Text.Json.JsonSerializer.Serialize(new
            {
                Bootstrap = new { Secret = secret, Initialized = false }
            });
            File.WriteAllText(stateFilePath, initialState);
        }

        builder.Configuration.AddJsonFile(stateFilePath, optional: true, reloadOnChange: true);
        builder.Services.AddSingleton<BootstrapStateService>(sp =>
            new BootstrapStateService(stateFilePath, sp.GetRequiredService<IConfiguration>()));

        // ── JSON 配置种子服务 ─────────────────────────────
        builder.Services.AddScoped<JsonConfigSeedService>();

        // ── Agent 头像服务（ADR-034 revised ─ JSON 内存目录）────
        builder.Services.AddSingleton<AgentAvatarCatalog>();
        builder.Services.AddSingleton<IAgentAvatarCatalog>(sp => sp.GetRequiredService<AgentAvatarCatalog>());
    }

}
