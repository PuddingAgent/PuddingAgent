import {
  type Dispatch,
  type SetStateAction,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  createWorkspaceAgent,
  listWorkspaceAgents,
  listWorkspaces,
  type WorkspaceAgentDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import type { ChatRouteSelection } from '../types/chatStateTypes';
import { logChatDiag } from '../utils/chatDiagnostics';
import {
  getAgentName,
  getChatRouteSelectionFromSearch,
  resolveInitialAgentId,
  resolveInitialWorkspaceId,
} from '../utils/chatStateUtils';

interface UseWorkspaceAgentSelectionOptions {
  routeSearch?: string;
  onError: (message: string | null) => void;
}

export interface UseWorkspaceAgentSelectionReturn {
  routeSelection: ChatRouteSelection;
  workspaces: WorkspaceWithPermDto[];
  setWorkspaces: Dispatch<SetStateAction<WorkspaceWithPermDto[]>>;
  workspaceId: string | undefined;
  setWorkspaceId: Dispatch<SetStateAction<string | undefined>>;
  workspaceLoading: boolean;
  agents: WorkspaceAgentDto[];
  setAgents: Dispatch<SetStateAction<WorkspaceAgentDto[]>>;
  agentId: string | undefined;
  setAgentId: Dispatch<SetStateAction<string | undefined>>;
  agentLoading: boolean;
  selectedAgent: WorkspaceAgentDto | undefined;
  wsOpts: { value: string; label: string; disabled: boolean }[];
  agOpts: { value: string; label: string; disabled: boolean }[];
  creatingSession: boolean;
  setCreatingSession: Dispatch<SetStateAction<boolean>>;
  suppressMainSessionEnsure: () => void;
  consumeMainSessionEnsureSuppression: () => boolean;
  resetMainSessionEnsureSuppression: (reason: string) => void;
}

export function useWorkspaceAgentSelection({
  routeSearch,
  onError,
}: UseWorkspaceAgentSelectionOptions): UseWorkspaceAgentSelectionReturn {
  const routeSelection = useMemo(
    () =>
      getChatRouteSelectionFromSearch(
        routeSearch ??
          (typeof window === 'undefined' ? '' : window.location.search),
      ),
    [routeSearch],
  );
  const [workspaces, setWorkspaces] = useState<WorkspaceWithPermDto[]>([]);
  const [workspaceId, setWorkspaceId] = useState<string>();
  const [workspaceLoading, setWorkspaceLoading] = useState(false);
  const [agents, setAgents] = useState<WorkspaceAgentDto[]>([]);
  const [agentId, setAgentId] = useState<string>();
  const [agentLoading, setAgentLoading] = useState(false);
  const [creatingSession, setCreatingSession] = useState(false);
  const suppressMainSessionEnsureRef = useRef(false);

  useEffect(() => {
    let active = true;
    void (async () => {
      setWorkspaceLoading(true);
      try {
        const items = await listWorkspaces();
        if (!active) return;
        setWorkspaces(items);
        const nextWorkspaceId = resolveInitialWorkspaceId(
          items,
          routeSelection.workspaceId,
        );
        setWorkspaceId(nextWorkspaceId);
        if (!nextWorkspaceId) onError('无可用工作空间');
      } catch (error: unknown) {
        if (active)
          onError(error instanceof Error ? error.message : '加载失败');
      } finally {
        if (active) setWorkspaceLoading(false);
      }
    })();
    return () => {
      active = false;
    };
  }, [onError, routeSelection.workspaceId]);

  useEffect(() => {
    let active = true;
    void (async () => {
      if (!workspaceId) {
        setAgents([]);
        setAgentId(undefined);
        setAgentLoading(false);
        return;
      }
      setAgentLoading(true);
      try {
        const items = await listWorkspaceAgents(workspaceId);
        if (!active) return;
        if (items.length === 0) {
          try {
            const created = await createWorkspaceAgent(workspaceId, {
              name: 'Pudding 助手',
              displayName: '布丁',
              sourceTemplateId: 'global:general-assistant',
            });
            if (!active) return;
            setAgents([created]);
            setAgentId(created.agentId);
          } catch {
            if (!active) return;
            setAgents([]);
            setAgentId(undefined);
          }
        } else {
          setAgents(items);
          setAgentId(resolveInitialAgentId(items, routeSelection.agentId));
        }
      } catch (error: unknown) {
        if (active)
          onError(error instanceof Error ? error.message : '加载Agent失败');
      } finally {
        if (active) setAgentLoading(false);
      }
    })();
    return () => {
      active = false;
    };
  }, [onError, workspaceId]);

  useEffect(() => {
    if (!routeSelection.agentId || agents.length === 0 || agentId) return;
    const nextAgentId = resolveInitialAgentId(agents, routeSelection.agentId);
    if (nextAgentId && nextAgentId !== agentId) setAgentId(nextAgentId);
  }, [agentId, agents, routeSelection.agentId]);

  const selectedAgent = useMemo(
    () => agents.find((agent) => agent.agentId === agentId),
    [agentId, agents],
  );
  const wsOpts = useMemo(
    () =>
      workspaces.map((workspace) => ({
        value: workspace.workspaceId,
        label: workspace.name || workspace.workspaceId,
        disabled: !workspace.isEnabled || workspace.isFrozen,
      })),
    [workspaces],
  );
  const agOpts = useMemo(
    () =>
      agents.map((agent) => ({
        value: agent.agentId,
        label: getAgentName(agent),
        disabled: !agent.isEnabled || agent.isFrozen,
      })),
    [agents],
  );

  const suppressMainSessionEnsure = useCallback(() => {
    suppressMainSessionEnsureRef.current = true;
  }, []);
  const consumeMainSessionEnsureSuppression = useCallback(() => {
    if (!suppressMainSessionEnsureRef.current) return false;
    suppressMainSessionEnsureRef.current = false;
    return true;
  }, []);
  const resetMainSessionEnsureSuppression = useCallback((reason: string) => {
    if (!suppressMainSessionEnsureRef.current) return;
    suppressMainSessionEnsureRef.current = false;
    logChatDiag('main.ensure.suppressionCleared', { reason });
  }, []);

  return {
    routeSelection,
    workspaces,
    setWorkspaces,
    workspaceId,
    setWorkspaceId,
    workspaceLoading,
    agents,
    setAgents,
    agentId,
    setAgentId,
    agentLoading,
    selectedAgent,
    wsOpts,
    agOpts,
    creatingSession,
    setCreatingSession,
    suppressMainSessionEnsure,
    consumeMainSessionEnsureSuppression,
    resetMainSessionEnsureSuppression,
  };
}
