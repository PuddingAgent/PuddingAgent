import type {
  OrchestrationCatalog,
  OrchestrationDataContract,
  OrchestrationEdgeDefinition,
  OrchestrationGraphDefinition,
  OrchestrationPortDefinition,
  OrchestrationRegisteredComponent,
} from './types';

export const CONTROL_INPUT_HANDLE = 'control:in';
export const CONTROL_OUTPUT_HANDLE = 'control:out';
export const dataInputHandle = (portId: string) => `data:in:${portId}`;
export const dataOutputHandle = (portId: string) => `data:out:${portId}`;

export interface OrchestrationHandle {
  kind: 'control' | 'data';
  direction: 'in' | 'out';
  portId?: string;
}

export interface OrchestrationConnection {
  source: string | null;
  sourceHandle?: string | null;
  target: string | null;
  targetHandle?: string | null;
}

export interface OrchestrationEdgeIssue {
  code: string;
  message: string;
}

export interface OrchestrationEdgeBuildResult {
  edge?: OrchestrationEdgeDefinition;
  error?: OrchestrationEdgeIssue;
}

export function parseOrchestrationHandle(
  handleId?: string | null,
): OrchestrationHandle | undefined {
  if (handleId === CONTROL_INPUT_HANDLE)
    return { kind: 'control', direction: 'in' };
  if (handleId === CONTROL_OUTPUT_HANDLE)
    return { kind: 'control', direction: 'out' };
  const match = /^data:(in|out):(.+)$/.exec(handleId ?? '');
  if (!match?.[2]?.trim()) return undefined;
  return {
    kind: 'data',
    direction: match[1] as 'in' | 'out',
    portId: match[2],
  };
}

function idEquals(left?: string, right?: string): boolean {
  return Boolean(
    left && right && left.trim().toLowerCase() === right.trim().toLowerCase(),
  );
}

function resolveComponent(
  definition: OrchestrationGraphDefinition,
  catalog: OrchestrationCatalog,
  nodeId: string,
): OrchestrationRegisteredComponent | undefined {
  const node = definition.nodes.find((item) => idEquals(item.nodeId, nodeId));
  if (!node) return undefined;
  return catalog.components.find(
    (item) =>
      idEquals(item.descriptor.componentType, node.component.componentType) &&
      idEquals(item.descriptor.version, node.component.version),
  );
}

function findPort(
  ports: OrchestrationPortDefinition[],
  portId: string,
): OrchestrationPortDefinition | undefined {
  return ports.find((port) => idEquals(port.portId, portId));
}

function mediaTypeMatches(source: string, target: string): boolean {
  const sourceValue = source.trim().toLowerCase();
  const targetValue = target.trim().toLowerCase();
  if (
    sourceValue === targetValue ||
    sourceValue === '*/*' ||
    targetValue === '*/*'
  )
    return true;
  const sourceSlash = sourceValue.indexOf('/');
  const targetSlash = targetValue.indexOf('/');
  return (
    (sourceSlash > 0 &&
      sourceValue.endsWith('/*') &&
      targetValue.startsWith(sourceValue.slice(0, sourceSlash + 1))) ||
    (targetSlash > 0 &&
      targetValue.endsWith('/*') &&
      sourceValue.startsWith(targetValue.slice(0, targetSlash + 1)))
  );
}

/** Mirrors AgentOrchestrationPortCompatibility.IsCompatible for immediate editor feedback. */
export function areDataContractsCompatible(
  source: OrchestrationDataContract,
  target: OrchestrationDataContract,
): boolean {
  if (
    target.dataType.toLowerCase() !== 'pudding.any' &&
    source.dataType.toLowerCase() !== target.dataType.toLowerCase()
  )
    return false;
  if (source.cardinality === 'many' && target.cardinality === 'one')
    return false;
  if (
    !source.deliveries.some((delivery) => target.deliveries.includes(delivery))
  )
    return false;
  if (source.mediaTypes.length === 0 || target.mediaTypes.length === 0)
    return true;
  return source.mediaTypes.some((sourceType) =>
    target.mediaTypes.some((targetType) =>
      mediaTypeMatches(sourceType, targetType),
    ),
  );
}

function wouldCreateCycle(
  definition: OrchestrationGraphDefinition,
  source: string,
  target: string,
): boolean {
  const outgoing = new Map<string, string[]>();
  for (const edge of definition.edges) {
    const sourceKey = edge.fromNodeId.trim().toLowerCase();
    const values = outgoing.get(sourceKey) ?? [];
    values.push(edge.toNodeId);
    outgoing.set(sourceKey, values);
  }
  const queue = [target];
  const visited = new Set<string>();
  while (queue.length > 0) {
    const current = queue.shift();
    if (!current) continue;
    if (idEquals(current, source)) return true;
    const key = current.trim().toLowerCase();
    if (visited.has(key)) continue;
    visited.add(key);
    queue.push(...(outgoing.get(key) ?? []));
  }
  return false;
}

function targetPortIsOccupied(
  definition: OrchestrationGraphDefinition,
  targetNodeId: string,
  targetPortId: string,
): boolean {
  const targetNode = definition.nodes.find((node) =>
    idEquals(node.nodeId, targetNodeId),
  );
  if (
    targetNode?.graphInputBindings?.some((binding) =>
      idEquals(binding.targetPortId, targetPortId),
    )
  ) {
    return true;
  }
  return definition.edges.some(
    (edge) =>
      edge.kind === 'data' &&
      idEquals(edge.toNodeId, targetNodeId) &&
      edge.bindings.some((binding) =>
        idEquals(binding.targetPortId, targetPortId),
      ),
  );
}

export function buildEdgeFromConnection(
  definition: OrchestrationGraphDefinition,
  catalog: OrchestrationCatalog,
  connection: OrchestrationConnection,
  edgeId: string,
): OrchestrationEdgeBuildResult {
  const source = connection.source?.trim();
  const target = connection.target?.trim();
  const sourceHandle = parseOrchestrationHandle(connection.sourceHandle);
  const targetHandle = parseOrchestrationHandle(connection.targetHandle);
  if (!source || !target || !sourceHandle || !targetHandle) {
    return {
      error: {
        code: 'edge.connection_incomplete',
        message: '连接必须包含源/目标节点和端口。',
      },
    };
  }
  if (idEquals(source, target)) {
    return {
      error: { code: 'edge.self_loop', message: '节点不能连接到自身。' },
    };
  }
  if (
    sourceHandle.direction !== 'out' ||
    targetHandle.direction !== 'in' ||
    sourceHandle.kind !== targetHandle.kind
  ) {
    return {
      error: {
        code: 'edge.handle_mismatch',
        message: '只能连接同类型的输出端口与输入端口。',
      },
    };
  }
  if (wouldCreateCycle(definition, source, target)) {
    return {
      error: {
        code: 'edge.cycle',
        message: '该连接会形成循环；单次编排必须保持 DAG。',
      },
    };
  }

  if (sourceHandle.kind === 'control') {
    if (
      definition.edges.some(
        (edge) =>
          edge.kind === 'control' &&
          idEquals(edge.fromNodeId, source) &&
          idEquals(edge.toNodeId, target),
      )
    ) {
      return {
        error: {
          code: 'edge.duplicate',
          message: '相同节点之间已经存在 control edge。',
        },
      };
    }
    return {
      edge: {
        edgeId,
        fromNodeId: source,
        toNodeId: target,
        kind: 'control',
        condition: 'onSuccess',
        bindings: [],
      },
    };
  }

  const sourceComponent = resolveComponent(definition, catalog, source);
  const targetComponent = resolveComponent(definition, catalog, target);
  const sourcePort = sourceHandle.portId
    ? findPort(
        sourceComponent?.descriptor.outputPorts ?? [],
        sourceHandle.portId,
      )
    : undefined;
  const targetPort = targetHandle.portId
    ? findPort(
        targetComponent?.descriptor.inputPorts ?? [],
        targetHandle.portId,
      )
    : undefined;
  if (!sourceComponent || !targetComponent || !sourcePort || !targetPort) {
    return {
      error: {
        code: 'edge.port_unknown',
        message: '组件目录中找不到连接端口，请刷新 Catalog。',
      },
    };
  }
  if (!areDataContractsCompatible(sourcePort.contract, targetPort.contract)) {
    return {
      error: {
        code: 'edge.ports_incompatible',
        message: `端口不兼容：${sourcePort.contract.dataType}/${sourcePort.contract.cardinality} → ${targetPort.contract.dataType}/${targetPort.contract.cardinality}。`,
      },
    };
  }
  if (
    definition.edges.some(
      (edge) =>
        edge.kind === 'data' &&
        idEquals(edge.fromNodeId, source) &&
        idEquals(edge.toNodeId, target) &&
        edge.bindings.some(
          (binding) =>
            idEquals(binding.sourcePortId, sourcePort.portId) &&
            idEquals(binding.targetPortId, targetPort.portId),
        ),
    )
  ) {
    return {
      error: {
        code: 'edge.duplicate',
        message: '相同端口之间已经存在 data edge。',
      },
    };
  }
  if (
    targetPort.contract.cardinality === 'one' &&
    targetPortIsOccupied(definition, target, targetPort.portId)
  ) {
    return {
      error: {
        code: 'edge.target_port_occupied',
        message: `单值端口 ${targetPort.portId} 已有输入来源。`,
      },
    };
  }

  return {
    edge: {
      edgeId,
      fromNodeId: source,
      toNodeId: target,
      kind: 'data',
      condition: 'onSuccess',
      bindings: [
        {
          sourcePortId: sourcePort.portId,
          sourcePath: '$',
          targetPortId: targetPort.portId,
          aggregation:
            targetPort.contract.cardinality === 'one' ? 'replace' : 'append',
        },
      ],
    },
  };
}

export function insertEdgeDraft(
  definition: OrchestrationGraphDefinition,
  edge: OrchestrationEdgeDefinition,
): OrchestrationGraphDefinition {
  if (definition.edges.some((item) => idEquals(item.edgeId, edge.edgeId)))
    return definition;
  return { ...definition, edges: [...definition.edges, edge] };
}

export function patchEdgeDraft(
  definition: OrchestrationGraphDefinition,
  edgeId: string,
  patch: Partial<OrchestrationEdgeDefinition>,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    edges: definition.edges.map((edge) =>
      idEquals(edge.edgeId, edgeId)
        ? { ...edge, ...patch, edgeId: edge.edgeId }
        : edge,
    ),
  };
}

export function removeEdgeFromDraft(
  definition: OrchestrationGraphDefinition,
  edgeId: string,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    edges: definition.edges.filter((edge) => !idEquals(edge.edgeId, edgeId)),
  };
}
