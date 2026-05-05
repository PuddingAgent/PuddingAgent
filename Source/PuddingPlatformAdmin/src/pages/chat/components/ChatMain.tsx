// ── ChatMain：右侧主聊天区（Header + MessageList + InputArea）─
import {
  MenuUnfoldOutlined,
  RobotOutlined,
  SettingOutlined,
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
import React, { useCallback } from 'react';
import { useChatStyles } from '../styles';
import type { ChatTurn } from '../types';
import { getAgentName, stringToColor } from '../hooks/useChatState';
import InputArea from './InputArea';
import MessageList from './MessageList';
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
  // chat
  turns: ChatTurn[];
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

const ChatMain: React.FC<ChatMainProps> = ({
  sidebarOpen, onToggleSidebar,
  workspaceId, workspaceLoading, wsOpts, onWorkspaceChange,
  agents, agentId, agentLoading, agOpts, selectedAgent, onAgentChange,
  onCreateWorkspace,
  turns, historyLoading, loadingMore, hasMoreMessages, error, onClearError, onLoadMore,
  inputValue, onInputChange, onKeyDown, loading, onSend, onStop, onExport, disabled,
  tLimit, tUsed, tPct,
  formatTime, getStepTone, onDeleteTurn, onToggleReasoning, onContextMenu,
  onRerunTurn, onPinTurn,
  messageListRef, listEndRef,
}) => {
  const { styles } = useChatStyles();

  const dropdownRender = useCallback((menu: React.ReactNode) => (
    <>
      {menu}
      <Divider style={{ margin: '4px 0' }} />
      <Button type="link" block size="small" onClick={onCreateWorkspace}>+ 新建工作空间</Button>
    </>
  ), [onCreateWorkspace]);

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
      </div>

      {/* Chat Body */}
      <div className={styles.chatBody}>
        <MessageList
          turns={turns}
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
        />
      </div>
    </div>
  );
};

export default ChatMain;
