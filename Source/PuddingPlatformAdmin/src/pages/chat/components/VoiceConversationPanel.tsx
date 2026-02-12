import {
  AudioOutlined,
  CloseOutlined,
  CustomerServiceOutlined,
  PauseOutlined,
  RedoOutlined,
  SendOutlined,
} from '@ant-design/icons';
import { Button, Tooltip } from 'antd';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type {
  BrowserVoiceInputAdapter,
  BrowserVoiceInputHandle,
} from '../hooks/browserVoiceInput';
import {
  type BrowserVoiceOutputAdapter,
  type BrowserVoiceOutputHandle,
  defaultBrowserVoiceOutputAdapter,
} from '../hooks/browserVoiceOutput';
import { createDashScopeVoiceInputAdapter } from '../hooks/dashScopeVoiceInput';
import { useChatStyles } from '../styles';

type VoicePanelState =
  | 'idle'
  | 'requesting_permission'
  | 'recording'
  | 'transcribing'
  | 'awaiting_confirmation'
  | 'sending'
  | 'failed';

interface VoiceConversationPanelProps {
  inputValue: string;
  disabled?: boolean;
  loading?: boolean;
  latestAssistantText?: string;
  voiceInputAdapter: BrowserVoiceInputAdapter;
  voiceOutputAdapter?: BrowserVoiceOutputAdapter;
  variant?: 'panel' | 'composer';
  onExit?: () => void;
  onDraftChange: (value: string) => void;
  onSend: (
    content: string,
    metadata: Record<string, string>,
  ) => Promise<void> | void;
  onVoiceCaptureStatus?: (
    status: string,
    detail?: { sessionId?: string; error?: string },
  ) => void;
  onVoicePlaybackStatus?: (
    status: string,
    detail?: { deliveryId?: string; error?: string },
  ) => void;
}

const VOICE_LANG = 'zh-CN';

function createVoiceId(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

const stateLabel: Record<VoicePanelState, string> = {
  idle: '待命',
  requesting_permission: '等待麦克风权限',
  recording: '正在听',
  transcribing: '正在转写',
  awaiting_confirmation: '待确认',
  sending: '发送中',
  failed: '语音异常',
};

const VoiceConversationPanel: React.FC<VoiceConversationPanelProps> = ({
  inputValue,
  disabled = false,
  loading = false,
  latestAssistantText = '',
  voiceInputAdapter,
  voiceOutputAdapter = defaultBrowserVoiceOutputAdapter,
  variant = 'panel',
  onExit,
  onDraftChange,
  onSend,
  onVoiceCaptureStatus,
  onVoicePlaybackStatus,
}) => {
  const { styles } = useChatStyles();
  const [panelState, setPanelState] = useState<VoicePanelState>('idle');
  const [interimTranscript, setInterimTranscript] = useState('');
  const [errorText, setErrorText] = useState<string | undefined>();
  const [playbackActive, setPlaybackActive] = useState(false);
  const [playbackError, setPlaybackError] = useState<string | undefined>();
  const captureHandleRef = useRef<BrowserVoiceInputHandle | null>(null);
  const playbackHandleRef = useRef<BrowserVoiceOutputHandle | null>(null);
  const voiceSessionIdRef = useRef<string | undefined>(undefined);
  const deliveryIdRef = useRef<string | undefined>(undefined);

  const voiceSupported = voiceInputAdapter.isSupported();
  const outputSupported = voiceOutputAdapter.isSupported();
  const draftText = inputValue;
  const isListening =
    panelState === 'requesting_permission' ||
    panelState === 'recording' ||
    panelState === 'transcribing';
  const canSend =
    Boolean(draftText.trim()) &&
    !disabled &&
    !loading &&
    panelState !== 'sending';
  const canSpeak =
    Boolean(latestAssistantText.trim()) && outputSupported && !disabled;

  const visibleTranscript = useMemo(() => {
    if (interimTranscript.trim()) return interimTranscript.trim();
    return draftText.trim();
  }, [draftText, interimTranscript]);

  const stopPlayback = useCallback(
    (status: 'cancelled' | 'completed' = 'cancelled') => {
      if (!playbackHandleRef.current) return;
      playbackHandleRef.current.stop();
      playbackHandleRef.current = null;
      setPlaybackActive(false);
      onVoicePlaybackStatus?.(status, { deliveryId: deliveryIdRef.current });
    },
    [onVoicePlaybackStatus],
  );

  const stopCapture = useCallback(
    (status: 'cancelled' | 'completed' = 'cancelled') => {
      captureHandleRef.current?.stop();
      captureHandleRef.current = null;
      setInterimTranscript('');
      setPanelState(status === 'completed' ? 'awaiting_confirmation' : 'idle');
      onVoiceCaptureStatus?.(status, { sessionId: voiceSessionIdRef.current });
    },
    [onVoiceCaptureStatus],
  );

  const startCapture = useCallback(async () => {
    if (!voiceSupported || disabled || loading || isListening) return;

    stopPlayback('cancelled');
    const voiceSessionId = createVoiceId('voice');
    voiceSessionIdRef.current = voiceSessionId;
    setErrorText(undefined);
    setInterimTranscript('');
    setPanelState('requesting_permission');
    onVoiceCaptureStatus?.('requesting_permission', {
      sessionId: voiceSessionId,
    });

    try {
      const handle = await voiceInputAdapter.start({
        onPermissionGranted: () => {
          setPanelState('recording');
          onVoiceCaptureStatus?.('recording', { sessionId: voiceSessionId });
        },
        onInterimTranscript: (text) => {
          setPanelState('transcribing');
          setInterimTranscript(text);
          onVoiceCaptureStatus?.('transcribing', { sessionId: voiceSessionId });
        },
        onFinalTranscript: (text) => {
          const nextText = text.trim();
          if (nextText) {
            onDraftChange(nextText);
          }
          setInterimTranscript('');
          setPanelState('awaiting_confirmation');
          onVoiceCaptureStatus?.('completed', { sessionId: voiceSessionId });
          captureHandleRef.current?.stop();
          captureHandleRef.current = null;
        },
        onError: (message) => {
          setPanelState('failed');
          setErrorText(message);
          onVoiceCaptureStatus?.('failed', {
            sessionId: voiceSessionId,
            error: message,
          });
          captureHandleRef.current?.stop();
          captureHandleRef.current = null;
        },
      });
      captureHandleRef.current = handle;
      setPanelState((current) =>
        current === 'requesting_permission' ? 'recording' : current,
      );
    } catch (err) {
      const message = err instanceof Error ? err.message : '语音输入启动失败';
      setPanelState('failed');
      setErrorText(message);
      onVoiceCaptureStatus?.('failed', {
        sessionId: voiceSessionId,
        error: message,
      });
      captureHandleRef.current = null;
    }
  }, [
    disabled,
    isListening,
    loading,
    onDraftChange,
    onVoiceCaptureStatus,
    stopPlayback,
    voiceInputAdapter,
    voiceSupported,
  ]);

  const sendVoiceMessage = useCallback(async () => {
    const content = draftText.trim();
    if (!content || disabled || loading) return;

    setPanelState('sending');
    const voiceSessionId = voiceSessionIdRef.current ?? createVoiceId('voice');
    voiceSessionIdRef.current = voiceSessionId;
    const metadata = {
      inputMode: 'voice',
      voiceSessionId,
      asrProvider: 'browser',
      asrModel: 'web-speech',
      language: VOICE_LANG,
    };

    await onSend(content, metadata);
    onDraftChange('');
    setPanelState('idle');
    setErrorText(undefined);
    onVoiceCaptureStatus?.('completed', { sessionId: voiceSessionId });
  }, [
    disabled,
    draftText,
    loading,
    onDraftChange,
    onSend,
    onVoiceCaptureStatus,
  ]);

  const speakLatestAnswer = useCallback(() => {
    const text = latestAssistantText.trim();
    if (!text || !canSpeak) return;

    stopCapture('cancelled');
    stopPlayback('cancelled');
    const deliveryId = createVoiceId('voice-out');
    deliveryIdRef.current = deliveryId;
    setPlaybackError(undefined);
    onVoicePlaybackStatus?.('synthesizing', { deliveryId });

    try {
      const handle = voiceOutputAdapter.speak(text, {
        lang: VOICE_LANG,
        onStart: () => {
          setPlaybackActive(true);
          onVoicePlaybackStatus?.('playing', { deliveryId });
        },
        onComplete: () => {
          playbackHandleRef.current = null;
          setPlaybackActive(false);
          onVoicePlaybackStatus?.('completed', { deliveryId });
        },
        onError: (message) => {
          playbackHandleRef.current = null;
          setPlaybackActive(false);
          setPlaybackError(message);
          onVoicePlaybackStatus?.('failed', { deliveryId, error: message });
        },
      });
      playbackHandleRef.current = handle;
    } catch (err) {
      const message = err instanceof Error ? err.message : '语音朗读启动失败';
      setPlaybackActive(false);
      setPlaybackError(message);
      onVoicePlaybackStatus?.('failed', { deliveryId, error: message });
    }
  }, [
    canSpeak,
    latestAssistantText,
    onVoicePlaybackStatus,
    stopCapture,
    stopPlayback,
    voiceOutputAdapter,
  ]);

  useEffect(
    () => () => {
      captureHandleRef.current?.stop();
      playbackHandleRef.current?.stop();
    },
    [],
  );

  if (variant === 'composer') {
    return (
      <div
        className={styles.voiceComposer}
        data-state={panelState}
        data-testid="voice-conversation-panel"
      >
        <div
          className={`${styles.voiceTranscriptBox} ${styles.voiceComposerTranscriptBox}`}
        >
          <textarea
            className={`${styles.composerTextarea} ${styles.voiceComposerTextarea}`}
            value={visibleTranscript}
            disabled={disabled || loading || isListening}
            placeholder={
              voiceSupported
                ? '开始语音输入后说话，转写结果会在这里等待确认。'
                : '当前浏览器不支持语音输入。'
            }
            aria-label="语音转写草稿"
            onChange={(event) => {
              onDraftChange(event.target.value);
              setPanelState(
                event.target.value.trim() ? 'awaiting_confirmation' : 'idle',
              );
            }}
          />
          {(errorText || playbackError) && (
            <div className={styles.voiceErrorText}>
              {errorText || playbackError}
            </div>
          )}
        </div>

        <div className={styles.composerToolbar}>
          <div className={styles.composerToolbarLeft}>
            <Tooltip title="返回键盘输入">
              <button
                type="button"
                className={styles.composerToolbarButton}
                aria-label="返回键盘输入"
                onClick={() => {
                  stopCapture('cancelled');
                  stopPlayback('cancelled');
                  onExit?.();
                }}
              >
                <CloseOutlined />
              </button>
            </Tooltip>
            <span
              className={styles.voiceComposerStatus}
              role="status"
              aria-live="polite"
            >
              {stateLabel[panelState]}
            </span>
          </div>

          <div
            className={styles.composerToolbarRight}
            data-testid="composer-action-area"
          >
            <Tooltip title={isListening ? '停止收音' : '开始收音'}>
              <button
                type="button"
                className={styles.composerToolbarButton}
                data-active={isListening ? 'true' : undefined}
                disabled={!voiceSupported || disabled || loading}
                aria-label={isListening ? '停止收音' : '开始语音会话'}
                onClick={
                  isListening ? () => stopCapture('cancelled') : startCapture
                }
              >
                {isListening ? <PauseOutlined /> : <AudioOutlined />}
              </button>
            </Tooltip>
            <Tooltip title="重录">
              <button
                type="button"
                className={styles.composerToolbarButton}
                aria-label="重录"
                disabled={!voiceSupported || disabled || loading}
                onClick={() => {
                  onDraftChange('');
                  void startCapture();
                }}
              >
                <RedoOutlined />
              </button>
            </Tooltip>
            <Tooltip title={playbackActive ? '停止朗读' : '朗读最新回复'}>
              <button
                type="button"
                className={styles.composerToolbarButton}
                aria-label={playbackActive ? '停止朗读' : '朗读最新回复'}
                disabled={!canSpeak}
                onClick={
                  playbackActive
                    ? () => stopPlayback('cancelled')
                    : speakLatestAnswer
                }
              >
                <CustomerServiceOutlined />
              </button>
            </Tooltip>
            <Tooltip title="发送语音内容">
              <button
                type="button"
                className={styles.composerSendButton}
                aria-label="发送语音内容"
                disabled={!canSend}
                data-loading={panelState === 'sending' ? 'true' : undefined}
                onClick={() => {
                  void sendVoiceMessage();
                }}
              >
                <SendOutlined />
              </button>
            </Tooltip>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div
      className={styles.voicePanel}
      data-state={panelState}
      data-testid="voice-conversation-panel"
    >
      <div className={styles.voicePanelHeader}>
        <div>
          <div className={styles.voicePanelTitle}>语音会话</div>
          <div className={styles.voicePanelSubtitle}>
            {voiceSupported
              ? 'Agent 可以通过当前浏览器听见你；转写会等待确认。'
              : '当前浏览器不支持语音输入。'}
          </div>
        </div>
        <span
          className={styles.voicePanelState}
          role="status"
          aria-live="polite"
        >
          {stateLabel[panelState]}
        </span>
      </div>

      <div className={styles.voicePanelBody}>
        <Tooltip title={isListening ? '停止收音' : '开始收音'}>
          <button
            type="button"
            className={styles.voicePrimaryControl}
            data-active={isListening ? 'true' : undefined}
            disabled={!voiceSupported || disabled || loading}
            aria-label={isListening ? '停止收音' : '开始语音会话'}
            onClick={
              isListening ? () => stopCapture('cancelled') : startCapture
            }
          >
            {isListening ? <PauseOutlined /> : <AudioOutlined />}
          </button>
        </Tooltip>

        <div className={styles.voiceTranscriptBox}>
          <textarea
            className={styles.voiceTranscriptTextarea}
            value={visibleTranscript}
            disabled={disabled || loading || isListening}
            placeholder="开始语音会话后说话，转写结果会在这里等待确认。"
            aria-label="语音转写草稿"
            onChange={(event) => {
              onDraftChange(event.target.value);
              setPanelState(
                event.target.value.trim() ? 'awaiting_confirmation' : 'idle',
              );
            }}
          />
          {(errorText || playbackError) && (
            <div className={styles.voiceErrorText}>
              {errorText || playbackError}
            </div>
          )}
        </div>
      </div>

      <div className={styles.voicePanelActions}>
        <Button
          size="small"
          icon={<RedoOutlined />}
          aria-label="重录"
          disabled={!voiceSupported || disabled || loading}
          onClick={() => {
            onDraftChange('');
            void startCapture();
          }}
        >
          重录
        </Button>
        <Button
          size="small"
          icon={<CustomerServiceOutlined />}
          aria-label={playbackActive ? '停止朗读' : '朗读最新回复'}
          disabled={!canSpeak}
          onClick={
            playbackActive ? () => stopPlayback('cancelled') : speakLatestAnswer
          }
        >
          {playbackActive ? '停止朗读' : '朗读最新回复'}
        </Button>
        <Button
          type="primary"
          size="small"
          icon={<SendOutlined />}
          aria-label="发送语音内容"
          disabled={!canSend}
          loading={panelState === 'sending'}
          onClick={() => {
            void sendVoiceMessage();
          }}
        >
          发送语音内容
        </Button>
      </div>
    </div>
  );
};

export default VoiceConversationPanel;
