using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using Pudding.ToolPlugins.Windows;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Plugins;

/// <summary>Composition adapter. No plugin assembly is ever loaded into Core.</summary>
public sealed class ExecutablePluginService : IContextualPuddingToolSource, IAsyncDisposable, IDisposable
{
    private readonly Lazy<PluginManager> _manager;
    private readonly Lazy<PluginPackageStore> _packages;
    private readonly Lazy<PluginCoreServices> _core;
    private readonly bool _configured;
    private readonly IPluginAgentApiLeaseFactory? _agentApi;
    private readonly IPluginOperatorApiLeaseFactory? _operatorApi;
    private readonly PuddingRuntime.Operators.PublicApi.ModelEvaluationApiFactory? _modelApi;
    public ExecutablePluginService(PuddingDataPaths? paths = null, IPluginWorkerFactory? workers = null, IPluginAgentApiLeaseFactory? agentApi = null,
        IPluginOperatorApiLeaseFactory? operatorApi = null, PuddingRuntime.Operators.PublicApi.ModelEvaluationApiFactory? modelApi = null)
    {
        _agentApi = agentApi;
        _operatorApi = operatorApi;
        _modelApi = modelApi;
        _configured = paths is not null;
        string Root() => paths is null ? throw new PluginException("plugin.not_configured", "configure", "DataRoot is not configured.")
            : Path.Combine(paths.DataRoot, "tool-plugins");
        _packages = new(() => new PluginPackageStore(Path.Combine(Root(), "packages")));
        _core = new(() => new PluginCoreServices(Path.Combine(Root(), "state")));
        _manager = new(() => new PluginManager(Packages, workers ?? new WindowsPluginWorkerFactory(WorkerPath)));
    }
    public static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "tools", "plugin-worker", "Pudding.ToolPlugins.Worker.exe");
    public static string SdkDirectory => Path.GetDirectoryName(WorkerPath)!;
    public PluginPackageStore Packages => _packages.Value;
    public PluginManager Manager => _manager.Value;
    public long Revision => _manager.IsValueCreated ? Manager.Revision : 0;
    public static PluginScope Scope(ToolExecutionContext context) => new(context.WorkspaceId, context.AgentInstanceId, context.SessionId);
    public IReadOnlyList<IPuddingTool> ListTools(ToolExecutionContext context)
        => !_configured || !_manager.IsValueCreated ? [] : Manager.Tools(Scope(context)).Select(t => (IPuddingTool)new ExecutableTool(this, t)).ToArray();
    public PluginPackage Find(string contentHash)
    {
        var item = Packages.Scan().Packages.SingleOrDefault(p => p.ContentHash == contentHash && p.Manifest is not null)
            ?? throw new PluginException("plugin.package_not_found", "load", "No installed package matches contentHash. Import a v2 ZIP first.");
        return Packages.Get(item.Id, item.Version, item.ContentHash);
    }
    public async ValueTask DisposeAsync() { if (_manager.IsValueCreated) await Manager.DisposeAsync(); }
    public void Dispose() { if (_manager.IsValueCreated) Task.Run(async () => await Manager.DisposeAsync()).GetAwaiter().GetResult(); }

    private sealed class ExecutableTool : IPuddingTool
    {
        private readonly ExecutablePluginService _service;
        private readonly PluginCallable _callable;
        public ExecutableTool(ExecutablePluginService service, PluginCallable callable)
        {
            _service = service; _callable = callable;
            var definition = callable.Definition;
            var readOnly = definition.Effects.All(e => e is "none" or "filesystem.read");
            Descriptor = new()
            {
                ToolId = callable.FunctionName, Name = definition.Name, Description = definition.Description,
                Category = Enum.TryParse<ToolCategory>(definition.Category, true, out var category) ? category : ToolCategory.General,
                PermissionLevel = readOnly ? ToolPermissionLevel.Low : ToolPermissionLevel.High,
                Safety = (readOnly ? ToolSafetyFlags.ReadOnly : ToolSafetyFlags.None)
                    | (definition.Effects.Contains("process") ? ToolSafetyFlags.RequiresShell : ToolSafetyFlags.None)
                    | (definition.Effects.Contains("filesystem.write") ? ToolSafetyFlags.RequiresFileWrite : ToolSafetyFlags.None)
                    | (definition.Effects.Contains("network") ? ToolSafetyFlags.RequiresNetwork : ToolSafetyFlags.None),
                Parameters = new([], [], definition.InputSchema), SourceKind = "Plugin",
                SourceId = callable.ActivationId, RuntimeStatus = "Available", PermissionOwnerToolId = "plugin_load",
            };
        }
        public ToolDescriptor Descriptor { get; }
        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
        {
            try
            {
                var scope = Scope(request.Context);
                var activation = _service.Manager.List(scope).SingleOrDefault(a => a.ActivationId == _callable.ActivationId)
                    ?? throw new PluginException("plugin.scope_denied", "execute", "Activation belongs to another scope.");
                var package = _service.Packages.Get(activation.PluginId, activation.Version, activation.ContentHash);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(package.Manifest.Limits.ExecutionTimeoutSeconds);
                if (request.Context.ExecutionDeadlineUtc is { } outer && outer < deadline) deadline = outer;
                var capabilities = package.Manifest.RequestedCapabilities;
                var needsAgentApi = capabilities.Any(c => c.StartsWith("agent.configuration.", StringComparison.Ordinal));
                if (needsAgentApi && _service._agentApi is null)
                    throw new PluginException("capability.unsupported", "core_callback", "Agent API host adapter is not installed.");
                if (needsAgentApi && request.Context.ConfigurationAgentInstanceId is { } owner && owner != scope.AgentId)
                    throw new PluginException("plugin.scope_denied", "core_callback", "Configuration delegation currently requires a persistent execution Agent.");
                await using var agentApi = needsAgentApi ? _service._agentApi!.Open(new(scope, activation.PluginId, activation.ActivationId,
                    request.ToolCallId, deadline, capabilities.Contains("agent.configuration.read"),
                    capabilities.Contains("agent.configuration.update") && _callable.Definition.Effects.Contains("agent.configuration.write"),
                    capabilities.Contains("agent.configuration.changes"))) : null;
                var needsOperators = capabilities.Any(c => c is "operators.list" or "operators.skill-value.score");
                if (needsOperators && _service._operatorApi is null)
                    throw new PluginException("capability.unsupported", "core_callback", "Operator API host adapter is not installed.");
                await using var operatorApi = needsOperators ? _service._operatorApi!.Open(new(scope, activation.PluginId, activation.ActivationId,
                    request.ToolCallId, deadline, capabilities.Contains("operators.list"), capabilities.Contains("operators.skill-value.score"))) : null;
                var needsModelApi = capabilities.Any(IsModelMethod);
                if (needsModelApi && _service._modelApi is null) throw new PluginException("capability.unsupported", "core_callback", "Model evaluation host adapter is not installed.");
                await using var modelApi = needsModelApi ? _service._modelApi!.Open(request.Context, deadline,
                    capabilities.Contains("operators.model.list"), capabilities.Contains("operators.text.classify"), capabilities.Contains("operators.delivery.review")) : null;
                using var json = JsonDocument.Parse(request.ArgumentsJson);
                var result = await _service.Manager.ExecuteAsync(Descriptor.ToolId, scope, request.ToolCallId, json.RootElement,
                    (method, args, token) => method.StartsWith("agent.", StringComparison.Ordinal)
                        ? agentApi?.CallAsync(method, args, token) ?? throw new PluginException("capability.unsupported", "core_callback", "Agent API is unavailable.")
                        : IsModelMethod(method)
                            ? modelApi?.CallAsync(method, args, token) ?? throw new PluginException("capability.unsupported", "core_callback", "Model evaluation API is unavailable.")
                        : method.StartsWith("operators.", StringComparison.Ordinal)
                            ? operatorApi?.CallAsync(method, args, token) ?? throw new PluginException("capability.unsupported", "core_callback", "Operator API is unavailable.")
                        : _service._core.Value.CallAsync(package, scope, request.Context.WorkingDirectory, method, args, token),
                    request.Context.ExecutionDeadlineUtc, ct);
                return new() { Success = result.Success, Output = result.Value.GetRawText(), Error = result.Error, ExitCode = result.Success ? 0 : 1 };
            }
            catch (PluginException ex)
            {
                return new() { Success = false, Output = JsonSerializer.Serialize(ex.Failure, PluginJson.Options), Error = ex.Message,
                    ExitCode = 1, Status = ex.Failure.Code };
            }
        }
        private static bool IsModelMethod(string method) => method is "operators.model.list" or "operators.text.classify" or "operators.delivery.review";
    }
}
