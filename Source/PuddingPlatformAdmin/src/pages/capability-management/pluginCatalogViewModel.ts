import type { PluginCatalogItemDto } from '@/services/platform/api';

export type QuietBadgeStatus = 'success' | 'processing' | 'default' | 'error' | 'warning';

export interface StatusBadgeView {
  status: QuietBadgeStatus;
  label: string;
}

export interface PluginCatalogSummary {
  totalPlugins: number;
  manifestOnlyPlugins: number;
  invalidPlugins: number;
  totalTools: number;
}

export function buildPluginCatalogSummary(plugins: PluginCatalogItemDto[]): PluginCatalogSummary {
  return {
    totalPlugins: plugins.length,
    manifestOnlyPlugins: plugins.filter((item) => item.status === 'ManifestOnly').length,
    invalidPlugins: plugins.filter((item) => item.status === 'ManifestInvalid').length,
    totalTools: plugins.reduce((sum, item) => sum + item.toolCount, 0),
  };
}

export function getPluginStatusBadge(status?: string): StatusBadgeView {
  if (status === 'ManifestInvalid') {
    return { status: 'error', label: 'ManifestInvalid' };
  }
  if (status === 'ManifestOnly') {
    return { status: 'warning', label: 'ManifestOnly' };
  }
  if (status === 'Discovered') {
    return { status: 'processing', label: 'Discovered' };
  }
  return { status: 'default', label: status || 'Unknown' };
}

export function getPluginToolStatusBadge(status?: string): StatusBadgeView {
  if (status === 'Available') {
    return { status: 'success', label: 'Available' };
  }
  if (status === 'ManifestOnly') {
    return { status: 'warning', label: 'ManifestOnly' };
  }
  return { status: 'default', label: status || 'Unknown' };
}

export function getPluginDiagnosticStatusBadge(status?: string): StatusBadgeView {
  if (status === 'succeeded') {
    return { status: 'success', label: 'succeeded' };
  }
  if (status === 'failed') {
    return { status: 'error', label: 'failed' };
  }
  if (status === 'requested') {
    return { status: 'processing', label: 'requested' };
  }
  return { status: 'default', label: status || 'recorded' };
}
