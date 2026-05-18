// ── SessionSidebar：左侧会话列表 ────────────────────────────
import {
  DeleteOutlined,
  EditOutlined,
  ExclamationCircleOutlined,
  FolderOpenOutlined,
  MenuFoldOutlined,
  MessageOutlined,
  PlusOutlined,
} from '@ant-design/icons';
import { Button, Dropdown, Input, Modal, Spin, Tooltip, Badge } from 'antd';
import React, { useState } from 'react';
import { useChatStyles } from '../styles';
import type { SessionGroup } from '../types';

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
}

const SessionSidebar: React.FC<SessionSidebarProps> = ({
  sidebarOpen, onToggleSidebar, sessionsLoading, groups, selectedSessionId,
  creatingSession, onNewSession, onSelectSession, onRenameStart, onArchiveSession, onDeleteSession,
  unreadCounts,
}) => {
  const { styles, cx } = useChatStyles();
  const [searchQuery, setSearchQuery] = useState('');

  const filteredGroups = searchQuery.trim()
    ? groups.map(g => ({
        ...g,
        items: g.items.filter(s => s.title.toLowerCase().includes(searchQuery.toLowerCase())),
      })).filter(g => g.items.length > 0)
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
    <div className={cx(styles.sidebar, !sidebarOpen && styles.sidebarCollapsed)}>
      <div className={styles.sidebarHeader}>
        <Button
          type="primary"
          icon={<PlusOutlined />}
          className={styles.sidebarNewBtn}
          onClick={onNewSession}
          disabled={creatingSession}
        >
          新对话
        </Button>
        <Tooltip title="收起">
          <Button type="text" size="small" icon={<MenuFoldOutlined />} onClick={onToggleSidebar} />
        </Tooltip>
      </div>
      <div className={styles.sidebarSearch}>
        <Input
          placeholder="搜索会话..."
          allowClear
          size="small"
          value={searchQuery}
          onChange={e => setSearchQuery(e.target.value)}
          prefix={<MessageOutlined style={{ color: 'var(--earth-brown)', opacity: 0.5 }} />}
        />
      </div>
      <div className={styles.sessionList}>
        {sessionsLoading && (
          <div style={{ textAlign: 'center', padding: 16 }}><Spin /></div>
        )}
        {!sessionsLoading && filteredGroups.length === 0 && (
          <div className={styles.sidebarEmpty}>{searchQuery ? '未找到匹配的会话' : '在这里开始你的第一段对话'}</div>
        )}
        {filteredGroups.map(g => {
          const isArchived = g.label.includes('归档');
          return (
          <React.Fragment key={g.label}>
            <div className={styles.groupLabel}>{g.label}</div>
            {g.items.map(s => {
              const unread = unreadCounts?.[s.sessionId];
              const itemNode = (
                <div
                  className={cx(
                    styles.sessionItem,
                    s.sessionId === selectedSessionId && styles.sessionItemActive,
                    isArchived && styles.sessionItemArchived,
                  )}
                  onClick={() => onSelectSession(s.sessionId)}
                >
                  <MessageOutlined style={{ fontSize: 14, flexShrink: 0 }} />
                  <span className={styles.sessionTitle}>{s.title}</span>
                </div>
              );
              const wrapped = unread && unread > 0
                ? <Badge key={s.sessionId} count={unread} size="small" offset={[-4, 4]}>{itemNode}</Badge>
                : itemNode;
              return (
              <Dropdown
                key={s.sessionId}
                trigger={['contextMenu']}
                menu={{
                  items: [
                    { key: 'rename', icon: <EditOutlined />, label: '重命名',
                      onClick: () => onRenameStart(s.sessionId, s.title) },
                    { key: 'archive', icon: <FolderOpenOutlined />, label: '归档',
                      onClick: () => confirmArchive(s.sessionId, s.title) },
                    { type: 'divider' },
                    { key: 'delete', icon: <DeleteOutlined />, label: '删除', danger: true,
                      onClick: () => confirmDelete(s.sessionId, s.title) },
                  ],
                }}
              >
                {wrapped}
              </Dropdown>
            );})}
          </React.Fragment>
        );})}
      </div>
    </div>
  );
};

export default SessionSidebar;
