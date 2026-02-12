// ── VoiceInputButton：语音输入入口 ────
import { AudioOutlined } from '@ant-design/icons';
import { Button, Tooltip } from 'antd';
import React, { useCallback } from 'react';
import { useChatStyles } from '../styles';

export type VoiceInputButtonStatus =
  | 'idle'
  | 'requesting_permission'
  | 'recording'
  | 'transcribing'
  | 'failed';

interface VoiceInputButtonProps {
  onVoiceStart?: () => void;
  onVoiceStop?: () => void;
  disabled?: boolean;
  enabled?: boolean;
  status?: VoiceInputButtonStatus;
  transcriptPreview?: string;
  unavailableReason?: string;
}

const VoiceInputButton: React.FC<VoiceInputButtonProps> = ({
  onVoiceStart,
  onVoiceStop,
  disabled,
  enabled = false,
  status = 'idle',
  transcriptPreview,
  unavailableReason,
}) => {
  const { styles } = useChatStyles();
  const active =
    status === 'requesting_permission' ||
    status === 'recording' ||
    status === 'transcribing';
  const buttonLabel = !enabled
    ? unavailableReason || '语音输入待接入'
    : active
      ? '停止语音输入'
      : '开始语音输入';

  const handleToggle = useCallback(() => {
    if (!enabled || disabled) return;
    if (active) {
      onVoiceStop?.();
      return;
    }
    onVoiceStart?.();
  }, [active, disabled, enabled, onVoiceStart, onVoiceStop]);

  return (
    <div className={styles.voiceArea}>
      <Tooltip title={buttonLabel}>
        <Button
          size="small"
          type="text"
          disabled={disabled || !enabled}
          icon={<AudioOutlined />}
          onClick={handleToggle}
          className={active ? styles.voiceBtnRecording : styles.voiceBtn}
          aria-label={buttonLabel}
        />
      </Tooltip>
      {transcriptPreview ? (
        <span className={styles.voiceTranscriptPreview}>
          {transcriptPreview}
        </span>
      ) : null}
    </div>
  );
};

export default VoiceInputButton;
