using Microsoft.Extensions.DependencyInjection.Extensions;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using PuddingRuntime.Services.Skills;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services.Plugins;

/// <summary>Only the product composition root enables these installation-owned declarative plugins.</summary>
public sealed class HostToolContributions : IContextualPuddingToolSource, IAsyncDisposable
{
    private sealed record Binding(Type Type, bool AdaptSkill);
    private readonly Dictionary<string, HostToolPackage> _packages;
    private sealed record BoundTool(Lazy<IPuddingTool> Implementation, Lazy<ToolDescriptor> Metadata);
    private readonly Dictionary<string, BoundTool> _bindings;
    private readonly HostToolMounts _mounts = new();
    private readonly object _autoGate = new();
    private readonly HashSet<string> _autoSuppressed = new(StringComparer.Ordinal);
    private readonly IToolPermissionPolicyService _permissions;
    private static readonly HashSet<Type> Kernel = [typeof(SearchToolsTool), typeof(RequestToolApprovalTool),
        typeof(ListToolApprovalsTool), typeof(PluginCatalogTool), typeof(PluginSearchTool), typeof(PluginDiagnosticsTool),
        typeof(PluginImportTool), typeof(PluginLoadTool), typeof(PluginUnloadTool), typeof(PluginUninstallTool)];

    private HostToolContributions(IServiceProvider services)
        : this(services, HostToolPackage.ReadInstalled(
            Path.Combine(AppContext.BaseDirectory, "tools", "host-packages")))
    {
    }

    /// <summary>Same wiring with an explicit package list, so tests do not depend on the install directory.</summary>
    internal HostToolContributions(IServiceProvider services, IReadOnlyList<HostToolPackage> packages)
    {
        _permissions = services.GetRequiredService<IToolPermissionPolicyService>();
        _packages = packages.ToDictionary(p => p.ContentHash, StringComparer.Ordinal);
        _bindings = services.GetServices<Binding>().ToDictionary(b => b.Type.FullName!, b =>
        {
            var implementation = new Lazy<IPuddingTool>(() => b.AdaptSkill
                ? new AgentSkillToolAdapter((IAgentSkill)services.GetRequiredService(b.Type))
                : (IPuddingTool)services.GetRequiredService(b.Type));
            return new BoundTool(implementation, new(() => Describe(b.Type, implementation)));
        }, StringComparer.Ordinal);
    }

    private static ToolDescriptor Describe(Type type, Lazy<IPuddingTool> implementation)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (!baseType.IsGenericType) continue;
            var generic = baseType.GetGenericTypeDefinition();
            if (generic == typeof(PuddingToolBase<>) || generic == typeof(PuddingToolBase<,>))
                return ToolDescriptorFactory.Create(type, baseType.GetGenericArguments()[0]);
        }
        // Legacy skills and hand-written descriptors own their metadata on the instance.
        return implementation.Value.Descriptor;
    }

    // Inventory for configuration and approval. A descriptor alone supplies no callable.
    public IEnumerable<ToolDescriptor> InstalledDescriptors()
    {
        foreach (var package in _packages.Values)
            foreach (var (id, type) in package.Exports)
                if (_bindings.TryGetValue(type, out var tool))
                    yield return Export(tool, id, package.Id) with { RuntimeStatus = "Installed", IsEnabledByDefault = false };
    }

    private static ToolDescriptor Export(BoundTool tool, string id, string package)
    {
        var descriptor = tool.Metadata.Value;
        if (descriptor.ToolId != id) throw new InvalidDataException($"Incorrect host tool binding: {id}");
        return descriptor with { SourceKind = "Plugin", SourceId = package };
    }

    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(sp => new HostToolContributions(sp));
        services.AddSingleton<IContextualPuddingToolSource>(sp => sp.GetRequiredService<HostToolContributions>());
    }

    internal static bool RegisterBinding(IServiceCollection services, Type type)
    {
        if (!services.Any(d => d.ServiceType == typeof(HostToolContributions))) return false;
        if (services.Any(d => d.ImplementationInstance is Binding b && b.Type == type)) return true;
        services.TryAdd(ServiceDescriptor.Singleton(type, type));
        var adapt = !typeof(IPuddingTool).IsAssignableFrom(type);
        if (adapt && !typeof(IAgentSkill).IsAssignableFrom(type))
            throw new InvalidOperationException($"Invalid host tool binding {type.FullName}.");
        services.AddSingleton(new Binding(type, adapt));
        if (Kernel.Contains(type))
            services.AddSingleton<IPuddingTool>(sp => (IPuddingTool)sp.GetRequiredService(type));
        return true;
    }

    public long Revision => _mounts.Revision;
    public object Catalog(ToolExecutionContext? caller = null)
    {
        var packages = _packages.Values.OrderBy(p => p.Id).Select(p => new
        {
            p.Id, p.Version, p.ContentHash, kind = "host-service",
            tools = p.Exports.Where(e => caller is null || Visible(e.Key, e.Value, caller))
                .Select(e => e.Key).Order(StringComparer.Ordinal).ToArray()
        }).Where(p => p.tools.Length > 0).ToArray();
        var visible = packages.Select(p => p.ContentHash).ToHashSet(StringComparer.Ordinal);
        return new
        {
            revision = Revision,
            packages,
            activations = _mounts.List(caller is null ? null : ExecutablePluginService.Scope(caller))
                .Where(a => visible.Contains(a.ContentHash)),
            unassignedBindings = caller is null
                ? _bindings.Keys.Except(Kernel.Select(t => t.FullName!), StringComparer.Ordinal)
                    .Except(_packages.Values.SelectMany(p => p.Exports.Values), StringComparer.Ordinal).Order().ToArray()
                : null
        };
    }

    private bool Visible(string id, string type, ToolExecutionContext caller) =>
        Selected(id, caller) && _bindings.TryGetValue(type, out var binding) && Permitted(binding.Metadata.Value, caller);

    /// <summary>Typed view for callers that need the package inventory without parsing the catalog payload.</summary>
    public sealed record HostPackageView(string Id, string Version, string ContentHash, IReadOnlyList<string> Tools, bool Active);

    public IReadOnlyList<HostPackageView> Packages(ToolExecutionContext? caller = null)
    {
        var active = _mounts.List(caller is null ? null : ExecutablePluginService.Scope(caller))
            .Where(a => a.State == "Active")
            .Select(a => a.ContentHash)
            .ToHashSet(StringComparer.Ordinal);
        return _packages.Values
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new HostPackageView(p.Id, p.Version, p.ContentHash,
                p.Exports.Where(e => caller is null || Visible(e.Key, e.Value, caller))
                    .Select(e => e.Key).Order(StringComparer.Ordinal).ToArray(),
                active.Contains(p.ContentHash)))
            .Where(p => p.Tools.Count > 0)
            .ToArray();
    }

    public bool IsInstalled(string hash) => _packages.ContainsKey(hash);
    public PluginActivation Load(string hash, PluginScope scope, string operation, long? revision, bool approved, ToolExecutionContext? caller)
    {
        if (!approved) throw new PluginException("plugin.capability_denied", "load", "Explicit load approval is required.");
        var package = _packages[hash];
        lock (_autoGate) _autoSuppressed.Remove(AutoKey(scope, hash));
        var activation = _mounts.Load(package, scope, operation, revision);
        return activation with { ToolNames = activation.State == "Active"
            ? package.Exports.Where(e => caller is null || Visible(e.Key, e.Value, caller)).Select(e => e.Key).ToArray()
            : [] };
    }
    public async Task<PluginActivation> UnloadAsync(string id, PluginScope scope, long? revision, CancellationToken ct)
    {
        // An explicit unload pins the package off for this agent until it is loaded again.
        var target = _mounts.List(scope).FirstOrDefault(a => a.ActivationId == id);
        if (target is not null)
            lock (_autoGate) _autoSuppressed.Add(AutoKey(scope, target.ContentHash));
        return await _mounts.UnloadAsync(id, scope, revision, ct);
    }

    public IReadOnlyList<IPuddingTool> ListTools(ToolExecutionContext context)
    {
        if (context.CapabilityPolicy is null) return [];
        AutoMount(context);
        var tools = new List<IPuddingTool>();
        foreach (var a in _mounts.List(ExecutablePluginService.Scope(context)).Where(a => a.State == "Active"))
        {
            foreach (var (id, type) in _packages[a.ContentHash].Exports)
            {
                if (!Selected(id, context) || !_bindings.TryGetValue(type, out var binding)) continue;
                var descriptor = Export(binding, id, a.PluginId);
                if (Permitted(descriptor, context))
                    tools.Add(new MountedTool(this, a, descriptor, binding.Implementation));
            }
        }
        return tools;
    }

    private static string AutoKey(PluginScope scope, string hash) =>
        $"{scope.WorkspaceId}|{scope.AgentId}|{hash}";

    /// <summary>
    /// Mounts declarative host packages on first use. A host package exports only operation names the product
    /// already registered, so mounting executes nothing; every call is still authorized by the agent capability
    /// policy in <see cref="Permitted"/>. An explicit unload pins the package off until it is loaded again.
    /// </summary>
    private void AutoMount(ToolExecutionContext context)
    {
        var scope = ExecutablePluginService.Scope(context);
        var active = _mounts.List(scope)
            .Where(a => a.State == "Active")
            .Select(a => a.ContentHash)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var package in _packages.Values)
        {
            if (active.Contains(package.ContentHash)) continue;
            lock (_autoGate)
                if (_autoSuppressed.Contains(AutoKey(scope, package.ContentHash))) continue;
            var offered = package.Exports.Any(e => _bindings.TryGetValue(e.Value, out var binding)
                && Selected(e.Key, context)
                && Permitted(Export(binding, e.Key, package.Id), context));
            if (!offered) continue;
            try
            {
                _mounts.Load(package, scope, "auto:" + package.ContentHash, null);
            }
            catch (PluginException)
            {
                // An unmountable scope simply keeps its tools hidden; the catalog still reports real state.
            }
        }
    }

    private static bool Selected(string id, ToolExecutionContext context) =>
        context.CapabilityPolicy is { } p && (p.AllowedToolNames.Count > 0
            ? p.AllowedToolNames.Contains(id, StringComparer.OrdinalIgnoreCase) : p.GetAllEffectiveToolNames().Contains(id));

    private bool Permitted(ToolDescriptor tool, ToolExecutionContext context) =>
        Selected(tool.ToolId, context) && context.CapabilityPolicy is { } p
        && (!tool.Safety.HasFlag(ToolSafetyFlags.RequiresShell) || p.AllowShellExecution)
        && (!tool.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite) || p.AllowFileWrite)
        && _permissions.CanExposeToAgent(tool, p);

    private sealed class MountedTool(HostToolContributions owner, PluginActivation activation,
        ToolDescriptor descriptor, Lazy<IPuddingTool> tool) : IPuddingTool
    {
        public ToolDescriptor Descriptor { get; } = descriptor;
        public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
        {
            if (!owner.Permitted(Descriptor, request.Context))
                return new() { Success = false, ExitCode = 1, Error = "Tool is outside the agent capability policy." };
            try
            {
                return await owner._mounts.RunAsync(activation.ActivationId, ExecutablePluginService.Scope(request.Context),
                    token => tool.Value.ExecuteAsync(request, token), ct);
            }
            catch (PluginException ex) { return new() { Success = false, ExitCode = 1, Error = ex.Message, Status = ex.Failure.Code }; }
        }
    }

    public ValueTask DisposeAsync() => _mounts.DisposeAsync();
}
