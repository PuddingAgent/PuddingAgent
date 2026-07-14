using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Diagnostics;
using PuddingPlatform.Services.AgentChat;
using PuddingPlatform.Services.MessageFabric;
using PuddingPlatform.Services.TaskPlanning;
using PuddingRuntime;
using PuddingRuntime.Services;
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

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Modular service registration for PuddingAgent startup.
/// Splits the monolithic Program.cs into focused Add* extension methods.
/// </summary>
public static class PuddingServiceCollectionExtensions
{
    public static IServiceCollection AddPuddingCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlatformDbContext>((sp, options) =>
        {
            var dataPath = configuration.GetValue<string>("Pudding:DataPath") ?? "data";
            var dbPath = Path.Combine(dataPath, "platform.db");
            options.UseSqlite($"Data Source={dbPath}");
            options.ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.AmbientTransactionWarning));
        });

        services.AddDbContextFactory<PlatformDbContext>((sp, options) =>
        {
            var dataPath = configuration.GetValue<string>("Pudding:DataPath") ?? "data";
            var dbPath = Path.Combine(dataPath, "platform.db");
            options.UseSqlite($"Data Source={dbPath}");
            options.ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.AmbientTransactionWarning));
        }, ServiceLifetime.Scoped);

        services.AddSingleton<ISessionStateManager>(sp => sp.GetRequiredService<SessionStateManager>());
        services.AddSingleton<ISessionEventReader>(sp => sp.GetRequiredService<SessionStateManager>());
        services.AddSingleton<ISessionHeadNotifier>(sp => sp.GetRequiredService<SessionStateManager>());
        services.AddSingleton<ISessionEventStream, SessionEventStreamService>();
        services.TryAddSingleton<ISessionTimelineRecorder, SessionTimelineRecorder>();
        services.TryAddSingleton<ITelemetryMetricSink, TelemetryMetricSink>();

        return services;
    }

    public static IServiceCollection AddPuddingChatExecution(this IServiceCollection services)
    {
        services.AddSingleton<IChatCommandStore, ChatCommandStore>();
        services.AddSingleton<ChatCommandAcceptanceService>();
        services.AddSingleton<ChatTelemetryRecorder>();
        services.AddHostedService<ChatExecutionWorker>();

        return services;
    }

    public static IServiceCollection AddPuddingMemory(this IServiceCollection services)
    {
        services.TryAddSingleton<IMemoryLibrary, MemoryLibrary>();
        services.TryAddSingleton<IMemoryEngine, MemoryEngine>();
        services.TryAddSingleton<IMemoryLibraryConvenience, MemoryLibraryConvenience>();
        services.TryAddSingleton<ISubconsciousOrchestrator, SubconsciousOrchestrator>();

        return services;
    }
}
