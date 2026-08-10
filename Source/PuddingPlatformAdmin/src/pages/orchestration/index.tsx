import '@xyflow/react/dist/style.css';

import { PageContainer } from '@ant-design/pro-components';
import {
  applyNodeChanges,
  Background,
  Controls,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  type NodeChange,
  type ReactFlowInstance,
} from '@xyflow/react';
import {
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Empty,
  Form,
  Input,
  InputNumber,
  message,
  Modal,
  Row,
  Select,
  Space,
  Spin,
  Statistic,
  Tag,
  theme,
  Timeline,
  Tooltip,
  Typography,
} from 'antd';
import dayjs from 'dayjs';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { history } from '@umijs/max';
import {
  getOrchestrationRevision,
  getLatestOrchestrationRevision,
  putOrchestrationRevision,
  validateOrchestrationDraft,
} from './api';
import {
  createOrchestrationGraph,
  deleteOrchestrationGraph,
  getOrchestrationCatalog,
  getOrchestrationEvents,
  getOrchestrationLayout,
  getOrchestrationRun,
  listOrchestrationGraphs,
  listOrchestrationRuns,
  putOrchestrationLayout,
  watchOrchestrationRun,
} from './api';
import {
  buildOrchestrationFlowModel,
  type OrchestrationFlowNode,
} from './graphViewModel';
import {
  buildCreateGraphRequest,
  createSuggestedGraphValues,
  getGraphDeletionBlocker,
  type OrchestrationGraphCreateFormValues,
} from './graphManagement';
import {
  buildOrchestrationLayoutWrite,
  getOrchestrationLayoutConflict,
  type OrchestrationLayoutConflict,
} from './layoutEditor';
import {
  applyServerRevision,
  buildNextRevisionPreview,
  createDraftFromSaved,
  createNodeDraftFromCatalog,
  formatRevisionId,
  formatValidationIssues,
  getLayoutSaveTarget,
  getRevisionConflict,
  insertNodeDraft,
  patchNodeDraft,
  reloadLatestRevision,
  removeNodeFromDraft,
  summarizeDefinitionDiff,
  validateNodeDraft,
  type OrchestrationDefinitionDiffSummary,
} from './revisionEditor';
import type {
  OrchestrationCatalog,
  OrchestrationDraftValidationResult,
  OrchestrationGraphDefinition,
  OrchestrationGraphLayout,
  OrchestrationGraphSummary,
  OrchestrationNodeRunStatus,
  OrchestrationRegisteredComponent,
  OrchestrationRevisionConflict,
  OrchestrationRunEvent,
  OrchestrationRunSnapshot,
  OrchestrationRunStatus,
  OrchestrationRunSummary,
} from './types';

const { Paragraph, Text } = Typography;

const runStatusMeta: Record<OrchestrationRunStatus, { color: string; label: string }> = {
  draft: { color: 'default', label: '草稿' },
  active: { color: 'processing', label: '运行中' },
  awaitingInput: { color: 'warning', label: '等待输入' },
  completed: { color: 'success', label: '已完成' },
  failed: { color: 'error', label: '失败' },
  cancelled: { color: 'default', label: '已取消' },
};

const nodeStatusMeta: Record<OrchestrationNodeRunStatus, { color: string; label: string }> = {
  pending: { color: 'default', label: '等待' },
  ready: { color: 'blue', label: '就绪' },
  claimed: { color: 'purple', label: '已认领' },
  running: { color: 'cyan', label: '运行中' },
  awaitingInput: { color: 'gold', label: '等待输入' },
  completed: { color: 'green', label: '完成' },
  failed: { color: 'red', label: '失败' },
  skipped: { color: 'default', label: '跳过' },
  cancelled: { color: 'default', label: '取消' },
};

const getErrorMessage = (error: unknown): string => {
  const apiMessage = (error as { data?: { message?: unknown } })?.data?.message;
  if (typeof apiMessage === 'string') return apiMessage;
  return error instanceof Error ? error.message : String(error);
};

async function loadCommittedEvents(runId: string) {
  const result: OrchestrationRunEvent[] = [];
  let cursor = 0;
  for (let pageIndex = 0; pageIndex < 20; pageIndex += 1) {
    const page = await getOrchestrationEvents(runId, cursor, 500);
    result.push(...page.events);
    cursor = page.nextSequence;
    if (!page.hasMore) break;
  }
  return { events: result.slice(-300), cursor };
}

function createDefinitionPreviewRun(
  definition: OrchestrationGraphDefinition,
): OrchestrationRunSnapshot {
  return {
    runId: `preview:${definition.revisionId}`,
    graphId: definition.graphId,
    revisionId: definition.revisionId,
    workspaceId: definition.workspaceId,
    rootSessionId: definition.rootSessionId,
    requestedByAgentId: definition.createdByAgentId,
    status: 'draft',
    version: 0,
    headSequence: 0,
    maxConcurrency: definition.maxConcurrency,
    createdAtUtc: definition.createdAtUtc,
    updatedAtUtc: definition.createdAtUtc,
    nodes: definition.nodes.map((node) => ({
      nodeId: node.nodeId,
      kind: node.kind,
      status: 'pending',
      attempt: 0,
      maxAttempts: node.maxAttempts,
      fencingToken: 0,
      updatedAtUtc: definition.createdAtUtc,
    })),
  };
}

const OrchestrationPage: React.FC = () => {
  const [form] = Form.useForm<{ runId: string }>();
  const [createForm] = Form.useForm<OrchestrationGraphCreateFormValues>();
  const { token } = theme.useToken();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const [watchError, setWatchError] = useState<string>();
  const [catalog, setCatalog] = useState<OrchestrationCatalog>();
  const [graphs, setGraphs] = useState<OrchestrationGraphSummary[]>([]);
  const [runSummaries, setRunSummaries] = useState<OrchestrationRunSummary[]>([]);
  const [selectedGraphId, setSelectedGraphId] = useState<string>();
  const [workspaceFilter, setWorkspaceFilter] = useState(
    () => new URLSearchParams(window.location.search).get('workspaceId') ?? '',
  );
  const [discoveryLoading, setDiscoveryLoading] = useState(false);
  const [run, setRun] = useState<OrchestrationRunSnapshot>();
  const [definition, setDefinition] = useState<OrchestrationGraphDefinition>();
  const [layout, setLayout] = useState<OrchestrationGraphLayout>();
  const [viewMode, setViewMode] = useState<'graph' | 'run'>('graph');
  const [events, setEvents] = useState<OrchestrationRunEvent[]>([]);
  const [watchStartSequence, setWatchStartSequence] = useState<number>();
  const [selectedNodeId, setSelectedNodeId] = useState<string>();
  const [editorNodes, setEditorNodes] = useState<OrchestrationFlowNode[]>([]);
  const [layoutDirty, setLayoutDirty] = useState(false);
  const [layoutSaving, setLayoutSaving] = useState(false);
  const [layoutSaveError, setLayoutSaveError] = useState<string>();
  const [layoutConflict, setLayoutConflict] = useState<OrchestrationLayoutConflict>();
  const [layoutResetNonce, setLayoutResetNonce] = useState(0);
  const [createModalOpen, setCreateModalOpen] = useState(false);
  const [managementLoading, setManagementLoading] = useState(false);
  const [draftDefinition, setDraftDefinition] = useState<OrchestrationGraphDefinition>();
  const [contentDirty, setContentDirty] = useState(false);
  const [revisionSaving, setRevisionSaving] = useState(false);
  const [revisionSaveError, setRevisionSaveError] = useState<string>();
  const [revisionConflict, setRevisionConflict] = useState<OrchestrationRevisionConflict>();
  const [draftValidation, setDraftValidation] = useState<OrchestrationDraftValidationResult>();
  const [latestRevision, setLatestRevision] = useState<OrchestrationGraphDefinition>();
  const [conflictDiff, setConflictDiff] = useState<OrchestrationDefinitionDiffSummary>();
  const [addNodeModalOpen, setAddNodeModalOpen] = useState(false);
  const [selectedComponentType, setSelectedComponentType] = useState<string>();
  const [nodeEditTitle, setNodeEditTitle] = useState('');
  const [nodeEditObjective, setNodeEditObjective] = useState('');
  const [conflictDiffModalOpen, setConflictDiffModalOpen] = useState(false);
  const flowInstanceRef = useRef<ReactFlowInstance<OrchestrationFlowNode> | null>(null);
  const editorIdentityRef = useRef<string | undefined>(undefined);

  const loadRun = useCallback(
    async (rawRunId: string, updateLocation = true): Promise<OrchestrationRunSnapshot | undefined> => {
      const runId = rawRunId.trim();
      if (!runId) return;
      setLoading(true);
      setError(undefined);
      setWatchError(undefined);
      setSelectedNodeId(undefined);
      try {
        const [nextRun, nextCatalog] = await Promise.all([
          getOrchestrationRun(runId),
          getOrchestrationCatalog(),
        ]);
        const [nextDefinition, replay] = await Promise.all([
          getOrchestrationRevision(nextRun.revisionId),
          loadCommittedEvents(runId),
        ]);
        const nextLayout = await getOrchestrationLayout(nextRun.graphId, nextRun.revisionId);
        setRun(nextRun);
        setCatalog(nextCatalog);
        setDefinition(nextDefinition);
        setLayout(nextLayout);
        setDraftDefinition(undefined);
        setContentDirty(false);
        setRevisionSaveError(undefined);
        setRevisionConflict(undefined);
        setDraftValidation(undefined);
        setConflictDiff(undefined);
        setLatestRevision(undefined);
        setViewMode('run');
        setSelectedGraphId(nextRun.graphId);
        setEvents(replay.events);
        setWatchStartSequence(replay.cursor);
        form.setFieldValue('runId', runId);
        if (updateLocation) {
          history.push(
            `/orchestration?workspaceId=${encodeURIComponent(nextRun.workspaceId)}` +
              `&graphId=${encodeURIComponent(nextRun.graphId)}` +
              `&runId=${encodeURIComponent(runId)}`,
          );
        }
        return nextRun;
      } catch (loadError) {
        setRun(undefined);
        setDefinition(undefined);
        setLayout(undefined);
        setEvents([]);
        setWatchStartSequence(undefined);
        setError(getErrorMessage(loadError));
        return undefined;
      } finally {
        setLoading(false);
      }
    },
    [form],
  );

  const loadGraphPreview = useCallback(
    async (graphId: string, updateLocation = true) => {
      setLoading(true);
      setError(undefined);
      setWatchError(undefined);
      setSelectedNodeId(undefined);
      try {
        const [nextDefinition, nextCatalog] = await Promise.all([
          getLatestOrchestrationRevision(graphId),
          getOrchestrationCatalog(),
        ]);
        const nextLayout = await getOrchestrationLayout(graphId, nextDefinition.revisionId);
        setRun(createDefinitionPreviewRun(nextDefinition));
        setCatalog(nextCatalog);
        setDefinition(nextDefinition);
        setLayout(nextLayout);
        setDraftDefinition(undefined);
        setContentDirty(false);
        setRevisionSaveError(undefined);
        setRevisionConflict(undefined);
        setDraftValidation(undefined);
        setConflictDiff(undefined);
        setLatestRevision(undefined);
        setViewMode('graph');
        setSelectedGraphId(graphId);
        setEvents([]);
        setWatchStartSequence(undefined);
        form.resetFields();
        if (updateLocation) {
          history.push(
            `/orchestration?workspaceId=${encodeURIComponent(nextDefinition.workspaceId)}` +
              `&graphId=${encodeURIComponent(graphId)}&mode=graph`,
          );
        }
      } catch (loadError) {
        setError(getErrorMessage(loadError));
      } finally {
        setLoading(false);
      }
    },
    [form],
  );

  const openGraph = useCallback(
    async (graphId: string, updateLocation = true) => {
      setDiscoveryLoading(true);
      setSelectedGraphId(graphId);
      try {
        const page = await listOrchestrationRuns({ graphId, limit: 100, offset: 0 });
        setRunSummaries(page.runs);
        if (page.runs.length > 0) {
          await loadRun(page.runs[0].runId, updateLocation);
        } else {
          await loadGraphPreview(graphId, updateLocation);
        }
      } catch (discoveryError) {
        setError(getErrorMessage(discoveryError));
      } finally {
        setDiscoveryLoading(false);
      }
    },
    [loadGraphPreview, loadRun],
  );

  const loadDiscovery = useCallback(
    async (workspaceId: string, preferredGraphId?: string, preferGraphPreview = false) => {
      setDiscoveryLoading(true);
      setError(undefined);
      try {
        const page = await listOrchestrationGraphs({
          workspaceId: workspaceId.trim() || undefined,
          limit: 100,
          offset: 0,
        });
        setGraphs(page.graphs);
        const selected =
          page.graphs.find((graph) => graph.graphId === preferredGraphId) ?? page.graphs[0];
        if (selected) {
          if (preferGraphPreview) {
            const runPage = await listOrchestrationRuns({ graphId: selected.graphId, limit: 100 });
            setRunSummaries(runPage.runs);
            await loadGraphPreview(selected.graphId);
          } else {
            await openGraph(selected.graphId);
          }
        } else {
          setSelectedGraphId(undefined);
          setRunSummaries([]);
          setRun(undefined);
          setDefinition(undefined);
          setLayout(undefined);
        }
      } catch (discoveryError) {
        setError(getErrorMessage(discoveryError));
      } finally {
        setDiscoveryLoading(false);
      }
    },
    [loadGraphPreview, openGraph],
  );

  const openCreateGraphModal = useCallback(() => {
    createForm.setFieldsValue(createSuggestedGraphValues(workspaceFilter));
    setCreateModalOpen(true);
  }, [createForm, workspaceFilter]);

  const handleCreateGraph = useCallback(
    async (values: OrchestrationGraphCreateFormValues) => {
      setManagementLoading(true);
      setError(undefined);
      try {
        const created = await createOrchestrationGraph(buildCreateGraphRequest(values));
        setCreateModalOpen(false);
        setWorkspaceFilter(created.workspaceId);
        message.success(`Graph ${created.graphId} 已创建`);
        await loadDiscovery(created.workspaceId, created.graphId, true);
      } catch (createError) {
        setError(getErrorMessage(createError));
      } finally {
        setManagementLoading(false);
      }
    },
    [loadDiscovery],
  );

  const handleDeleteGraph = useCallback(
    async (graph: OrchestrationGraphSummary) => {
      setManagementLoading(true);
      setError(undefined);
      try {
        await deleteOrchestrationGraph(graph.graphId, graph.currentRevision);
        setSelectedGraphId(undefined);
        setRun(undefined);
        setDefinition(undefined);
        setLayout(undefined);
        setEvents([]);
        setDraftDefinition(undefined);
        setContentDirty(false);
        setRevisionSaveError(undefined);
        setRevisionConflict(undefined);
        setDraftValidation(undefined);
        setConflictDiff(undefined);
        setLatestRevision(undefined);
        message.success(`Graph ${graph.graphId} 已删除`);
        await loadDiscovery(workspaceFilter);
      } catch (deleteError) {
        setError(getErrorMessage(deleteError));
      } finally {
        setManagementLoading(false);
      }
    },
    [loadDiscovery, workspaceFilter],
  );

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const runId = params.get('runId');
    const graphId = params.get('graphId') ?? undefined;
    const workspaceId = params.get('workspaceId') ?? '';
    if (!runId) {
      void loadDiscovery(workspaceId, graphId, params.get('mode') === 'graph');
      return;
    }

    void (async () => {
      const loadedRun = await loadRun(runId, false);
      if (!loadedRun) return;
      try {
        const [graphPage, runPage] = await Promise.all([
          listOrchestrationGraphs({ workspaceId: loadedRun.workspaceId, limit: 100 }),
          listOrchestrationRuns({ graphId: loadedRun.graphId, limit: 100 }),
        ]);
        setGraphs(graphPage.graphs);
        setRunSummaries(runPage.runs);
        setWorkspaceFilter(loadedRun.workspaceId);
      } catch (discoveryError) {
        setError(getErrorMessage(discoveryError));
      }
    })();
  }, [loadDiscovery, loadRun]);

  useEffect(() => {
    if (viewMode !== 'run' || !run?.runId || watchStartSequence === undefined) return undefined;
    const controller = new AbortController();
    void watchOrchestrationRun({
      runId: run.runId,
      afterSequence: watchStartSequence,
      signal: controller.signal,
      onEvent: (event) => {
        setWatchError(undefined);
        setEvents((current) => {
          if (current.some((item) => item.sequence === event.sequence)) return current;
          return [...current, event].sort((left, right) => left.sequence - right.sequence).slice(-300);
        });
        void getOrchestrationRun(run.runId)
          .then((nextRun) => {
            setRun((current) =>
              !current || nextRun.version >= current.version ? nextRun : current,
            );
          })
          .catch(() => undefined);
      },
      onError: (watchFailure) => setWatchError(watchFailure.message),
    }).catch((watchFailure) => {
      if (!controller.signal.aborted) setWatchError(getErrorMessage(watchFailure));
    });
    return () => controller.abort();
  }, [run?.runId, viewMode, watchStartSequence]);

  const effectiveDefinition = draftDefinition ?? definition;
  const displayRun = useMemo(() => {
    if (!definition) return run;
    if (viewMode === 'graph' && effectiveDefinition) {
      return createDefinitionPreviewRun(effectiveDefinition);
    }
    return run;
  }, [definition, effectiveDefinition, run, viewMode]);
  const flowModel = useMemo(
    () =>
      effectiveDefinition && displayRun
        ? buildOrchestrationFlowModel(effectiveDefinition, displayRun, layout)
        : undefined,
    [effectiveDefinition, displayRun, layout],
  );
  const themedFlowNodes = useMemo(
    () =>
      flowModel?.nodes.map((node) => ({
        ...node,
        style: {
          ...node.style,
          background: token.colorBgContainer,
          color: token.colorText,
        },
      })) ?? [],
    [flowModel, token.colorBgContainer, token.colorText],
  );
  const editorIdentity = `${definition?.revisionId ?? 'none'}:${draftDefinition ? 'draft' : 'saved'}:${layout?.layoutRevision ?? 'auto'}:${layoutResetNonce}`;

  useEffect(() => {
    if (editorIdentityRef.current !== editorIdentity) {
      editorIdentityRef.current = editorIdentity;
      setEditorNodes(themedFlowNodes);
      setLayoutDirty(false);
      setLayoutSaveError(undefined);
      setLayoutConflict(undefined);
      return;
    }

    setEditorNodes((current) => {
      const currentById = new Map(current.map((node) => [node.id, node]));
      return themedFlowNodes.map((node) => {
        const currentNode = currentById.get(node.id);
        return currentNode
          ? {
              ...node,
              position: currentNode.position,
              selected: currentNode.selected,
            }
          : node;
      });
    });
  }, [editorIdentity, themedFlowNodes]);

  useEffect(() => {
    if (!layoutDirty && !contentDirty) return undefined;
    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', handleBeforeUnload);
    return () => window.removeEventListener('beforeunload', handleBeforeUnload);
  }, [layoutDirty, contentDirty]);

  const markLayoutDirty = useCallback(() => {
    setLayoutDirty(true);
    setLayoutSaveError(undefined);
    setLayoutConflict(undefined);
  }, []);

  const handleNodesChange = useCallback(
    (changes: NodeChange<OrchestrationFlowNode>[]) => {
      if (
        changes.some(
          (change) => change.type === 'position' && change.position !== undefined,
        )
      ) {
        markLayoutDirty();
      }
      setEditorNodes((current) => applyNodeChanges(changes, current));
    },
    [markLayoutDirty],
  );

  const handleSaveLayout = useCallback(async () => {
    const instance = flowInstanceRef.current;
    if (!definition || !instance) return;
    if (getLayoutSaveTarget(definition, draftDefinition).blocked) {
      message.warning('内容草稿存在时不能把布局写入旧 base Revision；请先保存 Revision 再保存布局。');
      return;
    }

    setLayoutSaving(true);
    setLayoutSaveError(undefined);
    setLayoutConflict(undefined);
    try {
      const write = buildOrchestrationLayoutWrite({
        graphId: definition.graphId,
        baseRevisionId: definition.revisionId,
        currentLayout: layout,
        viewport: instance.getViewport(),
        nodes: editorNodes,
      });
      const savedLayout = await putOrchestrationLayout(definition.graphId, write);
      setLayout(savedLayout);
      setLayoutDirty(false);
      message.success(`布局 L${savedLayout.layoutRevision} 已保存`);
    } catch (saveError) {
      const conflict = getOrchestrationLayoutConflict(saveError);
      if (conflict) setLayoutConflict(conflict);
      else setLayoutSaveError(getErrorMessage(saveError));
    } finally {
      setLayoutSaving(false);
    }
  }, [definition, draftDefinition, editorNodes, layout]);
  const handleReloadLayout = useCallback(async () => {
    if (!definition) return;

    setLayoutSaving(true);
    setLayoutSaveError(undefined);
    try {
      const latestLayout = await getOrchestrationLayout(
        definition.graphId,
        definition.revisionId,
      );
      setLayout(latestLayout);
      setLayoutResetNonce((current) => current + 1);
      setLayoutDirty(false);
      setLayoutConflict(undefined);
      message.success(latestLayout ? '已重新加载最新布局' : '已恢复自动布局');
    } catch (reloadError) {
      setLayoutSaveError(getErrorMessage(reloadError));
    } finally {
      setLayoutSaving(false);
    }
  }, [definition]);

  // ---- S1 Revision Editor: draft, node CRUD, save & conflict handling ----
  const generateNodeId = useCallback((prefix: string) => {
    const existing = new Set(effectiveDefinition?.nodes.map((node) => node.nodeId) ?? []);
    let candidate = `${prefix}-${Math.random().toString(16).slice(2, 10)}`;
    while (existing.has(candidate)) {
      candidate = `${prefix}-${Math.random().toString(16).slice(2, 10)}`;
    }
    return candidate;
  }, [effectiveDefinition]);

  const beginDraft = useCallback(() => {
    if (!definition) return undefined;
    if (draftDefinition) return draftDefinition;
    const draft = createDraftFromSaved(definition);
    setDraftDefinition(draft);
    setContentDirty(true);
    setRevisionSaveError(undefined);
    setRevisionConflict(undefined);
    return draft;
  }, [definition, draftDefinition]);

  const handleAddNode = useCallback((component: OrchestrationRegisteredComponent) => {
    const draft = beginDraft();
    if (!draft) return;
    const node = createNodeDraftFromCatalog(component, generateNodeId(component.descriptor.nodeKind));
    node.objective = `使用 ${component.descriptor.displayName} 完成目标`;
    const nextDraft = insertNodeDraft(draft, node);
    setDraftDefinition(nextDraft);
    setContentDirty(true);
    setSelectedNodeId(node.nodeId);
    setAddNodeModalOpen(false);
    setSelectedComponentType(undefined);
    message.success(`已添加节点 ${node.nodeId}`);
  }, [beginDraft, generateNodeId]);

  const handleApplyNodeEdit = useCallback(() => {
    if (!selectedNodeId) return;
    const draft = beginDraft();
    if (!draft) return;
    const nextDraft = patchNodeDraft(draft, selectedNodeId, {
      title: nodeEditTitle.trim(),
      objective: nodeEditObjective.trim(),
    });
    setDraftDefinition(nextDraft);
    setContentDirty(true);
    setRevisionSaveError(undefined);
    setRevisionConflict(undefined);
  }, [beginDraft, nodeEditObjective, nodeEditTitle, selectedNodeId]);

  const handleDeleteSelectedNode = useCallback(() => {
    if (!selectedNodeId) return;
    const draft = beginDraft();
    if (!draft) return;
    const result = removeNodeFromDraft(draft, selectedNodeId);
    if (result.blocked) {
      message.warning(result.blocked);
      return;
    }
    setDraftDefinition(result.draft);
    setContentDirty(true);
    setSelectedNodeId(undefined);
    setRevisionSaveError(undefined);
    setRevisionConflict(undefined);
    message.success(
      result.removedEdgeIds.length > 0
        ? `节点已删除，并同步移除 ${result.removedEdgeIds.length} 条关联边`
        : '节点已删除',
    );
  }, [beginDraft, selectedNodeId]);

  const handleDiscardDraft = useCallback(() => {
    setDraftDefinition(undefined);
    setContentDirty(false);
    setRevisionSaveError(undefined);
    setRevisionConflict(undefined);
    setDraftValidation(undefined);
    setConflictDiff(undefined);
    setLatestRevision(undefined);
    setSelectedNodeId(undefined);
  }, []);

  const handleSaveRevision = useCallback(async () => {
    if (!definition || !draftDefinition) return;
    setRevisionSaving(true);
    setRevisionSaveError(undefined);
    setRevisionConflict(undefined);
    setDraftValidation(undefined);
    try {
      const preview = buildNextRevisionPreview(draftDefinition, definition);
      const validation = await validateOrchestrationDraft(definition.graphId, {
        graphId: definition.graphId,
        baseRevisionId: definition.revisionId,
        definition: preview,
      });
      if (!validation.isValid) {
        setDraftValidation(validation);
        setRevisionSaveError(
          `校验失败：${formatValidationIssues(validation.issues).join('；') || '存在阻塞诊断'}`,
        );
        return;
      }
      const serverRevision = await putOrchestrationRevision(definition.graphId, {
        definition: preview,
        expectedCurrentRevision: definition.revision,
      });
      const applied = applyServerRevision(definition, draftDefinition, serverRevision);
      setDefinition(applied.saved);
      setDraftDefinition(undefined);
      setContentDirty(false);
      setLayout(undefined);
      setDraftValidation(undefined);
      setGraphs((current) =>
        current.map((graph) =>
          graph.graphId === serverRevision.graphId
            ? {
                ...graph,
                currentRevision: serverRevision.revision,
                currentRevisionId: serverRevision.revisionId,
              }
            : graph,
        ),
      );
      message.success(`Revision ${serverRevision.revisionId} 已保存`);
      const nextLayout = await getOrchestrationLayout(
        serverRevision.graphId,
        serverRevision.revisionId,
      );
      setLayout(nextLayout);
    } catch (saveError) {
      const conflict = getRevisionConflict(saveError);
      if (conflict) {
        setRevisionConflict(conflict);
        try {
          const latest = await getLatestOrchestrationRevision(definition.graphId);
          setLatestRevision(latest);
          setConflictDiff(summarizeDefinitionDiff(draftDefinition, latest));
        } catch {
          // diff is best-effort; the conflict alert still offers reload
        }
      } else {
        setRevisionSaveError(getErrorMessage(saveError));
      }
    } finally {
      setRevisionSaving(false);
    }
  }, [definition, draftDefinition]);

  const handleReloadLatest = useCallback(async () => {
    if (!definition) return;
    setRevisionSaving(true);
    try {
      const latest =
        latestRevision ?? (await getLatestOrchestrationRevision(definition.graphId));
      const applied = reloadLatestRevision(latest);
      setDefinition(applied.saved);
      setDraftDefinition(undefined);
      setContentDirty(false);
      setRevisionConflict(undefined);
      setConflictDiff(undefined);
      setLatestRevision(undefined);
      setDraftValidation(undefined);
      setSelectedNodeId(undefined);
      setLayout(undefined);
      const nextLayout = await getOrchestrationLayout(latest.graphId, latest.revisionId);
      setLayout(nextLayout);
      setLayoutResetNonce((current) => current + 1);
      message.success(`已重新加载最新 Revision ${latest.revisionId}`);
    } catch (reloadError) {
      setRevisionSaveError(getErrorMessage(reloadError));
    } finally {
      setRevisionSaving(false);
    }
  }, [definition, latestRevision]);

  useEffect(() => {
    const selected = effectiveDefinition?.nodes.find((node) => node.nodeId === selectedNodeId);
    setNodeEditTitle(selected?.title ?? '');
    setNodeEditObjective(selected?.objective ?? '');
  }, [effectiveDefinition, selectedNodeId]);

  const selectedNodeDefinition = effectiveDefinition?.nodes.find(
    (node) => node.nodeId === selectedNodeId,
  );
  const selectedRun = displayRun?.nodes.find((node) => node.nodeId === selectedNodeId);
  const selectedComponent = catalog?.components.find(
    (component) =>
      component.descriptor.componentType === selectedNodeDefinition?.component.componentType &&
      component.descriptor.version === selectedNodeDefinition?.component.version,
  );
  const layoutSaveTarget = definition
    ? getLayoutSaveTarget(definition, draftDefinition)
    : undefined;
  const selectedNodeIssues = selectedNodeDefinition
    ? validateNodeDraft(selectedNodeDefinition)
    : [];
  const selectedGraphSummary = graphs.find((graph) => graph.graphId === selectedGraphId);
  const graphDeletionBlocker = getGraphDeletionBlocker(selectedGraphSummary);
  const completedNodes = displayRun?.nodes.filter((node) => node.status === 'completed').length ?? 0;
  const runningNodes = displayRun?.nodes.filter((node) => ['claimed', 'running'].includes(node.status)).length ?? 0;
  const failedNodes = displayRun?.nodes.filter((node) => node.status === 'failed').length ?? 0;

  return (
    <PageContainer
      header={{
        title: 'Agent 编排',
        subTitle: 'Graph/Run 可发现 · executable revision、editor layout 与运行事实相互隔离',
        tags: <Tag color="blue">Phase 4 / Layout Editor</Tag>,
      }}
    >
      <Card
        title="浏览编排"
        size="small"
        style={{ marginBottom: 16 }}
        extra={
          <Space>
            <Button type="primary" onClick={openCreateGraphModal}>
              新建 Graph
            </Button>
            <Tooltip title={graphDeletionBlocker}>
              <span>
                <Button
                  danger
                  disabled={Boolean(graphDeletionBlocker)}
                  loading={managementLoading}
                  onClick={() => {
                    if (!selectedGraphSummary || graphDeletionBlocker) return;
                    Modal.confirm({
                      title: `删除 Graph ${selectedGraphSummary.graphId}？`,
                      content:
                        '这会永久删除全部不可变 Revision 和编辑器布局；该 Graph 尚无 Run。此操作不可撤销。',
                      okText: '确认删除',
                      okButtonProps: { danger: true },
                      cancelText: '取消',
                      onOk: () => handleDeleteGraph(selectedGraphSummary),
                    });
                  }}
                >
                  删除 Graph
                </Button>
              </span>
            </Tooltip>
          </Space>
        }
      >
        <Row gutter={[12, 12]} align="middle">
          <Col xs={24} md={5}>
            <Input
              allowClear
              value={workspaceFilter}
              onChange={(event) => setWorkspaceFilter(event.target.value)}
              onPressEnter={() =>
                void loadDiscovery(workspaceFilter, selectedGraphId, viewMode === 'graph')
              }
              placeholder="工作区 ID（留空为全部）"
            />
          </Col>
          <Col xs={24} md={8}>
            <Select
              showSearch
              optionFilterProp="label"
              value={selectedGraphId}
              loading={discoveryLoading}
              placeholder="选择 Graph"
              style={{ width: '100%' }}
              onChange={(graphId) => void openGraph(graphId)}
              options={graphs.map((graph) => ({
                value: graph.graphId,
                label: `${graph.objective} · r${graph.currentRevision} · ${graph.runCount} runs`,
              }))}
            />
          </Col>
          <Col xs={24} md={8}>
            <Select
              showSearch
              allowClear
              optionFilterProp="label"
              value={viewMode === 'run' ? run?.runId : undefined}
              loading={discoveryLoading}
              placeholder={selectedGraphId ? '选择 Run；清空则预览定义' : '先选择 Graph'}
              style={{ width: '100%' }}
              onChange={(runId) => {
                if (runId) void loadRun(runId);
                else if (selectedGraphId) void loadGraphPreview(selectedGraphId);
              }}
              options={runSummaries.map((item) => ({
                value: item.runId,
                label: `${item.runId} · ${runStatusMeta[item.status].label} · #${item.headSequence}`,
              }))}
            />
          </Col>
          <Col xs={24} md={3}>
            <Button
              block
              loading={discoveryLoading}
              onClick={() =>
                void loadDiscovery(workspaceFilter, selectedGraphId, viewMode === 'graph')
              }
            >
              刷新
            </Button>
          </Col>
        </Row>
        <Form
          form={form}
          layout="inline"
          onFinish={({ runId }) => void loadRun(runId)}
          initialValues={{ runId: '' }}
          style={{ marginTop: 12 }}
        >
          <Form.Item
            name="runId"
            label="直接打开 Run ID"
            rules={[{ required: true, whitespace: true, message: '请输入编排 Run ID' }]}
            style={{ flex: 1 }}
          >
            <Input allowClear placeholder="高级入口；通常直接使用上方 Graph/Run 浏览器" />
          </Form.Item>
          <Form.Item>
            <Button type="primary" htmlType="submit" loading={loading}>
              打开运行图
            </Button>
          </Form.Item>
        </Form>
      </Card>

      {error ? <Alert type="error" showIcon message="运行图加载失败" description={error} style={{ marginBottom: 16 }} /> : null}
      {watchError ? (
        <Alert
          type="warning"
          showIcon
          closable
          message="实时事件流正在重连"
          description={watchError}
          onClose={() => setWatchError(undefined)}
          style={{ marginBottom: 16 }}
        />
      ) : null}
      {layoutConflict ? (
        <Alert
          type="warning"
          showIcon
          message="布局保存冲突"
          description={
            <Space direction="vertical" size={4}>
              <Text>{layoutConflict.message}</Text>
              <Text type="secondary">
                {layoutConflict.currentLayoutRevision !== undefined
                  ? `服务器当前为 L${layoutConflict.currentLayoutRevision}。`
                  : '服务器布局已发生变化。'}
                本地坐标仍保留；重新加载会放弃这些未保存修改。
              </Text>
            </Space>
          }
          action={
            <Button size="small" loading={layoutSaving} onClick={() => void handleReloadLayout()}>
              放弃本地修改并重新加载
            </Button>
          }
          style={{ marginBottom: 16 }}
        />
      ) : null}
      {layoutSaveError ? (
        <Alert
          type="error"
          showIcon
          closable
          message="布局操作失败"
          description={layoutSaveError}
          onClose={() => setLayoutSaveError(undefined)}
          style={{ marginBottom: 16 }}
        />
      ) : null}
      {revisionConflict ? (
        <Alert
          type="warning"
          showIcon
          message="Revision 保存冲突"
          description={
            <Space direction="vertical" size={4}>
              <Text>{revisionConflict.message}</Text>
              <Text type="secondary">
                {revisionConflict.currentRevision !== undefined
                  ? `服务器当前为 r${revisionConflict.currentRevision}。`
                  : '服务端 Revision 已发生变化。'}
                本地草稿已保留；重新加载会放弃这些未保存修改。
              </Text>
              {conflictDiff ? (
                <Text type="secondary">
                  本地 {effectiveDefinition?.nodes.length ?? 0} 节点 vs 最新{' '}
                  {latestRevision?.nodes.length ?? 0} 节点；新增{' '}
                  {conflictDiff.nodesAdded.length}、删除 {conflictDiff.nodesRemoved.length}。
                </Text>
              ) : null}
            </Space>
          }
          action={
            <Space>
              <Button
                size="small"
                danger
                loading={revisionSaving}
                onClick={() => {
                  Modal.confirm({
                    title: '放弃本地草稿并加载最新 Revision？',
                    content: '这会丢失尚未保存的节点编辑。此操作不可撤销。',
                    okText: '放弃草稿并加载',
                    okButtonProps: { danger: true },
                    cancelText: '取消',
                    onOk: () => void handleReloadLatest(),
                  });
                }}
              >
                放弃草稿并加载最新
              </Button>
              <Button
                size="small"
                disabled={!latestRevision}
                onClick={() => setConflictDiffModalOpen(true)}
              >
                保留草稿并查看差异
              </Button>
            </Space>
          }
          style={{ marginBottom: 16 }}
        />
      ) : null}
      {revisionSaveError ? (
        <Alert
          type="error"
          showIcon
          closable
          message="Revision 保存失败"
          description={
            <Space direction="vertical" size={4}>
              <Text>{revisionSaveError}</Text>
              {draftValidation ? (
                <ul style={{ margin: 0, paddingInlineStart: 20 }}>
                  {draftValidation.issues.map((issue) => (
                    <li key={`${issue.code}-${issue.elementId ?? issue.path ?? 'root'}`}>
                      <Text type="secondary">
                        [{issue.severity}] {issue.code}
                        {issue.elementId ? ` · ${issue.elementType}:${issue.elementId}` : ''} · {issue.message}
                      </Text>
                    </li>
                  ))}
                </ul>
              ) : null}
            </Space>
          }
          onClose={() => setRevisionSaveError(undefined)}
          style={{ marginBottom: 16 }}
        />
      ) : null}

      <Spin spinning={loading}>
        {!effectiveDefinition || !displayRun || !flowModel ? (
          <Card>
            <Empty description="当前过滤范围没有编排图；可从上方新建 Graph" />
          </Card>
        ) : (
          <>
            <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
              <Col xs={12} md={6}>
                <Card size="small">
                  <Statistic
                    title={viewMode === 'run' ? '运行状态' : '当前视图'}
                    valueRender={() => (
                      viewMode === 'run' ? (
                        <Tag color={runStatusMeta[displayRun.status].color}>{runStatusMeta[displayRun.status].label}</Tag>
                      ) : (
                        <Tag color="geekblue">定义预览</Tag>
                      )
                    )}
                  />
                </Card>
              </Col>
              <Col xs={12} md={6}>
                <Card size="small"><Statistic title="节点" value={effectiveDefinition.nodes.length} suffix={`完成 ${completedNodes}`} /></Card>
              </Col>
              <Col xs={12} md={6}>
                <Card size="small"><Statistic title="运行 / 失败" value={runningNodes} suffix={`/ ${failedNodes}`} /></Card>
              </Col>
              <Col xs={12} md={6}>
                <Card size="small"><Statistic title="事件高水位" value={displayRun.headSequence} /></Card>
              </Col>
            </Row>

            <Row gutter={[16, 16]}>
              <Col xs={24} xl={17}>
                <Card
                  title={effectiveDefinition.objective}
                  extra={
                    <Space size="small" wrap>
                      <Tag>{effectiveDefinition.graphId}</Tag>
                      <Tag color="geekblue">Revision {effectiveDefinition.revision}</Tag>
                      {draftDefinition ? (
                        <Tag color="orange">草稿 {formatRevisionId(effectiveDefinition.graphId, definition?.revision ? definition.revision + 1 : 0)}</Tag>
                      ) : null}
                      {contentDirty ? <Tag color="warning">内容未保存</Tag> : null}
                      <Tag>并发 {effectiveDefinition.maxConcurrency}</Tag>
                      <Tag color={layout ? 'purple' : 'default'}>
                        {layout ? `布局 L${layout.layoutRevision}` : '自动布局'}
                      </Tag>
                      {layoutDirty ? <Tag color="warning">布局未保存</Tag> : null}
                      <Button
                        size="small"
                        disabled={viewMode !== 'graph' || !definition}
                        onClick={() => setAddNodeModalOpen(true)}
                      >
                        新增节点
                      </Button>
                      <Button
                        size="small"
                        danger
                        disabled={!contentDirty}
                        onClick={() => {
                          Modal.confirm({
                            title: '放弃本地草稿？',
                            content: '这会丢弃尚未保存的节点编辑；不可撤销。',
                            okText: '放弃草稿',
                            okButtonProps: { danger: true },
                            cancelText: '取消',
                            onOk: () => handleDiscardDraft(),
                          });
                        }}
                      >
                        放弃草稿
                      </Button>
                      <Button
                        size="small"
                        type="primary"
                        loading={revisionSaving}
                        disabled={!contentDirty}
                        onClick={() => void handleSaveRevision()}
                      >
                        保存 Revision
                      </Button>
                      <Button
                        size="small"
                        disabled={!layout && !layoutDirty}
                        loading={layoutSaving}
                        onClick={() => void handleReloadLayout()}
                      >
                        重新加载布局
                      </Button>
                      <Tooltip title={layoutSaveTarget?.blocked ? layoutSaveTarget.reason : undefined}>
                        <span>
                          <Button
                            size="small"
                            type="primary"
                            disabled={!layoutDirty || layoutSaveTarget?.blocked}
                            loading={layoutSaving}
                            onClick={() => void handleSaveLayout()}
                          >
                            保存布局
                          </Button>
                        </span>
                      </Tooltip>
                    </Space>
                  }
                  styles={{ body: { padding: 0 } }}
                >
                  <div style={{ width: '100%', height: 620, background: token.colorFillAlter }}>
                    <ReactFlowProvider>
                      <ReactFlow
                        key={editorIdentity}
                        nodes={editorNodes}
                        edges={flowModel.edges}
                        fitView={!layout}
                        fitViewOptions={{ padding: 0.2 }}
                        defaultViewport={layout?.viewport}
                        nodesDraggable
                        nodesConnectable={false}
                        deleteKeyCode={null}
                        onInit={(instance) => {
                          flowInstanceRef.current = instance;
                        }}
                        onNodesChange={handleNodesChange}
                        onMoveEnd={(event) => {
                          if (event) markLayoutDirty();
                        }}
                        onNodeClick={(_, node) => setSelectedNodeId(node.id)}
                        proOptions={{ hideAttribution: true }}
                      >
                        <Background />
                        <MiniMap pannable zoomable />
                        <Controls showInteractive={false} />
                      </ReactFlow>
                    </ReactFlowProvider>
                  </div>
                </Card>
              </Col>

              <Col xs={24} xl={7}>
                <Card title="节点检查器" style={{ marginBottom: 16 }}>
                  {!selectedNodeDefinition || !selectedRun ? (
                    <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="选择画布中的节点" />
                  ) : (
                    <>
                      <Space wrap style={{ marginBottom: 12 }}>
                        <Tag color={nodeStatusMeta[selectedRun.status].color}>
                          {nodeStatusMeta[selectedRun.status].label}
                        </Tag>
                        <Tag>{selectedNodeDefinition.kind}</Tag>
                        <Tag>{selectedComponent?.descriptor.displayName ?? selectedNodeDefinition.component.componentType}</Tag>
                        {draftDefinition ? <Tag color="orange">草稿</Tag> : null}
                      </Space>
                      <Descriptions size="small" column={1} bordered>
                        <Descriptions.Item label="节点">{selectedNodeDefinition.title}</Descriptions.Item>
                        <Descriptions.Item label="目标">{selectedNodeDefinition.objective}</Descriptions.Item>
                        <Descriptions.Item label="尝试">{selectedRun.attempt} / {selectedRun.maxAttempts}</Descriptions.Item>
                        <Descriptions.Item label="精确路由">{selectedNodeDefinition.executor?.routeKey ?? '—'}</Descriptions.Item>
                        <Descriptions.Item label="Execution Run">{selectedRun.executionRunId ?? '—'}</Descriptions.Item>
                        <Descriptions.Item label="Sub-session">{selectedRun.subSessionId ?? '—'}</Descriptions.Item>
                        <Descriptions.Item label="更新时间">{dayjs(selectedRun.updatedAtUtc).format('YYYY-MM-DD HH:mm:ss')}</Descriptions.Item>
                      </Descriptions>
                      {selectedNodeIssues.length > 0 ? (
                        <Alert
                          type="warning"
                          showIcon
                          message="本地结构校验未通过"
                          description={
                            <ul style={{ margin: 0, paddingInlineStart: 20 }}>
                              {selectedNodeIssues.map((issue) => (
                                <li key={issue.code}>
                                  <Text type="secondary">{issue.code} · {issue.message}</Text>
                                </li>
                              ))}
                            </ul>
                          }
                          style={{ marginTop: 12 }}
                        />
                      ) : null}
                      <Form layout="vertical" style={{ marginTop: 16 }}>
                        <Form.Item label="标题" style={{ marginBottom: 8 }}>
                          <Input
                            value={nodeEditTitle}
                            onChange={(event) => setNodeEditTitle(event.target.value)}
                            disabled={viewMode !== 'graph'}
                          />
                        </Form.Item>
                        <Form.Item label="目标" style={{ marginBottom: 8 }}>
                          <Input.TextArea
                            rows={3}
                            value={nodeEditObjective}
                            onChange={(event) => setNodeEditObjective(event.target.value)}
                            disabled={viewMode !== 'graph'}
                          />
                        </Form.Item>
                        <Space wrap>
                          <Button
                            type="primary"
                            size="small"
                            disabled={viewMode !== 'graph'}
                            onClick={handleApplyNodeEdit}
                          >
                            应用修改
                          </Button>
                          <Button
                            danger
                            size="small"
                            disabled={viewMode !== 'graph'}
                            onClick={() => {
                              Modal.confirm({
                                title: `删除节点 ${selectedNodeDefinition.nodeId}？`,
                                content:
                                  '会同步删除该节点的所有入边/出边；保存 Revision 前可放弃草稿恢复。',
                                okText: '确认删除',
                                okButtonProps: { danger: true },
                                cancelText: '取消',
                                onOk: () => handleDeleteSelectedNode(),
                              });
                            }}
                          >
                            删除节点
                          </Button>
                        </Space>
                      </Form>
                      {selectedRun.outputSummary ? <Paragraph style={{ marginTop: 12 }}>{selectedRun.outputSummary}</Paragraph> : null}
                      {selectedRun.artifactReference ? <Text code copyable>{selectedRun.artifactReference}</Text> : null}
                      {selectedRun.errorMessage ? <Alert type="error" showIcon message={selectedRun.errorMessage} style={{ marginTop: 12 }} /> : null}
                    </>
                  )}
                </Card>

                <Card title={`运行事件（最近 ${events.length} 条）`}>
                  {events.length === 0 ? (
                    <Empty
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                      description={viewMode === 'graph' ? '定义预览不产生运行事件' : '尚无已提交事件'}
                    />
                  ) : (
                    <Timeline
                      items={[...events].reverse().slice(0, 80).map((event) => ({
                        color: event.eventType.endsWith('.failed') ? 'red' : event.eventType.endsWith('.completed') ? 'green' : 'blue',
                        children: (
                          <div>
                            <Space size={4} wrap>
                              <Text strong>#{event.sequence}</Text>
                              <Text>{event.eventType}</Text>
                              {event.nodeId ? <Tag>{event.nodeId}</Tag> : null}
                            </Space>
                            {event.summary ? <Paragraph ellipsis={{ rows: 3 }} style={{ marginBottom: 0 }}>{event.summary}</Paragraph> : null}
                            <Text type="secondary">{dayjs(event.recordedAtUtc).format('MM-DD HH:mm:ss')}</Text>
                          </div>
                        ),
                      }))}
                    />
                  )}
                </Card>
              </Col>
            </Row>
          </>
          )}
      </Spin>

      <Modal
        title="新建 Graph"
        open={createModalOpen}
        forceRender
        okText="创建"
        cancelText="取消"
        confirmLoading={managementLoading}
        onOk={() => createForm.submit()}
        onCancel={() => setCreateModalOpen(false)}
      >
        <Alert
          type="info"
          showIcon
          message="新 Graph 会包含一个 humanInput 占位节点"
          description="它是可校验的 Revision 1；后续组件编辑器可替换或扩展该节点。创建不会自动激活或运行。"
          style={{ marginBottom: 16 }}
        />
        <Form
          form={createForm}
          layout="vertical"
          onFinish={(values) => void handleCreateGraph(values)}
        >
          <Form.Item
            name="graphId"
            label="Graph ID"
            rules={[
              { required: true, whitespace: true, message: '请输入 Graph ID' },
              {
                pattern: /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/,
                message: '仅允许字母、数字、点、下划线和连字符',
              },
            ]}
          >
            <Input autoComplete="off" />
          </Form.Item>
          <Row gutter={12}>
            <Col span={12}>
              <Form.Item
                name="workspaceId"
                label="工作区 ID"
                rules={[{ required: true, whitespace: true, message: '请输入工作区 ID' }]}
              >
                <Input />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item
                name="rootSessionId"
                label="Root Session ID"
                rules={[{ required: true, whitespace: true, message: '请输入 Root Session ID' }]}
              >
                <Input />
              </Form.Item>
            </Col>
          </Row>
          <Form.Item
            name="objective"
            label="目标"
            rules={[{ required: true, whitespace: true, message: '请输入 Graph 目标' }]}
          >
            <Input.TextArea rows={3} placeholder="描述这个编排要完成的目标" />
          </Form.Item>
          <Form.Item
            name="maxConcurrency"
            label="最大并发"
            rules={[{ required: true, message: '请输入最大并发' }]}
          >
            <InputNumber min={1} max={64} precision={0} style={{ width: '100%' }} />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        title="从组件目录新增节点"
        open={addNodeModalOpen}
        okText="添加节点"
        cancelText="取消"
        onOk={() => {
          const component = catalog?.components.find(
            (item) =>
              `${item.descriptor.componentType}@${item.descriptor.version}` === selectedComponentType,
          );
          if (!component) {
            message.warning('请先选择要添加的组件');
            return;
          }
          handleAddNode(component);
        }}
        onCancel={() => {
          setAddNodeModalOpen(false);
          setSelectedComponentType(undefined);
        }}
      >
        <Alert
          type="info"
          showIcon
          message="只会从 /api/orchestrations/catalog 选择组件"
          description="节点 kind、executor/gate 形状和 contractHash 均由目录描述符决定，不手填。"
          style={{ marginBottom: 16 }}
        />
        <Select
          showSearch
          optionFilterProp="label"
          value={selectedComponentType}
          onChange={setSelectedComponentType}
          placeholder="搜索组件类型或名称"
          style={{ width: '100%' }}
          options={(catalog?.components ?? []).map((component) => ({
            value: `${component.descriptor.componentType}@${component.descriptor.version}`,
            label: `${component.descriptor.displayName} · ${component.descriptor.componentType}@${component.descriptor.version}`,
          }))}
        />
        {(() => {
          const selected = catalog?.components.find(
            (item) =>
              `${item.descriptor.componentType}@${item.descriptor.version}` === selectedComponentType,
          );
          if (!selected) return null;
          return (
            <Descriptions size="small" column={1} bordered style={{ marginTop: 16 }}>
              <Descriptions.Item label="类型">{selected.descriptor.componentType}</Descriptions.Item>
              <Descriptions.Item label="版本">{selected.descriptor.version}</Descriptions.Item>
              <Descriptions.Item label="分类">{selected.descriptor.category}</Descriptions.Item>
              <Descriptions.Item label="Node Kind">{selected.descriptor.nodeKind}</Descriptions.Item>
              <Descriptions.Item label="副作用">{selected.descriptor.sideEffect}</Descriptions.Item>
              <Descriptions.Item label="Contract Hash">
                <Text code copyable>{selected.contractHash}</Text>
              </Descriptions.Item>
            </Descriptions>
          );
        })()}
      </Modal>

      <Modal
        title="保留草稿并查看差异"
        open={conflictDiffModalOpen}
        footer={
          <Button type="primary" onClick={() => setConflictDiffModalOpen(false)}>
            关闭
          </Button>
        }
        onCancel={() => setConflictDiffModalOpen(false)}
      >
        {latestRevision && conflictDiff ? (
          <Space direction="vertical" size={8} style={{ width: '100%' }}>
            <Alert
              type="info"
              showIcon
              message={`本地草稿 r${definition?.revision ?? 0} + 1 vs 服务器最新 ${latestRevision.revisionId}`}
              description="以下为只读摘要；差异解决不会自动合并，保存前仍以服务端校验为准。"
            />
            <Descriptions size="small" column={1} bordered>
              <Descriptions.Item label="目标变化">{conflictDiff.objectiveChanged ? '是' : '否'}</Descriptions.Item>
              <Descriptions.Item label="新增节点">{conflictDiff.nodesAdded.length > 0 ? conflictDiff.nodesAdded.join('、') : '—'}</Descriptions.Item>
              <Descriptions.Item label="删除节点">{conflictDiff.nodesRemoved.length > 0 ? conflictDiff.nodesRemoved.join('、') : '—'}</Descriptions.Item>
              <Descriptions.Item label="新增边">{conflictDiff.edgesAdded.length > 0 ? conflictDiff.edgesAdded.join('、') : '—'}</Descriptions.Item>
              <Descriptions.Item label="删除边">{conflictDiff.edgesRemoved.length > 0 ? conflictDiff.edgesRemoved.join('、') : '—'}</Descriptions.Item>
            </Descriptions>
          </Space>
        ) : (
          <Empty description="尚未加载最新 Revision" />
        )}
      </Modal>
    </PageContainer>
  );
};

export default OrchestrationPage;
