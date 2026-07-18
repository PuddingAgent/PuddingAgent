import { history, useLocation, useParams } from '@umijs/max';
import {
  AimOutlined,
  HomeOutlined,
  InboxOutlined,
  MessageOutlined,
  DesktopOutlined,
} from '@ant-design/icons';
import { Alert, Button, Empty, Input, Modal, Select, Spin } from 'antd';
import React from 'react';
import { WorkspaceNavigationHeader } from '@/components';
import {
  awaitConversationTurn,
  ensureMainSession,
  listWorkspaceAgents,
  listSessions,
  listWorkspaces,
  submitConversationTurn,
  subscribeWorkspaceNotifications,
  type SessionRecord,
  type WorkspaceNotification,
  type WorkspaceAgentDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import WorkspaceStudioView from '../chat/components/WorkspaceStudioView';
import {
  buildWorkspaceStudioAgents,
  buildWorkspaceStudioSessionActivities,
  normalizeWorkspaceStudioSceneEvent,
  shouldRefreshWorkspaceStudioSessionsForNotification,
  summarizeWorkspaceStudioAgents,
  type WorkspaceStudioAgentCommand,
  type WorkspaceStudioObjectId,
  type WorkspaceStudioSceneEvent,
  type WorkspaceStudioSceneStatus,
} from '../chat/components/workspaceStudio';
import type { ChatTurn, SubAgentCardMap } from '../chat/types';
import {
  buildWorkspaceChatPath,
  buildWorkspacePath,
  buildWorkspaceSettingsPath,
  buildWorkspaceStudioPath,
  parseWorkspaceRouteContext,
  rememberWorkspaceVisit,
  resolveDefaultAgent,
  resolveDefaultWorkspace,
} from '@/utils/workspaceNavigation';

const { TextArea } = Input;

const emptyTurns: ChatTurn[] = [];
const emptySubAgentCards: SubAgentCardMap = {};

const workspaceStudioModalStyles: NonNullable<React.ComponentProps<typeof Modal>['styles']> = {
  content: {
    padding: 0,
    background: 'transparent',
    boxShadow: 'none',
  },
  body: {
    padding: 0,
  },
};

type StudioChatMessage = {
  id: string;
  role: 'user' | 'agent' | 'system';
  text: string;
};

const studioObjectLabels: Record<WorkspaceStudioObjectId, string> = {
  workbench: '工作台电脑',
  taskBoard: '任务板',
  statusBoard: '工作室状态板',
  activityBoard: '最近活动板',
  mailbox: '门口邮箱',
  door: '工作室入口',
  restArea: '休息区',
  meetingTable: '中部会议桌',
  gameConsole: '娱乐终端',
  sleepArea: '睡眠区',
  bookshelf: '资料书架',
  plant: '绿植',
};

function getStudioObjectDescription(objectId: WorkspaceStudioObjectId): string {
  switch (objectId) {
    case 'workbench':
      return '当前任务、对话与本地工作入口';
    case 'taskBoard':
      return '展示正在运行的任务和最近的会话反馈';
    case 'statusBoard':
      return '墙上的 Agent 状态总览，点击后查看工作室状态分布';
    case 'activityBoard':
      return '墙上的最近活动入口，点击后查看最新会话反馈';
    case 'mailbox':
      return 'Email、飞书与 Webhook 等外部消息入口';
    case 'door':
      return '返回工作空间列表与切换入口';
    case 'restArea':
      return '休息、等待和冷却中的 Agent 会停留在这里';
    case 'meetingTable':
      return '多 Agent 协作、交接和编队讨论的画面锚点';
    case 'gameConsole':
      return '娱乐、探索和低优先级创意任务的状态反馈点';
    case 'sleepArea':
      return '暂停、冻结或不可用的 Agent 会停留在这里';
    case 'bookshelf':
      return '知识库、提示词和团队规范的视觉入口';
    case 'plant':
      return '工作室氛围对象，后续可承载健康度或心情反馈';
    default:
      return '';
  }
}

function getStudioObjectBadges(
  objectId: WorkspaceStudioObjectId,
  status: WorkspaceStudioSceneStatus,
): string[] {
  switch (objectId) {
    case 'workbench':
      return [status.activeTaskCount > 0 ? `运行中 ${status.activeTaskCount}` : '本地工作台'];
    case 'taskBoard':
      return [status.activeTaskCount > 0 ? `任务 ${status.activeTaskCount}` : '暂无运行任务'];
    case 'statusBoard':
      return [
        `工作 ${status.activeTaskCount}`,
        `休息 ${status.restingAgentCount}`,
        `娱乐 ${status.playingAgentCount}`,
        `睡眠 ${status.sleepingAgentCount}`,
      ];
    case 'activityBoard':
      return [status.recentActivityCount > 0 ? `最近活动 ${status.recentActivityCount}` : '暂无活动'];
    case 'mailbox':
      return [status.recentActivityCount > 0 ? `最近活动 ${status.recentActivityCount}` : '新消息 0'];
    case 'door':
      return ['空间入口'];
    case 'restArea':
      return [`休息 ${status.restingAgentCount}`];
    case 'meetingTable':
      return [status.activeTaskCount > 1 ? '协作中' : '等待协作'];
    case 'gameConsole':
      return [status.playingAgentCount > 0 ? `娱乐 ${status.playingAgentCount}` : '空闲'];
    case 'sleepArea':
      return [`睡眠 ${status.sleepingAgentCount}`];
    case 'bookshelf':
      return ['资料入口'];
    case 'plant':
      return [status.activeTaskCount > 0 ? '工作室活跃' : '工作室安静'];
    default:
      return [];
  }
}

const pageStyles = {
  shell: {
    minHeight: '100vh',
    background: 'var(--warm-beige)',
    color: 'var(--text-primary)',
    display: 'flex',
    flexDirection: 'column',
  },
  body: {
    position: 'relative',
    width: '100%',
    minHeight: 'calc(100vh - 49px)',
    margin: 0,
    padding: '12px 18px 18px',
    display: 'flex',
    flexDirection: 'column',
    gap: 0,
    overflow: 'hidden',
  },
  studioLayout: {
    position: 'relative',
    display: 'block',
    minHeight: 'calc(100vh - 79px)',
    height: 'calc(100vh - 79px)',
  },
  studioStage: {
    height: '100%',
    minWidth: 0,
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'center',
  },
  focusCopy: {
    display: 'flex',
    flexDirection: 'column',
    gap: 4,
    minWidth: 0,
  },
  focusTitle: {
    fontSize: 14,
    fontWeight: 600,
    color: 'var(--text-primary)',
  },
  focusMeta: {
    fontSize: 12,
    color: 'var(--text-secondary)',
  },
  focusDetails: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    flexWrap: 'wrap',
  },
  focusBadge: {
    display: 'inline-flex',
    alignItems: 'center',
    maxWidth: 'min(360px, 100%)',
    minHeight: 22,
    padding: '2px 8px',
    borderRadius: 3,
    border: '2px solid rgba(79, 52, 37, 0.34)',
    background: 'rgba(255, 242, 198, 0.76)',
    fontSize: 12,
    color: 'var(--text-secondary)',
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  statGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: 8,
  },
  statTile: {
    minHeight: 54,
    border: '2px solid rgba(79, 52, 37, 0.22)',
    borderRadius: 4,
    padding: '8px 10px',
    background: 'rgba(255, 248, 232, 0.62)',
  },
  statValue: {
    display: 'block',
    fontSize: 18,
    lineHeight: '22px',
    fontWeight: 700,
    color: 'var(--text-primary)',
  },
  statLabel: {
    display: 'block',
    marginTop: 3,
    fontSize: 12,
    color: 'var(--text-secondary)',
  },
  activityList: {
    display: 'flex',
    flexDirection: 'column',
    gap: 8,
    margin: 0,
    padding: 0,
    listStyle: 'none',
  },
  activityItem: {
    display: 'flex',
    flexDirection: 'column',
    gap: 3,
    minHeight: 44,
    padding: '8px 9px',
    borderRadius: 4,
    border: '2px solid rgba(79, 52, 37, 0.16)',
    background: 'rgba(255, 248, 232, 0.56)',
  },
  activityTitle: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontSize: 12,
    fontWeight: 600,
    color: 'var(--text-primary)',
  },
  activityMeta: {
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    fontSize: 11,
    color: 'var(--text-secondary)',
  },
  emptyGameText: {
    minHeight: 44,
    padding: '10px 12px',
    border: '2px solid rgba(79, 52, 37, 0.16)',
    borderRadius: 4,
    background: 'rgba(255, 248, 232, 0.48)',
    color: 'var(--text-secondary)',
    fontSize: 13,
  },
  center: {
    minHeight: 420,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
  },
  gamePanel: {
    border: '3px solid #4f3425',
    borderRadius: 6,
    background: 'linear-gradient(180deg, #fff1c8 0%, #e8bf82 100%)',
    boxShadow: '0 5px 0 #2f2118, inset 0 0 0 2px rgba(255,255,255,.28)',
  },
  gamePanelHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: 10,
    marginBottom: 12,
  },
  gamePanelAvatar: {
    width: 42,
    height: 42,
    border: '2px solid #4f3425',
    borderRadius: 4,
    background: '#f9dfaa',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: 16,
    fontWeight: 800,
    color: '#3a251a',
    overflow: 'hidden',
  },
  gamePanelMessages: {
    height: 260,
    overflowY: 'auto',
    display: 'flex',
    flexDirection: 'column',
    gap: 8,
    padding: 10,
    border: '2px solid rgba(79,52,37,.5)',
    borderRadius: 4,
    background: 'rgba(255, 248, 232, .58)',
  },
  gameMessage: {
    maxWidth: '82%',
    padding: '7px 9px',
    border: '2px solid rgba(79,52,37,.38)',
    borderRadius: 4,
    background: 'rgba(255,255,255,.56)',
    color: '#3a251a',
    fontSize: 13,
    lineHeight: '19px',
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  gameMessageUser: {
    alignSelf: 'flex-end',
    background: 'rgba(124,58,237,.14)',
  },
  gameInputRow: {
    display: 'flex',
    gap: 8,
    marginTop: 10,
    alignItems: 'flex-end',
  },
  agentPanelBody: {
    marginTop: 12,
    padding: 12,
    border: '2px solid rgba(79,52,37,.32)',
    borderRadius: 4,
    background: 'rgba(255, 248, 232, .58)',
    color: '#3a251a',
  },
  agentPanelText: {
    margin: 0,
    color: '#4b3424',
    fontSize: 13,
    lineHeight: '20px',
  },
  agentPanelActions: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: 8,
    marginTop: 12,
  },
  objectPanelActions: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: 8,
    marginTop: 12,
  },
  objectPanelDescription: {
    margin: '6px 0 0',
    color: '#5b3d2b',
    fontSize: 13,
    lineHeight: '20px',
  },
} satisfies Record<string, React.CSSProperties>;

function formatSessionTime(value?: string): string {
  if (!value) return '刚刚';
  const time = new Date(value).getTime();
  if (Number.isNaN(time)) return '刚刚';
  const diffMinutes = Math.max(0, Math.floor((Date.now() - time) / 60_000));
  if (diffMinutes < 1) return '刚刚';
  if (diffMinutes < 60) return `${diffMinutes} 分钟前`;
  const diffHours = Math.floor(diffMinutes / 60);
  if (diffHours < 24) return `${diffHours} 小时前`;
  return `${Math.floor(diffHours / 24)} 天前`;
}

const WorkspaceStudioPage: React.FC = () => {
  const location = useLocation();
  const params = useParams<{ workspaceId?: string; agentId?: string }>();
  const [workspaces, setWorkspaces] = React.useState<WorkspaceWithPermDto[]>([]);
  const [workspaceId, setWorkspaceId] = React.useState<string>();
  const [agents, setAgents] = React.useState<WorkspaceAgentDto[]>([]);
  const [selectedAgentId, setSelectedAgentId] = React.useState<string>();
  const [workspaceLoading, setWorkspaceLoading] = React.useState(false);
  const [agentLoading, setAgentLoading] = React.useState(false);
  const [sessions, setSessions] = React.useState<SessionRecord[]>([]);
  const [sceneEvents, setSceneEvents] = React.useState<WorkspaceStudioSceneEvent[]>([]);
  const [selectedStudioObjectId, setSelectedStudioObjectId] = React.useState<WorkspaceStudioObjectId>();
  const [objectPanelOpen, setObjectPanelOpen] = React.useState(false);
  const [chatPanelAgentId, setChatPanelAgentId] = React.useState<string>();
  const [chatPanelInput, setChatPanelInput] = React.useState('');
  const [chatPanelSending, setChatPanelSending] = React.useState(false);
  const [chatPanelSessionIds, setChatPanelSessionIds] = React.useState<Record<string, string>>({});
  const [chatPanelMessages, setChatPanelMessages] = React.useState<Record<string, StudioChatMessage[]>>({});
  const [error, setError] = React.useState<string | null>(null);
  const routeContext = React.useMemo(() => {
    const queryContext = parseWorkspaceRouteContext(location.search);
    return {
      ...queryContext,
      workspaceId: params.workspaceId || queryContext.workspaceId,
      agentId: params.agentId || queryContext.agentId,
    };
  }, [location.search, params.agentId, params.workspaceId]);
  const requestedWorkspaceId = routeContext.workspaceId;
  const requestedAgentId = routeContext.agentId;

  React.useEffect(() => {
    let active = true;
    (async () => {
      setWorkspaceLoading(true);
      setError(null);
      try {
        const items = await listWorkspaces();
        if (!active) return;
        setWorkspaces(items);
        setWorkspaceId((current) => {
          if (requestedWorkspaceId && items.some((item) => item.workspaceId === requestedWorkspaceId)) {
            return requestedWorkspaceId;
          }
          if (current && items.some((item) => item.workspaceId === current)) return current;
          return resolveDefaultWorkspace(items);
        });
      } catch (e: unknown) {
        if (active) setError(e instanceof Error ? e.message : '加载工作空间失败');
      } finally {
        if (active) setWorkspaceLoading(false);
      }
    })();
    return () => { active = false; };
  }, [requestedWorkspaceId]);

  React.useEffect(() => {
    let active = true;
    (async () => {
      if (!workspaceId) {
        setAgents([]);
        setSelectedAgentId(undefined);
        return;
      }
      setAgentLoading(true);
      setError(null);
      try {
        const items = await listWorkspaceAgents(workspaceId);
        if (!active) return;
        setAgents(items);
        setSelectedAgentId((current) => {
          if (requestedAgentId && items.some((item) => item.agentId === requestedAgentId)) return requestedAgentId;
          if (current && items.some((item) => item.agentId === current)) return current;
          return resolveDefaultAgent(items);
        });
      } catch (e: unknown) {
        if (active) setError(e instanceof Error ? e.message : '加载 Agent 失败');
      } finally {
        if (active) setAgentLoading(false);
      }
    })();
    return () => { active = false; };
  }, [requestedAgentId, workspaceId]);

  React.useEffect(() => {
    if (!workspaceId) return;
    rememberWorkspaceVisit({ workspaceId, agentId: selectedAgentId });
  }, [selectedAgentId, workspaceId]);

  React.useEffect(() => {
    let active = true;
    let timer: number | undefined;
    const controller = new AbortController();

    const loadSessions = async () => {
      if (!workspaceId) {
        setSessions([]);
        setSceneEvents([]);
        return;
      }
      try {
        const items = await listSessions(workspaceId);
        if (active) setSessions(items);
      } catch {
        if (active) setSessions([]);
      }
    };

    void loadSessions();
    timer = window.setInterval(() => { void loadSessions(); }, 10_000);
    if (workspaceId) {
      subscribeWorkspaceNotifications(workspaceId, (event: WorkspaceNotification) => {
        if (!active || controller.signal.aborted) return;
        if (shouldRefreshWorkspaceStudioSessionsForNotification(event, workspaceId)) {
          void loadSessions();
        }
        const sceneEvent = normalizeWorkspaceStudioSceneEvent(event);
        if (sceneEvent) {
          setSceneEvents((current) => [sceneEvent, ...current].slice(0, 12));
        }
      }, controller.signal);
    }

    return () => {
      active = false;
      controller.abort();
      if (timer) window.clearInterval(timer);
    };
  }, [workspaceId]);

  const handleWorkspaceChange = React.useCallback((value: string) => {
    setWorkspaceId(value);
    setSelectedAgentId(undefined);
    setSelectedStudioObjectId(undefined);
    setObjectPanelOpen(false);
    setChatPanelAgentId(undefined);
    history.replace(buildWorkspaceStudioPath({ workspaceId: value }));
  }, []);

  const handleAgentChange = React.useCallback((agentId: string | undefined) => {
    setSelectedAgentId(agentId);
    setSelectedStudioObjectId(undefined);
    setObjectPanelOpen(false);
    setChatPanelAgentId(agentId);
    setChatPanelInput('');
    if (workspaceId) {
      history.replace(buildWorkspaceStudioPath({ workspaceId, agentId }));
    }
  }, [workspaceId]);

  const handleStudioObjectSelect = React.useCallback((objectId: WorkspaceStudioObjectId) => {
    setSelectedStudioObjectId(objectId);
    setObjectPanelOpen(true);
    setChatPanelAgentId(undefined);
  }, []);

  const handleAgentCommand = React.useCallback((agentId: string, command: WorkspaceStudioAgentCommand) => {
    if (command === 'chat') {
      setSelectedAgentId(agentId);
      setSelectedStudioObjectId(undefined);
      setObjectPanelOpen(false);
      setChatPanelAgentId(agentId);
      setChatPanelInput('');
      return;
    }
    setSelectedAgentId(agentId);
    setSelectedStudioObjectId(undefined);
    setObjectPanelOpen(false);
    if (workspaceId) {
      history.replace(buildWorkspaceStudioPath({ workspaceId, agentId }));
    }
  }, [workspaceId]);

  const selectedWorkspace = workspaces.find((item) => item.workspaceId === workspaceId);
  const selectedAgent = agents.find((item) => item.agentId === selectedAgentId);
  const agentActivities = React.useMemo(
    () => buildWorkspaceStudioSessionActivities(sessions),
    [sessions],
  );
  const studioAgents = React.useMemo(
    () => buildWorkspaceStudioAgents({
      agents,
      selectedAgentId,
      turns: emptyTurns,
      loading: false,
      subAgentCards: emptySubAgentCards,
      agentActivities,
    }),
    [agentActivities, agents, selectedAgentId],
  );
  const selectedStudioAgent = studioAgents.find((item) => item.selected) ?? studioAgents[0];
  const stateSummary = React.useMemo(() => summarizeWorkspaceStudioAgents(studioAgents), [studioAgents]);
  const sceneStatus = React.useMemo<WorkspaceStudioSceneStatus>(() => ({
    activeTaskCount: studioAgents.filter((item) => item.state === 'working').length,
    recentActivityCount: sessions.length,
    restingAgentCount: studioAgents.filter((item) => item.state === 'resting').length,
    playingAgentCount: studioAgents.filter((item) => item.state === 'playing').length,
    sleepingAgentCount: studioAgents.filter((item) => item.state === 'sleeping').length,
  }), [sessions.length, studioAgents]);
  const recentActivities = React.useMemo(
    () => sessions
      .slice()
      .sort((a, b) => new Date(b.lastActiveAt).getTime() - new Date(a.lastActiveAt).getTime())
      .slice(0, 4),
    [sessions],
  );
  const workspaceLabel = selectedWorkspace?.name || selectedWorkspace?.workspaceId || '工作空间';
  const agentLabel = selectedAgent?.displayName || selectedAgent?.name;
  const selectedObjectBadges = selectedStudioObjectId
    ? getStudioObjectBadges(selectedStudioObjectId, sceneStatus)
    : [];
  const chatPanelAgent = agents.find((item) => item.agentId === chatPanelAgentId);
  const chatPanelStudioAgent = studioAgents.find((item) => item.agentId === chatPanelAgentId);
  const chatPanelAgentName = chatPanelStudioAgent?.name
    || chatPanelAgent?.displayName
    || chatPanelAgent?.name
    || 'Agent';
  const currentChatMessages = chatPanelAgentId ? chatPanelMessages[chatPanelAgentId] ?? [] : [];
  const selectedObjectLabel = selectedStudioObjectId ? studioObjectLabels[selectedStudioObjectId] : '';
  const selectedObjectDescription = selectedStudioObjectId ? getStudioObjectDescription(selectedStudioObjectId) : '';
  const selectedObjectIsStatusBoard = selectedStudioObjectId === 'statusBoard';
  const selectedObjectIsActivityBoard = selectedStudioObjectId === 'activityBoard';

  const handleOpenWorkspaceAdmin = React.useCallback(() => {
    if (!workspaceId) return;
    history.push(buildWorkspaceSettingsPath(workspaceId));
  }, [workspaceId]);

  const handleOpenFullChat = React.useCallback(() => {
    history.push(buildWorkspaceChatPath(workspaceId));
  }, [workspaceId]);

  const handleSendChatPanelMessage = React.useCallback(async () => {
    const messageText = chatPanelInput.trim();
    if (!workspaceId || !chatPanelAgentId || !messageText || chatPanelSending) return;

    const userMessage: StudioChatMessage = {
      id: `${Date.now()}:user`,
      role: 'user',
      text: messageText,
    };
    setChatPanelMessages((current) => ({
      ...current,
      [chatPanelAgentId]: [...(current[chatPanelAgentId] ?? []), userMessage],
    }));
    setChatPanelInput('');
    setChatPanelSending(true);

    try {
      let conversationId = chatPanelSessionIds[chatPanelAgentId];
      if (!conversationId) {
        const session = await ensureMainSession({
          workspaceId,
          principalKind: 'agent',
          principalId: chatPanelAgentId,
          agentTemplateId: chatPanelAgent?.sourceTemplateId || `global:${chatPanelAgentId}`,
          title: chatPanelAgent?.displayName || chatPanelAgent?.name || chatPanelAgentId,
        });
        conversationId = session.sessionId;
        setChatPanelSessionIds((current) => ({
          ...current,
          [chatPanelAgentId]: conversationId,
        }));
      }

      const acceptance = await submitConversationTurn(
        workspaceId,
        conversationId,
        {
          clientRequestId: crypto.randomUUID(),
          clientMessageId: crypto.randomUUID(),
          recipients: { type: 'agent', agentIds: [chatPanelAgentId] },
          content: [{ type: 'text', text: messageText }],
        },
      );
      const result = await awaitConversationTurn(
        conversationId,
        acceptance.turnIds[0],
        acceptance.acceptedSequence,
      );
      const responseMessage: StudioChatMessage = {
        id: acceptance.messageId || `${Date.now()}:agent`,
        role: 'agent',
        text: result.reply || '已收到。',
      };
      setChatPanelMessages((current) => ({
        ...current,
        [chatPanelAgentId]: [...(current[chatPanelAgentId] ?? []), responseMessage],
      }));
    } catch (e: unknown) {
      const errorMessage: StudioChatMessage = {
        id: `${Date.now()}:error`,
        role: 'system',
        text: e instanceof Error ? e.message : '发送失败，请稍后再试。',
      };
      setChatPanelMessages((current) => ({
        ...current,
        [chatPanelAgentId]: [...(current[chatPanelAgentId] ?? []), errorMessage],
      }));
    } finally {
      setChatPanelSending(false);
    }
  }, [
    chatPanelAgentId,
    chatPanelAgent,
    chatPanelInput,
    chatPanelSending,
    chatPanelSessionIds,
    workspaceId,
  ]);

  return (
    <div style={pageStyles.shell}>
      <WorkspaceNavigationHeader
        crumbs={[
          {
            label: workspaceLabel,
            title: workspaceLabel,
            path: buildWorkspaceStudioPath({ workspaceId }),
            disabled: !workspaceId,
          },
          ...(agentLabel ? [{ label: agentLabel, title: agentLabel }] : []),
        ]}
        controls={
          <Select
            style={{ minWidth: 220 }}
            size="small"
            value={workspaceId}
            loading={workspaceLoading}
            placeholder="选择工作空间"
            options={workspaces.map((item) => ({
              value: item.workspaceId,
              label: item.name || item.workspaceId,
            }))}
            onChange={handleWorkspaceChange}
          />
        }
        primaryAction={
          <Button
            type="primary"
            size="small"
            disabled={!selectedStudioAgent?.canChat}
            onClick={() => {
              if (selectedStudioAgent) handleAgentCommand(selectedStudioAgent.agentId, 'chat');
            }}
          >
            进入对话
          </Button>
        }
      />

      <main className="workspace-studio-page-body" style={pageStyles.body}>
        {error && <Alert type="error" message={error} closable onClose={() => setError(null)} />}

        {workspaceLoading || agentLoading ? (
          <div style={pageStyles.center}>
            <Spin />
          </div>
        ) : selectedWorkspace ? (
          <div className="workspace-studio-layout" style={pageStyles.studioLayout}>
            <div style={pageStyles.studioStage}>
              <WorkspaceStudioView
                agents={agents}
                selectedAgentId={selectedAgentId}
                turns={emptyTurns}
                loading={false}
                subAgentCards={emptySubAgentCards}
                agentActivities={agentActivities}
                sceneStatus={sceneStatus}
                sceneEvents={sceneEvents}
                onAgentChange={handleAgentChange}
                onAgentCommand={handleAgentCommand}
                onObjectSelect={handleStudioObjectSelect}
                selectedObjectId={selectedStudioObjectId}
                variant="page"
              />
            </div>
          </div>
        ) : (
          <div style={pageStyles.center}>
            <Empty description="暂无工作空间" />
          </div>
        )}
      </main>
      <Modal
        open={Boolean(chatPanelAgentId)}
        title={null}
        footer={null}
        width={560}
        styles={workspaceStudioModalStyles}
        destroyOnClose
        onCancel={() => {
          setChatPanelAgentId(undefined);
          setChatPanelInput('');
        }}
      >
        <div style={{ ...pageStyles.gamePanel, padding: 16 }}>
          <div style={pageStyles.gamePanelHeader}>
            <span style={pageStyles.gamePanelAvatar}>
              {chatPanelAgent?.avatarUrl ? <img src={chatPanelAgent.avatarUrl} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} /> : chatPanelAgentName.charAt(0)}
            </span>
            <div style={pageStyles.focusCopy}>
              <span style={pageStyles.focusTitle}>和 {chatPanelAgentName} 对话</span>
              <span style={pageStyles.focusMeta}>
                {chatPanelStudioAgent ? `${chatPanelStudioAgent.stateLabel} · ${chatPanelStudioAgent.activity}` : '工作室内对话面板'}
              </span>
            </div>
          </div>
          <div style={pageStyles.gamePanelMessages} aria-label={`${chatPanelAgentName} 对话记录`}>
            {currentChatMessages.length ? currentChatMessages.map((item) => (
              <div
                key={item.id}
                style={{
                  ...pageStyles.gameMessage,
                  ...(item.role === 'user' ? pageStyles.gameMessageUser : {}),
                  ...(item.role === 'system' ? { alignSelf: 'center', background: 'rgba(255,255,255,.38)' } : {}),
                }}
              >
                {item.text}
              </div>
            )) : (
              <div style={{ ...pageStyles.gameMessage, alignSelf: 'center' }}>
                点击小人后可以直接在工作室里和 {chatPanelAgentName} 对话。
              </div>
            )}
          </div>
          <div style={pageStyles.gameInputRow}>
            <TextArea
              value={chatPanelInput}
              autoSize={{ minRows: 2, maxRows: 4 }}
              placeholder={`向 ${chatPanelAgentName} 发送指令`}
              disabled={chatPanelSending || chatPanelStudioAgent?.canChat === false}
              onChange={(event) => setChatPanelInput(event.target.value)}
              onPressEnter={(event) => {
                if (event.shiftKey) return;
                event.preventDefault();
                void handleSendChatPanelMessage();
              }}
            />
            <Button
              type="primary"
              loading={chatPanelSending}
              disabled={!chatPanelInput.trim() || chatPanelStudioAgent?.canChat === false}
              onClick={() => { void handleSendChatPanelMessage(); }}
            >
              发送
            </Button>
          </div>
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 10 }}>
            <Button size="small" onClick={handleOpenFullChat}>
              完整对话
            </Button>
          </div>
        </div>
      </Modal>
      <Modal
        open={objectPanelOpen && Boolean(selectedStudioObjectId)}
        title={null}
        footer={null}
        width={520}
        styles={workspaceStudioModalStyles}
        destroyOnClose
        onCancel={() => setObjectPanelOpen(false)}
      >
        <div style={{ ...pageStyles.gamePanel, padding: 16 }}>
          <div style={pageStyles.gamePanelHeader}>
            <span style={pageStyles.gamePanelAvatar}>{selectedObjectLabel.charAt(0)}</span>
            <div style={pageStyles.focusCopy}>
              <span style={pageStyles.focusTitle}>{selectedObjectLabel}</span>
              <span style={pageStyles.focusMeta}>工作室物品面板</span>
            </div>
          </div>
          <p style={pageStyles.objectPanelDescription}>{selectedObjectDescription}</p>
          <div style={pageStyles.focusDetails}>
            {selectedObjectBadges.map((badge) => (
              <span key={badge} style={pageStyles.focusBadge}>{badge}</span>
            ))}
          </div>
          {selectedObjectIsStatusBoard ? (
            <div style={pageStyles.statGrid}>
              {stateSummary.length ? stateSummary.map((summary) => (
                <div key={summary.state} style={pageStyles.statTile}>
                  <span style={pageStyles.statValue}>{summary.count}</span>
                  <span style={pageStyles.statLabel}>{summary.label}</span>
                </div>
              )) : (
                <div style={pageStyles.statTile}>
                  <span style={pageStyles.statValue}>0</span>
                  <span style={pageStyles.statLabel}>暂无 Agent</span>
                </div>
              )}
            </div>
          ) : null}
          {selectedObjectIsActivityBoard ? (
            recentActivities.length ? (
              <ul style={pageStyles.activityList}>
                {recentActivities.map((session) => (
                  <li key={session.sessionId} style={pageStyles.activityItem}>
                    <span style={pageStyles.activityTitle}>{session.title?.trim() || '未命名会话'}</span>
                    <span style={pageStyles.activityMeta}>
                      {session.status} · {formatSessionTime(session.lastActiveAt)}
                    </span>
                  </li>
                ))}
              </ul>
            ) : (
              <div style={pageStyles.emptyGameText}>暂无活动</div>
            )
          ) : null}
          <div style={pageStyles.objectPanelActions}>
            {selectedObjectIsStatusBoard || selectedObjectIsActivityBoard ? (
              <Button type="primary" onClick={() => setObjectPanelOpen(false)}>
                收起面板
              </Button>
            ) : selectedStudioObjectId === 'door' ? (
              <Button type="primary" icon={<HomeOutlined />} onClick={() => history.push(buildWorkspacePath())}>
                工作空间列表
              </Button>
            ) : (
              <Button type="primary" icon={<DesktopOutlined />} disabled={!workspaceId} onClick={handleOpenWorkspaceAdmin}>
                工作空间后台
              </Button>
            )}
            {(selectedStudioObjectId === 'workbench' || selectedStudioObjectId === 'taskBoard' || selectedStudioObjectId === 'meetingTable') && (
              <Button icon={<MessageOutlined />} onClick={() => handleOpenFullChat()}>
                完整对话
              </Button>
            )}
            {selectedStudioObjectId === 'mailbox' && (
              <Button icon={<InboxOutlined />} onClick={handleOpenWorkspaceAdmin}>
                消息入口管理
              </Button>
            )}
            {(selectedStudioObjectId === 'restArea' || selectedStudioObjectId === 'sleepArea' || selectedStudioObjectId === 'gameConsole') && (
              <Button icon={<AimOutlined />} onClick={() => history.push('/global-agent-template')}>
                全局模板
              </Button>
            )}
            {(selectedStudioObjectId === 'plant' || selectedStudioObjectId === 'bookshelf') && (
              <Button icon={<AimOutlined />} onClick={() => history.push('/global-agent-template')}>
                全局配置
              </Button>
            )}
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default WorkspaceStudioPage;
