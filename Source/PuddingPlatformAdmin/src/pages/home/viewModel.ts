import type { WorkspaceWithPermDto } from '@/services/platform/api';

export interface HomeWorkspaceSummary {
  total: number;
  available: number;
  frozen: number;
  teamCount: number;
}

export function createHomeWorkspaceSummary(
  workspaces: WorkspaceWithPermDto[],
): HomeWorkspaceSummary {
  return {
    total: workspaces.length,
    available: workspaces.filter(
      (workspace) => workspace.isEnabled && !workspace.isFrozen,
    ).length,
    frozen: workspaces.filter((workspace) => workspace.isFrozen).length,
    teamCount: new Set(workspaces.map((workspace) => workspace.teamId)).size,
  };
}

export function getHomeGreeting(hour: number): string {
  if (hour < 6) return '夜深了';
  if (hour < 12) return '早上好';
  if (hour < 14) return '中午好';
  if (hour < 18) return '下午好';
  return '晚上好';
}
