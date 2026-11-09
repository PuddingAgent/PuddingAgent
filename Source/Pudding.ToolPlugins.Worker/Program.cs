using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Protocol;
using Pudding.Api.Contracts;

if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--activation") return 2;
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
var activation = args[3];
try
{
    await pipe.ConnectAsync(15000);
    IToolPlugin? plugin = null; WorkerContext? context = null;
    while (true)
    {
        var request = await PluginWire.ReadAsync(pipe, CancellationToken.None);
        if (request.ActivationId != activation || request.ReplyTo is not null || request.Deadline is null) return 3;
        using var deadline = new CancellationTokenSource();
        var remaining = request.Deadline.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return 4;
        deadline.CancelAfter(remaining);
        JsonElement response; var kind = "reply";
        try
        {
            switch (request.Kind)
            {
                case "initialize" when plugin is null:
                    var init = request.Payload.Deserialize<InitializePayload>(PluginJson.Options)!;
                    var assemblyPath = Path.GetFullPath(Path.Combine(init.Directory, init.Manifest.Entry.Assembly));
                    if (!assemblyPath.StartsWith(Path.GetFullPath(init.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 5;
                    var loader = new PluginLoadContext(assemblyPath, init.Directory);
                    var type = loader.LoadFromAssemblyPath(assemblyPath).GetType(init.Manifest.Entry.Type, throwOnError: true)!;
                    plugin = Activator.CreateInstance(type) as IToolPlugin
                        ?? throw new PluginException("plugin.entry_invalid", "initialize", "Entry does not implement IToolPlugin.");
                    context = new WorkerContext(init.Scope, pipe, activation);
                    context.CurrentRequest = request;
                    await plugin.InitializeAsync(context, deadline.Token);
                    response = PluginJson.Value(new { toolIds = plugin.ToolIds, hookIds = plugin.HookIds, subscriptionIds = plugin.SubscriptionIds });
                    break;
                case "execute" when plugin is not null:
                    context!.CurrentRequest = request;
                    response = PluginJson.Value(await plugin.ExecuteAsync(request.Payload.Deserialize<PluginCall>(PluginJson.Options)!, deadline.Token));
                    break;
                case "hook" when plugin is not null:
                    context!.CurrentRequest = request;
                    response = PluginJson.Value(await plugin.OnToolResultAsync(request.Payload.Deserialize<PluginHookCall>(PluginJson.Options)!, deadline.Token));
                    break;
                case "event" when plugin is not null:
                    context!.CurrentRequest = request;
                    await plugin.OnWorkspaceEventAsync(
                        request.Payload.Deserialize<PluginWorkspaceEvent>(PluginJson.Options)!, deadline.Token);
                    response = PluginJson.Value(new { accepted = true });
                    break;
                case "shutdown" when plugin is not null:
                    await plugin.ShutdownAsync(deadline.Token);
                    await PluginWire.WriteAsync(pipe, new("reply", Guid.NewGuid().ToString("N"), activation, PluginJson.Value(new { stopped = true }), request.RequestId), CancellationToken.None);
                    return 0;
                default: throw new PluginException("plugin.protocol_invalid", "execute", "Unexpected lifecycle message.");
            }
        }
        catch (Exception ex)
        {
            kind = "error";
            // Exception bodies can contain arbitrary data. Report the type; preserve correlation without secrets.
            var error = ex is PluginException p ? p.Failure : ex is ConfigurationApiException config
                ? new PluginFailure(config.Code, "core_callback", Guid.NewGuid().ToString("N"), config.Message, config.EffectOutcome)
                // A read-only scoring failure cannot certify earlier effects of the enclosing tool.
                : ex is OperatorApiException op ? new PluginFailure(op.Code, "core_callback", Guid.NewGuid().ToString("N"), op.Message,
                    request.Kind == "execute" ? "unknown" : "none")
                : ex is ModelEvaluationException evaluation ? new PluginFailure(evaluation.Code, "core_callback", Guid.NewGuid().ToString("N"), evaluation.Message,
                    request.Kind == "execute" ? "unknown" : "none")
                : new PluginFailure(
                request.Kind == "initialize" ? "plugin.initialize_failed" : "plugin.execution_failed", request.Kind,
                Guid.NewGuid().ToString("N"), $"Plugin raised {ex.GetType().Name}.", request.Kind == "execute" ? "unknown" : "none");
            response = PluginJson.Value(error);
        }
        if (context is not null) context.CurrentRequest = null;
        try { await PluginWire.WriteAsync(pipe, new(kind, Guid.NewGuid().ToString("N"), activation, response, request.RequestId), CancellationToken.None); }
        catch (PluginException ex) { await PluginWire.WriteAsync(pipe, new("error", Guid.NewGuid().ToString("N"), activation, PluginJson.Value(ex.Failure), request.RequestId), CancellationToken.None); }
    }
}
catch (Exception ex)
{
    // Diagnostic metadata only: exception messages may contain paths or plugin input.
    Console.Error.WriteLine($"Plugin worker transport failed: {ex.GetType().Name} (0x{ex.HResult:X8}) at {new System.Diagnostics.StackTrace(ex).GetFrame(0)?.GetMethod()?.Name}.");
    return 1;
}

sealed class PluginLoadContext(string assembly, string directory) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(assembly);
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is "Pudding.ToolPlugins.Contracts" or "Pudding.ToolPlugins.Sdk" or "Pudding.ToolPlugins.AgentSdk" or "Pudding.Api.Contracts" or "Pudding.Api.Sdk")
            return Default.LoadFromAssemblyName(name);
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is null) return null;
        EnsureInside(path); return LoadFromAssemblyPath(path);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        // Native code is not part of the first supported package contract.
        throw new DllNotFoundException("Native plugin dependencies are not supported by this SDK version.");
    }
    private void EnsureInside(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new FileLoadException("Dependency resolves outside package.");
    }
}
sealed class WorkerContext(PluginScope scope, Stream pipe, string activation) : IPluginContext, IPluginCoreApi, IPluginAgentApiTransport, IPluginEventAudit
{
    public PluginScope Scope => scope;
    public IPluginCoreApi Core => this;
    private readonly AsyncLocal<RequestLease?> _request = new();
    public PluginFrame? CurrentRequest
    {
        get => _request.Value?.Frame;
        set
        {
            // Tasks spawned by plugin code retain the old lease object, never a later invocation's identity.
            if (_request.Value is { } previous) previous.Active = false;
            _request.Value = value is null ? null : new(value);
        }
    }
    private sealed class RequestLease(PluginFrame frame)
    {
        public PluginFrame Frame { get; } = frame;
        public volatile bool Active = true;
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private async Task<JsonElement> Call(string method, object arguments, CancellationToken ct)
    {
        var lease = _request.Value;
        await _gate.WaitAsync(ct);
        try
        {
            if (lease is null || !lease.Active || lease.Frame.Deadline <= DateTimeOffset.UtcNow)
                throw new PluginException("plugin.invocation_expired", "core_callback", "This callback's invocation has ended.");
            var parent = lease.Frame;
            var id = Guid.NewGuid().ToString("N");
            await PluginWire.WriteAsync(pipe, new("core_call", id, activation, PluginJson.Value(new { method, arguments }), parent.RequestId), ct);
            var reply = await PluginWire.ReadAsync(pipe, ct);
            if (reply.ActivationId != activation || reply.ReplyTo != id) throw new InvalidOperationException("Unexpected callback reply.");
            if (reply.Kind == "error") throw new PluginException(reply.Payload.Deserialize<PluginFailure>(PluginJson.Options)!);
            if (reply.Kind != "reply") throw new InvalidOperationException("Unexpected callback reply kind.");
            return reply.Payload;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<string> ReadWorkspaceTextAsync(string relativePath, CancellationToken ct = default) => (await Call("workspace.read", new { path = relativePath }, ct)).GetString()!;
    public async ValueTask<string> ReadAssetTextAsync(string relativePath, CancellationToken ct = default) => (await Call("assets.read", new { path = relativePath }, ct)).GetString()!;
    public async ValueTask<string?> ReadStateAsync(string key, CancellationToken ct = default) => (await Call("state.read", new { key }, ct)).GetString();
    public async ValueTask WriteStateAsync(string key, string value, CancellationToken ct = default) => _ = await Call("state.write", new { key, value }, ct);
    public async ValueTask<JsonElement> InvokeAgentApiAsync(string method, JsonElement arguments, CancellationToken ct = default)
        => await Call(method, arguments, ct);
    public async ValueTask<bool> RequestArchiveAsync(string eventId, CancellationToken ct = default)
    {
        var reply = await Call("plugin.archive.record", new { eventId }, ct);
        return reply.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True;
    }
}
