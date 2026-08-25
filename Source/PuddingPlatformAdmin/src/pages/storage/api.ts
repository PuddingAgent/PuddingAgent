import { request } from '@umijs/max';
import type {
  StorageCleanupJob,
  StorageCleanupJobEvent,
  StorageCleanupPreview,
  StorageCleanupPreviewRequest,
  StorageDataClass,
  StorageInventoryRefreshStatus,
  StorageInventorySnapshot,
  StorageInventoryTrendPoint,
  StorageRetentionPolicy,
  StorageRetentionPolicyUpdateRequest,
} from './types';

// ── ADR-076 语义存储管理 API（同源 Core，登录态 Admin JWT 自动携带）──
// 注意：只消费缓存快照与有界估算；刷新是异步 202 请求，不阻塞 UI。

export async function getStorageOverview(): Promise<StorageInventorySnapshot> {
  return request('/api/admin/storage/overview', { method: 'GET' });
}

export async function getStorageDataClasses(): Promise<StorageDataClass[]> {
  return request('/api/admin/storage/data-classes', { method: 'GET' });
}

export async function getProtectedObjects(): Promise<string[]> {
  return request('/api/admin/storage/protected-objects', { method: 'GET' });
}

export async function requestInventoryRefresh(): Promise<StorageInventoryRefreshStatus> {
  return request('/api/admin/storage/inventory/refresh', { method: 'POST' });
}

export async function getInventoryTrend(days: number): Promise<StorageInventoryTrendPoint[]> {
  return request(`/api/admin/storage/inventory/history?days=${days}`, { method: 'GET' });
}

export async function getRetentionPolicy(): Promise<StorageRetentionPolicy> {
  return request('/api/admin/storage/retention-policy', { method: 'GET' });
}

export async function updateRetentionPolicy(
  req: StorageRetentionPolicyUpdateRequest,
): Promise<StorageRetentionPolicy> {
  return request('/api/admin/storage/retention-policy', { method: 'PUT', data: req });
}

export async function createCleanupPreview(
  req: StorageCleanupPreviewRequest,
): Promise<StorageCleanupPreview> {
  return request('/api/admin/storage/cleanup/previews', { method: 'POST', data: req });
}

export async function createCleanupJob(previewId: string): Promise<{ jobId: string }> {
  return request('/api/admin/storage/cleanup/jobs', {
    method: 'POST',
    data: { previewId, requestId: `web-${previewId}` },
  });
}

export async function listCleanupJobs(limit = 20): Promise<StorageCleanupJob[]> {
  return request(`/api/admin/storage/cleanup/jobs?limit=${limit}`, { method: 'GET' });
}

export async function cancelCleanupJob(jobId: string): Promise<void> {
  return request(`/api/admin/storage/cleanup/jobs/${jobId}/cancel`, { method: 'POST' });
}

export async function confirmCleanupJob(jobId: string): Promise<void> {
  return request(`/api/admin/storage/cleanup/jobs/${jobId}/confirm`, { method: 'POST' });
}

export async function getCleanupJobEvents(jobId: string): Promise<StorageCleanupJobEvent[]> {
  return request(`/api/admin/storage/cleanup/jobs/${jobId}/events`, { method: 'GET' });
}

// ── 展示工具 ────────────────────────────────────────────────────

export function formatBytes(bytes?: number | null): string {
  if (bytes === null || bytes === undefined) return '—';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = -1;
  do {
    value /= 1024;
    unit++;
  } while (value >= 1024 && unit < units.length - 1);
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
}

export function formatCount(count?: number | null): string {
  if (count === null || count === undefined) return '—';
  if (count >= 100_000_000) return `${(count / 100_000_000).toFixed(1)} 亿`;
  if (count >= 10_000) return `${(count / 10_000).toFixed(1)} 万`;
  return count.toLocaleString('zh-CN');
}

export function formatUtc(iso?: string | null): string {
  if (!iso) return '—';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '—';
  return date.toLocaleString('zh-CN', { hour12: false });
}
