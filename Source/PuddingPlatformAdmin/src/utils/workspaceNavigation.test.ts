import {
  RECENT_WORKSPACE_VISIT_KEY,
  buildChatPath,
  buildChatPathWithQuery,
  buildWorkspaceChatPath,
  buildWorkspacePath,
  buildWorkspaceSettingsPath,
  buildWorkspaceStudioPath,
  clearRecentWorkspaceVisit,
  parseWorkspaceRouteContext,
  readRecentWorkspaceVisit,
  rememberWorkspaceVisit,
  resolveDefaultAgent,
  resolveDefaultWorkspace,
  resolveWorkspaceEntryPath,
} from './workspaceNavigation';

describe('workspace navigation helpers', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('builds user-facing workspace paths with stable query params', () => {
    expect(buildWorkspacePath()).toBe('/pudding/workspaces');
    expect(buildWorkspaceStudioPath()).toBe('/pudding/workspaces');
    expect(buildWorkspaceStudioPath({ workspaceId: 'default', agentId: 'agent/a' }))
      .toBe('/pudding/workspaces/default/agent%2Fa');
    expect(buildWorkspaceSettingsPath('default')).toBe('/workspace/default');
    expect(buildWorkspaceSettingsPath('team/a', 'channels')).toBe('/workspace/team%2Fa?tab=channels');
    expect(buildChatPath({ workspaceId: 'default', agentId: 'agent-1', sessionId: 'session 1' }))
      .toBe('/chat?workspaceId=default&agentId=agent-1&sessionId=session+1');
    expect(buildWorkspaceChatPath('default')).toBe('/chat?workspaceId=default');
  });

  it('updates chat route params while preserving unrelated query params', () => {
    expect(buildChatPathWithQuery(
      { workspaceId: 'default', agentId: 'agent-2', sessionId: 'session 2' },
      '?debug=1&panel=trace&workspaceId=old&agentId=old&sessionId=old&filter=a+b',
    )).toBe('/chat?debug=1&panel=trace&workspaceId=default&agentId=agent-2&sessionId=session+2&filter=a+b');

    expect(buildChatPathWithQuery(
      { sessionId: null },
      '?debug=1&workspaceId=default&agentId=agent-1&sessionId=session-1',
    )).toBe('/chat?debug=1&workspaceId=default&agentId=agent-1');
  });

  it('parses workspace route context and ignores blank values', () => {
    expect(parseWorkspaceRouteContext('?workspaceId=default&agentId=a&sessionId=s')).toEqual({
      workspaceId: 'default',
      agentId: 'a',
      sessionId: 's',
    });
    expect(parseWorkspaceRouteContext('?workspaceId=%20&agentId=&sessionId=')).toEqual({
      workspaceId: undefined,
      agentId: undefined,
      sessionId: undefined,
    });
  });

  it('resolves default workspace with requested, default, enabled, and fallback order', () => {
    const workspaces = [
      { workspaceId: 'frozen', isEnabled: true, isFrozen: true },
      { workspaceId: 'default', isEnabled: true, isFrozen: false },
      { workspaceId: 'other', isEnabled: true, isFrozen: false },
    ];

    expect(resolveDefaultWorkspace(workspaces, 'other')).toBe('other');
    expect(resolveDefaultWorkspace(workspaces, 'missing')).toBe('default');
    expect(resolveDefaultWorkspace([{ workspaceId: 'only-disabled', isEnabled: false, isFrozen: false }]))
      .toBe('only-disabled');
  });

  it('resolves default agent with requested and enabled priority', () => {
    const agents = [
      { agentId: 'disabled', isEnabled: false, isFrozen: false },
      { agentId: 'active', isEnabled: true, isFrozen: false },
    ];

    expect(resolveDefaultAgent(agents, 'active')).toBe('active');
    expect(resolveDefaultAgent(agents, 'disabled')).toBe('active');
    expect(resolveDefaultAgent([{ agentId: 'fallback', isEnabled: false, isFrozen: false }]))
      .toBe('fallback');
  });

  it('stores and clears the recent workspace visit safely', () => {
    rememberWorkspaceVisit({ workspaceId: 'default', agentId: 'agent-1', visitedAt: 123 });

    expect(JSON.parse(localStorage.getItem(RECENT_WORKSPACE_VISIT_KEY) ?? '{}')).toEqual({
      workspaceId: 'default',
      agentId: 'agent-1',
      visitedAt: 123,
    });
    expect(readRecentWorkspaceVisit()).toEqual({
      workspaceId: 'default',
      agentId: 'agent-1',
      visitedAt: 123,
    });

    clearRecentWorkspaceVisit();
    expect(readRecentWorkspaceVisit()).toBeUndefined();
  });

  it('chooses workspace-first entry by recent visit, single workspace, then list page', () => {
    expect(resolveWorkspaceEntryPath([
      { workspaceId: 'default', isEnabled: true, isFrozen: false },
      { workspaceId: 'team', isEnabled: true, isFrozen: false },
    ], { workspaceId: 'team', agentId: 'agent-1', visitedAt: 1 }))
      .toBe('/pudding/workspaces/team/agent-1');

    expect(resolveWorkspaceEntryPath([
      { workspaceId: 'default', isEnabled: true, isFrozen: false },
    ])).toBe('/pudding/workspaces/default');

    expect(resolveWorkspaceEntryPath([
      { workspaceId: 'default', isEnabled: true, isFrozen: false },
      { workspaceId: 'team', isEnabled: true, isFrozen: false },
    ])).toBe('/pudding/workspaces');
  });
});
