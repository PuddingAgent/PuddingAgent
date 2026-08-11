import type {
  OrchestrationGraphCreateRequest,
  OrchestrationGraphSummary,
} from './types';

export interface OrchestrationGraphCreateFormValues {
  graphId: string;
  workspaceId: string;
  rootSessionId: string;
  objective: string;
  maxConcurrency: number;
  templateId: 'blank' | 'image-generation';
}

type DeletableGraphSummary = Pick<
  OrchestrationGraphSummary,
  'graphId' | 'runCount'
>;

export function createSuggestedGraphValues(
  workspaceId: string,
  createIdentity: () => string = () =>
    crypto.randomUUID().replace(/-/g, '').slice(0, 12),
): OrchestrationGraphCreateFormValues {
  const identity = createIdentity();
  return {
    graphId: `graph-${identity}`,
    workspaceId: workspaceId.trim() || 'default',
    rootSessionId: `admin-orchestration-${identity}`,
    objective: '',
    maxConcurrency: 1,
    templateId: 'image-generation',
  };
}

export function buildCreateGraphRequest(
  values: OrchestrationGraphCreateFormValues,
): OrchestrationGraphCreateRequest {
  return {
    graphId: values.graphId.trim(),
    workspaceId: values.workspaceId.trim(),
    rootSessionId: values.rootSessionId.trim(),
    objective: values.objective.trim(),
    maxConcurrency: values.maxConcurrency,
    templateId: values.templateId,
  };
}

export function getGraphDeletionBlocker(
  graph?: DeletableGraphSummary,
): string | undefined {
  if (!graph) return '请先选择 Graph';
  if (graph.runCount > 0) {
    return `该 Graph 已有 ${graph.runCount} 个 Run，为保护运行历史不能删除`;
  }
  return undefined;
}
