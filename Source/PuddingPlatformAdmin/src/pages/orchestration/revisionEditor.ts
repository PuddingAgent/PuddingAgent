import type {
  OrchestrationGraphDefinition,
  OrchestrationNodeDefinition,
  OrchestrationRegisteredComponent,
  OrchestrationRevisionConflict,
  OrchestrationValidationIssue,
} from './types';

/**
 * S1 Revision Editor helpers (doc 84 §20 "revisionEditor.ts", §24 UI-S1).
 *
 * These functions are intentionally pure and dependency-free so the ten §5.2 behaviors can be
 * exercised with plain Jest tests. The page component only orchestrates them; it never mutates
 * the saved definition in place and never treats client audit fields as authoritative.
 */

// ---------------------------------------------------------------------------
// Draft lifecycle
// ---------------------------------------------------------------------------

/** Deep-clones the server definition so drafts never mutate the saved object (doc 84 §3.1). */
export function createDraftFromSaved(
  saved: OrchestrationGraphDefinition,
): OrchestrationGraphDefinition {
  return JSON.parse(JSON.stringify(saved)) as OrchestrationGraphDefinition;
}

/** A content draft exists whenever the editor is holding one, regardless of whether it matches saved. */
export function hasContentDraft(
  draft: OrchestrationGraphDefinition | undefined,
): draft is OrchestrationGraphDefinition {
  return draft !== undefined;
}

/** Compares only executable content; revision identity/audit fields are excluded (doc 84 §3.2). */
export function isContentDirty(
  saved: OrchestrationGraphDefinition,
  draft: OrchestrationGraphDefinition | undefined,
): boolean {
  if (!draft) return false;
  return contentSignature(saved) !== contentSignature(draft);
}

function contentSignature(definition: OrchestrationGraphDefinition): string {
  return JSON.stringify({
    schemaVersion: definition.schemaVersion,
    objective: definition.objective,
    requiresExplicitActivation: definition.requiresExplicitActivation,
    maxConcurrency: definition.maxConcurrency,
    inputs: definition.inputs ?? [],
    triggers: definition.triggers ?? [],
    nodes: definition.nodes,
    edges: definition.edges,
    metadata: definition.metadata,
  });
}

/**
 * beforeunload guard decision (doc 84 §3.1: "切换 Graph/Revision/Run 前，如有 dirty state 必须确认").
 * The component installs a real `beforeunload` listener whenever this returns true.
 */
export function shouldPromptBeforeUnload(
  saved: OrchestrationGraphDefinition,
  draft: OrchestrationGraphDefinition | undefined,
): boolean {
  return hasContentDraft(draft) || isContentDirty(saved, draft);
}

// ---------------------------------------------------------------------------
// Catalog -> node draft (doc 84 §7.1)
// ---------------------------------------------------------------------------

/**
 * Builds a node draft from a catalog component. The contract hash is frozen from the registered
 * component; node kind and executor/gate shape follow the descriptor so the user never hand-fills
 * component hashes, executor ids or arbitrary node kinds.
 */
export function createNodeDraftFromCatalog(
  component: OrchestrationRegisteredComponent,
  nodeId: string,
): OrchestrationNodeDefinition {
  const { descriptor, contractHash } = component;
  const executor: OrchestrationNodeDefinition['executor'] =
    descriptor.nodeKind === 'subAgent'
      ? { kind: 'subAgent', role: '', templateId: '', routeKey: '' }
      : descriptor.nodeKind === 'tool'
        ? {
            kind: 'tool',
            toolId:
              descriptor.componentType === 'pudding.media.image-generate'
                ? 'generate_image'
                : descriptor.componentType === 'pudding.media.image-preview'
                  ? 'preview_image'
                  : '',
          }
        : undefined;
  const gate: OrchestrationNodeDefinition['gate'] =
    descriptor.nodeKind === 'gate'
      ? { evaluatorId: descriptor.executorId, parameters: {} }
      : undefined;
  return {
    nodeId,
    kind: descriptor.nodeKind,
    title: descriptor.displayName,
    objective: '',
    component: {
      componentType: descriptor.componentType,
      version: descriptor.version,
      contractHash,
    },
    executor,
    gate,
    expectedOutputContract:
      descriptor.outputPorts[0]?.contract.dataType ?? 'pudding.anything',
    configuration:
      descriptor.componentType === 'pudding.media.image-generate'
        ? {
            mode: 'default',
            size: '2K',
            watermark: true,
            outputFormat: 'png',
          }
        : {},
    permissionMode: 'readOnly',
    failureBehavior: 'failRun',
    maxAttempts: 1,
    metadata: {},
  };
}

// ---------------------------------------------------------------------------
// Local node validation (doc 84 §19)
// ---------------------------------------------------------------------------

export interface OrchestrationNodeIssue {
  code: string;
  message: string;
}

/**
 * Local structural validation. HumanInput nodes are allowed to have no executor; SubAgent nodes
 * must carry role/template/route; Tool nodes must carry toolId. The server compiler remains the
 * final authority before a save.
 */
export function validateNodeDraft(
  node: OrchestrationNodeDefinition,
): OrchestrationNodeIssue[] {
  const issues: OrchestrationNodeIssue[] = [];
  if (!node.title.trim()) {
    issues.push({ code: 'node.title_required', message: '节点标题不能为空' });
  }
  if (!node.objective.trim()) {
    issues.push({
      code: 'node.objective_required',
      message: '节点目标不能为空',
    });
  }
  switch (node.kind) {
    case 'subAgent':
      if (node.executor?.kind !== 'subAgent') {
        issues.push({
          code: 'node.subagent_executor_required',
          message: 'SubAgent 必须配置 role、template 和 route',
        });
      } else {
        if (!node.executor.role?.trim()) {
          issues.push({
            code: 'node.subagent_role_required',
            message: 'SubAgent 必须指定 role',
          });
        }
        if (!node.executor.templateId?.trim()) {
          issues.push({
            code: 'node.subagent_template_required',
            message: 'SubAgent 必须指定 template',
          });
        }
        if (!node.executor.routeKey?.trim()) {
          issues.push({
            code: 'node.subagent_route_required',
            message: 'SubAgent 必须指定 route',
          });
        }
      }
      break;
    case 'tool':
      if (node.executor?.kind !== 'tool' || !node.executor.toolId?.trim()) {
        issues.push({
          code: 'node.tool_id_required',
          message: 'Tool 节点必须指定 toolId',
        });
      }
      break;
    case 'gate':
      if (!node.gate?.evaluatorId.trim()) {
        issues.push({
          code: 'node.gate_evaluator_required',
          message: 'Gate 节点必须指定 evaluatorId',
        });
      }
      break;
    case 'humanInput':
      // HumanInput 可以没有 executor。
      break;
  }
  return issues;
}

// ---------------------------------------------------------------------------
// Node CRUD over the draft (doc 84 §7)
// ---------------------------------------------------------------------------

/** Appends a new node to the draft; callers place it via the layout editor afterwards. */
export function insertNodeDraft(
  draft: OrchestrationGraphDefinition,
  node: OrchestrationNodeDefinition,
): OrchestrationGraphDefinition {
  if (draft.nodes.some((existing) => existing.nodeId === node.nodeId)) {
    return draft;
  }
  return { ...draft, nodes: [...draft.nodes, node] };
}

/** Patches one node in the draft without touching the saved definition. */
export function patchNodeDraft(
  draft: OrchestrationGraphDefinition,
  nodeId: string,
  patch: Partial<OrchestrationNodeDefinition>,
): OrchestrationGraphDefinition {
  return {
    ...draft,
    nodes: draft.nodes.map((node) =>
      node.nodeId === nodeId ? { ...node, ...patch, nodeId } : node,
    ),
  };
}

export interface RemoveNodeResult {
  draft: OrchestrationGraphDefinition;
  removedEdgeIds: string[];
  /** Set when the deletion is refused (for example deleting the last node). */
  blocked?: string;
}

/**
 * Deletes a node and every incoming/outgoing edge in one step (doc 84 §7.3). The last legal node
 * cannot be deleted because an empty Draft is not yet supported by the backend.
 */
export function removeNodeFromDraft(
  draft: OrchestrationGraphDefinition,
  nodeId: string,
): RemoveNodeResult {
  if (!draft.nodes.some((node) => node.nodeId === nodeId)) {
    return { draft, removedEdgeIds: [] };
  }
  if (draft.nodes.length <= 1) {
    return {
      draft,
      removedEdgeIds: [],
      blocked: '至少保留一个合法节点；空 Draft 尚未开放。',
    };
  }
  const removedEdgeIds = draft.edges
    .filter((edge) => edge.fromNodeId === nodeId || edge.toNodeId === nodeId)
    .map((edge) => edge.edgeId);
  return {
    draft: {
      ...draft,
      nodes: draft.nodes.filter((node) => node.nodeId !== nodeId),
      edges: draft.edges.filter(
        (edge) => edge.fromNodeId !== nodeId && edge.toNodeId !== nodeId,
      ),
    },
    removedEdgeIds,
  };
}

// ---------------------------------------------------------------------------
// Next revision preview (doc 84 §11.1)
// ---------------------------------------------------------------------------

/** Server revision ids use `${graphId}/r` plus a three-digit zero-padded number. */
export function formatRevisionId(graphId: string, revision: number): string {
  return `${graphId}/r${String(revision).padStart(3, '0')}`;
}

/**
 * Builds the definition to submit as the next revision. Only revision/parent/id are derived here
 * for a truthful preview; the server re-authors every audit field (revision, revisionId, parent,
 * createdBy, createdAt) from the graph head, so the submitted copies are never trusted
 * (ALREADY_KNOWN ③).
 */
export function buildNextRevisionPreview(
  draft: OrchestrationGraphDefinition,
  saved: OrchestrationGraphDefinition,
): OrchestrationGraphDefinition {
  const revision = saved.revision + 1;
  return {
    ...draft,
    graphId: saved.graphId,
    revision,
    revisionId: formatRevisionId(saved.graphId, revision),
    parentRevisionId: saved.revisionId,
    workspaceId: saved.workspaceId,
    rootSessionId: saved.rootSessionId,
  };
}

// ---------------------------------------------------------------------------
// Save / conflict handling (doc 84 §11.2)
// ---------------------------------------------------------------------------

/** Extracts 409 head-conflict facts so the UI can preserve the draft and offer explicit actions. */
export function getRevisionConflict(
  error: unknown,
): OrchestrationRevisionConflict | undefined {
  const candidate = error as {
    response?: { status?: number; data?: unknown };
    data?: unknown;
  };
  if (candidate?.response?.status !== 409) return undefined;

  const data = (candidate.data ?? candidate.response.data) as
    | {
        code?: unknown;
        message?: unknown;
        currentRevision?: unknown;
        currentRevisionId?: unknown;
      }
    | undefined;
  return {
    ...(typeof data?.code === 'string' ? { code: data.code } : {}),
    message:
      typeof data?.message === 'string'
        ? data.message
        : 'Revision 已被其他编辑者更新。',
    ...(typeof data?.currentRevision === 'number'
      ? { currentRevision: data.currentRevision }
      : {}),
    ...(typeof data?.currentRevisionId === 'string'
      ? { currentRevisionId: data.currentRevisionId }
      : {}),
  };
}

/**
 * A 409 never discards the local draft; the caller keeps it and only an explicit user action
 * (reload latest / discard) calls `reloadLatestRevision`.
 */
export function preserveDraftOnConflict(
  draft: OrchestrationGraphDefinition,
  conflict: OrchestrationRevisionConflict,
): {
  draft: OrchestrationGraphDefinition;
  conflict: OrchestrationRevisionConflict;
} {
  return { draft, conflict };
}

/** Explicit "重新加载最新 Revision" action: discards the local draft and adopts the latest server revision. */
export function reloadLatestRevision(latest: OrchestrationGraphDefinition): {
  saved: OrchestrationGraphDefinition;
  draft: undefined;
} {
  return { saved: latest, draft: undefined };
}

/**
 * Save success: the graph preview switches to the server-authored revision; the draft is cleared
 * and content dirty state disappears (doc 84 §11.1 success branch).
 */
export function applyServerRevision(
  saved: OrchestrationGraphDefinition,
  draft: OrchestrationGraphDefinition | undefined,
  serverRevision: OrchestrationGraphDefinition,
): { saved: OrchestrationGraphDefinition; draft: undefined } {
  void saved;
  void draft;
  return { saved: serverRevision, draft: undefined };
}

// ---------------------------------------------------------------------------
// Layout guard (doc 84 §12)
// ---------------------------------------------------------------------------

export interface LayoutSaveTarget {
  baseRevisionId: string;
  blocked: boolean;
  reason?: string;
}

/**
 * While a content draft exists, layout writes must not target the old base revision: the draft's
 * nodes/edges belong to a revision that does not exist yet, so coordinates must follow the new
 * base after the Revision is saved. This blocks the "保存布局" button with a stable reason.
 */
export function getLayoutSaveTarget(
  saved: OrchestrationGraphDefinition,
  draft: OrchestrationGraphDefinition | undefined,
): LayoutSaveTarget {
  if (hasContentDraft(draft)) {
    return {
      baseRevisionId: saved.revisionId,
      blocked: true,
      reason:
        '内容草稿存在时不能把布局写入旧 base Revision；请先保存 Revision 再保存布局。',
    };
  }
  return { baseRevisionId: saved.revisionId, blocked: false };
}

// ---------------------------------------------------------------------------
// Read-only conflict diff summary (doc 84 §11.2 "保留草稿并查看差异")
// ---------------------------------------------------------------------------

export interface OrchestrationDefinitionDiffSummary {
  objectiveChanged: boolean;
  nodesAdded: string[];
  nodesRemoved: string[];
  edgesAdded: string[];
  edgesRemoved: string[];
  inputsAdded: string[];
  inputsRemoved: string[];
  triggersAdded: string[];
  triggersRemoved: string[];
}

export function summarizeDefinitionDiff(
  local: OrchestrationGraphDefinition,
  latest: OrchestrationGraphDefinition,
): OrchestrationDefinitionDiffSummary {
  const localNodes = new Set(local.nodes.map((node) => node.nodeId));
  const latestNodes = new Set(latest.nodes.map((node) => node.nodeId));
  return {
    objectiveChanged: local.objective !== latest.objective,
    nodesAdded: latest.nodes
      .filter((node) => !localNodes.has(node.nodeId))
      .map((node) => node.nodeId),
    nodesRemoved: local.nodes
      .filter((node) => !latestNodes.has(node.nodeId))
      .map((node) => node.nodeId),
    edgesAdded: latest.edges
      .filter(
        (edge) => !local.edges.some((item) => item.edgeId === edge.edgeId),
      )
      .map((edge) => edge.edgeId),
    edgesRemoved: local.edges
      .filter(
        (edge) => !latest.edges.some((item) => item.edgeId === edge.edgeId),
      )
      .map((edge) => edge.edgeId),
    inputsAdded: (latest.inputs ?? [])
      .filter(
        (input) =>
          !(local.inputs ?? []).some((item) => item.inputId === input.inputId),
      )
      .map((input) => input.inputId),
    inputsRemoved: (local.inputs ?? [])
      .filter(
        (input) =>
          !(latest.inputs ?? []).some((item) => item.inputId === input.inputId),
      )
      .map((input) => input.inputId),
    triggersAdded: (latest.triggers ?? [])
      .filter(
        (trigger) =>
          !(local.triggers ?? []).some(
            (item) => item.triggerId === trigger.triggerId,
          ),
      )
      .map((trigger) => trigger.triggerId),
    triggersRemoved: (local.triggers ?? [])
      .filter(
        (trigger) =>
          !(latest.triggers ?? []).some(
            (item) => item.triggerId === trigger.triggerId,
          ),
      )
      .map((trigger) => trigger.triggerId),
  };
}

/** Flattens server/local diagnostics into stable display lines. */
export function formatValidationIssues(
  issues: OrchestrationValidationIssue[],
): string[] {
  return issues.map((issue) => `${issue.code}: ${issue.message}`);
}

// ---------------------------------------------------------------------------
// S2-B5-3a: structured validation issue rows (doc 85 §6.2:145).
// The string version above stays for existing callers (validate summary
// messages); this branch preserves elementId/portId so the UI can render
// severity levels and click-to-locate, instead of flattening them away.
// ---------------------------------------------------------------------------

/** One display row for the structured issues list; keeps every locator field. */
export interface ValidationIssueRow {
  key: string;
  severity: OrchestrationValidationIssue['severity'];
  code: string;
  message: string;
  elementType?: string;
  elementId?: string;
  portId?: string;
}

/**
 * Maps validation issues to stable structured rows without losing
 * elementId/portId. The key is content-based so React can diff rows across
 * consecutive validate calls; the row is assignable to OrchestrationValidationIssue
 * so the same object can be handed to the issue click handler.
 */
export function formatValidationIssuesStructured(
  issues: OrchestrationValidationIssue[],
): ValidationIssueRow[] {
  return issues.map((issue) => ({
    key: `${issue.code}:${issue.elementId ?? issue.path ?? 'root'}:${issue.portId ?? ''}:${issue.message}`,
    severity: issue.severity,
    code: issue.code,
    message: issue.message,
    ...(issue.elementType ? { elementType: issue.elementType } : {}),
    ...(issue.elementId ? { elementId: issue.elementId } : {}),
    ...(issue.portId ? { portId: issue.portId } : {}),
  }));
}
