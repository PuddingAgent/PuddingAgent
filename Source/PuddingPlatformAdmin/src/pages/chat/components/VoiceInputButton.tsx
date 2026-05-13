// ── VoiceInputButton：语音输入按钮 + 波形可视化 ────
import { AudioOutlined, AudioMutedOutlined, PauseCircleOutlined, PlayCircleOutlined } from '@ant-design/icons';
import { Button, Tooltip } from 'antd';
import React, { useCallback, useState } from 'react';
import { useChatStyles } from '../styles';

interface VoiceInputButtonProps {
  onVoiceStart?: () => void;
  onVoiceStop?: () => void;
  disabled?: boolean;
}

/** 语音状态：idle=待命, recording=录音中, playing=播放Agent回复 */
type VoiceState = 'idle' | 'recording' | 'playing';

const VoiceInputButton: React.FC<VoiceInputButtonProps> = ({ onVoiceStart, onVoiceStop, disabled }) => {
  const { styles } = useChatStyles();
  const [voiceState, setVoiceState] = useState<VoiceState>('idle');

  const handleToggle = useCallback(() => {
    if (voiceState === 'idle') {
      setVoiceState('recording');
      onVoiceStart?.();
    } else if (voiceState === 'recording') {
      setVoiceState('idle');
      onVoiceStop?.();
    }
  }, [voiceState, onVoiceStart, onVoiceStop]);

  const handlePlayToggle = useCallback(() => {
    setVoiceState(prev => prev === 'playing' ? 'idle' : 'playing');
  }, []);

  return (
    <div className={styles.voiceArea}>
      {/* 录音按钮 */}
      <Tooltip title={voiceState === 'recording' ? '停止录音' : '语音输入（即将开放）'}>
        <Button
          size="small"
          type="text"
          disabled={disabled || voiceState === 'playing'}
          icon={voiceState === 'recording' ? <AudioMutedOutlined /> : <AudioOutlined />}
          onClick={handleToggle}
          className={voiceState === 'recording' ? styles.voiceBtnRecording : styles.voiceBtn}
        />
      </Tooltip>

      {/* 波形可视化 — 占位 */}
      {voiceState !== 'idle' && (
        <div className={styles.voiceWaveform}>
          {Array.from({ length: 5 }).map((_, i) => (
            <span
              key={i}
              className={voiceState === 'recording' ? styles.voiceBarRecording : styles.voiceBarPlaying}
              style={{ animationDelay: `${i * 0.12}s` }}
            />
          ))}
        </div>
      )}

      {/* Agent 语音播放按钮 — 占位 */}
      {voiceState === 'playing' && (
        <Tooltip title="停止播放">
          <Button size="small" type="text" icon={<PauseCircleOutlined />} onClick={handlePlayToggle} className={styles.voiceBtn} />
        </Tooltip>
      )}

      {/* 若无录音且无播放，显示一个可点击激活语音的按钮 */}
      {voiceState === 'idle' && (
        <Tooltip title="Agent 语音播报（即将开放）">
          <Button size="small" type="text" icon={<PlayCircleOutlined />} onClick={handlePlayToggle} disabled className={styles.voiceBtn} />
        </Tooltip>
      )}
    </div>
  );
};

export default VoiceInputButton;
