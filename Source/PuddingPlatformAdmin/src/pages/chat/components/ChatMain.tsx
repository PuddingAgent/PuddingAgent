// ── ChatMain：右侧主聊天区（Header + MessageList + InputArea）─
import {
  MenuUnfoldOutlined,
  RobotOutlined,
  SettingOutlined,
  BugOutlined,
  SmileOutlined,
  BarChartOutlined,
  ThunderboltOutlined,
  StarOutlined,
  FireOutlined,
  BulbOutlined,
  RocketOutlined,
  HeartOutlined,
} from '@ant-design/icons';
import { history } from '@umijs/max';
import { Avatar, Button, Divider, Select, Space, Tooltip } from 'antd';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useChatStyles } from '../styles';
import type { ChatTurn, SubAgentCardMap } from '../types';
import { getAgentName, stringToColor } from '../hooks/useChatState';
import InputArea, { type ChatStatus } from './InputArea';
import MessageList from './MessageList';
import DevPanel, { type DevRawEvent } from './DevPanel';
import type { WorkspaceAgentDto, WorkspaceWithPermDto } from '@/services/platform/api';

interface ChatMainProps {
  // layout
  sidebarOpen: boolean;
  onToggleSidebar: () => void;
  // workspace
  workspaces: WorkspaceWithPermDto[];
  workspaceId: string | undefined;
  workspaceLoading: boolean;
  wsOpts: { value: string; label: string; disabled: boolean }[];
  onWorkspaceChange: (v: string | undefined) => void;
  // agent
  agents: WorkspaceAgentDto[];
  agentId: string | undefined;
  agentLoading: boolean;
  agOpts: { value: string; label: React.ReactNode; disabled: boolean }[];
  selectedAgent: WorkspaceAgentDto | undefined;
  onAgentChange: (v: string | undefined) => void;
  // new workspace
  onCreateWorkspace: () => void;
  // session
  selectedSessionId: string | null;
  // chat
  turns: ChatTurn[];
  subAgentCards: SubAgentCardMap;
  historyLoading: boolean;
  loadingMore: boolean;
  hasMoreMessages: boolean;
  error: string | null;
  onClearError: () => void;
  onLoadMore: () => void;
  // input
  inputValue: string;
  onInputChange: (v: string) => void;
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  loading: boolean;
  onSend: () => void;
  onStop: () => void;
  onExport: () => void;
  disabled: boolean;
  // token
  tLimit: number;
  tUsed: number;
  tPct: number;
  // message rendering
  formatTime: (ts: number) => string;
  getStepTone: (status?: string) => 'executing' | 'success' | 'error';
  onDeleteTurn: (turnId: string) => void;
  onToggleReasoning: (turnId: string, blockId: string) => void;
  onContextMenu: (e: React.MouseEvent, turnId: string, role: 'user' | 'assistant') => void;
  onRerunTurn: (turnId: string) => void;
  onPinTurn: (turnId: string) => void;
  // refs
  messageListRef: React.RefObject<HTMLDivElement | null>;
  listEndRef: React.RefObject<HTMLDivElement | null>;
}

const emojiIconMap: Record<string, React.ReactNode> = {
  '😊': <SmileOutlined />,
  '🤖': <RobotOutlined />,
  '📊': <BarChartOutlined />,
  '⚡': <ThunderboltOutlined />,
  '⭐': <StarOutlined />,
  '🔥': <FireOutlined />,
  '💡': <BulbOutlined />,
  '🚀': <RocketOutlined />,
  '❤️': <HeartOutlined />,
};

const renderAgentOption = (a: WorkspaceAgentDto) => {
  const name = getAgentName(a).trim() || 'Agent';
  const emoji = a.avatarEmoji?.trim();
  if (emoji && emojiIconMap[emoji]) {
    return <Space size={6}>{emojiIconMap[emoji]}<span>{name}</span></Space>;
  }
  return (
    <Space size={6}>
      <RobotOutlined />
      <span>{name}</span>
    </Space>
  );
};

type TurnSnapshot = {
  reasoningCount: number;
  stepCount: number;
  answerLen: number;
  status: string;
  usageTotal: number;
};

const DEV_MODE_KEY = 'pudding-dev-mode';
const MAX_DEV_EVENTS = 500;

const ChatMain: React.FC<ChatMainProps> = ({
  sidebarOpen, onToggleSidebar,
  workspaceId, workspaceLoading, wsOpts, onWorkspaceChange,
  agents, agentId, agentLoading, agOpts, selectedAgent, onAgentChange,
  onCreateWorkspace,
  selectedSessionId,
  turns, historyLoading, loadingMore, hasMoreMessages, error, onClearError, onLoadMore,
  inputValue, onInputChange, onKeyDown, loading, onSend, onStop, onExport, disabled,
  tLimit, tUsed, tPct,
  formatTime, getStepTone, onDeleteTurn, onToggleReasoning, onContextMenu,
  onRerunTurn, onPinTurn,
  messageListRef, listEndRef, subAgentCards,
}) => {
  const { styles } = useChatStyles();
  const [devMode, setDevMode] = useState<boolean>(() => localStorage.getItem(DEV_MODE_KEY) === '1');
  const [rawEvents, setRawEvents] = useState<DevRawEvent[]>([]);
  const [inferredSessionId, setInferredSessionId] = useState<string | null>(null);
  const turnSnapshotRef = useRef<Map<string, TurnSnapshot>>(new Map());

  /** 根据当前 turns 和 loading 推导 Agent Console 状态文案 */
  const chatStatus: ChatStatus = React.useMemo(() => {
    if (!loading && turns.length === 0) return 'idle';
    // 用户正在输入内容但未发送
    if (!loading && inputValue.trim().length > 0) return 'composing';
    const lastTurn = turns[turns.length - 1];
    if (loading) {
      const st = lastTurn?.assistant.status;
      if (st === 'thinking') return 'thinking';
      if (st === 'executing') return 'tool_executing';
      return 'streaming';
    }
    // 非 loading 状态，检查最后一轮结果
    const st = lastTurn?.assistant.status;
    if (st === 'error' || st === 'cancelled') return 'error';
    return 'completed';
  }, [loading, turns, inputValue]);

  const dropdownRender = useCallback((menu: React.ReactNode) => (
    <>
      {menu}
      <Divider style={{ margin: '4px 0' }} />
      <Button type="link" block size="small" onClick={onCreateWorkspace}>+ 新建工作空间</Button>
    </>
  ), [onCreateWorkspace]);

  useEffect(() => {
    localStorage.setItem(DEV_MODE_KEY, devMode ? '1' : '0');
  }, [devMode]);

  useEffect(() => {
    if (!workspaceId) {
      setInferredSessionId(null);
      return;
    }

    let alive = true;
    const token = localStorage.getItem('pudding_token');
    if (!token) return;

    const loadSession = async () => {
      try {
        const resp = await fetch(`/api/sessions?workspaceId=${encodeURIComponent(workspaceId)}`, {
          method: 'GET',
          headers: { Authorization: `Bearer ${token}` },
        });
        if (!resp.ok || !alive) return;
        const sessions = await resp.json() as Array<{ sessionId: string }>;
        if (alive && Array.isArray(sessions) && sessions.length > 0) {
          setInferredSessionId(sessions[0]?.sessionId ?? null);
        }
      } catch {
        // no-op：开发者面板容错，不影响主聊天流程
      }
    };

    void loadSession();
  }, [workspaceId, turns.length]);

  useEffect(() => {
    const now = Date.now();
    const nextSnapshot = new Map<string, TurnSnapshot>();
    const appended: DevRawEvent[] = [];

    for (const turn of turns) {
      const prev = turnSnapshotRef.current.get(turn.turnId);
      const items = turn.assistant.timelineItems ?? [];
      const thinkingCount = items.filter(i => i.type === 'thinking').length;
      const stepCount = items.filter(i => i.type !== 'thinking').length;
      const current: TurnSnapshot = {
        reasoningCount: thinkingCount,
        stepCount,
        answerLen: turn.assistant.answerMarkdown.length,
        status: turn.assistant.status,
        usageTotal: turn.assistant.usage?.totalTokens ?? 0,
      };

      if (!prev) {
        if (thinkingCount > 0) {
          appended.push(...items.filter(i => i.type === 'thinking').map((x) => ({
            id: `evt-${turn.turnId}-thinking-${x.id}`,
            timestamp: now,
            event: 'thinking',
            payload: x.text ?? "",
          })));
        }
        if (stepCount > 0) {
          appended.push(...items.filter(i => i.type !== 'thinking').map((x) => ({
            id: `evt-${turn.turnId}-step-${x.id}`,
            timestamp: x.timestamp || now,
            event: 'step',
            payload: `[${x.status}] ${x.message}`,
          })));
        }
      } else {
        if (current.reasoningCount > prev.reasoningCount) {
          const newBlocks = items.filter(i => i.type === 'thinking').slice(prev.reasoningCount);
          appended.push(...newBlocks.map((x) => ({
            id: `evt-${turn.turnId}-thinking-${x.id}`,
            timestamp: now,
            event: 'thinking',
            payload: x.text ?? "",
          })));
        }

        if (current.stepCount > prev.stepCount) {
          const newCards = items.filter(i => i.type !== 'thinking').slice(prev.stepCount);
          appended.push(...newCards.map((x) => ({
            id: `evt-${turn.turnId}-step-${x.id}`,
            timestamp: x.timestamp || now,
            event: 'step',
            payload: `[${x.status}] ${x.message}`,
          })));
        }

        if (current.answerLen > prev.answerLen) {
          const delta = turn.assistant.answerMarkdown.slice(prev.answerLen);
          if (delta.trim()) {
            appended.push({
              id: `evt-${turn.turnId}-delta-${current.answerLen}`,
              timestamp: now,
              event: 'delta',
              payload: delta,
            });
          }
        }

        if (current.usageTotal > 0 && current.usageTotal !== prev.usageTotal) {
          appended.push({
            id: `evt-${turn.turnId}-usage-${current.usageTotal}`,
            timestamp: now,
            event: 'usage',
            payload: `totalTokens=${current.usageTotal}`,
          });
        }

        if (current.status !== prev.status) {
          const ev = current.status === 'success'
            ? 'done'
            : current.status === 'error'
              ? 'error'
              : current.status === 'cancelled'
                ? 'cancelled'
                : 'status';
          appended.push({
            id: `evt-${turn.turnId}-status-${current.status}-${now}`,
            timestamp: now,
            event: ev,
            payload: current.status,
          });
        }
      }

      nextSnapshot.set(turn.turnId, current);
    }

    turnSnapshotRef.current = nextSnapshot;
    if (appended.length > 0) {
      setRawEvents((prev) => [...prev, ...appended].slice(-MAX_DEV_EVENTS));
    }
  }, [turns]);

  return (
    <div className={styles.mainArea}>
      {/* Header */}
      <div className={styles.header}>
        {!sidebarOpen && (
          <Button type="text" size="small" icon={<MenuUnfoldOutlined />} onClick={onToggleSidebar} />
        )}
        <img src="/admin/assets/images/logo.png" alt="P" className={styles.headerLogo} />
        <span className={styles.headerBrand}>Pudding</span>
        <Select
          className={styles.headerSelect}
          size="small"
          variant="borderless"
          value={workspaceId}
          loading={workspaceLoading}
          options={wsOpts}
          onChange={onWorkspaceChange}
          placeholder="工作空间"
          popupMatchSelectWidth={false}
          dropdownRender={dropdownRender}
        />
        <Select
          className={styles.headerSelect}
          size="small"
          variant="borderless"
          value={agentId}
          loading={agentLoading}
          options={agOpts}
          onChange={onAgentChange}
          placeholder="Agent"
          popupMatchSelectWidth={false}
          notFoundContent="无Agent"
          optionRender={(option) => {
            const a = agents.find(x => x.agentId === option.value);
            return a ? renderAgentOption(a) : option.label;
          }}
        />
        {(inferredSessionId ?? selectedSessionId) && (
          <Tooltip title="点击复制 Session ID">
            <span
              onClick={() => { navigator.clipboard.writeText((inferredSessionId ?? selectedSessionId)!); }}
              style={{ cursor: 'pointer', fontSize: 11, color: 'var(--earth-brown)', opacity: 0.6, marginLeft: 8, fontFamily: 'monospace', userSelect: 'all' }}
            >
              {(inferredSessionId ?? selectedSessionId)!.slice(0, 8)}...
            </span>
          </Tooltip>
        )}
        <div className={styles.headerSpacer} />
        {selectedAgent && (
          <Tooltip title={getAgentName(selectedAgent)}>
            <Avatar
              size={26}
              src={selectedAgent.avatarUrl || undefined}
              style={{ background: stringToColor(getAgentName(selectedAgent)), flexShrink: 0 }}
            >
              {getAgentName(selectedAgent).charAt(0)}
            </Avatar>
          </Tooltip>
        )}
        <Tooltip title="控制台">
          <Button type="text" size="small" icon={<SettingOutlined />} onClick={() => history.push('/workspace')} />
        </Tooltip>
        <Tooltip title="开发者模式">
          <Button
            type="text"
            size="small"
            icon={<BugOutlined />}
            onClick={() => setDevMode(!devMode)}
            className={devMode ? styles.devModeActive : ''}
          />
        </Tooltip>
      </div>

      {/* Chat Body */}
      <div className={styles.chatBody}>
        <div className={devMode ? styles.chatBodyWithDev : styles.chatBodyMain}>
          <div className={styles.chatBodyMain}>
            <MessageList
              turns={turns}
              subAgentCards={subAgentCards}
              agentId={agentId}
              selectedAgent={selectedAgent}
              error={error}
              historyLoading={historyLoading}
              loadingMore={loadingMore}
              hasMoreMessages={hasMoreMessages}
              onClearError={onClearError}
              onLoadMore={onLoadMore}
              formatTime={formatTime}
              getStepTone={getStepTone}
              onDeleteTurn={onDeleteTurn}
              onToggleReasoning={onToggleReasoning}
              onContextMenu={onContextMenu}
              onRerunTurn={onRerunTurn}
              onPinTurn={onPinTurn}
              messageListRef={messageListRef}
              listEndRef={listEndRef}
            />
            <InputArea
              inputValue={inputValue}
              onInputChange={onInputChange}
              onKeyDown={onKeyDown}
              loading={loading}
              onSend={onSend}
              onStop={onStop}
              onExport={onExport}
              disabled={disabled}
              tLimit={tLimit}
              tUsed={tUsed}
              tPct={tPct}
              status={chatStatus}
              sessionId={inferredSessionId ?? selectedSessionId}
            />
          </div>

          {devMode && (
            <DevPanel
              workspaceId={workspaceId}
              sessionId={inferredSessionId}
              rawEvents={rawEvents}
            />
          )}
        </div>
      </div>
    </div>
  );
};

export default ChatMain;
