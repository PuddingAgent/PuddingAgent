// ── TranscriptModeSwitch：转录视图分级切换器 ──────────────────
// 三档：Normal（默认折叠）/ Verbose（全展开）/ Summary（一行摘要）
import { Segmented } from 'antd';
import React from 'react';
import { useChatMessageStyles } from '../styles/messageStyleContext';

export type TranscriptMode = 'normal' | 'verbose' | 'summary';

const MODE_OPTIONS: Array<{ label: string; value: TranscriptMode }> = [
  { label: '普通', value: 'normal' },
  { label: '详细', value: 'verbose' },
  { label: '摘要', value: 'summary' },
];

interface TranscriptModeSwitchProps {
  value: TranscriptMode;
  onChange: (value: TranscriptMode) => void;
}

const TranscriptModeSwitch: React.FC<TranscriptModeSwitchProps> = ({
  value,
  onChange,
}) => {
  const { styles } = useChatMessageStyles();
  return (
    <div className={styles.transcriptModeSwitch}>
      <Segmented
        size="small"
        value={value}
        onChange={(next) => onChange(next as TranscriptMode)}
        options={MODE_OPTIONS}
        data-testid="transcript-mode-switch"
      />
    </div>
  );
};

export default React.memo(TranscriptModeSwitch);
