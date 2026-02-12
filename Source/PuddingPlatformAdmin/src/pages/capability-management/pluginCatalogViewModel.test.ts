import type { PluginCatalogItemDto } from '@/services/platform/api';
import {
  buildPluginCatalogSummary,
  getPluginDiagnosticStatusBadge,
  getPluginStatusBadge,
  getPluginToolStatusBadge,
} from './pluginCatalogViewModel';

const plugin = (partial: Partial<PluginCatalogItemDto>): PluginCatalogItemDto => ({
  pluginId: 'pudding.sample',
  name: 'Sample Plugin',
  version: '1.0.0',
  status: 'ManifestOnly',
  statusReason: '',
  toolCount: 1,
  tools: [
    {
      toolId: 'sample_tool',
      name: 'Sample Tool',
      description: '',
      category: 'Local',
      permissionLevel: 'Low',
      safety: 'Safe',
      runtimeStatus: 'ManifestOnly',
      isEnabledByDefault: false,
      sortOrder: 1,
      parameters: {},
    },
  ],
  ...partial,
});

describe('plugin catalog view model', () => {
  it('summarizes manifest-only and invalid plugin packages', () => {
    const summary = buildPluginCatalogSummary([
      plugin({ pluginId: 'pudding.sample', status: 'ManifestOnly', toolCount: 2 }),
      plugin({ pluginId: 'bad.plugin', status: 'ManifestInvalid', toolCount: 0 }),
    ]);

    expect(summary.totalPlugins).toBe(2);
    expect(summary.manifestOnlyPlugins).toBe(1);
    expect(summary.invalidPlugins).toBe(1);
    expect(summary.totalTools).toBe(2);
  });

  it('maps plugin and tool runtime states to quiet status badges', () => {
    expect(getPluginStatusBadge('ManifestOnly')).toEqual({
      status: 'warning',
      label: 'ManifestOnly',
    });
    expect(getPluginStatusBadge('ManifestInvalid')).toEqual({
      status: 'error',
      label: 'ManifestInvalid',
    });
    expect(getPluginToolStatusBadge('Available')).toEqual({
      status: 'success',
      label: 'Available',
    });
  });

  it('maps plugin diagnostic states to badges', () => {
    expect(getPluginDiagnosticStatusBadge('succeeded')).toEqual({
      status: 'success',
      label: 'succeeded',
    });
    expect(getPluginDiagnosticStatusBadge('failed')).toEqual({
      status: 'error',
      label: 'failed',
    });
    expect(getPluginDiagnosticStatusBadge(undefined)).toEqual({
      status: 'default',
      label: 'recorded',
    });
  });
});
