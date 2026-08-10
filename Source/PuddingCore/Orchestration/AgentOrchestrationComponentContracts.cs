using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingCode.Orchestration;

/// <summary>Stable semantic data types used by orchestration ports.</summary>
public static class AgentOrchestrationDataTypes
{
    public const string Any = "pudding.any";
    public const string Boolean = "pudding.boolean";
    public const string Content = "pudding.content";
    public const string Event = "pudding.event";
    public const string Json = "pudding.json";
    public const string Number = "pudding.number";
    public const string Text = "pudding.text";
    public const string Artifact = "pudding.artifact";
}

/// <summary>How many values a port can accept or produce.</summary>
public enum AgentOrchestrationPortCardinality
{
    One,
    Many
}

/// <summary>How a port value is delivered between nodes.</summary>
public enum AgentOrchestrationValueDelivery
{
    Inline,
    Artifact,
    Stream,
    Event
}

/// <summary>Side-effect class used by activation policy and the editor.</summary>
public enum AgentOrchestrationSideEffect
{
    None,
    Read,
    Write
}

/// <summary>One immutable artifact carried by a graph input or node output.</summary>
public sealed record AgentOrchestrationArtifactReference
{
    public required string ArtifactId { get; init; }
    public required string ContentType { get; init; }
    public string? FileName { get; init; }
    public long? SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// A small inline JSON value or one/many durable artifacts. Large media is never embedded as base64.
/// </summary>
public sealed record AgentOrchestrationValueEnvelope
{
    public required string DataType { get; init; }
    public string? ContentType { get; init; }
    public JsonElement? InlineValue { get; init; }
    public IReadOnlyList<AgentOrchestrationArtifactReference> Artifacts { get; init; }
        = Array.Empty<AgentOrchestrationArtifactReference>();
}

/// <summary>Reusable type, media, cardinality, and delivery contract.</summary>
public sealed record AgentOrchestrationDataContract
{
    public required string DataType { get; init; }
    public IReadOnlyList<string> MediaTypes { get; init; } = Array.Empty<string>();
    public AgentOrchestrationPortCardinality Cardinality { get; init; }
        = AgentOrchestrationPortCardinality.One;
    public IReadOnlyList<AgentOrchestrationValueDelivery> Deliveries { get; init; }
        = [AgentOrchestrationValueDelivery.Inline];
}

/// <summary>One typed component port exposed to the compiler and graph editor.</summary>
public sealed record AgentOrchestrationPortDefinition
{
    public required string PortId { get; init; }
    public required string DisplayName { get; init; }
    public required AgentOrchestrationDataContract Contract { get; init; }
    public bool Required { get; init; }
}

/// <summary>Versioned component contract registered by Core or a trusted extension.</summary>
public sealed record AgentOrchestrationComponentDescriptor
{
    public required string ComponentType { get; init; }
    public required string Version { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public required AgentOrchestrationNodeKind NodeKind { get; init; }
    public required string ExecutorId { get; init; }
    public string? ConfigSchemaReference { get; init; }
    public AgentOrchestrationSideEffect SideEffect { get; init; }
    public IReadOnlyList<AgentOrchestrationPortDefinition> InputPorts { get; init; }
        = Array.Empty<AgentOrchestrationPortDefinition>();
    public IReadOnlyList<AgentOrchestrationPortDefinition> OutputPorts { get; init; }
        = Array.Empty<AgentOrchestrationPortDefinition>();
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
}

/// <summary>Reference frozen into a graph revision after registry resolution.</summary>
public sealed record AgentOrchestrationComponentReference
{
    public required string ComponentType { get; init; }
    public required string Version { get; init; }
    public string? ContractHash { get; init; }
}

/// <summary>Resolved component plus its deterministic contract hash.</summary>
public sealed record AgentOrchestrationRegisteredComponent
{
    public required AgentOrchestrationComponentDescriptor Descriptor { get; init; }
    public required string ContractHash { get; init; }
}

/// <summary>Stable built-in component identifiers.</summary>
public static class AgentOrchestrationComponentTypes
{
    public const string SubAgent = "pudding.agent.subagent";
    public const string ToolInvoke = "pudding.tool.invoke";
    public const string Gate = "pudding.control.gate";
    public const string HumanInput = "pudding.control.human-input";
}

/// <summary>Versioned event source that creates a new graph run.</summary>
public sealed record AgentOrchestrationTriggerDescriptor
{
    public required string TriggerType { get; init; }
    public required string Version { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public string? ConfigSchemaReference { get; init; }
    public required string ExecutorId { get; init; }
}

public sealed record AgentOrchestrationTriggerReference
{
    public required string TriggerType { get; init; }
    public required string Version { get; init; }
    public string? ContractHash { get; init; }
}

public sealed record AgentOrchestrationRegisteredTrigger
{
    public required AgentOrchestrationTriggerDescriptor Descriptor { get; init; }
    public required string ContractHash { get; init; }
}

public static class AgentOrchestrationTriggerTypes
{
    public const string Manual = "pudding.trigger.manual";
    public const string ChatMessage = "pudding.trigger.chat-message";
    public const string Schedule = "pudding.trigger.schedule";
    public const string Webhook = "pudding.trigger.webhook";
    public const string ConnectorEvent = "pudding.trigger.connector-event";
    public const string OrchestrationEvent = "pudding.trigger.orchestration-event";
}

/// <summary>Maps a trigger payload field into a named graph input.</summary>
public sealed record AgentOrchestrationTriggerInputBinding
{
    public string SourcePath { get; init; } = "$";
    public required string TargetInputId { get; init; }
}

/// <summary>One configured graph trigger. A trigger starts a new run; it is not a long-lived DAG node.</summary>
public sealed record AgentOrchestrationTriggerDefinition
{
    public required string TriggerId { get; init; }
    public required AgentOrchestrationTriggerReference Trigger { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyDictionary<string, JsonElement> Configuration { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    public IReadOnlyList<AgentOrchestrationTriggerInputBinding> InputBindings { get; init; }
        = Array.Empty<AgentOrchestrationTriggerInputBinding>();
}

/// <summary>Read-only registry consumed by compilation, API discovery, and the Admin component palette.</summary>
public interface IAgentOrchestrationComponentRegistry
{
    IReadOnlyList<AgentOrchestrationRegisteredComponent> Components { get; }
    IReadOnlyList<AgentOrchestrationRegisteredTrigger> Triggers { get; }

    bool TryResolveComponent(
        string componentType,
        string version,
        out AgentOrchestrationRegisteredComponent component);

    bool TryResolveTrigger(
        string triggerType,
        string version,
        out AgentOrchestrationRegisteredTrigger trigger);
}

/// <summary>Immutable registry with the minimal built-ins required by the current runtime.</summary>
public sealed class AgentOrchestrationComponentRegistry : IAgentOrchestrationComponentRegistry
{
    private readonly IReadOnlyDictionary<string, AgentOrchestrationRegisteredComponent> _components;
    private readonly IReadOnlyDictionary<string, AgentOrchestrationRegisteredTrigger> _triggers;

    public static AgentOrchestrationComponentRegistry Default { get; } = CreateDefault();

    public AgentOrchestrationComponentRegistry(
        IEnumerable<AgentOrchestrationComponentDescriptor> components,
        IEnumerable<AgentOrchestrationTriggerDescriptor>? triggers = null)
    {
        ArgumentNullException.ThrowIfNull(components);

        _components = BuildComponentIndex(components);
        _triggers = BuildTriggerIndex(triggers ?? Array.Empty<AgentOrchestrationTriggerDescriptor>());
        Components = Array.AsReadOnly(_components.Values.OrderBy(value => value.Descriptor.ComponentType, StringComparer.Ordinal)
            .ThenBy(value => value.Descriptor.Version, StringComparer.Ordinal).ToArray());
        Triggers = Array.AsReadOnly(_triggers.Values.OrderBy(value => value.Descriptor.TriggerType, StringComparer.Ordinal)
            .ThenBy(value => value.Descriptor.Version, StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<AgentOrchestrationRegisteredComponent> Components { get; }
    public IReadOnlyList<AgentOrchestrationRegisteredTrigger> Triggers { get; }

    public bool TryResolveComponent(
        string componentType,
        string version,
        out AgentOrchestrationRegisteredComponent component)
        => _components.TryGetValue(Key(componentType, version), out component!);

    public bool TryResolveTrigger(
        string triggerType,
        string version,
        out AgentOrchestrationRegisteredTrigger trigger)
        => _triggers.TryGetValue(Key(triggerType, version), out trigger!);

    private static IReadOnlyDictionary<string, AgentOrchestrationRegisteredComponent> BuildComponentIndex(
        IEnumerable<AgentOrchestrationComponentDescriptor> descriptors)
    {
        var result = new Dictionary<string, AgentOrchestrationRegisteredComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            ValidateComponentDescriptor(descriptor);
            var key = Key(descriptor.ComponentType, descriptor.Version);
            var snapshot = Snapshot(descriptor);
            if (!result.TryAdd(key, new AgentOrchestrationRegisteredComponent
                {
                    Descriptor = snapshot,
                    ContractHash = ComputeHash(snapshot)
                }))
            {
                throw new ArgumentException($"Duplicate orchestration component '{key}'.", nameof(descriptors));
            }
        }

        return new ReadOnlyDictionary<string, AgentOrchestrationRegisteredComponent>(result);
    }

    private static IReadOnlyDictionary<string, AgentOrchestrationRegisteredTrigger> BuildTriggerIndex(
        IEnumerable<AgentOrchestrationTriggerDescriptor> descriptors)
    {
        var result = new Dictionary<string, AgentOrchestrationRegisteredTrigger>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            ValidateTriggerDescriptor(descriptor);
            var key = Key(descriptor.TriggerType, descriptor.Version);
            var snapshot = descriptor with
            {
                TriggerType = descriptor.TriggerType.Trim(),
                Version = descriptor.Version.Trim(),
                DisplayName = descriptor.DisplayName.Trim(),
                Category = descriptor.Category.Trim(),
                ConfigSchemaReference = TrimOrNull(descriptor.ConfigSchemaReference),
                ExecutorId = descriptor.ExecutorId.Trim()
            };
            if (!result.TryAdd(key, new AgentOrchestrationRegisteredTrigger
                {
                    Descriptor = snapshot,
                    ContractHash = ComputeHash(snapshot)
                }))
            {
                throw new ArgumentException($"Duplicate orchestration trigger '{key}'.", nameof(descriptors));
            }
        }

        return new ReadOnlyDictionary<string, AgentOrchestrationRegisteredTrigger>(result);
    }

    private static AgentOrchestrationComponentDescriptor Snapshot(AgentOrchestrationComponentDescriptor descriptor)
        => descriptor with
        {
            ComponentType = descriptor.ComponentType.Trim(),
            Version = descriptor.Version.Trim(),
            DisplayName = descriptor.DisplayName.Trim(),
            Category = descriptor.Category.Trim(),
            ExecutorId = descriptor.ExecutorId.Trim(),
            ConfigSchemaReference = TrimOrNull(descriptor.ConfigSchemaReference),
            InputPorts = SnapshotPorts(descriptor.InputPorts),
            OutputPorts = SnapshotPorts(descriptor.OutputPorts),
            RequiredCapabilities = Array.AsReadOnly((descriptor.RequiredCapabilities ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray())
        };

    private static IReadOnlyList<AgentOrchestrationPortDefinition> SnapshotPorts(
        IReadOnlyList<AgentOrchestrationPortDefinition>? ports)
        => Array.AsReadOnly((ports ?? Array.Empty<AgentOrchestrationPortDefinition>())
            .Select(port => port with
            {
                PortId = port.PortId.Trim(),
                DisplayName = port.DisplayName.Trim(),
                Contract = port.Contract with
                {
                    DataType = port.Contract.DataType.Trim(),
                    MediaTypes = Array.AsReadOnly((port.Contract.MediaTypes ?? Array.Empty<string>())
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray()),
                    Deliveries = Array.AsReadOnly((port.Contract.Deliveries ?? Array.Empty<AgentOrchestrationValueDelivery>())
                        .Distinct()
                        .Order()
                        .ToArray())
                }
            })
            .ToArray());

    private static string ComputeHash<T>(T descriptor)
    {
        var json = JsonSerializer.Serialize(descriptor, AgentOrchestrationJson.CreateSerializerOptions());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void ValidateComponentDescriptor(AgentOrchestrationComponentDescriptor? descriptor)
    {
        if (descriptor is null)
            throw new ArgumentException("Component descriptor cannot be null.", nameof(descriptor));
        EnsureText(descriptor.ComponentType, nameof(descriptor.ComponentType));
        EnsureText(descriptor.Version, nameof(descriptor.Version));
        EnsureText(descriptor.DisplayName, nameof(descriptor.DisplayName));
        EnsureText(descriptor.Category, nameof(descriptor.Category));
        EnsureText(descriptor.ExecutorId, nameof(descriptor.ExecutorId));
        if (!Enum.IsDefined(descriptor.NodeKind))
            throw new ArgumentException($"Unsupported node kind '{descriptor.NodeKind}'.", nameof(descriptor));
        if (!Enum.IsDefined(descriptor.SideEffect))
            throw new ArgumentException($"Unsupported side effect '{descriptor.SideEffect}'.", nameof(descriptor));
        ValidatePorts(descriptor.InputPorts, "input", descriptor.ComponentType);
        ValidatePorts(descriptor.OutputPorts, "output", descriptor.ComponentType);
    }

    private static void ValidatePorts(
        IReadOnlyList<AgentOrchestrationPortDefinition>? ports,
        string direction,
        string componentType)
    {
        var values = ports ?? Array.Empty<AgentOrchestrationPortDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in values)
        {
            if (port is null)
                throw new ArgumentException($"Component '{componentType}' contains a null {direction} port.", nameof(ports));
            EnsureText(port.PortId, nameof(port.PortId));
            EnsureText(port.DisplayName, nameof(port.DisplayName));
            if (!ids.Add(port.PortId.Trim()))
                throw new ArgumentException($"Component '{componentType}' contains duplicate {direction} port '{port.PortId}'.", nameof(ports));
            if (port.Contract is null)
                throw new ArgumentException($"Port '{port.PortId}' requires a data contract.", nameof(ports));
            EnsureText(port.Contract.DataType, nameof(port.Contract.DataType));
            if (!Enum.IsDefined(port.Contract.Cardinality))
                throw new ArgumentException($"Port '{port.PortId}' has unsupported cardinality.", nameof(ports));
            var deliveries = port.Contract.Deliveries ?? Array.Empty<AgentOrchestrationValueDelivery>();
            if (deliveries.Count == 0 || deliveries.Any(delivery => !Enum.IsDefined(delivery)))
                throw new ArgumentException($"Port '{port.PortId}' requires valid delivery modes.", nameof(ports));
            if (string.Equals(port.Contract.DataType, AgentOrchestrationDataTypes.Artifact, StringComparison.OrdinalIgnoreCase) &&
                deliveries.Contains(AgentOrchestrationValueDelivery.Inline))
            {
                throw new ArgumentException($"Artifact port '{port.PortId}' cannot allow inline delivery.", nameof(ports));
            }
        }
    }

    private static void ValidateTriggerDescriptor(AgentOrchestrationTriggerDescriptor? descriptor)
    {
        if (descriptor is null)
            throw new ArgumentException("Trigger descriptor cannot be null.", nameof(descriptor));
        EnsureText(descriptor.TriggerType, nameof(descriptor.TriggerType));
        EnsureText(descriptor.Version, nameof(descriptor.Version));
        EnsureText(descriptor.DisplayName, nameof(descriptor.DisplayName));
        EnsureText(descriptor.Category, nameof(descriptor.Category));
        EnsureText(descriptor.ExecutorId, nameof(descriptor.ExecutorId));
    }

    private static void EnsureText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
    }

    private static string Key(string type, string version)
        => $"{type?.Trim()}@{version?.Trim()}";

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AgentOrchestrationComponentRegistry CreateDefault()
        => new(CreateBuiltInComponents(), CreateBuiltInTriggers());

    private static IReadOnlyList<AgentOrchestrationComponentDescriptor> CreateBuiltInComponents()
        =>
        [
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = AgentOrchestrationComponentTypes.SubAgent,
                Version = "1",
                DisplayName = "Sub-agent",
                Category = "agent",
                NodeKind = AgentOrchestrationNodeKind.SubAgent,
                ExecutorId = "pudding.runtime.sub-agent/v1",
                InputPorts =
                [
                    Port("request", "Request", AgentOrchestrationDataTypes.Any, true,
                        AgentOrchestrationPortCardinality.One,
                        AgentOrchestrationValueDelivery.Inline,
                        AgentOrchestrationValueDelivery.Artifact),
                    Port("context", "Context", AgentOrchestrationDataTypes.Content, false,
                        AgentOrchestrationPortCardinality.Many,
                        AgentOrchestrationValueDelivery.Inline,
                        AgentOrchestrationValueDelivery.Artifact)
                ],
                OutputPorts =
                [
                    Port("result", "Result", AgentOrchestrationDataTypes.Content, true,
                        AgentOrchestrationPortCardinality.One,
                        AgentOrchestrationValueDelivery.Inline,
                        AgentOrchestrationValueDelivery.Artifact,
                        AgentOrchestrationValueDelivery.Stream)
                ]
            },
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = AgentOrchestrationComponentTypes.ToolInvoke,
                Version = "1",
                DisplayName = "Tool invocation",
                Category = "tool",
                NodeKind = AgentOrchestrationNodeKind.Tool,
                ExecutorId = "pudding.runtime.tool/v1",
                InputPorts =
                [
                    Port("input", "Input", AgentOrchestrationDataTypes.Any, false,
                        AgentOrchestrationPortCardinality.Many,
                        AgentOrchestrationValueDelivery.Inline,
                        AgentOrchestrationValueDelivery.Artifact)
                ],
                OutputPorts =
                [
                    Port("result", "Result", AgentOrchestrationDataTypes.Any, true,
                        AgentOrchestrationPortCardinality.One,
                        AgentOrchestrationValueDelivery.Inline,
                        AgentOrchestrationValueDelivery.Artifact,
                        AgentOrchestrationValueDelivery.Stream)
                ]
            },
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = AgentOrchestrationComponentTypes.Gate,
                Version = "1",
                DisplayName = "Gate",
                Category = "control",
                NodeKind = AgentOrchestrationNodeKind.Gate,
                ExecutorId = "pudding.runtime.gate/v1",
                InputPorts =
                [
                    Port("results", "Results", AgentOrchestrationDataTypes.Content, false,
                        AgentOrchestrationPortCardinality.Many,
                        AgentOrchestrationValueDelivery.Inline,
                        AgentOrchestrationValueDelivery.Artifact)
                ],
                OutputPorts =
                [
                    Port("decision", "Decision", AgentOrchestrationDataTypes.Json, true,
                        AgentOrchestrationPortCardinality.One,
                        AgentOrchestrationValueDelivery.Inline)
                ]
            },
            new AgentOrchestrationComponentDescriptor
            {
                ComponentType = AgentOrchestrationComponentTypes.HumanInput,
                Version = "1",
                DisplayName = "Human input",
                Category = "control",
                NodeKind = AgentOrchestrationNodeKind.HumanInput,
                ExecutorId = "pudding.runtime.human-input/v1",
                InputPorts =
                [
                    Port("prompt", "Prompt", AgentOrchestrationDataTypes.Content, false,
                        AgentOrchestrationPortCardinality.Many,
                        AgentOrchestrationValueDelivery.Inline,
                        AgentOrchestrationValueDelivery.Artifact)
                ],
                OutputPorts =
                [
                    Port("response", "Response", AgentOrchestrationDataTypes.Content, true,
                        AgentOrchestrationPortCardinality.One,
                        AgentOrchestrationValueDelivery.Inline,
                        AgentOrchestrationValueDelivery.Artifact)
                ]
            }
        ];

    private static IReadOnlyList<AgentOrchestrationTriggerDescriptor> CreateBuiltInTriggers()
        =>
        [
            Trigger(AgentOrchestrationTriggerTypes.Manual, "Manual", "manual"),
            Trigger(AgentOrchestrationTriggerTypes.ChatMessage, "Chat message", "channel"),
            Trigger(AgentOrchestrationTriggerTypes.Schedule, "Schedule", "time"),
            Trigger(AgentOrchestrationTriggerTypes.Webhook, "Webhook", "network"),
            Trigger(AgentOrchestrationTriggerTypes.ConnectorEvent, "Connector event", "channel"),
            Trigger(AgentOrchestrationTriggerTypes.OrchestrationEvent, "Orchestration event", "event")
        ];

    private static AgentOrchestrationPortDefinition Port(
        string id,
        string displayName,
        string dataType,
        bool required,
        AgentOrchestrationPortCardinality cardinality,
        params AgentOrchestrationValueDelivery[] deliveries)
        => new()
        {
            PortId = id,
            DisplayName = displayName,
            Required = required,
            Contract = new AgentOrchestrationDataContract
            {
                DataType = dataType,
                Cardinality = cardinality,
                Deliveries = Array.AsReadOnly(deliveries)
            }
        };

    private static AgentOrchestrationTriggerDescriptor Trigger(string type, string displayName, string category)
        => new()
        {
            TriggerType = type,
            Version = "1",
            DisplayName = displayName,
            Category = category,
            ExecutorId = $"{type}/v1"
        };
}

/// <summary>Pure compatibility rules shared by the compiler and editor API.</summary>
public static class AgentOrchestrationPortCompatibility
{
    public static bool IsCompatible(
        AgentOrchestrationPortDefinition source,
        AgentOrchestrationPortDefinition target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (!string.Equals(target.Contract.DataType, AgentOrchestrationDataTypes.Any, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(source.Contract.DataType, target.Contract.DataType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (source.Contract.Cardinality == AgentOrchestrationPortCardinality.Many &&
            target.Contract.Cardinality == AgentOrchestrationPortCardinality.One)
        {
            return false;
        }

        if (!(source.Contract.Deliveries ?? Array.Empty<AgentOrchestrationValueDelivery>())
            .Intersect(target.Contract.Deliveries ?? Array.Empty<AgentOrchestrationValueDelivery>())
            .Any())
        {
            return false;
        }

        var sourceMediaTypes = source.Contract.MediaTypes ?? Array.Empty<string>();
        var targetMediaTypes = target.Contract.MediaTypes ?? Array.Empty<string>();
        return sourceMediaTypes.Count == 0 || targetMediaTypes.Count == 0 ||
               sourceMediaTypes.Any(sourceType => targetMediaTypes.Any(targetType => MediaTypeMatches(sourceType, targetType)));
    }

    public static bool AcceptsMediaType(AgentOrchestrationDataContract contract, string? mediaType)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var accepted = contract.MediaTypes ?? Array.Empty<string>();
        return accepted.Count == 0 ||
               !string.IsNullOrWhiteSpace(mediaType) && accepted.Any(value => MediaTypeMatches(mediaType, value));
    }

    private static bool MediaTypeMatches(string source, string target)
    {
        var sourceValue = source.Trim().ToLowerInvariant();
        var targetValue = target.Trim().ToLowerInvariant();
        if (sourceValue == targetValue || sourceValue == "*/*" || targetValue == "*/*")
            return true;
        var sourceSeparator = sourceValue.IndexOf('/');
        var targetSeparator = targetValue.IndexOf('/');
        return sourceSeparator > 0 && sourceValue.EndsWith("/*", StringComparison.Ordinal) &&
               targetValue.StartsWith(sourceValue[..(sourceSeparator + 1)], StringComparison.Ordinal) ||
               targetSeparator > 0 && targetValue.EndsWith("/*", StringComparison.Ordinal) &&
               sourceValue.StartsWith(targetValue[..(targetSeparator + 1)], StringComparison.Ordinal);
    }
}

/// <summary>Editor-only layout; moving cards never changes executable graph content or its hash.</summary>
public sealed record AgentOrchestrationGraphLayout
{
    public required string GraphId { get; init; }
    public required string BaseRevisionId { get; init; }
    public int LayoutRevision { get; init; } = 1;
    public AgentOrchestrationViewport Viewport { get; init; } = new();
    public IReadOnlyList<AgentOrchestrationNodeLayout> Nodes { get; init; }
        = Array.Empty<AgentOrchestrationNodeLayout>();
}

public sealed record AgentOrchestrationViewport
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Zoom { get; init; } = 1;
}

public sealed record AgentOrchestrationNodeLayout
{
    public required string NodeId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public string? ParentNodeId { get; init; }
    public bool Collapsed { get; init; }
}
