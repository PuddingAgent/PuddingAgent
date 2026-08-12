// ── AgentAvatar：Agent 头像组件 ─────────────────────────────
import React, { useState } from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';

interface AgentAvatarProps {
  name?: string;
  emoji?: string;
  color?: string;
  imageUrl?: string;
  grouped?: boolean;
}

const AgentAvatar: React.FC<AgentAvatarProps> = ({
  name,
  emoji,
  color,
  imageUrl,
  grouped,
}) => {
  const { styles } = useChatMessageStyles();
  const [imgFailed, setImgFailed] = useState(false);

  if (grouped) {
    return <div className={styles.agentAvatarGrouped} />;
  }

  // 图片加载失败时回退到 emoji / 色块首字母，避免显示裂图
  if (imageUrl && !imgFailed) {
    return (
      <div className={styles.agentAvatarWrapper}>
        <img
          src={imageUrl}
          alt=""
          className={styles.agentAvatarImg}
          onError={() => setImgFailed(true)}
        />
      </div>
    );
  }

  return (
    <div
      className={styles.agentAvatarWrapper}
      style={{
        background: color
          ? `color-mix(in srgb, ${color} 15%, transparent)`
          : undefined,
      }}
    >
      {emoji || (name ? name.trim().charAt(0).toUpperCase() : '🤖')}
    </div>
  );
};

export default React.memo(AgentAvatar);
