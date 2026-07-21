import { act, renderHook, waitFor } from '@testing-library/react';
import {
  createWorkspaceAgent,
  listWorkspaceAgents,
  listWorkspaces,
} from '@/services/platform/api';
import { useWorkspaceAgentSelection } from './useWorkspaceAgentSelection';

jest.mock('@/services/platform/api', () => ({
  createWorkspaceAgent: jest.fn(),
  listWorkspaceAgents: jest.fn(),
  listWorkspaces: jest.fn(),
}));

jest.mock('../utils/chatDiagnostics', () => ({
  logChatDiag: jest.fn(),
}));

const workspaces = [
  {
    id: 1,
    workspaceId: 'default',
    slug: 'default',
    teamId: 'team-1',
    teamName: '默认团队',
    name: '默认工作空间',
    isEnabled: true,
    isFrozen: false,
    memberCount: 1,
    teamAccessPolicy: 'Write',
    companyAccessPolicy: 'None',
    createdAt: '2026-07-21T00:00:00Z',
  },
  {
    id: 2,
    workspaceId: 'other',
    slug: 'other',
    teamId: 'team-1',
    teamName: '默认团队',
    name: 'Other',
    isEnabled: true,
    isFrozen: false,
    memberCount: 1,
    teamAccessPolicy: 'Write',
    companyAccessPolicy: 'None',
    createdAt: '2026-07-21T00:00:00Z',
  },
];

const agents = [
  {
    agentId: 'agent-a',
    name: 'Agent A',
    displayName: 'Agent A',
    sourceTemplateId: 'global:a',
    isEnabled: true,
    isFrozen: false,
    createdAt: '2026-07-21T00:00:00Z',
    updatedAt: '2026-07-21T00:00:00Z',
  },
  {
    agentId: 'agent-b',
    name: 'Agent B',
    displayName: 'Agent B',
    sourceTemplateId: 'global:b',
    isEnabled: true,
    isFrozen: false,
    createdAt: '2026-07-21T00:00:00Z',
    updatedAt: '2026-07-21T00:00:00Z',
  },
];

describe('useWorkspaceAgentSelection', () => {
  const onError = jest.fn();

  beforeEach(() => {
    jest.clearAllMocks();
    (listWorkspaces as jest.Mock).mockResolvedValue(workspaces);
    (listWorkspaceAgents as jest.Mock).mockResolvedValue(agents);
  });

  it('loads the route-selected workspace and agent with stable options', async () => {
    const { result } = renderHook(() =>
      useWorkspaceAgentSelection({
        routeSearch: '?workspaceId=other&agentId=agent-b',
        onError,
      }),
    );

    await waitFor(() => expect(result.current.agentId).toBe('agent-b'));

    expect(result.current.workspaceId).toBe('other');
    expect(listWorkspaceAgents).toHaveBeenCalledWith('other');
    expect(result.current.selectedAgent?.displayName).toBe('Agent B');
    expect(result.current.wsOpts).toEqual([
      { value: 'default', label: '默认工作空间', disabled: false },
      { value: 'other', label: 'Other', disabled: false },
    ]);
    expect(result.current.agOpts[1]).toEqual({
      value: 'agent-b',
      label: 'Agent B',
      disabled: false,
    });
    expect(result.current.workspaceLoading).toBe(false);
    expect(result.current.agentLoading).toBe(false);
  });

  it('creates the default agent when a workspace has none', async () => {
    const created = { ...agents[0], agentId: 'created-agent' };
    (listWorkspaceAgents as jest.Mock).mockResolvedValue([]);
    (createWorkspaceAgent as jest.Mock).mockResolvedValue(created);

    const { result } = renderHook(() =>
      useWorkspaceAgentSelection({ routeSearch: '', onError }),
    );

    await waitFor(() => expect(result.current.agentId).toBe('created-agent'));

    expect(createWorkspaceAgent).toHaveBeenCalledWith('default', {
      name: 'Pudding 助手',
      displayName: '布丁',
      sourceTemplateId: 'global:general-assistant',
    });
    expect(result.current.agents).toEqual([created]);
  });

  it('encapsulates one-shot main-session ensure suppression', async () => {
    const { result } = renderHook(() =>
      useWorkspaceAgentSelection({ routeSearch: '', onError }),
    );
    await waitFor(() => expect(result.current.agentId).toBe('agent-a'));

    act(() => result.current.suppressMainSessionEnsure());
    expect(result.current.consumeMainSessionEnsureSuppression()).toBe(true);
    expect(result.current.consumeMainSessionEnsureSuppression()).toBe(false);

    act(() => result.current.suppressMainSessionEnsure());
    act(() => result.current.resetMainSessionEnsureSuppression('test'));
    expect(result.current.consumeMainSessionEnsureSuppression()).toBe(false);
  });
});
