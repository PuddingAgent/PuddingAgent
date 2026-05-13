// ── SubconsciousLlmIndicator：潜意识 LLM 工作状态指示器 ────
import React from 'react';
import { Tooltip } from 'antd';
import { BulbOutlined } from '@ant-design/icons';
import { useChatStyles } from '../styles';

interface SubconsciousLlmIndicatorProps {
  active?: boolean;
  statusText?: string;
}

const SubconsciousLlmIndicator: React.FC<SubconsciousLlmIndicatorProps> = ({ active, statusText }) => {
  const { styles } = useChatStyles();

  return (
    <Tooltip title={statusText || (active ? '潜意识 LLM 运行中' : '潜意识 LLM 待机')}>
      <span className={styles.statusIconGroup}>
        <BulbOutlined className={`${styles.statusIconThunder} ${active ? styles.subconsciousGlow : ''}`}
          style={{
            fontSize: 12,
            color: active ? '#a78bfa' : 'var(--earth-brown)',
            opacity: active ? 1 : 0.4,
          }} />
        <span className={styles.statusIconLabel} style={{
          color: active ? '#a78bfa' : undefined,
          opacity: active ? 1 : 0.5,
        }}>
          {active ? '潜意识' : '潜意识'}
        </span>
      </span>
    </Tooltip>
  );
};

export default SubconsciousLlmIndicator;
