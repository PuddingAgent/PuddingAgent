export type OrchestrationValidationSeverity = 'error' | 'warning' | 'info';

export interface OrchestrationValidationIssue {
  code: string;
  message: string;
  path?: string;
  severity: OrchestrationValidationSeverity;
  /** Stable canvas element kind: 'node' | 'edge' | 'trigger' | 'input'. */
  elementType?: string;
  elementId?: string;
  portId?: string;
}

export interface OrchestrationDraftValidationResult {
  isValid: boolean;
  normalizedDefinition?: OrchestrationGraphDefinition;
  issues: OrchestrationValidationIssue[];
  topologicalNodeIds: string[];
}

/** Transport DTO for the no-side-effect draft validation endpoint. */
export interface OrchestrationDraftValidateRequest {
  graphId: string;
  /** Optional base revision the editor believes it is editing; used for stale-draft hints. */
  baseRevisionId?: string;
  definition: OrchestrationGraphDefinition;
}

/** Transport DTO for the head CAS revision save. Audit fields are server-authored only. */
export interface OrchestrationRevisionWriteRequest {
  definition: OrchestrationGraphDefinition;
  expectedCurrentRevision: number;
}

/** Facts returned by a 409 head conflict; the editor keeps its local draft until an explicit action. */
export interface OrchestrationRevisionConflict {
  code?: string;
  message: string;
  currentRevision?: number;
  currentRevisionId?: string;
}

export type OrchestrationRunStatus =
  | 'draft'
  | 'active'
  | 'awaitingInput'
  | 'completed'
  | 'failed'
  | 'cancelled';

export type OrchestrationNodeRunStatus =
  | 'pending'
  | 'ready'
  | 'claimed'
  | 'running'
  | 'awaitingInput'
  | 'completed'
  | 'failed'
  | 'skipped'
  | 'cancelled';

export type OrchestrationNodeKind = 'subAgent' | 'tool' | 'humanInput' | 'gate';
export type OrchestrationEdgeKind = 'control' | 'data';

export interface OrchestrationDataContract {
  dataType: string;
  mediaTypes: string[];
  cardinality: 'one' | 'many';
  deliveries: Array<'inline' | 'artifact' | 'stream' | 'event'>;
}

export interface OrchestrationPortDefinition {
  portId: string;
  displayName: string;
  contract: OrchestrationDataContract;
  required: boolean;
}

export interface OrchestrationComponentDescriptor {
  componentType: string;
  version: string;
  displayName: string;
  category: string;
  nodeKind: OrchestrationNodeKind;
  executorId: string;
  configSchemaReference?: string;
  sideEffect: 'none' | 'read' | 'write';
  inputPorts: OrchestrationPortDefinition[];
  outputPorts: OrchestrationPortDefinition[];
  requiredCapabilities: string[];
}

export interface OrchestrationRegisteredComponent {
  descriptor: OrchestrationComponentDescriptor;
  contractHash: string;
}

export interface OrchestrationRegisteredTrigger {
  descriptor: {
    triggerType: string;
    version: string;
    displayName: string;
    category: string;
    configSchemaReference?: string;
    executorId: string;
  };
  contractHash: string;
}

export interface OrchestrationCatalog {
  schemaVersion: string;
  components: OrchestrationRegisteredComponent[];
  triggers: OrchestrationRegisteredTrigger[];
}

export interface OrchestrationGraphSummary {
  graphId: string;
  workspaceId: string;
  rootSessionId: string;
  createdByAgentId: string;
  objective: string;
  currentRevision: number;
  currentRevisionId: string;
  runCount: number;
  activeRunCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface OrchestrationGraphPage {
  workspaceId?: string;
  offset: number;
  count: number;
  hasMore: boolean;
  graphs: OrchestrationGraphSummary[];
}

export interface OrchestrationGraphCreateRequest {
  graphId: string;
  workspaceId: string;
  rootSessionId: string;
  objective: string;
  maxConcurrency: number;
}

export interface OrchestrationGraphDeleteReceipt {
  graphId: string;
  previousRevision: number;
  deletedRevisionCount: number;
  deletedLayoutCount: number;
}

export interface OrchestrationComponentReference {
  componentType: string;
  version: string;
  contractHash?: string;
}

export interface OrchestrationExecutorBinding {
  kind: 'subAgent' | 'tool';
  role?: string;
  templateId?: string;
  routeKey?: string;
  toolId?: string;
}

/**
 * One typed graph-level input (ADR-071 §7.4 `inputs`; doc 84 §4).
 * Mirrors AgentOrchestrationGraphInput: inputId, contract, optional defaultValue,
 * requiredAtActivation. The defaultValue envelope stays loosely typed (`unknown`)
 * until the S7 stage introduces the OrchestrationValueEnvelope mirror.
 */
export interface OrchestrationGraphInput {
  inputId: string;
  contract: OrchestrationDataContract;
  /** Server-side ValueEnvelope; typed as unknown until S7 lands the envelope mirror. */
  defaultValue?: unknown;
  /** Defaults to true on the server when omitted. */
  requiredAtActivation?: boolean;
}

/**
 * Maps a graph-level input to a typed component input port (ADR-071 §7.4
 * `graphInputBindings`; doc 84 §4). Mirrors AgentOrchestrationGraphInputBinding.
 */
export interface OrchestrationGraphInputBinding {
  inputId: string;
  targetPortId: string;
  targetKey?: string;
}

/** Versioned trigger reference frozen into a graph revision. */
export interface OrchestrationTriggerReference {
  triggerType: string;
  version: string;
  contractHash?: string;
}

/** Maps a trigger payload field into a named graph input. */
export interface OrchestrationTriggerInputBinding {
  /** Defaults to "$" on the server when omitted. */
  sourcePath?: string;
  targetInputId: string;
}

/** One configured graph trigger; a trigger starts a new run, it is not a DAG node. */
export interface OrchestrationTriggerDefinition {
  triggerId: string;
  trigger: OrchestrationTriggerReference;
  /** Defaults to true on the server when omitted. */
  enabled?: boolean;
  configuration?: Record<string, unknown>;
  inputBindings?: OrchestrationTriggerInputBinding[];
}

export interface OrchestrationNodeDefinition {
  nodeId: string;
  kind: OrchestrationNodeKind;
  title: string;
  objective: string;
  component: OrchestrationComponentReference;
  executor?: OrchestrationExecutorBinding;
  graphInputBindings?: OrchestrationGraphInputBinding[];
  expectedOutputContract: string;
  configuration: Record<string, unknown>;
  permissionMode: 'readOnly' | 'explicitWrite';
  failureBehavior: 'failRun' | 'continue' | 'awaitDecision';
  maxAttempts: number;
  timeoutSeconds?: number;
  metadata: Record<string, string>;
}

export interface OrchestrationDataBinding {
  sourcePortId: string;
  sourcePath: string;
  targetPortId: string;
  targetKey?: string;
  aggregation: 'replace' | 'append';
}

export interface OrchestrationEdgeDefinition {
  edgeId: string;
  fromNodeId: string;
  toNodeId: string;
  kind: OrchestrationEdgeKind;
  condition: 'onSuccess' | 'onCompletion' | 'always';
  bindings: OrchestrationDataBinding[];
}

export interface OrchestrationGraphDefinition {
  schemaVersion: string;
  graphId: string;
  revisionId: string;
  revision: number;
  parentRevisionId?: string;
  workspaceId: string;
  rootSessionId: string;
  createdByAgentId: string;
  objective: string;
  requiresExplicitActivation: boolean;
  maxConcurrency: number;
  inputs?: OrchestrationGraphInput[];
  triggers?: OrchestrationTriggerDefinition[];
  nodes: OrchestrationNodeDefinition[];
  edges: OrchestrationEdgeDefinition[];
  metadata: Record<string, string>;
  createdAtUtc: string;
}

export interface OrchestrationNodeRunSnapshot {
  nodeId: string;
  kind: OrchestrationNodeKind;
  status: OrchestrationNodeRunStatus;
  attempt: number;
  maxAttempts: number;
  claimId?: string;
  leaseOwner?: string;
  leaseExpiresAtUtc?: string;
  fencingToken: number;
  executionRunId?: string;
  subSessionId?: string;
  outputSummary?: string;
  artifactReference?: string;
  errorMessage?: string;
  startedAtUtc?: string;
  completedAtUtc?: string;
  updatedAtUtc: string;
}

export interface OrchestrationRunSnapshot {
  runId: string;
  graphId: string;
  revisionId: string;
  workspaceId: string;
  rootSessionId: string;
  requestedByAgentId: string;
  status: OrchestrationRunStatus;
  version: number;
  headSequence: number;
  maxConcurrency: number;
  nodes: OrchestrationNodeRunSnapshot[];
  createdAtUtc: string;
  activatedAtUtc?: string;
  updatedAtUtc: string;
  completedAtUtc?: string;
  errorMessage?: string;
}

export type OrchestrationRunSummary = Omit<OrchestrationRunSnapshot, 'nodes'>;

export interface OrchestrationRunPage {
  workspaceId?: string;
  graphId?: string;
  status?: OrchestrationRunStatus;
  offset: number;
  count: number;
  hasMore: boolean;
  runs: OrchestrationRunSummary[];
}

export interface OrchestrationGraphLayout {
  graphId: string;
  baseRevisionId: string;
  layoutRevision: number;
  viewport: { x: number; y: number; zoom: number };
  nodes: Array<{
    nodeId: string;
    x: number;
    y: number;
    width?: number;
    height?: number;
    parentNodeId?: string;
    collapsed: boolean;
  }>;
}

export interface OrchestrationLayoutWriteRequest {
  layout: OrchestrationGraphLayout;
  expectedCurrentLayoutRevision: number;
}

export interface OrchestrationRunEvent {
  eventId: string;
  runId: string;
  graphId: string;
  revisionId: string;
  sequence: number;
  eventType: string;
  nodeId?: string;
  executionRunId?: string;
  subSessionId?: string;
  summary?: string;
  artifactReference?: string;
  attributes: Record<string, string>;
  recordedAtUtc: string;
}

export interface OrchestrationEventPage {
  runId: string;
  afterSequence: number;
  nextSequence: number;
  headSequence: number;
  hasMore: boolean;
  events: OrchestrationRunEvent[];
}
