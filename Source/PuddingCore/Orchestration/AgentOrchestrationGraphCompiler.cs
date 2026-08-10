using System.Collections.ObjectModel;
using System.Text.Json;

namespace PuddingCode.Orchestration;

/// <summary>
/// Normalizes and validates an agent-authored graph without performing I/O or dispatching work.
/// Invalid input never produces a partially activatable definition.
/// </summary>
public sealed class AgentOrchestrationGraphCompiler
{
    private readonly IAgentOrchestrationComponentRegistry _components;

    public AgentOrchestrationGraphCompiler()
        : this(AgentOrchestrationComponentRegistry.Default)
    {
    }

    public AgentOrchestrationGraphCompiler(IAgentOrchestrationComponentRegistry components)
    {
        _components = components ?? throw new ArgumentNullException(nameof(components));
    }

    public AgentOrchestrationCompilationResult Compile(
        AgentOrchestrationGraphDefinition definition,
        AgentOrchestrationValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        options ??= new AgentOrchestrationValidationOptions();

        var issues = Validate(definition, options);
        if (issues.Count > 0)
        {
            return new AgentOrchestrationCompilationResult
            {
                Issues = issues
            };
        }

        var snapshot = Snapshot(definition);
        var topologicalNodeIds = TopologicalSort(snapshot.Nodes, snapshot.Edges);
        if (topologicalNodeIds.Count != snapshot.Nodes.Count)
        {
            return new AgentOrchestrationCompilationResult
            {
                Issues =
                [
                    new AgentOrchestrationValidationIssue(
                        "graph.cycle_detected",
                        "The orchestration graph must be acyclic.",
                        "edges")
                ]
            };
        }

        return new AgentOrchestrationCompilationResult
        {
            Definition = snapshot,
            Issues = Array.Empty<AgentOrchestrationValidationIssue>(),
            TopologicalNodeIds = Array.AsReadOnly(topologicalNodeIds.ToArray())
        };
    }

    private List<AgentOrchestrationValidationIssue> Validate(
        AgentOrchestrationGraphDefinition definition,
        AgentOrchestrationValidationOptions options)
    {
        var issues = new List<AgentOrchestrationValidationIssue>();
        if (!string.Equals(
                definition.SchemaVersion,
                AgentOrchestrationSchemas.GraphDefinitionV2,
                StringComparison.Ordinal))
        {
            issues.Add(new(
                "graph.schema_unsupported",
                $"SchemaVersion must be '{AgentOrchestrationSchemas.GraphDefinitionV2}'.",
                "schemaVersion"));
        }

        RequireText(definition.GraphId, "graph.id_required", "GraphId is required.", "graphId", issues);
        RequireText(definition.RevisionId, "graph.revision_id_required", "RevisionId is required.", "revisionId", issues);
        RequireText(definition.WorkspaceId, "graph.workspace_required", "WorkspaceId is required.", "workspaceId", issues);
        RequireText(definition.RootSessionId, "graph.session_required", "RootSessionId is required.", "rootSessionId", issues);
        RequireText(definition.CreatedByAgentId, "graph.agent_required", "CreatedByAgentId is required.", "createdByAgentId", issues);
        RequireText(definition.Objective, "graph.objective_required", "Objective is required.", "objective", issues);
        if (definition.Revision < 1)
            issues.Add(new("graph.revision_invalid", "Revision must be positive.", "revision"));
        if (definition.Revision == 1 && !string.IsNullOrWhiteSpace(definition.ParentRevisionId))
            issues.Add(new("graph.parent_revision_unexpected", "The first revision cannot have ParentRevisionId.", "parentRevisionId"));
        if (definition.Revision > 1 && string.IsNullOrWhiteSpace(definition.ParentRevisionId))
            issues.Add(new("graph.parent_revision_required", "A revision after the first must reference ParentRevisionId.", "parentRevisionId"));
        if (IdEquals(definition.RevisionId, definition.ParentRevisionId))
            issues.Add(new("graph.parent_revision_self_reference", "ParentRevisionId cannot equal RevisionId.", "parentRevisionId"));
        if (definition.MaxConcurrency < 1)
            issues.Add(new("graph.max_concurrency_invalid", "MaxConcurrency must be positive.", "maxConcurrency"));

        var inputs = definition.Inputs ?? Array.Empty<AgentOrchestrationGraphInput>();
        AddDuplicateIssues(
            inputs.Select(input => input?.InputId),
            "graph.input_id_duplicate",
            "InputId",
            "inputs",
            issues);
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var path = $"inputs[{index}]";
            if (input is null)
            {
                issues.Add(new("graph.input_required", "Graph input cannot be null.", path));
                continue;
            }

            RequireText(input.InputId, "graph.input_id_required", "InputId is required.", $"{path}.inputId", issues);
            ValidateDataContract(input.Contract, $"{path}.contract", issues);
            if (input.DefaultValue is not null)
                ValidateValue(input.DefaultValue, input.Contract, $"{path}.defaultValue", issues);
        }

        ValidateTriggers(definition.Triggers, inputIds: inputs
            .Where(input => input is not null && !string.IsNullOrWhiteSpace(input.InputId))
            .Select(input => input!.InputId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase), issues);

        var nodes = definition.Nodes ?? Array.Empty<AgentOrchestrationNodeDefinition>();
        if (nodes.Count == 0)
            issues.Add(new("graph.nodes_required", "At least one node is required.", "nodes"));
        AddDuplicateIssues(
            nodes.Select(node => node?.NodeId),
            "graph.node_id_duplicate",
            "NodeId",
            "nodes",
            issues);

        var inputById = inputs
            .Where(input => input is not null && !string.IsNullOrWhiteSpace(input.InputId))
            .GroupBy(input => input!.InputId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()!, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var path = $"nodes[{index}]";
            if (node is null)
            {
                issues.Add(new("graph.node_required", "Graph node cannot be null.", path));
                continue;
            }

            ValidateNode(node, path, inputById, options, issues);
        }

        var nodeIds = nodes
            .Where(node => node is not null && !string.IsNullOrWhiteSpace(node.NodeId))
            .Select(node => node!.NodeId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = definition.Edges ?? Array.Empty<AgentOrchestrationEdgeDefinition>();
        AddDuplicateIssues(
            edges.Select(edge => edge?.EdgeId),
            "graph.edge_id_duplicate",
            "EdgeId",
            "edges",
            issues);

        var edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            var path = $"edges[{index}]";
            if (edge is null)
            {
                issues.Add(new("graph.edge_required", "Graph edge cannot be null.", path));
                continue;
            }

            RequireText(edge.EdgeId, "graph.edge_id_required", "EdgeId is required.", $"{path}.edgeId", issues);
            RequireText(edge.FromNodeId, "graph.edge_source_required", "FromNodeId is required.", $"{path}.fromNodeId", issues);
            RequireText(edge.ToNodeId, "graph.edge_target_required", "ToNodeId is required.", $"{path}.toNodeId", issues);
            if (!Enum.IsDefined(edge.Kind))
                issues.Add(new("graph.edge_kind_unsupported", $"Unsupported edge kind '{edge.Kind}'.", $"{path}.kind"));
            if (!Enum.IsDefined(edge.Condition))
                issues.Add(new("graph.edge_condition_unsupported", $"Unsupported edge condition '{edge.Condition}'.", $"{path}.condition"));
            if (!string.IsNullOrWhiteSpace(edge.FromNodeId) && !nodeIds.Contains(edge.FromNodeId.Trim()))
                issues.Add(new("graph.edge_source_unknown", $"Unknown source node '{edge.FromNodeId}'.", $"{path}.fromNodeId"));
            if (!string.IsNullOrWhiteSpace(edge.ToNodeId) && !nodeIds.Contains(edge.ToNodeId.Trim()))
                issues.Add(new("graph.edge_target_unknown", $"Unknown target node '{edge.ToNodeId}'.", $"{path}.toNodeId"));
            if (IdEquals(edge.FromNodeId, edge.ToNodeId))
                issues.Add(new("graph.edge_self_reference", "An edge cannot reference the same source and target node.", path));

            if (!string.IsNullOrWhiteSpace(edge.FromNodeId) && !string.IsNullOrWhiteSpace(edge.ToNodeId))
            {
                var edgeKey = $"{edge.Kind}:{edge.FromNodeId.Trim()}:{edge.ToNodeId.Trim()}";
                if (!edgeKeys.Add(edgeKey))
                    issues.Add(new("graph.edge_duplicate", "Only one edge of each kind is allowed for a source/target pair.", path));
            }

            ValidateBindings(edge, path, nodes, issues);
        }

        ValidateRequiredAndSingleInputs(nodes, edges, issues);

        if (issues.Count == 0 && TopologicalSort(nodes!, edges!).Count != nodes.Count)
            issues.Add(new("graph.cycle_detected", "The orchestration graph must be acyclic.", "edges"));

        return issues;
    }

    private void ValidateNode(
        AgentOrchestrationNodeDefinition node,
        string path,
        IReadOnlyDictionary<string, AgentOrchestrationGraphInput> inputs,
        AgentOrchestrationValidationOptions options,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        RequireText(node.NodeId, "graph.node_id_required", "NodeId is required.", $"{path}.nodeId", issues);
        RequireText(node.Title, "graph.node_title_required", "Title is required.", $"{path}.title", issues);
        RequireText(node.Objective, "graph.node_objective_required", "Objective is required.", $"{path}.objective", issues);
        RequireText(
            node.ExpectedOutputContract,
            "graph.node_output_contract_required",
            "ExpectedOutputContract is required.",
            $"{path}.expectedOutputContract",
            issues);
        if (node.MaxAttempts < 1)
            issues.Add(new("graph.node_attempts_invalid", "MaxAttempts must be positive.", $"{path}.maxAttempts"));
        if (node.TimeoutSeconds is <= 0)
            issues.Add(new("graph.node_timeout_invalid", "TimeoutSeconds must be positive when provided.", $"{path}.timeoutSeconds"));
        if (node.PermissionMode == AgentOrchestrationPermissionMode.ExplicitWrite && !options.AllowExplicitWriteNodes)
        {
            issues.Add(new(
                "graph.node_write_not_allowed",
                "Explicit-write nodes require an approval-aware activation policy.",
                $"{path}.permissionMode"));
        }
        else if (!Enum.IsDefined(node.PermissionMode))
        {
            issues.Add(new(
                "graph.node_permission_mode_unsupported",
                $"Unsupported permission mode '{node.PermissionMode}'.",
                $"{path}.permissionMode"));
        }
        if (!Enum.IsDefined(node.FailureBehavior))
        {
            issues.Add(new(
                "graph.node_failure_behavior_unsupported",
                $"Unsupported failure behavior '{node.FailureBehavior}'.",
                $"{path}.failureBehavior"));
        }

        AgentOrchestrationRegisteredComponent? registeredComponent = null;
        if (node.Component is null)
        {
            issues.Add(new("graph.node_component_required", "A versioned component reference is required.", $"{path}.component"));
        }
        else
        {
            RequireText(node.Component.ComponentType, "graph.node_component_type_required", "ComponentType is required.", $"{path}.component.componentType", issues);
            RequireText(node.Component.Version, "graph.node_component_version_required", "Component version is required.", $"{path}.component.version", issues);
            if (!string.IsNullOrWhiteSpace(node.Component.ComponentType) &&
                !string.IsNullOrWhiteSpace(node.Component.Version))
            {
                if (!_components.TryResolveComponent(node.Component.ComponentType, node.Component.Version, out registeredComponent))
                {
                    issues.Add(new(
                        "graph.node_component_unknown",
                        $"Unknown component '{node.Component.ComponentType}@{node.Component.Version}'.",
                        $"{path}.component"));
                }
                else
                {
                    if (registeredComponent.Descriptor.NodeKind != node.Kind)
                    {
                        issues.Add(new(
                            "graph.node_component_kind_mismatch",
                            $"Component requires node kind '{registeredComponent.Descriptor.NodeKind}'.",
                            $"{path}.kind"));
                    }

                    if (!string.IsNullOrWhiteSpace(node.Component.ContractHash) &&
                        !string.Equals(node.Component.ContractHash, registeredComponent.ContractHash, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new(
                            "graph.node_component_contract_mismatch",
                            "Component contract hash does not match the registered version.",
                            $"{path}.component.contractHash"));
                    }

                    if (registeredComponent.Descriptor.SideEffect == AgentOrchestrationSideEffect.Write &&
                        node.PermissionMode != AgentOrchestrationPermissionMode.ExplicitWrite)
                    {
                        issues.Add(new(
                            "graph.node_component_write_permission_required",
                            "Write-capable components require ExplicitWrite permission mode.",
                            $"{path}.permissionMode"));
                    }
                }
            }
        }

        var graphInputBindings = node.GraphInputBindings ?? Array.Empty<AgentOrchestrationGraphInputBinding>();
        AddDuplicateIssues(
            graphInputBindings.Select(binding => binding is null ? null : $"{binding.InputId}:{binding.TargetPortId}:{binding.TargetKey}"),
            "graph.node_input_binding_duplicate",
            "Graph input binding",
            $"{path}.graphInputBindings",
            issues);
        for (var index = 0; index < graphInputBindings.Count; index++)
        {
            var binding = graphInputBindings[index];
            var bindingPath = $"{path}.graphInputBindings[{index}]";
            if (binding is null)
            {
                issues.Add(new("graph.node_input_binding_required", "Graph input binding cannot be null.", bindingPath));
                continue;
            }

            RequireText(binding.InputId, "graph.node_input_required", "InputId is required.", $"{bindingPath}.inputId", issues);
            RequireText(binding.TargetPortId, "graph.node_input_target_port_required", "TargetPortId is required.", $"{bindingPath}.targetPortId", issues);
            if (string.IsNullOrWhiteSpace(binding.InputId) || !inputs.TryGetValue(binding.InputId.Trim(), out var graphInput))
            {
                issues.Add(new(
                    "graph.node_input_unknown",
                    $"Node references unknown graph input '{binding.InputId}'.",
                    $"{bindingPath}.inputId"));
                continue;
            }

            if (registeredComponent is null || string.IsNullOrWhiteSpace(binding.TargetPortId))
                continue;
            var targetPort = FindPort(registeredComponent.Descriptor.InputPorts, binding.TargetPortId);
            if (targetPort is null)
            {
                issues.Add(new(
                    "graph.node_input_target_port_unknown",
                    $"Component has no input port '{binding.TargetPortId}'.",
                    $"{bindingPath}.targetPortId"));
                continue;
            }

            var graphInputPort = new AgentOrchestrationPortDefinition
            {
                PortId = graphInput.InputId,
                DisplayName = graphInput.InputId,
                Contract = graphInput.Contract
            };
            if (!AgentOrchestrationPortCompatibility.IsCompatible(graphInputPort, targetPort))
            {
                issues.Add(new(
                    "graph.node_input_port_incompatible",
                    $"Graph input '{graphInput.InputId}' is incompatible with port '{binding.TargetPortId}'.",
                    bindingPath));
            }
        }

        switch (node.Kind)
        {
            case AgentOrchestrationNodeKind.SubAgent:
                ValidateSubAgentExecutor(node.Executor, path, options, issues);
                RequireNoGate(node, path, issues);
                break;
            case AgentOrchestrationNodeKind.Tool:
                ValidateToolExecutor(node.Executor, path, issues);
                RequireNoGate(node, path, issues);
                break;
            case AgentOrchestrationNodeKind.Gate:
                RequireNoExecutor(node, path, issues);
                if (node.Gate is null)
                    issues.Add(new("graph.node_gate_required", "Gate nodes require a gate definition.", $"{path}.gate"));
                else
                    RequireText(node.Gate.EvaluatorId, "graph.node_gate_evaluator_required", "EvaluatorId is required.", $"{path}.gate.evaluatorId", issues);
                break;
            case AgentOrchestrationNodeKind.HumanInput:
                RequireNoExecutor(node, path, issues);
                RequireNoGate(node, path, issues);
                break;
            default:
                issues.Add(new("graph.node_kind_unsupported", $"Unsupported node kind '{node.Kind}'.", $"{path}.kind"));
                break;
        }
    }

    private static void ValidateSubAgentExecutor(
        AgentOrchestrationExecutorBinding? executor,
        string path,
        AgentOrchestrationValidationOptions options,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        if (executor is null)
        {
            issues.Add(new("graph.node_executor_required", "Sub-agent nodes require an executor binding.", $"{path}.executor"));
            return;
        }

        if (executor.Kind != AgentOrchestrationExecutorKind.SubAgent)
            issues.Add(new("graph.node_executor_kind_invalid", "Sub-agent nodes require a subAgent executor.", $"{path}.executor.kind"));
        RequireText(executor.Role, "graph.node_role_required", "Sub-agent role is required.", $"{path}.executor.role", issues);
        RequireText(executor.TemplateId, "graph.node_template_required", "Sub-agent TemplateId is required.", $"{path}.executor.templateId", issues);
        if (options.RequireFrozenRoutes)
            RequireText(executor.RouteKey, "graph.node_route_required", "An exact provider/model RouteKey is required.", $"{path}.executor.routeKey", issues);
        if (!string.IsNullOrWhiteSpace(executor.RouteKey) && !IsRouteKey(executor.RouteKey))
            issues.Add(new("graph.node_route_invalid", "RouteKey must use provider/model format.", $"{path}.executor.routeKey"));
        if (!string.IsNullOrWhiteSpace(executor.ToolId))
            issues.Add(new("graph.node_tool_unexpected", "Sub-agent executors cannot specify ToolId.", $"{path}.executor.toolId"));
    }

    private static void ValidateToolExecutor(
        AgentOrchestrationExecutorBinding? executor,
        string path,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        if (executor is null)
        {
            issues.Add(new("graph.node_executor_required", "Tool nodes require an executor binding.", $"{path}.executor"));
            return;
        }

        if (executor.Kind != AgentOrchestrationExecutorKind.Tool)
            issues.Add(new("graph.node_executor_kind_invalid", "Tool nodes require a tool executor.", $"{path}.executor.kind"));
        RequireText(executor.ToolId, "graph.node_tool_required", "ToolId is required.", $"{path}.executor.toolId", issues);
        if (!string.IsNullOrWhiteSpace(executor.RouteKey) || !string.IsNullOrWhiteSpace(executor.TemplateId))
            issues.Add(new("graph.node_route_unexpected", "Tool executors cannot specify RouteKey or TemplateId.", $"{path}.executor"));
    }

    private static void RequireNoExecutor(
        AgentOrchestrationNodeDefinition node,
        string path,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        if (node.Executor is not null)
            issues.Add(new("graph.node_executor_unexpected", $"{node.Kind} nodes are runtime-owned and cannot specify an executor.", $"{path}.executor"));
    }

    private static void RequireNoGate(
        AgentOrchestrationNodeDefinition node,
        string path,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        if (node.Gate is not null)
            issues.Add(new("graph.node_gate_unexpected", $"{node.Kind} nodes cannot specify a gate definition.", $"{path}.gate"));
    }

    private void ValidateBindings(
        AgentOrchestrationEdgeDefinition edge,
        string path,
        IReadOnlyList<AgentOrchestrationNodeDefinition> nodes,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        var bindings = edge.Bindings ?? Array.Empty<AgentOrchestrationDataBinding>();
        if (edge.Kind == AgentOrchestrationEdgeKind.Control && bindings.Count > 0)
        {
            issues.Add(new("graph.control_edge_bindings_unexpected", "Control edges cannot carry data bindings.", $"{path}.bindings"));
            return;
        }

        if (edge.Kind == AgentOrchestrationEdgeKind.Data && bindings.Count == 0)
        {
            issues.Add(new("graph.data_edge_bindings_required", "Data edges require at least one binding.", $"{path}.bindings"));
            return;
        }

        var sourceNode = nodes.FirstOrDefault(node => node is not null && IdEquals(node.NodeId, edge.FromNodeId));
        var targetNode = nodes.FirstOrDefault(node => node is not null && IdEquals(node.NodeId, edge.ToNodeId));
        var sourceComponent = ResolveComponent(sourceNode);
        var targetComponent = ResolveComponent(targetNode);

        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            var bindingPath = $"{path}.bindings[{index}]";
            if (binding is null)
            {
                issues.Add(new("graph.data_binding_required", "Data binding cannot be null.", bindingPath));
                continue;
            }

            RequireText(binding.SourcePortId, "graph.data_source_port_required", "SourcePortId is required.", $"{bindingPath}.sourcePortId", issues);
            RequireText(binding.SourcePath, "graph.data_source_path_required", "SourcePath is required.", $"{bindingPath}.sourcePath", issues);
            RequireText(binding.TargetPortId, "graph.data_target_port_required", "TargetPortId is required.", $"{bindingPath}.targetPortId", issues);
            if (!Enum.IsDefined(binding.Aggregation))
                issues.Add(new("graph.data_aggregation_unsupported", $"Unsupported data aggregation '{binding.Aggregation}'.", $"{bindingPath}.aggregation"));

            if (sourceComponent is null || targetComponent is null ||
                string.IsNullOrWhiteSpace(binding.SourcePortId) || string.IsNullOrWhiteSpace(binding.TargetPortId))
            {
                continue;
            }

            var sourcePort = FindPort(sourceComponent.Descriptor.OutputPorts, binding.SourcePortId);
            if (sourcePort is null)
            {
                issues.Add(new(
                    "graph.data_source_port_unknown",
                    $"Source component has no output port '{binding.SourcePortId}'.",
                    $"{bindingPath}.sourcePortId"));
                continue;
            }

            var targetPort = FindPort(targetComponent.Descriptor.InputPorts, binding.TargetPortId);
            if (targetPort is null)
            {
                issues.Add(new(
                    "graph.data_target_port_unknown",
                    $"Target component has no input port '{binding.TargetPortId}'.",
                    $"{bindingPath}.targetPortId"));
                continue;
            }

            if (!AgentOrchestrationPortCompatibility.IsCompatible(sourcePort, targetPort))
            {
                issues.Add(new(
                    "graph.data_ports_incompatible",
                    $"Output '{binding.SourcePortId}' is incompatible with input '{binding.TargetPortId}'.",
                    bindingPath));
            }

            if (targetPort.Contract.Cardinality == AgentOrchestrationPortCardinality.One &&
                binding.Aggregation != AgentOrchestrationDataAggregation.Replace)
            {
                issues.Add(new(
                    "graph.data_single_port_requires_replace",
                    "A single-value target port requires replace aggregation.",
                    $"{bindingPath}.aggregation"));
            }
        }
    }

    private void ValidateTriggers(
        IReadOnlyList<AgentOrchestrationTriggerDefinition>? triggers,
        ISet<string> inputIds,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        var values = triggers ?? Array.Empty<AgentOrchestrationTriggerDefinition>();
        AddDuplicateIssues(
            values.Select(trigger => trigger?.TriggerId),
            "graph.trigger_id_duplicate",
            "TriggerId",
            "triggers",
            issues);

        for (var index = 0; index < values.Count; index++)
        {
            var trigger = values[index];
            var path = $"triggers[{index}]";
            if (trigger is null)
            {
                issues.Add(new("graph.trigger_required", "Trigger cannot be null.", path));
                continue;
            }

            RequireText(trigger.TriggerId, "graph.trigger_id_required", "TriggerId is required.", $"{path}.triggerId", issues);
            if (trigger.Trigger is null)
            {
                issues.Add(new("graph.trigger_reference_required", "A versioned trigger reference is required.", $"{path}.trigger"));
                continue;
            }

            RequireText(trigger.Trigger.TriggerType, "graph.trigger_type_required", "TriggerType is required.", $"{path}.trigger.triggerType", issues);
            RequireText(trigger.Trigger.Version, "graph.trigger_version_required", "Trigger version is required.", $"{path}.trigger.version", issues);
            if (!string.IsNullOrWhiteSpace(trigger.Trigger.TriggerType) &&
                !string.IsNullOrWhiteSpace(trigger.Trigger.Version))
            {
                if (!_components.TryResolveTrigger(trigger.Trigger.TriggerType, trigger.Trigger.Version, out var registered))
                {
                    issues.Add(new(
                        "graph.trigger_unknown",
                        $"Unknown trigger '{trigger.Trigger.TriggerType}@{trigger.Trigger.Version}'.",
                        $"{path}.trigger"));
                }
                else if (!string.IsNullOrWhiteSpace(trigger.Trigger.ContractHash) &&
                         !string.Equals(trigger.Trigger.ContractHash, registered.ContractHash, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new(
                        "graph.trigger_contract_mismatch",
                        "Trigger contract hash does not match the registered version.",
                        $"{path}.trigger.contractHash"));
                }
            }

            var bindings = trigger.InputBindings ?? Array.Empty<AgentOrchestrationTriggerInputBinding>();
            AddDuplicateIssues(
                bindings.Select(binding => binding?.TargetInputId),
                "graph.trigger_input_duplicate",
                "Trigger target input",
                $"{path}.inputBindings",
                issues);
            for (var bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
            {
                var binding = bindings[bindingIndex];
                var bindingPath = $"{path}.inputBindings[{bindingIndex}]";
                if (binding is null)
                {
                    issues.Add(new("graph.trigger_input_binding_required", "Trigger input binding cannot be null.", bindingPath));
                    continue;
                }

                RequireText(binding.SourcePath, "graph.trigger_source_path_required", "SourcePath is required.", $"{bindingPath}.sourcePath", issues);
                RequireText(binding.TargetInputId, "graph.trigger_target_input_required", "TargetInputId is required.", $"{bindingPath}.targetInputId", issues);
                if (!string.IsNullOrWhiteSpace(binding.TargetInputId) && !inputIds.Contains(binding.TargetInputId.Trim()))
                {
                    issues.Add(new(
                        "graph.trigger_target_input_unknown",
                        $"Trigger references unknown graph input '{binding.TargetInputId}'.",
                        $"{bindingPath}.targetInputId"));
                }
            }
        }
    }

    private void ValidateRequiredAndSingleInputs(
        IReadOnlyList<AgentOrchestrationNodeDefinition> nodes,
        IReadOnlyList<AgentOrchestrationEdgeDefinition> edges,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        foreach (var node in nodes.Where(node => node is not null))
        {
            var component = ResolveComponent(node);
            if (component is null)
                continue;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in node.GraphInputBindings ?? Array.Empty<AgentOrchestrationGraphInputBinding>())
            {
                if (binding is not null && !string.IsNullOrWhiteSpace(binding.TargetPortId))
                    counts[binding.TargetPortId.Trim()] = counts.GetValueOrDefault(binding.TargetPortId.Trim()) + 1;
            }

            foreach (var binding in (edges ?? Array.Empty<AgentOrchestrationEdgeDefinition>())
                         .Where(edge => edge is not null && edge.Kind == AgentOrchestrationEdgeKind.Data && IdEquals(edge.ToNodeId, node.NodeId))
                         .SelectMany(edge => edge.Bindings ?? Array.Empty<AgentOrchestrationDataBinding>()))
            {
                if (binding is not null && !string.IsNullOrWhiteSpace(binding.TargetPortId))
                    counts[binding.TargetPortId.Trim()] = counts.GetValueOrDefault(binding.TargetPortId.Trim()) + 1;
            }

            foreach (var port in component.Descriptor.InputPorts)
            {
                var count = counts.GetValueOrDefault(port.PortId);
                if (port.Required && count == 0)
                {
                    issues.Add(new(
                        "graph.node_required_input_unbound",
                        $"Required input port '{port.PortId}' is not bound.",
                        $"nodes[{node.NodeId}].inputs.{port.PortId}"));
                }
                if (port.Contract.Cardinality == AgentOrchestrationPortCardinality.One && count > 1)
                {
                    issues.Add(new(
                        "graph.node_single_input_multiple_bindings",
                        $"Single-value input port '{port.PortId}' has multiple bindings.",
                        $"nodes[{node.NodeId}].inputs.{port.PortId}"));
                }
            }
        }
    }

    private static void ValidateDataContract(
        AgentOrchestrationDataContract? contract,
        string path,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        if (contract is null)
        {
            issues.Add(new("graph.data_contract_required", "A data contract is required.", path));
            return;
        }

        RequireText(contract.DataType, "graph.data_type_required", "DataType is required.", $"{path}.dataType", issues);
        if (!Enum.IsDefined(contract.Cardinality))
            issues.Add(new("graph.data_cardinality_unsupported", $"Unsupported cardinality '{contract.Cardinality}'.", $"{path}.cardinality"));
        var deliveries = contract.Deliveries ?? Array.Empty<AgentOrchestrationValueDelivery>();
        if (deliveries.Count == 0)
            issues.Add(new("graph.data_delivery_required", "At least one delivery mode is required.", $"{path}.deliveries"));
        foreach (var delivery in deliveries.Where(delivery => !Enum.IsDefined(delivery)))
            issues.Add(new("graph.data_delivery_unsupported", $"Unsupported delivery '{delivery}'.", $"{path}.deliveries"));
        if (string.Equals(contract.DataType, AgentOrchestrationDataTypes.Artifact, StringComparison.OrdinalIgnoreCase) &&
            deliveries.Contains(AgentOrchestrationValueDelivery.Inline))
        {
            issues.Add(new(
                "graph.artifact_inline_delivery_forbidden",
                "Artifact data must use artifact delivery and cannot be embedded inline.",
                $"{path}.deliveries"));
        }
        foreach (var mediaType in contract.MediaTypes ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.Contains("/", StringComparison.Ordinal))
                issues.Add(new("graph.data_media_type_invalid", $"Invalid media type '{mediaType}'.", $"{path}.mediaTypes"));
        }
    }

    private static void ValidateValue(
        AgentOrchestrationValueEnvelope value,
        AgentOrchestrationDataContract? contract,
        string path,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        RequireText(value.DataType, "graph.value_data_type_required", "Value DataType is required.", $"{path}.dataType", issues);
        var hasInline = value.InlineValue.HasValue;
        var artifacts = value.Artifacts ?? Array.Empty<AgentOrchestrationArtifactReference>();
        var hasArtifacts = artifacts.Count > 0;
        if (hasInline == hasArtifacts)
        {
            issues.Add(new(
                "graph.value_storage_invalid",
                "Exactly one of InlineValue or Artifacts is required.",
                path));
        }

        if (contract is not null)
        {
            if (!string.Equals(contract.DataType, AgentOrchestrationDataTypes.Any, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(contract.DataType, value.DataType, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("graph.value_data_type_mismatch", "Value DataType does not match its input contract.", $"{path}.dataType"));
            }
            if (hasInline && !(contract.Deliveries ?? Array.Empty<AgentOrchestrationValueDelivery>()).Contains(AgentOrchestrationValueDelivery.Inline))
                issues.Add(new("graph.value_inline_not_allowed", "The input contract does not allow inline values.", path));
            if (hasArtifacts && !(contract.Deliveries ?? Array.Empty<AgentOrchestrationValueDelivery>()).Contains(AgentOrchestrationValueDelivery.Artifact))
                issues.Add(new("graph.value_artifact_not_allowed", "The input contract does not allow artifact values.", path));
            if (hasInline && !AgentOrchestrationPortCompatibility.AcceptsMediaType(contract, value.ContentType))
                issues.Add(new("graph.value_media_type_mismatch", "Inline ContentType is not accepted by the input contract.", $"{path}.contentType"));
            if (contract.Cardinality == AgentOrchestrationPortCardinality.One && artifacts.Count > 1)
                issues.Add(new("graph.value_cardinality_mismatch", "A single-value input cannot contain multiple artifacts.", $"{path}.artifacts"));
        }

        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            var artifactPath = $"{path}.artifacts[{index}]";
            if (artifact is null)
            {
                issues.Add(new("graph.artifact_required", "Artifact reference cannot be null.", artifactPath));
                continue;
            }
            RequireText(artifact.ArtifactId, "graph.artifact_id_required", "ArtifactId is required.", $"{artifactPath}.artifactId", issues);
            RequireText(artifact.ContentType, "graph.artifact_content_type_required", "ContentType is required.", $"{artifactPath}.contentType", issues);
            if (contract is not null && !AgentOrchestrationPortCompatibility.AcceptsMediaType(contract, artifact.ContentType))
                issues.Add(new("graph.value_media_type_mismatch", "Artifact ContentType is not accepted by the input contract.", $"{artifactPath}.contentType"));
            if (artifact.SizeBytes is < 0)
                issues.Add(new("graph.artifact_size_invalid", "SizeBytes cannot be negative.", $"{artifactPath}.sizeBytes"));
            if (!string.IsNullOrWhiteSpace(artifact.Sha256) &&
                (artifact.Sha256.Length != 64 || artifact.Sha256.Any(character => !Uri.IsHexDigit(character))))
            {
                issues.Add(new("graph.artifact_sha256_invalid", "Sha256 must contain exactly 64 hexadecimal characters.", $"{artifactPath}.sha256"));
            }
        }
    }

    private AgentOrchestrationRegisteredComponent? ResolveComponent(AgentOrchestrationNodeDefinition? node)
        => node?.Component is not null &&
           !string.IsNullOrWhiteSpace(node.Component.ComponentType) &&
           !string.IsNullOrWhiteSpace(node.Component.Version) &&
           _components.TryResolveComponent(node.Component.ComponentType, node.Component.Version, out var component)
            ? component
            : null;

    private static AgentOrchestrationPortDefinition? FindPort(
        IReadOnlyList<AgentOrchestrationPortDefinition> ports,
        string portId)
        => (ports ?? Array.Empty<AgentOrchestrationPortDefinition>())
            .FirstOrDefault(port => port is not null && IdEquals(port.PortId, portId));

    private AgentOrchestrationGraphDefinition Snapshot(AgentOrchestrationGraphDefinition definition)
        => definition with
        {
            SchemaVersion = definition.SchemaVersion.Trim(),
            GraphId = definition.GraphId.Trim(),
            RevisionId = definition.RevisionId.Trim(),
            ParentRevisionId = TrimOrNull(definition.ParentRevisionId),
            WorkspaceId = definition.WorkspaceId.Trim(),
            RootSessionId = definition.RootSessionId.Trim(),
            CreatedByAgentId = definition.CreatedByAgentId.Trim(),
            Objective = definition.Objective.Trim(),
            Inputs = Array.AsReadOnly((definition.Inputs ?? Array.Empty<AgentOrchestrationGraphInput>())
                .Select(input => input with
                {
                    InputId = input.InputId.Trim(),
                    Contract = SnapshotDataContract(input.Contract),
                    DefaultValue = input.DefaultValue is null ? null : SnapshotValue(input.DefaultValue)
                })
                .ToArray()),
            Triggers = Array.AsReadOnly((definition.Triggers ?? Array.Empty<AgentOrchestrationTriggerDefinition>())
                .Select(SnapshotTrigger)
                .ToArray()),
            Nodes = Array.AsReadOnly((definition.Nodes ?? Array.Empty<AgentOrchestrationNodeDefinition>())
                .Select(SnapshotNode)
                .ToArray()),
            Edges = Array.AsReadOnly((definition.Edges ?? Array.Empty<AgentOrchestrationEdgeDefinition>())
                .Select(SnapshotEdge)
                .ToArray()),
            Metadata = SnapshotDictionary(definition.Metadata)
        };

    private AgentOrchestrationNodeDefinition SnapshotNode(AgentOrchestrationNodeDefinition node)
        => node with
        {
            NodeId = node.NodeId.Trim(),
            Title = node.Title.Trim(),
            Objective = node.Objective.Trim(),
            ExpectedOutputContract = node.ExpectedOutputContract.Trim(),
            Component = node.Component with
            {
                ComponentType = node.Component.ComponentType.Trim(),
                Version = node.Component.Version.Trim(),
                ContractHash = ResolveComponent(node)?.ContractHash ?? TrimOrNull(node.Component.ContractHash)
            },
            Executor = node.Executor is null
                ? null
                : node.Executor with
                {
                    Role = TrimOrNull(node.Executor.Role),
                    TemplateId = TrimOrNull(node.Executor.TemplateId),
                    RouteKey = TrimOrNull(node.Executor.RouteKey),
                    ToolId = TrimOrNull(node.Executor.ToolId)
                },
            Gate = node.Gate is null
                ? null
                : node.Gate with
                {
                    EvaluatorId = node.Gate.EvaluatorId.Trim(),
                    Parameters = SnapshotDictionary(node.Gate.Parameters)
                },
            GraphInputBindings = Array.AsReadOnly((node.GraphInputBindings ?? Array.Empty<AgentOrchestrationGraphInputBinding>())
                .Select(binding => binding with
                {
                    InputId = binding.InputId.Trim(),
                    TargetPortId = binding.TargetPortId.Trim(),
                    TargetKey = TrimOrNull(binding.TargetKey)
                })
                .ToArray()),
            Configuration = SnapshotJsonDictionary(node.Configuration),
            Metadata = SnapshotDictionary(node.Metadata)
        };

    private static AgentOrchestrationEdgeDefinition SnapshotEdge(AgentOrchestrationEdgeDefinition edge)
        => edge with
        {
            EdgeId = edge.EdgeId.Trim(),
            FromNodeId = edge.FromNodeId.Trim(),
            ToNodeId = edge.ToNodeId.Trim(),
            Bindings = Array.AsReadOnly((edge.Bindings ?? Array.Empty<AgentOrchestrationDataBinding>())
                .Select(binding => binding with
                {
                    SourcePortId = binding.SourcePortId.Trim(),
                    SourcePath = binding.SourcePath.Trim(),
                    TargetPortId = binding.TargetPortId.Trim(),
                    TargetKey = TrimOrNull(binding.TargetKey)
                })
                .ToArray())
        };

    private AgentOrchestrationTriggerDefinition SnapshotTrigger(AgentOrchestrationTriggerDefinition trigger)
    {
        _components.TryResolveTrigger(trigger.Trigger.TriggerType, trigger.Trigger.Version, out var registered);
        return trigger with
        {
            TriggerId = trigger.TriggerId.Trim(),
            Trigger = trigger.Trigger with
            {
                TriggerType = trigger.Trigger.TriggerType.Trim(),
                Version = trigger.Trigger.Version.Trim(),
                ContractHash = registered?.ContractHash ?? TrimOrNull(trigger.Trigger.ContractHash)
            },
            Configuration = SnapshotJsonDictionary(trigger.Configuration),
            InputBindings = Array.AsReadOnly((trigger.InputBindings ?? Array.Empty<AgentOrchestrationTriggerInputBinding>())
                .Select(binding => binding with
                {
                    SourcePath = binding.SourcePath.Trim(),
                    TargetInputId = binding.TargetInputId.Trim()
                })
                .ToArray())
        };
    }

    private static AgentOrchestrationDataContract SnapshotDataContract(AgentOrchestrationDataContract contract)
        => contract with
        {
            DataType = contract.DataType.Trim(),
            MediaTypes = Array.AsReadOnly((contract.MediaTypes ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()),
            Deliveries = Array.AsReadOnly((contract.Deliveries ?? Array.Empty<AgentOrchestrationValueDelivery>())
                .Distinct()
                .Order()
                .ToArray())
        };

    private static AgentOrchestrationValueEnvelope SnapshotValue(AgentOrchestrationValueEnvelope value)
        => value with
        {
            DataType = value.DataType.Trim(),
            ContentType = TrimOrNull(value.ContentType)?.ToLowerInvariant(),
            InlineValue = value.InlineValue?.Clone(),
            Artifacts = Array.AsReadOnly((value.Artifacts ?? Array.Empty<AgentOrchestrationArtifactReference>())
                .Select(artifact => artifact with
                {
                    ArtifactId = artifact.ArtifactId.Trim(),
                    ContentType = artifact.ContentType.Trim().ToLowerInvariant(),
                    FileName = TrimOrNull(artifact.FileName),
                    Sha256 = TrimOrNull(artifact.Sha256)?.ToLowerInvariant(),
                    Metadata = SnapshotDictionary(artifact.Metadata)
                })
                .ToArray())
        };

    private static IReadOnlyDictionary<string, JsonElement> SnapshotJsonDictionary(
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        var snapshot = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in (values ?? new Dictionary<string, JsonElement>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
                snapshot[pair.Key.Trim()] = pair.Value.Clone();
        }

        return new ReadOnlyDictionary<string, JsonElement>(snapshot);
    }

    private static IReadOnlyDictionary<string, string> SnapshotDictionary(
        IReadOnlyDictionary<string, string>? values)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (values ?? new Dictionary<string, string>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;
            snapshot[pair.Key.Trim()] = pair.Value ?? string.Empty;
        }

        return new ReadOnlyDictionary<string, string>(snapshot);
    }

    private static List<string> TopologicalSort(
        IReadOnlyList<AgentOrchestrationNodeDefinition> nodes,
        IReadOnlyList<AgentOrchestrationEdgeDefinition> edges)
    {
        var nodeIds = nodes.Select(node => node.NodeId.Trim()).ToArray();
        var indegree = nodeIds.ToDictionary(nodeId => nodeId, _ => 0, StringComparer.OrdinalIgnoreCase);
        var outgoing = nodeIds.ToDictionary(
            nodeId => nodeId,
            _ => new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            if (!indegree.ContainsKey(edge.FromNodeId) || !indegree.ContainsKey(edge.ToNodeId))
                continue;
            outgoing[edge.FromNodeId].Add(edge.ToNodeId);
            indegree[edge.ToNodeId]++;
        }

        var ready = new SortedSet<string>(
            indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.Ordinal);
        var ordered = new List<string>(nodes.Count);
        while (ready.Count > 0)
        {
            var nodeId = ready.Min!;
            ready.Remove(nodeId);
            ordered.Add(nodeId);
            foreach (var targetId in outgoing[nodeId].Order(StringComparer.Ordinal))
            {
                indegree[targetId]--;
                if (indegree[targetId] == 0)
                    ready.Add(targetId);
            }
        }

        return ordered;
    }

    private static void AddDuplicateIssues(
        IEnumerable<string?> values,
        string code,
        string label,
        string path,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        foreach (var duplicate in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value!.Trim())
                     .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            issues.Add(new(code, $"{label} '{duplicate}' is duplicated.", path));
        }
    }

    private static void RequireText(
        string? value,
        string code,
        string message,
        string path,
        ICollection<AgentOrchestrationValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new(code, message, path));
    }

    private static bool IsRouteKey(string value)
    {
        var separator = value.Trim().IndexOf('/');
        return separator > 0 && separator < value.Trim().Length - 1;
    }

    private static bool IdEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? TrimOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
