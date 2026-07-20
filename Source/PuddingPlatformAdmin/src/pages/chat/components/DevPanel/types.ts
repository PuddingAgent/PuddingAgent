// ── DevPanel 类型定义 ──────────────────────────────────────

import type {
  BenchmarkCaseSummaryDto,
} from '@/services/platform/api';

export interface DevRawEvent {
  id: string;
  timestamp: number;
  event: string;
  payload: string;
}

export interface ContextLayerInfo {
  layerName: string;
  tokenCount: number;
  contentPreview: string;
}

export interface ContextSnapshot {
  sessionId: string;
  assembledAt?: string;
  layers: ContextLayerInfo[];
  totalTokens: number;
  message?: string;
}

export interface SubconsciousResult {
  sessionId: string;
  job?: {
    jobId: string;
    status: string;
    factsExtracted: number;
    factsMerged: number;
    factsDiscarded: number;
    chaptersCreated: number;
    llmTokensUsed: number;
    llmModelId?: string;
    elapsedMs: number;
    errorMessage?: string;
    startedAt?: number;
    completedAt?: number;
    createdAt: number;
  };
  facts: Array<{
    factId: string;
    statement: string;
    confidence: number;
    category: string;
    status: string;
    updatedAt: number;
  }>;
  preferences: Array<{
    preferenceId: string;
    category: string;
    key: string;
    value: string;
    updatedAt: number;
  }>;
  llmRawResponse?: string | null;
  note?: string;
}

export interface DevPanelProps {
  workspaceId?: string;
  sessionId?: string | null;
  rawEvents: DevRawEvent[];
  onRunBenchmarkPrompt?: (
    prompt: string,
    metadata: Record<string, string>,
  ) => Promise<void> | void;
}
