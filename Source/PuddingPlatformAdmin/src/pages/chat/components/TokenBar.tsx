// ── TokenBar：Token 用量进度条 ──────────────────────────────
import { Progress } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';

interface TokenBarProps {
  tLimit: number;
  tUsed: number;
  tPct: number;
}

const TokenBar: React.FC<TokenBarProps> = ({ tLimit, tUsed, tPct }) => {
  const { styles } = useChatStyles();
  return (
    <div className={styles.tokenIndicator}>
      <span className={styles.sendingText}>Tokens</span>
      <Progress className={styles.tokenProgress} percent={tPct} size="small" />
      <span className={styles.sendingText}>{tUsed}/{tLimit}</span>
    </div>
  );
};

export default TokenBar;
