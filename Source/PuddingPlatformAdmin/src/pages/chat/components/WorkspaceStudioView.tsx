import React from 'react';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import { useChatStyles } from '../styles';
import type { ChatTurn, SubAgentCardMap } from '../types';
import WorkspaceStudioGameCanvas from './WorkspaceStudioGameCanvas';
import {
  buildWorkspaceStudioAgents,
  type WorkspaceStudioAgentActivity,
  type WorkspaceStudioAgentCommand,
  type WorkspaceStudioObjectId,
  type WorkspaceStudioSceneEvent,
  type WorkspaceStudioSceneStatus,
} from './workspaceStudio';

interface WorkspaceStudioViewProps {
  agents: WorkspaceAgentDto[];
  selectedAgentId?: string;
  turns: ChatTurn[];
  loading: boolean;
  subAgentCards: SubAgentCardMap;
  agentActivities?: Record<string, WorkspaceStudioAgentActivity>;
  sceneStatus?: WorkspaceStudioSceneStatus;
  sceneEvents?: WorkspaceStudioSceneEvent[];
  onAgentChange: (agentId: string | undefined) => void;
  onAgentCommand?: (
    agentId: string,
    command: WorkspaceStudioAgentCommand,
  ) => void;
  onObjectSelect?: (objectId: WorkspaceStudioObjectId) => void;
  selectedObjectId?: WorkspaceStudioObjectId;
  variant?: 'compact' | 'page';
}

function getWorkspaceStudioAvatarFallback(name: string): string {
  const trimmed = name.trim();
  if (!trimmed) return 'A';
  return Array.from(trimmed)[0]?.toUpperCase() || 'A';
}

const WorkspaceStudioHudAvatar: React.FC<{
  avatarUrl?: string;
  avatarEmoji?: string;
  name: string;
  className: string;
  imageClassName: string;
  fallbackClassName: string;
}> = ({
  avatarUrl,
  avatarEmoji,
  name,
  className,
  imageClassName,
  fallbackClassName,
}) => {
  const [imageFailed, setImageFailed] = React.useState(false);

  React.useEffect(() => {
    setImageFailed(false);
  }, [avatarUrl]);

  return (
    <span
      className={className}
      data-studio-hud-portrait
      aria-hidden={!avatarUrl && !avatarEmoji}
    >
      {avatarUrl && !imageFailed ? (
        <img
          src={avatarUrl}
          alt={`${name} 头像`}
          className={imageClassName}
          onError={() => setImageFailed(true)}
        />
      ) : (
        <span className={fallbackClassName}>
          {avatarEmoji || getWorkspaceStudioAvatarFallback(name)}
        </span>
      )}
    </span>
  );
};

const WorkspaceStudioView: React.FC<WorkspaceStudioViewProps> = ({
  agents,
  selectedAgentId,
  turns,
  loading,
  subAgentCards,
  agentActivities,
  sceneStatus,
  sceneEvents,
  onAgentChange,
  onAgentCommand,
  onObjectSelect,
  selectedObjectId,
  variant = 'compact',
}) => {
  const { styles } = useChatStyles();
  const items = React.useMemo(
    () =>
      buildWorkspaceStudioAgents({
        agents,
        selectedAgentId,
        turns,
        loading,
        subAgentCards,
        agentActivities,
      }),
    [agentActivities, agents, selectedAgentId, turns, loading, subAgentCards],
  );

  if (!items.length) {
    return (
      <section
        className={`${styles.workspaceStudioPanel} ${variant === 'page' ? styles.workspaceStudioPanelPage : ''}`}
        aria-label="工作空间视图"
      >
        <div
          className={`${styles.workspaceStudioHeader} ${variant === 'page' ? styles.workspaceStudioHeaderPage : ''}`}
        >
          <span className={styles.workspaceStudioTitle}>工作室</span>
          <span className={styles.workspaceStudioMeta}>暂无 Agent</span>
        </div>
      </section>
    );
  }

  return (
    <section
      className={`${styles.workspaceStudioPanel} ${variant === 'page' ? styles.workspaceStudioPanelPage : ''}`}
      aria-label="工作空间视图"
    >
      <div
        className={`${styles.workspaceStudioHeader} ${variant === 'page' ? styles.workspaceStudioHeaderPage : ''}`}
      >
        <div className={styles.workspaceStudioHeaderCopy}>
          <span className={styles.workspaceStudioTitle}>工作室</span>
          <span className={styles.workspaceStudioMeta}>
            {items.length} 个 Agent
          </span>
        </div>
      </div>
      <div
        className={`${styles.workspaceStudioRoom} ${styles.workspaceStudioGameRoom} ${variant === 'page' ? styles.workspaceStudioRoomPage : ''}`}
        role="application"
        aria-label="工作空间 Agent 状态"
      >
        <div
          className={styles.workspaceStudioAgentHudList}
          aria-label="工作室 Agent 头像列表"
        >
          {items.map((item) => (
            <button
              key={item.agentId}
              type="button"
              className={styles.workspaceStudioAgentHud}
              aria-label={`${item.selected ? '打开' : '切换到'} ${item.name} 角色面板`}
              aria-current={item.selected ? 'true' : undefined}
              data-selected={item.selected || undefined}
              title={item.name}
              onClick={() => {
                if (item.selected) {
                  onAgentCommand?.(item.agentId, 'chat');
                  return;
                }
                onAgentChange(item.agentId);
              }}
            >
              <WorkspaceStudioHudAvatar
                avatarUrl={item.avatarUrl}
                avatarEmoji={item.avatarEmoji}
                name={item.name}
                className={styles.workspaceStudioAgentHudPortrait}
                imageClassName={styles.workspaceStudioAgentHudPortraitImg}
                fallbackClassName={
                  styles.workspaceStudioAgentHudPortraitFallback
                }
              />
            </button>
          ))}
        </div>
        <WorkspaceStudioGameCanvas
          agents={items}
          sceneStatus={sceneStatus}
          sceneEvents={sceneEvents}
          selectedObjectId={selectedObjectId}
          onAgentCommand={onAgentCommand}
          onObjectSelect={onObjectSelect}
        />
      </div>
    </section>
  );
};

export default React.memo(WorkspaceStudioView);
