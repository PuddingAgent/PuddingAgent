import { CameraOutlined, CloseOutlined, SendOutlined } from '@ant-design/icons';
import { Alert, Button, Input, Modal, Space } from 'antd';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  uploadVisionArtifact,
  type VisionArtifactUploadResponse,
} from '@/services/platform/api';
import {
  type BrowserCameraInputAdapter,
  type BrowserCameraInputHandle,
  defaultBrowserCameraInputAdapter,
} from '../hooks/browserCameraInput';
import {
  type CameraCaptureSession,
  reduceCameraCaptureSession,
  toCameraMessageDraft,
} from '../hooks/cameraCaptureSession';
import { useChatStyles } from '../styles';

export type UploadVisionArtifactFn = (
  workspaceId: string,
  file: Blob,
  metadata?: { width?: number; height?: number; capturedAt?: number },
  signal?: AbortSignal,
) => Promise<VisionArtifactUploadResponse>;

interface CameraInputModalProps {
  open: boolean;
  workspaceId?: string;
  disabled?: boolean;
  initialPrompt?: string;
  cameraInputAdapter?: BrowserCameraInputAdapter;
  uploadArtifact?: UploadVisionArtifactFn;
  onCancel: () => void;
  onSend: (
    content: string,
    metadata: Record<string, string>,
  ) => Promise<void> | void;
}

const createCameraSessionId = () =>
  `camera-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;

const CAMERA_STATUS_LABEL: Record<string, string> = {
  idle: '摄像头未启动',
  requesting_permission: '正在请求摄像头权限',
  ready: '摄像头已就绪',
  previewing: '正在预览',
  capturing: '正在捕获画面',
  awaiting_confirmation: '截图已准备好',
  sending: '正在发送视觉请求',
  completed: '已发送',
  cancelled: '已取消',
  failed: '摄像头不可用',
};

const CameraInputModal: React.FC<CameraInputModalProps> = ({
  open,
  workspaceId,
  disabled = false,
  initialPrompt = '',
  cameraInputAdapter = defaultBrowserCameraInputAdapter,
  uploadArtifact = uploadVisionArtifact,
  onCancel,
  onSend,
}) => {
  const { styles } = useChatStyles();
  const videoRef = useRef<HTMLVideoElement>(null);
  const cameraHandleRef = useRef<BrowserCameraInputHandle | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const [session, setSession] = useState<CameraCaptureSession | undefined>();
  const [prompt, setPrompt] = useState('');
  const [previewUrl, setPreviewUrl] = useState<string | undefined>();

  const supported = cameraInputAdapter.isSupported();
  const busy =
    session?.status === 'requesting_permission' ||
    session?.status === 'capturing' ||
    session?.status === 'sending';
  const canCapture =
    supported && !disabled && !!workspaceId && session?.status === 'previewing';
  const canSend =
    supported &&
    !disabled &&
    !!workspaceId &&
    session?.status === 'awaiting_confirmation';

  const dispatchCameraEvent = useCallback(
    (event: Parameters<typeof reduceCameraCaptureSession>[1]) => {
      setSession((current) => reduceCameraCaptureSession(current, event));
    },
    [],
  );

  const stopCamera = useCallback(() => {
    abortRef.current?.abort();
    abortRef.current = null;
    cameraHandleRef.current?.stop();
    cameraHandleRef.current = null;
    if (videoRef.current) {
      videoRef.current.srcObject = null;
    }
  }, []);

  useEffect(() => {
    if (!open) {
      stopCamera();
      setSession(undefined);
      setPrompt('');
      setPreviewUrl(undefined);
      return undefined;
    }

    const sessionId = createCameraSessionId();
    setPrompt(initialPrompt.trim() || '请分析这张图像。');
    setPreviewUrl(undefined);
    dispatchCameraEvent({
      type: 'permission_requested',
      sessionId,
      workspaceId: workspaceId || 'unknown',
      roomId: 'chat',
      participantId: 'local-user',
    });

    if (!workspaceId) {
      dispatchCameraEvent({
        type: 'fail',
        sessionId,
        error: '请选择工作空间后再使用摄像头',
      });
      return () => stopCamera();
    }

    if (!supported) {
      dispatchCameraEvent({
        type: 'fail',
        sessionId,
        error: '当前浏览器不支持摄像头输入',
      });
      return () => stopCamera();
    }

    let alive = true;
    void (async () => {
      try {
        const handle = await cameraInputAdapter.startPreview();
        if (!alive) {
          handle.stop();
          return;
        }

        cameraHandleRef.current = handle;
        dispatchCameraEvent({
          type: 'permission_granted',
          sessionId,
          deviceLabel: handle.deviceLabel,
        });

        const video = videoRef.current;
        if (video) {
          video.srcObject = handle.stream;
          await video.play().catch(() => undefined);
        }

        const settings =
          handle.stream.getVideoTracks()[0]?.getSettings?.() ?? {};
        dispatchCameraEvent({
          type: 'preview_started',
          sessionId,
          width: video?.videoWidth || settings.width,
          height: video?.videoHeight || settings.height,
        });
      } catch (err) {
        if (!alive) return;
        dispatchCameraEvent({
          type: 'permission_denied',
          sessionId,
          error: err instanceof Error ? err.message : '摄像头权限被拒绝',
        });
      }
    })();

    return () => {
      alive = false;
      stopCamera();
    };
  }, [
    cameraInputAdapter,
    dispatchCameraEvent,
    initialPrompt,
    open,
    stopCamera,
    supported,
    workspaceId,
  ]);

  useEffect(
    () => () => {
      if (previewUrl) URL.revokeObjectURL(previewUrl);
    },
    [previewUrl],
  );

  const handleCapture = useCallback(async () => {
    const video = videoRef.current;
    const handle = cameraHandleRef.current;
    if (!session || !workspaceId || !video || !handle || !canCapture) return;

    if (previewUrl) {
      URL.revokeObjectURL(previewUrl);
      setPreviewUrl(undefined);
    }

    dispatchCameraEvent({
      type: 'snapshot_requested',
      sessionId: session.sessionId,
      prompt,
    });

    const ctrl = new AbortController();
    abortRef.current = ctrl;

    try {
      const frame = await handle.captureFrame(video);
      setPreviewUrl(URL.createObjectURL(frame.blob));
      const uploaded = await uploadArtifact(
        workspaceId,
        frame.blob,
        {
          width: frame.width,
          height: frame.height,
          capturedAt: frame.capturedAt,
        },
        ctrl.signal,
      );

      dispatchCameraEvent({
        type: 'snapshot_captured',
        sessionId: session.sessionId,
        artifactId: uploaded.artifactId,
        mimeType: uploaded.mimeType || frame.mimeType,
        width: uploaded.width || frame.width,
        height: uploaded.height || frame.height,
        capturedAt: uploaded.capturedAt || frame.capturedAt,
      });
    } catch (err) {
      if (err instanceof Error && err.name === 'AbortError') return;
      dispatchCameraEvent({
        type: 'fail',
        sessionId: session.sessionId,
        error: err instanceof Error ? err.message : '摄像头截图上传失败',
      });
    } finally {
      if (abortRef.current === ctrl) abortRef.current = null;
    }
  }, [
    canCapture,
    dispatchCameraEvent,
    previewUrl,
    prompt,
    session,
    uploadArtifact,
    workspaceId,
  ]);

  const handleSend = useCallback(async () => {
    if (!session || !canSend) return;
    const readySession: CameraCaptureSession = { ...session, prompt };
    dispatchCameraEvent({ type: 'confirm_send', sessionId: session.sessionId });
    try {
      const draft = toCameraMessageDraft(readySession);
      await onSend(draft.content, draft.metadata);
      dispatchCameraEvent({ type: 'sent', sessionId: session.sessionId });
      stopCamera();
      onCancel();
    } catch (err) {
      dispatchCameraEvent({
        type: 'fail',
        sessionId: session.sessionId,
        error: err instanceof Error ? err.message : '视觉请求发送失败',
      });
    }
  }, [
    canSend,
    dispatchCameraEvent,
    onCancel,
    onSend,
    prompt,
    session,
    stopCamera,
  ]);

  const handleCancel = useCallback(() => {
    if (session)
      dispatchCameraEvent({ type: 'cancel', sessionId: session.sessionId });
    stopCamera();
    onCancel();
  }, [dispatchCameraEvent, onCancel, session, stopCamera]);

  const statusLabel = useMemo(
    () => CAMERA_STATUS_LABEL[session?.status || 'idle'] || '摄像头状态更新',
    [session?.status],
  );

  return (
    <Modal
      title="摄像头视觉输入"
      open={open}
      onCancel={handleCancel}
      footer={null}
      width={560}
      destroyOnHidden
    >
      <div className={styles.cameraInputModalBody}>
        <div
          className={styles.cameraInputStatusRow}
          data-status={session?.status}
        >
          <span className={styles.cameraInputStatusDot} />
          <span>{statusLabel}</span>
          {session?.width && session?.height ? (
            <span className={styles.cameraInputResolution}>
              {session.width}x{session.height}
            </span>
          ) : null}
        </div>

        {session?.status === 'failed' && session.error ? (
          <Alert type="warning" showIcon message={session.error} />
        ) : null}

        <div className={styles.cameraInputPreviewFrame}>
          {previewUrl ? (
            <img
              src={previewUrl}
              alt="摄像头截图预览"
              className={styles.cameraInputPreviewMedia}
            />
          ) : (
            <video
              ref={videoRef}
              className={styles.cameraInputPreviewMedia}
              muted
              playsInline
              autoPlay
            />
          )}
        </div>

        <Input.TextArea
          value={prompt}
          onChange={(event) => setPrompt(event.target.value)}
          placeholder="给这张图像补充问题，例如：请分析屏幕里的错误。"
          autoSize={{ minRows: 2, maxRows: 4 }}
          maxLength={300}
          disabled={busy}
        />

        <div className={styles.cameraInputActionRow}>
          <Button icon={<CloseOutlined />} onClick={handleCancel}>
            取消
          </Button>
          <Space size={8}>
            <Button
              icon={<CameraOutlined />}
              onClick={handleCapture}
              loading={session?.status === 'capturing'}
              disabled={!canCapture}
            >
              截取画面
            </Button>
            <Button
              type="primary"
              icon={<SendOutlined />}
              onClick={handleSend}
              loading={session?.status === 'sending'}
              disabled={!canSend}
            >
              发送图像
            </Button>
          </Space>
        </div>
      </div>
    </Modal>
  );
};

export default CameraInputModal;
