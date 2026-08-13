import {
  applyServerRevision,
  buildNextRevisionPreview,
  createDraftFromSaved,
  createNodeDraftFromCatalog,
  formatRevisionId,
  formatValidationIssues,
  formatValidationIssuesStructured,
  getLayoutSaveTarget,
  getRevisionConflict,
  insertNodeDraft,
  isContentDirty,
  patchNodeDraft,
  preserveDraftOnConflict,
  reloadLatestRevision,
  removeNodeFromDraft,
  shouldPromptBeforeUnload,
  summarizeDefinitionDiff,
  validateNodeDraft,
} from './revisionEditor';
import type {
  OrchestrationGraphDefinition,
  OrchestrationNodeDefinition,
  OrchestrationRegisteredComponent,
  OrchestrationValidationIssue,
} from './types';

function savedRevision(
  overrides: Partial<OrchestrationGraphDefinition> = {},
): OrchestrationGraphDefinition {
  return {
    schemaVersion: 'pudding.agent-orchestration/v2',
    graphId: 'graph-1',
    revisionId: 'graph-1/r001',
    revision: 1,
    workspaceId: 'default',
    rootSessionId: 'session-1',
    createdByAgentId: 'agent-1',
    objective: 'Initial objective',
    requiresExplicitActivation: true,
    maxConcurrency: 1,
    nodes: [
      {
        nodeId: 'start',
        kind: 'humanInput',
        title: 'Start',
        objective: 'Collect initial input.',
        component: {
          componentType: 'pudding.agent.human-input',
          version: '1',
          contractHash: 'hash-human-1',
        },
        expectedOutputContract: 'pudding.content',
        configuration: {},
        permissionMode: 'readOnly',
        failureBehavior: 'awaitDecision',
        maxAttempts: 1,
        metadata: {},
      },
    ],
    edges: [],
    metadata: { createdFrom: 'admin-orchestration-editor' },
    createdAtUtc: '2026-08-09T00:00:00Z',
    ...overrides,
  };
}

function nodeDraft(
  nodeId: string,
  kind: OrchestrationNodeDefinition['kind'],
  overrides: Partial<OrchestrationNodeDefinition> = {},
): OrchestrationNodeDefinition {
  return {
    nodeId,
    kind,
    title: kind === 'humanInput' ? 'Human Input' : 'Sub Agent',
    objective: `${nodeId} objective`,
    component: {
      componentType:
        kind === 'humanInput'
          ? 'pudding.agent.human-input'
          : 'pudding.agent.subagent',
      version: '1',
      contractHash: `hash-${kind}-${nodeId}`,
    },
    executor:
      kind === 'subAgent'
        ? {
            kind: 'subAgent',
            role: 'reviewer',
            templateId: 'tpl-1',
            routeKey: 'route-1',
          }
        : undefined,
    expectedOutputContract: 'pudding.content',
    configuration: {},
    permissionMode: 'readOnly',
    failureBehavior: 'failRun',
    maxAttempts: 1,
    metadata: {},
    ...overrides,
  };
}

function catalogComponent(
  kind: OrchestrationNodeDefinition['kind'],
): OrchestrationRegisteredComponent {
  return {
    descriptor: {
      componentType: `pudding.agent.${kind === 'humanInput' ? 'human-input' : kind}`,
      version: '1',
      displayName: `Catalog ${kind}`,
      category: 'Agent',
      nodeKind: kind,
      executorId: kind === 'humanInput' ? '' : `executor.${kind}`,
      sideEffect: 'read',
      inputPorts: [],
      outputPorts: [
        {
          portId: 'result',
          displayName: 'Result',
          contract: {
            dataType: 'pudding.content',
            mediaTypes: ['text/plain'],
            cardinality: 'one',
            deliveries: ['inline'],
          },
          required: true,
        },
      ],
      requiredCapabilities: [],
    },
    contractHash: `hash-${kind}-1`,
  };
}

describe('orchestration revision editor (S1)', () => {
  describe('catalog -> node draft', () => {
    it('maps a catalog component to the correct node kind/executor/gate and freezes the contract hash', () => {
      const subAgent = createNodeDraftFromCatalog(
        catalogComponent('subAgent'),
        'sub-1',
      );
      expect(subAgent.kind).toBe('subAgent');
      expect(subAgent.executor).toEqual({
        kind: 'subAgent',
        role: '',
        templateId: '',
        routeKey: '',
      });
      expect(subAgent.component).toEqual({
        componentType: 'pudding.agent.subAgent',
        version: '1',
        contractHash: 'hash-subAgent-1',
      });

      const tool = createNodeDraftFromCatalog(
        catalogComponent('tool'),
        'tool-1',
      );
      expect(tool.kind).toBe('tool');
      expect(tool.executor).toEqual({ kind: 'tool', toolId: '' });

      const image = createNodeDraftFromCatalog(
        {
          ...catalogComponent('tool'),
          descriptor: {
            ...catalogComponent('tool').descriptor,
            componentType: 'pudding.media.image-generate',
            displayName: 'Image Generate',
          },
        },
        'image-1',
      );
      expect(image.executor).toEqual({
        kind: 'tool',
        toolId: 'generate_image',
      });
      expect(image.configuration).toEqual({
        mode: 'default',
        size: '2K',
        watermark: true,
        outputFormat: 'png',
      });

      const preview = createNodeDraftFromCatalog(
        {
          ...catalogComponent('tool'),
          descriptor: {
            ...catalogComponent('tool').descriptor,
            componentType: 'pudding.media.image-preview',
            displayName: 'Image Preview',
          },
        },
        'preview-1',
      );
      expect(preview.executor).toEqual({
        kind: 'tool',
        toolId: 'preview_image',
      });

      const gate = createNodeDraftFromCatalog(
        catalogComponent('gate'),
        'gate-1',
      );
      expect(gate.kind).toBe('gate');
      expect(gate.executor).toBeUndefined();
      expect(gate.gate).toEqual({
        evaluatorId: 'executor.gate',
        parameters: {},
      });
      expect(
        validateNodeDraft({
          ...gate,
          objective: 'Evaluate upstream results.',
        }),
      ).toEqual([]);

      const human = createNodeDraftFromCatalog(
        catalogComponent('humanInput'),
        'human-1',
      );
      expect(human.kind).toBe('humanInput');
      expect(human.executor).toBeUndefined();
    });

    it('allows a humanInput node without an executor', () => {
      const human = createNodeDraftFromCatalog(
        catalogComponent('humanInput'),
        'human-1',
      );
      expect(
        validateNodeDraft({
          ...human,
          title: 'Start',
          objective: 'Ask for input.',
        }),
      ).toEqual([]);
    });

    it('requires role/template/route on a subAgent node', () => {
      const incomplete = createNodeDraftFromCatalog(
        catalogComponent('subAgent'),
        'sub-1',
      );
      incomplete.objective = 'Do the work.';
      const issues = validateNodeDraft(incomplete);
      const codes = issues.map((issue) => issue.code).sort();
      expect(codes).toEqual(
        [
          'node.subagent_role_required',
          'node.subagent_route_required',
          'node.subagent_template_required',
        ].sort(),
      );

      const complete = nodeDraft('sub-1', 'subAgent');
      expect(validateNodeDraft(complete)).toEqual([]);
    });

    it('rejects a gate node without a catalog-derived evaluator', () => {
      const gate = createNodeDraftFromCatalog(
        {
          ...catalogComponent('gate'),
          descriptor: {
            ...catalogComponent('gate').descriptor,
            executorId: '',
          },
        },
        'gate-1',
      );
      gate.objective = 'Evaluate upstream results.';

      expect(validateNodeDraft(gate)).toContainEqual({
        code: 'node.gate_evaluator_required',
        message: 'Gate 节点必须指定 evaluatorId',
      });
    });
  });

  describe('node CRUD', () => {
    it('deletes a node and synchronously removes its incoming/outgoing edges', () => {
      const draft = createDraftFromSaved(savedRevision());
      const withEdges = {
        ...draft,
        nodes: [
          ...draft.nodes,
          nodeDraft('b', 'subAgent'),
          nodeDraft('c', 'subAgent'),
        ],
        edges: [
          {
            edgeId: 'e1',
            fromNodeId: 'start',
            toNodeId: 'b',
            kind: 'control' as const,
            condition: 'onSuccess' as const,
            bindings: [],
          },
          {
            edgeId: 'e2',
            fromNodeId: 'b',
            toNodeId: 'c',
            kind: 'control' as const,
            condition: 'onSuccess' as const,
            bindings: [],
          },
          {
            edgeId: 'e3',
            fromNodeId: 'start',
            toNodeId: 'c',
            kind: 'data' as const,
            condition: 'onSuccess' as const,
            bindings: [],
          },
        ],
      };

      const result = removeNodeFromDraft(withEdges, 'b');
      expect(result.blocked).toBeUndefined();
      expect(result.removedEdgeIds.sort()).toEqual(['e1', 'e2']);
      expect(result.draft.nodes.map((node) => node.nodeId)).toEqual([
        'start',
        'c',
      ]);
      expect(result.draft.edges.map((edge) => edge.edgeId)).toEqual(['e3']);
    });

    it('refuses to delete the last remaining node', () => {
      const draft = createDraftFromSaved(savedRevision());
      const result = removeNodeFromDraft(draft, 'start');
      expect(result.blocked).toMatch(/至少保留一个合法节点/);
      expect(result.draft.nodes).toHaveLength(1);
      expect(result.removedEdgeIds).toEqual([]);
    });

    it('inserts and patches nodes without mutating the saved definition', () => {
      const saved = savedRevision();
      const draft = createDraftFromSaved(saved);
      const inserted = insertNodeDraft(
        draft,
        nodeDraft('research', 'subAgent'),
      );
      expect(inserted.nodes.map((node) => node.nodeId)).toEqual([
        'start',
        'research',
      ]);
      expect(saved.nodes).toHaveLength(1);

      const patched = patchNodeDraft(inserted, 'research', {
        objective: 'Research the topic.',
      });
      expect(patched.nodes[1].objective).toBe('Research the topic.');
      expect(inserted.nodes[1].objective).toBe('research objective');
    });
  });

  describe('dirty state and navigation guards', () => {
    it('prompts before unload whenever a content draft exists', () => {
      const saved = savedRevision();
      expect(shouldPromptBeforeUnload(saved, undefined)).toBe(false);

      const identicalDraft = createDraftFromSaved(saved);
      // Draft presence alone triggers the guard even before any edit is applied.
      expect(shouldPromptBeforeUnload(saved, identicalDraft)).toBe(true);
      expect(isContentDirty(saved, identicalDraft)).toBe(false);

      const edited = insertNodeDraft(
        identicalDraft,
        nodeDraft('research', 'subAgent'),
      );
      expect(isContentDirty(saved, edited)).toBe(true);
      expect(shouldPromptBeforeUnload(saved, edited)).toBe(true);
    });

    it('treats graph input and trigger edits as executable content changes', () => {
      const saved = savedRevision();
      const withInput: OrchestrationGraphDefinition = {
        ...saved,
        inputs: [
          {
            inputId: 'request',
            contract: {
              dataType: 'pudding.text',
              mediaTypes: [],
              cardinality: 'one',
              deliveries: ['inline'],
            },
            requiredAtActivation: true,
          },
        ],
      };
      const withTrigger: OrchestrationGraphDefinition = {
        ...saved,
        triggers: [
          {
            triggerId: 'manual',
            trigger: { triggerType: 'pudding.trigger.manual', version: '1' },
            enabled: true,
            configuration: {},
            inputBindings: [],
          },
        ],
      };
      expect(isContentDirty(saved, withInput)).toBe(true);
      expect(isContentDirty(saved, withTrigger)).toBe(true);
    });

    it('blocks layout writes to the old base revision while a draft exists', () => {
      const saved = savedRevision();
      const targetWithoutDraft = getLayoutSaveTarget(saved, undefined);
      expect(targetWithoutDraft.blocked).toBe(false);
      expect(targetWithoutDraft.baseRevisionId).toBe('graph-1/r001');

      const targetWithDraft = getLayoutSaveTarget(
        saved,
        createDraftFromSaved(saved),
      );
      expect(targetWithDraft.blocked).toBe(true);
      expect(targetWithDraft.baseRevisionId).toBe('graph-1/r001');
      expect(targetWithDraft.reason).toMatch(/旧 base Revision/);
    });
  });

  describe('next revision preview', () => {
    it('builds the next revision with correct revision/parent/id', () => {
      const saved = savedRevision();
      const draft = createDraftFromSaved(saved);
      const preview = buildNextRevisionPreview(draft, saved);

      expect(preview.revision).toBe(2);
      expect(preview.revisionId).toBe('graph-1/r002');
      expect(preview.parentRevisionId).toBe('graph-1/r001');
      expect(preview.graphId).toBe('graph-1');
      expect(formatRevisionId('graph-1', 9)).toBe('graph-1/r009');
      expect(formatRevisionId('graph-1', 10)).toBe('graph-1/r010');
    });
  });

  describe('save and conflict handling', () => {
    it('preserves the draft after a 409 conflict and only an explicit action reloads', () => {
      const saved = savedRevision();
      const draft = insertNodeDraft(
        createDraftFromSaved(saved),
        nodeDraft('research', 'subAgent'),
      );

      const conflict = getRevisionConflict({
        response: { status: 409 },
        data: {
          code: 'orchestration.revision_conflict',
          message: 'Graph head advanced to r2.',
          currentRevision: 2,
          currentRevisionId: 'graph-1/r002',
        },
      });
      expect(conflict).toEqual({
        code: 'orchestration.revision_conflict',
        message: 'Graph head advanced to r2.',
        currentRevision: 2,
        currentRevisionId: 'graph-1/r002',
      });

      expect(conflict).toBeDefined();
      const preserved = preserveDraftOnConflict(
        draft,
        conflict as NonNullable<typeof conflict>,
      );
      expect(preserved.draft).toBe(draft);
      expect(preserved.draft.nodes.map((node) => node.nodeId)).toEqual([
        'start',
        'research',
      ]);

      // Reloading is a separate, explicit action that discards the local draft.
      const latest = savedRevision({
        revision: 2,
        revisionId: 'graph-1/r002',
        nodes: [nodeDraft('start', 'humanInput')],
      });
      const reloaded = reloadLatestRevision(latest);
      expect(reloaded.saved).toBe(latest);
      expect(reloaded.draft).toBeUndefined();

      // Non-409 failures are not treated as conflicts.
      expect(
        getRevisionConflict({ response: { status: 422 } }),
      ).toBeUndefined();
    });

    it('switches the graph preview to the server revision after a successful save', () => {
      const saved = savedRevision();
      const draft = insertNodeDraft(
        createDraftFromSaved(saved),
        nodeDraft('research', 'subAgent'),
      );
      const serverRevision = buildNextRevisionPreview(draft, saved);

      const applied = applyServerRevision(saved, draft, serverRevision);
      expect(applied.saved.revisionId).toBe('graph-1/r002');
      expect(applied.saved).toBe(serverRevision);
      expect(applied.draft).toBeUndefined();
      expect(isContentDirty(applied.saved, applied.draft)).toBe(false);
    });
  });

  describe('read-only conflict diff', () => {
    it('summarizes local vs latest definition changes', () => {
      const local = savedRevision({ objective: 'Local objective' });
      const latest = savedRevision({
        objective: 'Latest objective',
        nodes: [
          nodeDraft('start', 'humanInput'),
          nodeDraft('other', 'humanInput'),
        ],
        inputs: [
          {
            inputId: 'request',
            requiredAtActivation: true,
            contract: {
              dataType: 'pudding.content',
              mediaTypes: ['text/plain'],
              cardinality: 'one',
              deliveries: ['inline'],
            },
          },
        ],
        triggers: [
          {
            triggerId: 'manual',
            trigger: { triggerType: 'pudding.manual', version: '1' },
          },
        ],
      });
      const diff = summarizeDefinitionDiff(local, latest);
      expect(diff.objectiveChanged).toBe(true);
      expect(diff.nodesAdded).toEqual(['other']);
      expect(diff.nodesRemoved).toEqual([]);
      expect(diff.inputsAdded).toEqual(['request']);
      expect(diff.triggersAdded).toEqual(['manual']);
    });
  });

  describe('validation issue rendering (S2-B5-3a)', () => {
    it('keeps formatValidationIssues as the flattened string version', () => {
      expect(
        formatValidationIssues([
          {
            code: 'graph.edge_condition_missing',
            message: 'Missing condition',
            severity: 'error',
          },
        ]),
      ).toEqual(['graph.edge_condition_missing: Missing condition']);
    });

    it('renders structured rows that preserve elementId/portId and severity', () => {
      const rows = formatValidationIssuesStructured([
        {
          code: 'graph.data_source_port_unknown',
          message: 'Source port missing.',
          severity: 'error',
          elementType: 'edge',
          elementId: 'edge-9',
          portId: 'out',
        },
        {
          code: 'graph.node_input_port_incompatible',
          message: 'Incompatible port.',
          severity: 'warning',
          elementType: 'node',
          elementId: 'node-2',
          portId: 'request',
        },
      ]);
      expect(rows).toHaveLength(2);
      expect(rows[0]).toMatchObject({
        severity: 'error',
        code: 'graph.data_source_port_unknown',
        elementType: 'edge',
        elementId: 'edge-9',
        portId: 'out',
      });
      expect(rows[0].key).toContain('edge-9');
      expect(rows[0].key).toContain('out');
      expect(rows[1].portId).toBe('request');
    });

    it('keeps rows assignable to OrchestrationValidationIssue for click handlers', () => {
      const [row] = formatValidationIssuesStructured([
        {
          code: 'graph.cycle',
          message: 'Cycle detected.',
          severity: 'error',
          elementType: 'node',
          elementId: 'n1',
        },
      ]);
      const clickable: OrchestrationValidationIssue = row;
      expect(clickable.elementId).toBe('n1');
    });
  });
});
