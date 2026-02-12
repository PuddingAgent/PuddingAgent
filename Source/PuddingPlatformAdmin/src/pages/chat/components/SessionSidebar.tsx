// ── SessionSidebar：左侧会话列表 ────────────────────────────
import {
  DeleteOutlined,
  EditOutlined,
  ExclamationCircleOutlined,
  FolderOpenOutlined,
  MenuFoldOutlined,
  MessageOutlined,
  PlusOutlined,
  RobotOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import {
  Avatar,
  Badge,
  Button,
  Dropdown,
  Input,
  Modal,
  Spin,
  Tooltip,
} from 'antd';
import React, { useState } from 'react';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import { getAgentName, stringToColor } from '../hooks/useChatState';
import { useChatStyles } from '../styles';
import type { SessionGroup } from '../types';

export type AgentContactStatusTone = 'working' | 'idle' | 'disabled';

export interface AgentContactStatus {
  label: string;
  tone: AgentContactStatusTone;
}

export type AgentStatusChipProjection = {
  status: 'idle' | 'running' | 'waiting' | 'failed' | 'offline';
  summary?: string;
};

export function getAgentContactStatus(
  agent: WorkspaceAgentDto,
  isWorking: boolean,
): AgentContactStatus {
  if (!agent.isEnabled) return { label: '停用', tone: 'disabled' };
  if (agent.isFrozen) return { label: '冻结', tone: 'disabled' };
  if (isWorking) return { label: '工作中', tone: 'working' };
  return { label: '在线', tone: 'idle' };
}

interface SessionSidebarProps {
  sidebarOpen: boolean;
  onToggleSidebar: () => void;
  sessionsLoading: boolean;
  groups: SessionGroup[];
  selectedSessionId: string | null;
  creatingSession: boolean;
  onNewSession: () => void;
  onSelectSession: (sid: string) => void;
  onRenameStart: (sid: string, title: string) => void;
  onArchiveSession: (sid: string) => void;
  onDeleteSession: (sid: string) => void;
  /** T-201: 会话未读计数 */
  unreadCounts?: Record<string, number>;
  agents: WorkspaceAgentDto[];
  agentId: string | undefined;
  agentLoading: boolean;
  onAgentChange: (agentId: string | undefined) => void;
  agentStatuses?: Record<string, AgentStatusChipProjection>;
  workingAgentIds?: string[];
}

const SessionSidebar: React.FC<SessionSidebarProps> = ({
  sidebarOpen,
  onToggleSidebar,
  sessionsLoading,
  groups,
  selectedSessionId,
  creatingSession,
  onNewSession,
  onSelectSession,
  onRenameStart,
  onArchiveSession,
  onDeleteSession,
  unreadCounts,
  agents,
  agentId,
  agentLoading,
  onAgentChange,
  agentStatuses = {},
  workingAgentIds = [],
}) => {
  const { styles, cx } = useChatStyles();
  const [searchQuery, setSearchQuery] = useState('');
  const [historyOpen, setHistoryOpen] = useState(false);
  const normalizedQuery = searchQuery.trim().toLowerCase();

  const filteredAgents = normalizedQuery
    ? agents.filter((agent) => {
        const name = getAgentName(agent).toLowerCase();
        return (
          name.includes(normalizedQuery) ||
          agent.agentId.toLowerCase().includes(normalizedQuery)
        );
      })
    : agents;

  const filteredGroups = normalizedQuery
    ? groups
        .map((g) => ({
          ...g,
          items: g.items.filter((s) =>
            s.title.toLowerCase().includes(normalizedQuery),
          ),
        }))
        .filter((g) => g.items.length > 0)
    : groups;

  const confirmArchive = (sid: string, title: string) => {
    Modal.confirm({
      title: '确认归档',
      icon: <ExclamationCircleOutlined />,
      content: `归档会话「${title}」？归档后不再显示在列表中。`,
      okText: '归档',
      cancelText: '取消',
      onOk: () => onArchiveSession(sid),
    });
  };

  const confirmDelete = (sid: string, title: string) => {
    Modal.confirm({
      title: '确认删除',
      icon: <ExclamationCircleOutlined />,
      content: `删除会话「${title}」后将无法恢复。`,
      okText: '删除',
      okType: 'danger',
      cancelText: '取消',
      onOk: () => onDeleteSession(sid),
    });
  };

  return (
    <div
      className={cx(styles.sidebar, !sidebarOpen && styles.sidebarCollapsed)}
    >
      <div className={styles.sidebarHeader}>
        <div className={styles.sidebarTitleBlock}>
          <div className={styles.sidebarTitle}>Agents</div>
          <div className={styles.sidebarSubtitle}>主线入口</div>
        </div>
        <Tooltip title="新任务">
          <Button
            type="text"
            icon={<PlusOutlined />}
            className={styles.sidebarIconBtn}
            onClick={onNewSession}
            disabled={creatingSession}
            aria-label="新任务"
            data-testid="chat-new-session"
          />
        </Tooltip>
        <Button
          type="text"
          icon={<MessageOutlined />}
          className={styles.sidebarIconBtn}
          onClick={() => setHistoryOpen((open) => !open)}
          aria-label={historyOpen ? '隐藏历史会话' : '查看历史会话'}
          aria-expanded={historyOpen}
          title={historyOpen ? '隐藏历史会话' : '查看历史会话'}
          data-testid="chat-history-toggle"
        />
        <Tooltip title="收起">
          <Button
            type="text"
            size="small"
            icon={<MenuFoldOutlined />}
            onClick={onToggleSidebar}
          />
        </Tooltip>
      </div>
      <div className={styles.sidebarSearch}>
        <Input
          placeholder="搜索 Agent 或会话"
          allowClear
          size="small"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          prefix={
            <RobotOutlined
              style={{ color: 'var(--earth-brown)', opacity: 0.5 }}
            />
          }
        />
      </div>
      <nav className={styles.agentContactList} aria-label="Agent 通讯录">
        {agentLoading && (
          <div style={{ textAlign: 'center', padding: 16 }}>
            <Spin />
          </div>
        )}
        {!agentLoading && filteredAgents.length === 0 && (
          <div className={styles.sidebarEmpty}>
            {searchQuery ? '未找到匹配的 Agent' : '当前工作空间还没有 Agent'}
          </div>
        )}
        {!agentLoading &&
          filteredAgents.map((agent) => {
            const name = getAgentName(agent);
            const isSelected = agent.agentId === agentId;
            const projected = agentStatuses[agent.agentId];
            const isWorking = projected
              ? projected.status === 'running' || projected.status === 'waiting'
              : workingAgentIds.includes(agent.agentId);
            const status = getProjectedAgentContactStatus(
              agent,
              projected,
              isWorking,
            );
            const isDisabled = !agent.isEnabled || agent.isFrozen;
            return (
              <button
                key={agent.agentId}
                type="button"
                className={cx(
                  styles.agentContactItem,
                  isSelected && styles.agentContactItemActive,
                )}
                aria-label={`${name}${isSelected ? ' 当前' : ''} ${status.label}`}
                disabled={isDisabled}
                onClick={() => onAgentChange(agent.agentId)}
              >
                <Avatar
                  size={36}
                  src={agent.avatarUrl || undefined}
                  className={styles.agentContactAvatar}
                  style={{ background: stringToColor(agent.agentId) }}
                >
                  {(agent.avatarEmoji || name.charAt(0) || 'A').slice(0, 1)}
                </Avatar>
                <span className={styles.agentContactBody}>
                  <span className={styles.agentContactName}>{name}</span>
                  <span className={styles.agentContactMeta}>主线会话</span>
                </span>
                <span
                  className={cx(
                    styles.agentStatusTag,
                    styles[`agentStatusTag_${status.tone}`],
                  )}
                >
                  {status.label}
                </span>
              </button>
            );
          })}
        <div className={styles.sidebarSecondaryLabel}>Groups</div>
        <div className={styles.sidebarEmptyInline}>
          <TeamOutlined />
          <span>群组即将接入</span>
        </div>
      </nav>
      {historyOpen && (
        <section className={styles.sessionDetailArea} aria-label="会话细节">
          <div className={styles.sessionDetailHeader}>
            <span>最近会话</span>
            <MessageOutlined />
          </div>
          <div className={styles.sessionList}>
            {sessionsLoading && (
              <div style={{ textAlign: 'center', padding: 16 }}>
                <Spin />
              </div>
            )}
            {!sessionsLoading && filteredGroups.length === 0 && (
              <div className={styles.sidebarEmpty}>
                {searchQuery ? '未找到匹配的会话' : '暂无会话细节'}
              </div>
            )}
            {filteredGroups.map((g) => {
              const isArchived = g.label.includes('归档');
              return (
                <React.Fragment key={g.label}>
                  <div className={styles.groupLabel}>{g.label}</div>
                  {g.items.map((s) => {
                    const unread = unreadCounts?.[s.sessionId];
                    const itemNode = (
                      <div
                        className={cx(
                          styles.sessionItem,
                          s.sessionId === selectedSessionId &&
                            styles.sessionItemActive,
                          isArchived && styles.sessionItemArchived,
                        )}
                        onClick={() => onSelectSession(s.sessionId)}
                        data-testid={`chat-session-${s.sessionId}`}
                      >
                        <MessageOutlined
                          style={{ fontSize: 14, flexShrink: 0 }}
                        />
                        <span className={styles.sessionTitle}>{s.title}</span>
                      </div>
                    );
                    const wrapped =
                      unread && unread > 0 ? (
                        <Badge
                          key={s.sessionId}
                          count={unread}
                          size="small"
                          offset={[-4, 4]}
                        >
                          {itemNode}
                        </Badge>
                      ) : (
                        itemNode
                      );
                    return (
                      <Dropdown
                        key={s.sessionId}
                        trigger={['contextMenu']}
                        menu={{
                          items: [
                            {
                              key: 'rename',
                              icon: <EditOutlined />,
                              label: '重命名',
                              onClick: () =>
                                onRenameStart(s.sessionId, s.title),
                            },
                            {
                              key: 'archive',
                              icon: <FolderOpenOutlined />,
                              label: '归档',
                              onClick: () =>
                                confirmArchive(s.sessionId, s.title),
                            },
                            { type: 'divider' },
                            {
                              key: 'delete',
                              icon: <DeleteOutlined />,
                              label: '删除',
                              danger: true,
                              onClick: () =>
                                confirmDelete(s.sessionId, s.title),
                            },
                          ],
                        }}
                      >
                        {wrapped}
                      </Dropdown>
                    );
                  })}
                </React.Fragment>
              );
            })}
          </div>
        </section>
      )}
    </div>
  );
};

export default SessionSidebar;

function getProjectedAgentContactStatus(
  agent: WorkspaceAgentDto,
  projected: AgentStatusChipProjection | undefined,
  isWorking: boolean,
): AgentContactStatus {
  if (!agent.isEnabled || agent.isFrozen || !projected)
    return getAgentContactStatus(agent, isWorking);
  if (projected.status === 'failed') return { label: '异常', tone: 'disabled' };
  if (projected.status === 'offline')
    return { label: '离线', tone: 'disabled' };
  return getAgentContactStatus(agent, isWorking);
}
