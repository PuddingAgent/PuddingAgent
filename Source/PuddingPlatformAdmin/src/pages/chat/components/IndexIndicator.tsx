// ── IndexIndicator：全文索引引擎状态指示器 ────

import { SearchOutlined } from '@ant-design/icons';
import { Tooltip } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';

interface IndexIndicatorProps {
  active?: boolean;
  fileCount?: number;
  statusText?: string;
}

const IndexIndicator: React.FC<IndexIndicatorProps> = ({
  active,
  fileCount,
  statusText,
}) => {
  const { styles } = useChatStyles();

  return (
    <Tooltip
      title={
        statusText ||
        (active ? `索引已激活 · ${fileCount ?? '—'} 个文件` : '索引未启动')
      }
    >
      <span className={styles.statusIconGroup}>
        <SearchOutlined
          className={styles.statusIconThunder}
          style={{
            fontSize: 12,
            color: active ? '#3b82f6' : 'var(--earth-brown)',
            opacity: active ? 1 : 0.4,
          }}
        />
        <span
          className={styles.statusIconLabel}
          style={{
            color: active ? '#3b82f6' : undefined,
            opacity: active ? 1 : 0.5,
          }}
        >
          {active ? '已索引' : '索引'}
        </span>
      </span>
    </Tooltip>
  );
};

export default IndexIndicator;
