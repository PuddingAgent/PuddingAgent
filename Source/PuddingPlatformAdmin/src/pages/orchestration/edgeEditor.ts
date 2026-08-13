import type {
  OrchestrationCatalog,
  OrchestrationDataBinding,
  OrchestrationDataContract,
  OrchestrationEdgeDefinition,
  OrchestrationEdgePredicate,
  OrchestrationGraphDefinition,
  OrchestrationPortDefinition,
  OrchestrationRegisteredComponent,
  OrchestrationValidationIssue,
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

// ---------------------------------------------------------------------------
// S2-B5-2: Port-aware canvas connection UX wrappers (doc 84 §8.2:249-261).
// These wrap the existing buildEdgeFromConnection rules for ReactFlow live
// validation; no rule logic is duplicated here.
// ---------------------------------------------------------------------------

/** CSS class applied to edges that failed backend validate (doc 84 §8.2:260-261). */
export const ORCHESTRATION_EDGE_FAILED_CLASS = 'orchestration-edge-failed';

/**
 * ReactFlow `isValidConnection` gate (first line of defence, doc 84 §8.2:249-255):
 * a live connection drop is rejected whenever the port pair or DAG constraint
 * fails the same rules as buildEdgeFromConnection. The probe edge id is only
 * used to satisfy the builder contract and is never persisted.
 */
export function isConnectionValid(
  definition: OrchestrationGraphDefinition | undefined,
  catalog: OrchestrationCatalog | undefined,
  connection: OrchestrationConnection,
): boolean {
  if (!definition || !catalog) return false;
  return (
    buildEdgeFromConnection(
      definition,
      catalog,
      connection,
      'orchestration-validity-probe',
    ).edge !== undefined
  );
}

interface OrchestrationNodeHandleRef {
  nodeId: string;
  handleId: string;
  direction: 'in' | 'out';
}

/** Enumerates every rendered canvas handle of a definition (control + typed data). */
function enumerateNodeHandles(
  definition: OrchestrationGraphDefinition,
  catalog: OrchestrationCatalog,
): OrchestrationNodeHandleRef[] {
  const refs: OrchestrationNodeHandleRef[] = [];
  for (const node of definition.nodes) {
    const component = resolveComponent(definition, catalog, node.nodeId);
    refs.push({
      nodeId: node.nodeId,
      handleId: CONTROL_INPUT_HANDLE,
      direction: 'in',
    });
    refs.push({
      nodeId: node.nodeId,
      handleId: CONTROL_OUTPUT_HANDLE,
      direction: 'out',
    });
    for (const port of component?.descriptor.inputPorts ?? []) {
      refs.push({
        nodeId: node.nodeId,
        handleId: dataInputHandle(port.portId),
        direction: 'in',
      });
    }
    for (const port of component?.descriptor.outputPorts ?? []) {
      refs.push({
        nodeId: node.nodeId,
        handleId: dataOutputHandle(port.portId),
        direction: 'out',
      });
    }
  }
  return refs;
}

/**
 * Drag-time port compatibility map (doc 84 §8.2:256-259). Keyed by
 * `${nodeId}::${handleId}` for every opposite-direction handle; the value is
 * true when dropping on that handle would create a valid edge from the dragged
 * start handle. Reverse drags (starting from an input handle) are normalized
 * the same way ReactFlow normalizes connection direction, so the probe is
 * always source(out) -> target(in).
 */
export function buildHandleCompatibilityMap(
  definition: OrchestrationGraphDefinition,
  catalog: OrchestrationCatalog,
  startNodeId: string | null,
  startHandleId: string | null,
): Record<string, boolean> {
  const map: Record<string, boolean> = {};
  const startHandle = parseOrchestrationHandle(startHandleId);
  if (!startNodeId || !startHandleId || !startHandle) return map;
  for (const ref of enumerateNodeHandles(definition, catalog)) {
    if (ref.direction === startHandle.direction) continue;
    const connection: OrchestrationConnection =
      startHandle.direction === 'out'
        ? {
            source: startNodeId,
            sourceHandle: startHandleId,
            target: ref.nodeId,
            targetHandle: ref.handleId,
          }
        : {
            source: ref.nodeId,
            sourceHandle: ref.handleId,
            target: startNodeId,
            targetHandle: startHandleId,
          };
    map[`${ref.nodeId}::${ref.handleId}`] = isConnectionValid(
      definition,
      catalog,
      connection,
    );
  }
  return map;
}

/**
 * Edge ids flagged by backend validate: error-severity issues whose
 * elementType is 'edge' (backend AgentOrchestrationGraphCompiler.cs emits
 * ElementType="edge" + ElementId=edge.EdgeId for edge-level issues).
 */
export function collectEdgeValidationFailures(
  issues: readonly OrchestrationValidationIssue[] | undefined,
): ReadonlySet<string> {
  const failed = new Set<string>();
  for (const issue of issues ?? []) {
    if (
      issue.severity === 'error' &&
      issue.elementType === 'edge' &&
      issue.elementId?.trim()
    ) {
      failed.add(issue.elementId.trim());
    }
  }
  return failed;
}

/** Edge className callback: marks backend-failed edges for red styling. */
export function buildFailedEdgeClass(
  edgeId: string,
  failedEdgeIds: ReadonlySet<string>,
): string | undefined {
  return failedEdgeIds.has(edgeId)
    ? ORCHESTRATION_EDGE_FAILED_CLASS
    : undefined;
}

/** Edge style callback: deterministic red stroke override for failed edges. */
export function buildFailedEdgeStroke(
  edgeId: string,
  failedEdgeIds: ReadonlySet<string>,
): { stroke: string } | undefined {
  return failedEdgeIds.has(edgeId) ? { stroke: '#ff4d4f' } : undefined;
}

// ---------------------------------------------------------------------------
// S2-B5-3a: Edge Inspector dual-form model helpers (doc 84 §8.3:262-280,
// §8.4:281-292). Append-only; none of the build/validation rules above are
// changed. All helpers are pure so the inspector picker and the localization
// logic stay unit-testable.
// ---------------------------------------------------------------------------

/**
 * Restricted JSONPath subset for `sourcePath` (doc 83 §13.1): only `$`,
 * field names and array indexes; functions, scripts and recursive expressions
 * are forbidden by the compiler too.
 */
export function isRestrictedSourcePath(path: string): boolean {
  if (!path) return false;
  return /^\$(\.([A-Za-z_][A-Za-z0-9_]*)|\[\d+\])*$/.test(path.trim());
}

/** Empty predicate template used when the user adds a predicate to a control edge. */
export function createDefaultEdgePredicate(): OrchestrationEdgePredicate {
  return {
    evaluatorId: '',
    version: '',
    sourcePortId: '',
    sourcePath: '$',
    parameters: {},
  };
}

/**
 * Merges a partial predicate patch into the edge's predicate (draft CRUD).
 * When the edge has no predicate yet, an empty default predicate is created
 * first so the caller can use this as the "添加谓词" action.
 */
export function patchEdgePredicateDraft(
  definition: OrchestrationGraphDefinition,
  edgeId: string,
  patch: Partial<OrchestrationEdgePredicate>,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    edges: definition.edges.map((edge) => {
      if (!idEquals(edge.edgeId, edgeId)) return edge;
      const current = edge.predicate ?? createDefaultEdgePredicate();
      return { ...edge, predicate: { ...current, ...patch } };
    }),
  };
}

/** Removes the optional predicate from a control edge (draft CRUD). */
export function removeEdgePredicateFromDraft(
  definition: OrchestrationGraphDefinition,
  edgeId: string,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    edges: definition.edges.map((edge) =>
      idEquals(edge.edgeId, edgeId)
        ? { ...edge, predicate: undefined }
        : edge,
    ),
  };
}

// ---- Predicate picker form <-> model conversion (doc 84 §8.4:291-292) ----

/** Editable picker values; parameters is held as JSON text until committed. */
export interface EdgePredicateFormValues {
  evaluatorId: string;
  version: string;
  contractHash: string;
  sourcePortId: string;
  sourcePath: string;
  parametersText: string;
}

/** Model -> form: serializes the parameters map into indented JSON text. */
export function predicateModelToForm(
  predicate: OrchestrationEdgePredicate | undefined,
): EdgePredicateFormValues {
  return {
    evaluatorId: predicate?.evaluatorId ?? '',
    version: predicate?.version ?? '',
    contractHash: predicate?.contractHash ?? '',
    sourcePortId: predicate?.sourcePortId ?? '',
    sourcePath: predicate?.sourcePath ?? '$',
    parametersText: predicate
      ? JSON.stringify(predicate.parameters ?? {}, null, 2)
      : '{}',
  };
}

/**
 * Form -> model with basic format validation (doc 84 §8.4 picker is
 * field-level only: no registry API, no free string expressions, doc 82 §10).
 * When the form is invalid the predicate is still returned so the picker can
 * render the last valid state, but `issues` explains what blocks a save.
 */
export function predicateFormToModel(
  form: EdgePredicateFormValues,
): {
  predicate: OrchestrationEdgePredicate;
  issues: OrchestrationEdgeIssue[];
} {
  const issues: OrchestrationEdgeIssue[] = [];
  const evaluatorId = form.evaluatorId.trim();
  const version = form.version.trim();
  const sourcePortId = form.sourcePortId.trim();
  const sourcePath = form.sourcePath.trim();
  const contractHash = form.contractHash.trim();

  if (!evaluatorId) {
    issues.push({
      code: 'predicate.evaluator_required',
      message: 'evaluatorId 不能为空',
    });
  } else if (/\s/.test(form.evaluatorId)) {
    issues.push({
      code: 'predicate.evaluator_format',
      message: 'evaluatorId 不能包含空白字符',
    });
  }
  if (!version) {
    issues.push({
      code: 'predicate.version_required',
      message: 'version 不能为空',
    });
  } else if (/\s/.test(form.version)) {
    issues.push({
      code: 'predicate.version_format',
      message: 'version 不能包含空白字符',
    });
  }
  if (!sourcePortId) {
    issues.push({
      code: 'predicate.source_port_required',
      message: 'sourcePortId 不能为空',
    });
  }
  if (!sourcePath) {
    issues.push({
      code: 'predicate.source_path_required',
      message: 'sourcePath 不能为空',
    });
  } else if (!isRestrictedSourcePath(sourcePath)) {
    issues.push({
      code: 'predicate.source_path_invalid',
      message:
        'sourcePath 仅支持受限 JSONPath（$、字段、数组索引），禁止函数/脚本/递归表达式',
    });
  }

  let parameters: Record<string, unknown> = {};
  const text = form.parametersText.trim();
  if (text) {
    try {
      const parsed: unknown = JSON.parse(text);
      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
        issues.push({
          code: 'predicate.parameters_invalid',
          message: 'parameters 必须是 JSON 对象',
        });
      } else {
        parameters = parsed as Record<string, unknown>;
      }
    } catch {
      issues.push({
        code: 'predicate.parameters_invalid',
        message: 'parameters 不是合法 JSON',
      });
    }
  }

  return {
    predicate: {
      evaluatorId,
      version,
      ...(contractHash ? { contractHash } : {}),
      sourcePortId,
      sourcePath,
      parameters,
    },
    issues,
  };
}

// ---- Data edge form helpers (doc 84 §8.3:271-279) ----

/** Edits one binding of a data edge through draft CRUD. */
export function patchEdgeBindingDraft(
  definition: OrchestrationGraphDefinition,
  edgeId: string,
  bindingIndex: number,
  patch: Partial<OrchestrationDataBinding>,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    edges: definition.edges.map((edge) => {
      if (!idEquals(edge.edgeId, edgeId)) return edge;
      return {
        ...edge,
        bindings: edge.bindings.map((binding, index) =>
          index === bindingIndex ? { ...binding, ...patch } : binding,
        ),
      };
    }),
  };
}

/** Appends an empty binding to a data edge through draft CRUD. */
export function appendEdgeBindingDraft(
  definition: OrchestrationGraphDefinition,
  edgeId: string,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    edges: definition.edges.map((edge) =>
      idEquals(edge.edgeId, edgeId)
        ? {
            ...edge,
            bindings: [
              ...edge.bindings,
              {
                sourcePortId: '',
                sourcePath: '$',
                targetPortId: '',
                aggregation: 'replace',
              },
            ],
          }
        : edge,
    ),
  };
}

/** Removes one binding from a data edge through draft CRUD. */
export function removeEdgeBindingDraft(
  definition: OrchestrationGraphDefinition,
  edgeId: string,
  bindingIndex: number,
): OrchestrationGraphDefinition {
  return {
    ...definition,
    edges: definition.edges.map((edge) => {
      if (!idEquals(edge.edgeId, edgeId)) return edge;
      return {
        ...edge,
        bindings: edge.bindings.filter((_, index) => index !== bindingIndex),
      };
    }),
  };
}

/** Resolved source/target contracts of a data edge's first binding (doc 84 §8.3). */
export interface EdgeResolvedContracts {
  source?: OrchestrationDataContract;
  target?: OrchestrationDataContract;
  sourcePortName?: string;
  targetPortName?: string;
}

/**
 * Resolves the binding's source/target port contracts from the catalog for the
 * data edge form's read-only contract summary. Returns undefined contracts when
 * the catalog or ports cannot be resolved (e.g. draft ports not yet in catalog).
 */
export function resolveEdgeSourceContract(
  definition: OrchestrationGraphDefinition,
  catalog: OrchestrationCatalog | undefined,
  edge: OrchestrationEdgeDefinition,
): EdgeResolvedContracts {
  if (!catalog) return {};
  const sourceComponent = resolveComponent(definition, catalog, edge.fromNodeId);
  const targetComponent = resolveComponent(definition, catalog, edge.toNodeId);
  const binding = edge.bindings[0];
  const sourcePort = binding?.sourcePortId
    ? findPort(
        sourceComponent?.descriptor.outputPorts ?? [],
        binding.sourcePortId,
      )
    : undefined;
  const targetPort = binding?.targetPortId
    ? findPort(
        targetComponent?.descriptor.inputPorts ?? [],
        binding.targetPortId,
      )
    : undefined;
  return {
    source: sourcePort?.contract,
    target: targetPort?.contract,
    sourcePortName: sourcePort?.displayName,
    targetPortName: targetPort?.displayName,
  };
}

// ---- Control edge read-only previews (doc 84 §8.3:264-269) ----

/**
 * 失败/跳过说明预览: renders the edge's routing semantics (condition plus the
 * optional predicate) as stable display lines. Never reads run artifacts.
 */
export function buildEdgeRoutingPreview(
  edge: OrchestrationEdgeDefinition,
): string[] {
  const conditionText =
    edge.condition === 'onSuccess'
      ? '上游成功'
      : edge.condition === 'onCompletion'
        ? '上游完成'
        : '始终';
  const lines: string[] = [
    `condition=${edge.condition}：${edge.condition === 'always' ? '不依赖上游终态' : `上游${conditionText}后本边才可传播。`}`,
  ];
  if (edge.kind === 'control' && edge.predicate) {
    lines.push(
      `受管谓词 ${edge.predicate.evaluatorId}@${edge.predicate.version} 会对已提交 output 做纯函数判定。`,
    );
    lines.push('谓词判定失败时，目标节点按失败/跳过策略处理。');
  } else if (edge.kind === 'control') {
    lines.push('无谓词：普通依赖边，仅按 condition 传播。');
  }
  return lines;
}

/**
 * 可达性诊断: local reachability of the edge endpoints from DAG roots.
 * Roots are nodes without incoming edges; every node reachable from a root is
 * considered reachable. The server compiler remains the final authority.
 */
export function buildEdgeReachabilityDiagnostics(
  definition: OrchestrationGraphDefinition,
  edge: OrchestrationEdgeDefinition,
): OrchestrationEdgeIssue[] {
  const issues: OrchestrationEdgeIssue[] = [];
  const hasIncoming = (nodeId: string): boolean =>
    definition.edges.some((item) => item.toNodeId === nodeId);
  const reachable = new Set<string>();
  const queue: string[] = definition.nodes
    .filter((node) => !hasIncoming(node.nodeId))
    .map((node) => node.nodeId);
  while (queue.length > 0) {
    const current = queue.shift();
    if (!current || reachable.has(current)) continue;
    reachable.add(current);
    for (const item of definition.edges) {
      if (item.fromNodeId === current && !reachable.has(item.toNodeId)) {
        queue.push(item.toNodeId);
      }
    }
  }
  if (!reachable.has(edge.fromNodeId)) {
    issues.push({
      code: 'edge.source_unreachable',
      message: `源节点 ${edge.fromNodeId} 不在任何可达路径上`,
    });
  }
  if (!reachable.has(edge.toNodeId)) {
    issues.push({
      code: 'edge.target_unreachable',
      message: `目标节点 ${edge.toNodeId} 不在任何可达路径上`,
    });
  }
  return issues;
}
