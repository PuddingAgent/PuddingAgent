// ── ProviderBalanceIndicator：LLM 服务商余额指示器 ────
import React from 'react';
import { Tooltip } from 'antd';
import { useChatStyles } from '../styles';

interface ProviderBalanceIndicatorProps {
  provider?: string;
  balance?: number;
  currency?: string;
}

/** DeepSeek 风格 SVG 图标（占位） */
const ProviderIcon: React.FC<{ provider: string }> = ({ provider }) => {
  if (provider === 'deepseek' || provider === 'DeepSeek') {
    return (
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" style={{ verticalAlign: 'middle' }}>
        <circle cx="12" cy="12" r="10" fill="#4F46E5" />
        <path d="M7 12l3.5 3.5L17 9" stroke="#fff" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
    );
  }
  if (provider === 'mimo' || provider === 'Mimo') {
    return (
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" style={{ verticalAlign: 'middle' }}>
        <rect x="2" y="2" width="20" height="20" rx="4" fill="#f59e0b" />
        <text x="12" y="16" textAnchor="middle" fontSize="12" fontWeight="bold" fill="#fff">M</text>
      </svg>
    );
  }
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" style={{ verticalAlign: 'middle' }}>
      <circle cx="12" cy="12" r="10" fill="var(--earth-brown)" opacity={0.6} />
      <text x="12" y="16" textAnchor="middle" fontSize="10" fontWeight="bold" fill="#fff">?</text>
    </svg>
  );
};

const ProviderBalanceIndicator: React.FC<ProviderBalanceIndicatorProps> = ({
  provider = 'DeepSeek', balance, currency = '¥',
}) => {
  const { styles } = useChatStyles();

  const display = balance != null ? `${currency}${balance.toFixed(2)}` : '—';

  return (
    <Tooltip title={`${provider} 余额：${display}`}>
      <span className={styles.statusIconGroup}>
        <ProviderIcon provider={provider} />
        <span className={styles.statusIconLabel}>{display}</span>
      </span>
    </Tooltip>
  );
};

export default ProviderBalanceIndicator;
