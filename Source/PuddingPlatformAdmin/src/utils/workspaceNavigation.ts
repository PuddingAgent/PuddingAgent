export interface WorkspaceRouteContext {
  workspaceId?: string;
  agentId?: string;
  sessionId?: string | null;
}

export interface WorkspaceRoutePatch {
  workspaceId?: string | null;
  agentId?: string | null;
  sessionId?: string | null;
}

export interface WorkspaceNavigationItem {
  workspaceId: string;
  isEnabled?: boolean;
  isFrozen?: boolean;
}

export interface WorkspaceRecentVisit {
  workspaceId: string;
  agentId?: string;
  visitedAt: number;
}

export const RECENT_WORKSPACE_VISIT_KEY = 'pudding:recentWorkspaceVisit';
export const PUDDING_WORKSPACES_PATH = '/pudding/workspaces';

function appendQuery(path: string, params: URLSearchParams): string {
  const query = params.toString();
  return query ? `${path}?${query}` : path;
}

function clean(value?: string | null): string | undefined {
  return value?.trim() || undefined;
}

function canUseWorkspace(workspace: WorkspaceNavigationItem): boolean {
  return workspace.isEnabled !== false && workspace.isFrozen !== true;
}

export function buildWorkspacePath(): string {
  return PUDDING_WORKSPACES_PATH;
}

export function buildWorkspaceSettingsPath(workspaceId: string, tab?: string): string {
  const params = new URLSearchParams();
  if (tab) params.set('tab', tab);
  return appendQuery(`/workspace/${encodeURIComponent(workspaceId)}`, params);
}

export function buildWorkspaceStudioPath(context: Pick<WorkspaceRouteContext, 'workspaceId' | 'agentId'> = {}): string {
  if (!context.workspaceId) return buildWorkspacePath();
  const workspacePath = `${PUDDING_WORKSPACES_PATH}/${encodeURIComponent(context.workspaceId)}`;
  return context.agentId ? `${workspacePath}/${encodeURIComponent(context.agentId)}` : workspacePath;
}

export function buildChatPath(context: WorkspaceRouteContext = {}): string {
  const params = new URLSearchParams();
  if (context.workspaceId) params.set('workspaceId', context.workspaceId);
  if (context.agentId) params.set('agentId', context.agentId);
  if (context.sessionId) params.set('sessionId', context.sessionId);
  return appendQuery('/chat', params);
}

function applyRouteParam(params: URLSearchParams, key: string, value?: string | null): void {
  if (value === undefined) return;
  const cleaned = clean(value);
  if (cleaned) {
    params.set(key, cleaned);
  } else {
    params.delete(key);
  }
}

export function buildChatPathWithQuery(context: WorkspaceRoutePatch = {}, currentSearch = ''): string {
  const params = new URLSearchParams(currentSearch);
  applyRouteParam(params, 'workspaceId', context.workspaceId);
  applyRouteParam(params, 'agentId', context.agentId);
  applyRouteParam(params, 'sessionId', context.sessionId);
  return appendQuery('/chat', params);
}

export function buildWorkspaceChatPath(workspaceId?: string): string {
  return buildChatPath({ workspaceId });
}

export function parseWorkspaceRouteContext(search: string): WorkspaceRouteContext {
  const params = new URLSearchParams(search);
  return {
    workspaceId: clean(params.get('workspaceId')),
    agentId: clean(params.get('agentId')),
    sessionId: clean(params.get('sessionId')),
  };
}

export function resolveDefaultWorkspace(
  workspaces: WorkspaceNavigationItem[],
  requestedWorkspaceId?: string,
): string | undefined {
  if (requestedWorkspaceId) {
    const requested = workspaces.find((workspace) => workspace.workspaceId === requestedWorkspaceId);
    if (requested && canUseWorkspace(requested)) return requested.workspaceId;
  }

  return workspaces.find((workspace) => workspace.workspaceId === 'default' && canUseWorkspace(workspace))?.workspaceId
    ?? workspaces.find(canUseWorkspace)?.workspaceId
    ?? workspaces.find((workspace) => workspace.workspaceId === 'default')?.workspaceId
    ?? workspaces[0]?.workspaceId;
}

export function resolveDefaultAgent<T extends { agentId: string; isEnabled?: boolean; isFrozen?: boolean }>(
  agents: T[],
  requestedAgentId?: string,
): string | undefined {
  if (requestedAgentId) {
    const requested = agents.find((agent) => agent.agentId === requestedAgentId);
    if (requested && requested.isEnabled !== false && requested.isFrozen !== true) return requested.agentId;
  }

  return agents.find((agent) => agent.isEnabled !== false && agent.isFrozen !== true)?.agentId
    ?? agents.find((agent) => agent.isEnabled !== false)?.agentId
    ?? agents[0]?.agentId;
}

export function readRecentWorkspaceVisit(): WorkspaceRecentVisit | undefined {
  if (typeof window === 'undefined') return undefined;
  try {
    const raw = window.localStorage.getItem(RECENT_WORKSPACE_VISIT_KEY);
    if (!raw) return undefined;
    const parsed = JSON.parse(raw) as Partial<WorkspaceRecentVisit>;
    const workspaceId = clean(parsed.workspaceId);
    if (!workspaceId) return undefined;
    return {
      workspaceId,
      agentId: clean(parsed.agentId),
      visitedAt: typeof parsed.visitedAt === 'number' ? parsed.visitedAt : 0,
    };
  } catch {
    return undefined;
  }
}

export function rememberWorkspaceVisit(visit: Pick<WorkspaceRecentVisit, 'workspaceId'> & Partial<WorkspaceRecentVisit>): void {
  if (typeof window === 'undefined') return;
  const workspaceId = clean(visit.workspaceId);
  if (!workspaceId) return;
  const payload: WorkspaceRecentVisit = {
    workspaceId,
    agentId: clean(visit.agentId),
    visitedAt: visit.visitedAt ?? Date.now(),
  };
  try {
    window.localStorage.setItem(RECENT_WORKSPACE_VISIT_KEY, JSON.stringify(payload));
  } catch {
    // localStorage can be unavailable in privacy modes; routing still works without recents.
  }
}

export function clearRecentWorkspaceVisit(): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.removeItem(RECENT_WORKSPACE_VISIT_KEY);
  } catch {
    // ignore storage failures
  }
}

export function resolveWorkspaceEntryPath(
  workspaces: WorkspaceNavigationItem[],
  recentVisit?: WorkspaceRecentVisit,
): string {
  if (recentVisit?.workspaceId) {
    const recent = workspaces.find((workspace) => workspace.workspaceId === recentVisit.workspaceId);
    if (recent && canUseWorkspace(recent)) {
      return buildWorkspaceStudioPath({
        workspaceId: recent.workspaceId,
        agentId: recentVisit.agentId,
      });
    }
  }

  const available = workspaces.filter(canUseWorkspace);
  if (available.length === 1) {
    return buildWorkspaceStudioPath({ workspaceId: available[0].workspaceId });
  }

  return buildWorkspacePath();
}
