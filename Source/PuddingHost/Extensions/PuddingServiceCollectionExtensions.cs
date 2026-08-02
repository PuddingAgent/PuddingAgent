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
using PuddingHost.Hosting;
using System.Threading.Channels;

namespace PuddingAgent.Services;

/// <summary>
/// Owns the host composition root registrations that previously lived in Program.cs.
/// Registration order is preserved while the groups expose their architectural boundary.
/// </summary>
public static partial class PuddingServiceCollectionExtensions
{
    public static WebApplicationBuilder AddPuddingApplicationServices(
        this WebApplicationBuilder builder,
        PuddingDataPaths dataPaths,
        IConfiguration bootstrapConfiguration,
        string aspnetcoreEnvironment)
    {
        AddPlatformServices(builder, dataPaths, aspnetcoreEnvironment);
        AddRuntimeServices(builder, dataPaths, bootstrapConfiguration);
        AddConnectorServices(builder);
        AddBootstrapServices(builder, dataPaths);
        AddBrowserBridgeServices(builder);
        return builder;
    }

}
