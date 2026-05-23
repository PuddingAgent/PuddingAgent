// ── AgentAvatar：Agent 头像组件 ─────────────────────────────
import React from 'react';
import { useChatStyles } from '../styles';

interface AgentAvatarProps {
  name?: string;
  emoji?: string;
  color?: string;
  imageUrl?: string;
  grouped?: boolean;
}

const AgentAvatar: React.FC<AgentAvatarProps> = ({
  emoji,
  color,
  imageUrl,
  grouped,
}) => {
  const { styles } = useChatStyles();

  if (grouped) {
    return <div className={styles.agentAvatarGrouped} />;
  }

  if (imageUrl) {
    return (
      <div className={styles.agentAvatarWrapper}>
        <img src={imageUrl} alt="" className={styles.agentAvatarImg} />
      </div>
    );
  }

  return (
    <div
      className={styles.agentAvatarWrapper}
      style={{ background: color ? `color-mix(in srgb, ${color} 15%, transparent)` : undefined }}
    >
      {emoji || '🤖'}
    </div>
  );
};

export default React.memo(AgentAvatar);
