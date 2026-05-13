// ── AspLspIndicator：ASP / LSP 语言服务器状态指示器 ────
import React from 'react';
import { Tooltip } from 'antd';
import { CodeOutlined } from '@ant-design/icons';
import { useChatStyles } from '../styles';

interface AspLspIndicatorProps {
  aspActive?: boolean;
  lspActive?: boolean;
}

const AspLspIndicator: React.FC<AspLspIndicatorProps> = ({ aspActive, lspActive }) => {
  const { styles } = useChatStyles();

  return (
    <Tooltip title={`ASP: ${aspActive ? '已连接' : '未连接'} · LSP: ${lspActive ? '已激活' : '未激活'}`}>
      <span className={styles.statusIconGroup}>
        <CodeOutlined className={styles.statusIconThunder} style={{ fontSize: 12 }} />
        <span className={styles.statusIconLabel} style={{
          color: (aspActive && lspActive) ? '#22c55e' : 'var(--earth-brown)',
          opacity: (aspActive && lspActive) ? 1 : 0.5,
        }}>
          {aspActive && lspActive ? 'ASP/LSP' : 'ASP'}
        </span>
      </span>
    </Tooltip>
  );
};

export default AspLspIndicator;
