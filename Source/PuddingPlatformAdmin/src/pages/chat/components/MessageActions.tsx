// ── MessageActions：消息操作按钮组 ─────────────────────────
import {
  CopyOutlined,
  DeleteOutlined,
  LoadingOutlined,
  PauseCircleOutlined,
  PushpinOutlined,
  ReloadOutlined,
  SoundOutlined,
} from '@ant-design/icons';
import { Tooltip } from 'antd';
import React from 'react';
import type {
  BrowserVoiceOutputAdapter,
  BrowserVoiceOutputHandle,
} from '../hooks/browserVoiceOutput';
import { useChatStyles } from '../styles';

interface MessageActionsProps {
  content: string;
  visible: boolean;
  onCopy?: () => void;
  onRerun?: () => void;
  onPin?: () => void;
  onDelete?: () => void;
  voiceOutputAdapter?: BrowserVoiceOutputAdapter;
  /** 后端 TTS 朗读（DashScope API），优先级高于浏览器语音。 */
  onTtsSpeak?: () => void;
  ttsPlaying?: boolean;
  ttsLoading?: boolean;
}

const MessageActions: React.FC<MessageActionsProps> = ({
  content,
  visible,
  onCopy,
  onRerun,
  onPin,
  onDelete,
  voiceOutputAdapter,
  onTtsSpeak,
  ttsPlaying,
  ttsLoading,
}) => {
  const { styles, cx } = useChatStyles();
  const [voicePlaying, setVoicePlaying] = React.useState(false);
  const [voiceError, setVoiceError] = React.useState<string | undefined>();
  const voiceHandleRef = React.useRef<BrowserVoiceOutputHandle | null>(null);
  const canSpeak = Boolean(voiceOutputAdapter && content.trim());
  const voiceSupported = voiceOutputAdapter?.isSupported() ?? false;

  const stopVoice = React.useCallback(() => {
    voiceHandleRef.current?.stop();
    voiceHandleRef.current = null;
    setVoicePlaying(false);
  }, []);

  const startVoice = React.useCallback(() => {
    if (!voiceOutputAdapter || !content.trim() || !voiceSupported) return;
    setVoiceError(undefined);
    try {
      voiceHandleRef.current = voiceOutputAdapter.speak(content, {
        lang: 'zh-CN',
        onStart: () => setVoicePlaying(true),
        onComplete: () => {
          voiceHandleRef.current = null;
          setVoicePlaying(false);
        },
        onError: (message) => {
          voiceHandleRef.current = null;
          setVoicePlaying(false);
          setVoiceError(message);
        },
      });
      setVoicePlaying(true);
    } catch (err) {
      setVoiceError(err instanceof Error ? err.message : '语音朗读失败');
      setVoicePlaying(false);
      voiceHandleRef.current = null;
    }
  }, [content, voiceOutputAdapter, voiceSupported]);

  React.useEffect(() => stopVoice, [stopVoice]);

  return (
    <div
      className={cx(
        styles.messageActionsNew,
        visible && styles.messageActionsVisible,
      )}
      onClick={(e) => e.stopPropagation()}
      aria-hidden={!visible}
    >
      {onCopy && (
        <Tooltip title="复制">
          <button
            className={styles.messageActionBtn}
            onClick={() => {
              navigator.clipboard.writeText(content).catch(() => {});
              onCopy?.();
            }}
            aria-label="复制"
            tabIndex={visible ? 0 : -1}
          >
            <CopyOutlined />
          </button>
        </Tooltip>
      )}
      {onRerun && (
        <Tooltip title="重新生成">
          <button
            className={styles.messageActionBtn}
            onClick={onRerun}
            aria-label="重新生成"
            tabIndex={visible ? 0 : -1}
          >
            <ReloadOutlined />
          </button>
        </Tooltip>
      )}
      {onPin && (
        <Tooltip title="固定">
          <button
            className={styles.messageActionBtn}
            onClick={onPin}
            aria-label="固定"
            tabIndex={visible ? 0 : -1}
          >
            <PushpinOutlined />
          </button>
        </Tooltip>
      )}
      {/* 后端 TTS 朗读（DashScope API），优先于浏览器语音 */}
      {onTtsSpeak && (
        <Tooltip
          title={ttsLoading ? '合成中...' : ttsPlaying ? '停止朗读' : 'AI 朗读'}
        >
          <button
            className={styles.messageActionBtn}
            onClick={ttsPlaying ? undefined : onTtsSpeak}
            aria-label={
              ttsLoading ? '合成中' : ttsPlaying ? '停止朗读' : 'AI 朗读'
            }
            tabIndex={visible ? 0 : -1}
          >
            {ttsLoading ? (
              <LoadingOutlined spin />
            ) : ttsPlaying ? (
              <PauseCircleOutlined />
            ) : (
              <SoundOutlined />
            )}
          </button>
        </Tooltip>
      )}
      {canSpeak && !onTtsSpeak && (
        <Tooltip
          title={
            voiceSupported
              ? voicePlaying
                ? '停止朗读'
                : '朗读回复'
              : '浏览器不支持语音朗读'
          }
        >
          <button
            className={styles.messageActionBtn}
            onClick={voicePlaying ? stopVoice : startVoice}
            aria-label={
              voiceSupported
                ? voicePlaying
                  ? '停止朗读'
                  : '朗读回复'
                : '浏览器不支持语音朗读'
            }
            disabled={!voiceSupported}
            title={voiceError}
            tabIndex={visible ? 0 : -1}
          >
            {voicePlaying ? <PauseCircleOutlined /> : <SoundOutlined />}
          </button>
        </Tooltip>
      )}
      {onDelete && (
        <Tooltip title="删除">
          <button
            className={`${styles.messageActionBtn} ${styles.messageActionBtnDanger}`}
            onClick={onDelete}
            aria-label="删除"
            tabIndex={visible ? 0 : -1}
          >
            <DeleteOutlined />
          </button>
        </Tooltip>
      )}
    </div>
  );
};

export default React.memo(MessageActions);
