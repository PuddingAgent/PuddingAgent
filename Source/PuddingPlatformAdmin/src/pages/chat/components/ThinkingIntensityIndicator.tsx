// ── ThinkingIntensityIndicator：思考强度指示器（状态栏）────
import React from 'react';
import { Tooltip } from 'antd';
import { ThunderboltOutlined } from '@ant-design/icons';
import { useChatStyles } from '../styles';

interface ThinkingIntensityIndicatorProps {
  intensity?: 'auto' | 'low' | 'medium' | 'high';
}

const ThinkingIntensityIndicator: React.FC<ThinkingIntensityIndicatorProps> = ({ intensity = 'auto' }) => {
  const { styles } = useChatStyles();

  const colors: Record<string, string> = {
    auto: 'var(--earth-brown)',
    low: '#22c55e',
    medium: '#f59e0b',
    high: '#ef4444',
  };

  const labels: Record<string, string> = {
    auto: '自动',
    low: '低强度',
    medium: '中强度',
    high: '高强度',
  };

  return (
    <Tooltip title={`思考强度：${labels[intensity]}`}>
      <span className={styles.statusIcon}>
        <ThunderboltOutlined className={styles.statusIconThunder} style={{ color: colors[intensity] || colors.auto, fontSize: 12 }} />
      </span>
    </Tooltip>
  );
};

export default ThinkingIntensityIndicator;
