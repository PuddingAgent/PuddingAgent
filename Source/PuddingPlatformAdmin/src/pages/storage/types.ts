// ── ADR-076 存储管理 wire DTO（与 PuddingCore/Storage 合同对齐）──

export type StorageSafetyLevel = 'Disposable' | 'Derived' | 'Evidence' | 'UserData';

export type StorageEstimateState = 'Estimating' | 'Updated' | 'Unavailable';

export type StorageCleanupJobStatus =
  | 'Queued'
  | 'Running'
  | 'PausedBusy'
  | 'NeedsConfirmation'
  | 'Cancelling'
  | 'Completed'
  | 'Partial'
  | 'Failed'
  | 'Cancelled';

export type StorageInventoryRefreshState = 'Idle' | 'Running' | 'Completed' | 'Failed';

export interface StorageDataClass {
  targetId: string;
  displayName: string;
  description: string;
  safetyLevel: StorageSafetyLevel;
  safetyLevelName: string;
  catalogVersion: number;
  manualCleanupAllowed: boolean;
  automaticCleanupAllowed: boolean;
  protected: boolean;
  protectionReason?: string | null;
  defaultRetentionDays?: number | null;
  minRetentionDays?: number | null;
  maxRetentionDays?: number | null;
  requiresRollupBeforeAutomatic: boolean;
}

export interface StorageInventoryDatabase {
  databaseId: string;
  displayName: string;
  relativePath: string;
  mainBytes: number;
  walBytes: number;
  sharedMemoryBytes: number;
  totalBytes: number;
  pageSizeBytes: number;
  pageCount: number;
  freePageCount: number;
  reusableFreeBytes: number;
}

export interface StorageInventoryClass {
  targetId: string;
  displayName: string;
  estimatedBytes?: number | null;
  estimatedRows?: number | null;
  oldestUtc?: string | null;
  newestUtc?: string | null;
  estimateState: StorageEstimateState;
  updatedAtUtc?: string | null;
}

export interface StorageInventorySnapshot {
  snapshotId: string;
  revision: number;
  schemaVersion: number;
  capturedAtUtc: string;
  updatedAtUtc: string;
  databases: StorageInventoryDatabase[];
  classes: StorageInventoryClass[];
  isRefreshing: boolean;
  warnings: string[];
}

export interface StorageInventoryRefreshStatus {
  refreshId: string;
  state: StorageInventoryRefreshState;
  requestedAtUtc: string;
  completedAtUtc?: string | null;
  snapshotRevision: number;
}

export interface StorageInventoryTrendPoint {
  capturedAtUtc: string;
  classBytes: Record<string, number>;
  databaseTotalBytes: number;
}

export interface StorageRetentionPolicyTarget {
  targetId: string;
  displayName: string;
  enabled: boolean;
  retentionDays?: number | null;
  automaticCleanupAllowed: boolean;
  defaultRetentionDays?: number | null;
  minRetentionDays?: number | null;
  maxRetentionDays?: number | null;
}

export interface StorageRetentionPolicy {
  policyRevision: number;
  automaticCleanupEnabled: boolean;
  runIntervalHours: number;
  startupDelaySeconds: number;
  lastCompletedAtUtc?: string | null;
  nextRunEstimateUtc?: string | null;
  targets: StorageRetentionPolicyTarget[];
  warnings: string[];
}

export interface StorageRetentionPolicyUpdateRequest {
  expectedRevision: number;
  automaticCleanupEnabled?: boolean | null;
  targets?: StorageRetentionPolicyTargetUpdate[] | null;
}

export interface StorageRetentionPolicyTargetUpdate {
  targetId: string;
  enabled?: boolean | null;
  retentionDays?: number | null;
}

export interface StorageCleanupPreviewRequest {
  targetIds: string[];
  olderThanDays?: number | null;
  cutoffUtc?: string | null;
}

export interface StorageCleanupTargetPreview {
  targetId: string;
  displayName: string;
  actionSummary: string;
  estimatedCandidateRows: number;
  candidatesTruncated: boolean;
  estimatedBytes?: number | null;
  oldestUtc?: string | null;
}

export interface StorageCleanupPreview {
  previewId: string;
  catalogVersion: number;
  policyRevision: number;
  createdAtUtc: string;
  expiresAtUtc: string;
  cutoffUtc: string;
  targets: StorageCleanupTargetPreview[];
  warnings: string[];
  hasCandidates: boolean;
}

export interface StorageCleanupJobProgress {
  discoveredRows: number;
  processedRows: number;
  deletedRows: number;
  clearedRows: number;
  skippedRows: number;
  failedRows: number;
  deletedFiles: number;
  reusableBytesEstimate: number;
  remainingRowsEstimate?: number | null;
}

export interface StorageCleanupJob {
  jobId: string;
  trigger: string;
  status: StorageCleanupJobStatus;
  createdAtUtc: string;
  startedAtUtc?: string | null;
  finishedAtUtc?: string | null;
  cutoffUtc: string;
  targetIds: string[];
  progress: StorageCleanupJobProgress;
  warnings: string[];
  errorCode?: string | null;
  errorMessage?: string | null;
}

export interface StorageCleanupJobEvent {
  timestampUtc: string;
  kind: string;
  targetId?: string | null;
  counters?: Record<string, number> | null;
  message?: string | null;
}
