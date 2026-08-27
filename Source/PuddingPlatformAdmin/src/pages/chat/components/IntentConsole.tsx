// ── InputArea：安静胶囊 Composer + 轻反馈带 ────────
import {
  AudioOutlined,
  DeleteOutlined,
  DownOutlined,
  LoadingOutlined,
  PlusOutlined,
  SendOutlined,
  SettingOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { message, Popover, Tooltip } from 'antd';
import React, { useCallback, useRef, useState } from 'react';
import {
  type CacheDiagnosticsReport,
  type ContextHealthSnapshot,
  getCacheDiagnostics,
  getContextHealth,
  uploadVisionArtifact,
} from '@/services/platform/api';
import {
  type BrowserCameraInputAdapter,
  defaultBrowserCameraInputAdapter,
} from '../hooks/browserCameraInput';
import type {
  BrowserVoiceInputAdapter,
  BrowserVoiceInputHandle,
} from '../hooks/browserVoiceInput';
import {
  type BrowserVoiceOutputAdapter,
  defaultBrowserVoiceOutputAdapter,
} from '../hooks/browserVoiceOutput';
import { createDashScopeVoiceInputAdapter } from '../hooks/dashScopeVoiceInput';
import type { ChatInteractionQueueItem } from '../hooks/useChatState';
import type { AutoReviewClassifierState } from '../classifier/autoReviewClassifier';
import type { RecentlyDeniedItem } from '../classifier/autoReviewClassifier';
import type { SandboxBoundaryInfo, SandboxNetworkMode } from '../sandbox/sandboxBoundary';
import { useChatStyles } from '../styles';
import type { PermissionMode } from '../types/chatStateTypes';
import AutoReviewIndicator from './AutoReviewIndicator';
import ComposerTextInput, {
  type ComposerTextInputHandle,
} from './ComposerTextInput';
import ComposerActionMenu from './ComposerActionMenu';
import ComposerContextBar from './ComposerContextBar';
import ComposerFeedbackStrip, {
  type FeedbackState,
} from './ComposerFeedbackStrip';
import ComposerStatusDetails, {
  type ComposerRuntimeSummary,
} from './ComposerStatusDetails';
import PermissionModeSelector from './PermissionModeSelector';
import MessageQueueDropdown from './MessageQueueDropdown';
import SandboxBoundaryIndicator from './SandboxBoundaryIndicator';
import { normalizeVisionArtifactFile } from './visionArtifactImage';

const CameraInputModal =
  process.env.NODE_ENV === 'test'
    ? (require('./CameraInputModal')
        .default as typeof import('./CameraInputModal').default)
    : React.lazy(() => import('./CameraInputModal'));

/** Composer 的聊天状态 */
export type ChatStatus =
  | 'idle'
  | 'initializing'
  | 'composing'
  | 'thinking'
  | 'tool_executing'
  | 'streaming'
  | 'completed'
  | 'error';

/** chatState → 自然语言状态文案 */
const STATUS_LABEL: Record<ChatStatus, string> = {
  idle: '',
  initializing: '· 正在连接…',
  composing: '',
  thinking: '· 正在整理上下文…',
  tool_executing: '· 正在调用工具…',
  streaming: '· 正在生成回复…',
  completed: '· 已完成',
  error: '· 出错了，可重试',
};

const getRequestErrorMessage = (error: unknown, fallback: string): string => {
  if (error && typeof error === 'object') {
    const data =
      'data' in error ? (error as { data?: unknown }).data : undefined;
    if (data && typeof data === 'object' && 'message' in data) {
      const message = (data as { message?: unknown }).message;
      if (typeof message === 'string' && message.trim()) return message;
    }

    if ('message' in error) {
      const message = (error as { message?: unknown }).message;
      if (typeof message === 'string' && message.trim()) return message;
    }
  }

  return fallback;
};

/** 读取图片尺寸；解析失败或非图片时返回 undefined */
const readImageDimensions = (
  file: File,
): Promise<{ width: number; height: number } | undefined> =>
  new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      resolve({ width: img.naturalWidth, height: img.naturalHeight });
      URL.revokeObjectURL(url);
    };
    img.onerror = () => {
      resolve(undefined);
      URL.revokeObjectURL(url);
    };
    img.src = url;
  });

interface PendingComposerImage {
  id: string;
  file: File;
  previewUrl: string;
}

const MAX_PENDING_IMAGES = 8;

interface IntentConsoleProps {
  inputValue: string;
  onInputChange: (v: string) => void;
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  loading: boolean;
  interactionQueue?: ChatInteractionQueueItem[];
  onUpdateQueuedInteraction?: (id: string, text: string) => void;
  onDeleteQueuedInteraction?: (id: string) => void;
    onSendQueuedInteractionNow?: (id: string) => Promise<void>;
  onSteerQueuedInteraction?: (id: string) => Promise<void>;
  /** P1#6：本地待发队列拖拽重排 */
  onReorderQueuedInteraction?: (fromId: string, toId: string) => void;
  /** P1#6：取消全部（中止当前请求 + 清空待发队列） */
  onStopAll?: () => void;
  onSend: () => void;
  onSendWithMetadata?: (
    content: string,
    metadata: Record<string, string>,
    imageParts?: { type: 'image'; artifactId: string; detail?: 'original' | 'low' }[],
  ) => Promise<void> | void;
  onStop: () => void;
  onExport: () => void;
  onOpenDevDetails?: () => void;
  disabled: boolean;
  tLimit: number;
  tUsed: number;
  tPct: number;
  status: ChatStatus;
  sessionId?: string | null;
  workspaceId?: string;
  cacheHitTokens?: number;
  cacheMissTokens?: number;
  cacheHitRate?: number;
  /** 来自 useCompaction hook 的压缩状态文案（如 "上次压缩: 2分钟前"） */
  compactionStatus?: string | null;
  /** 当前会话可见的子任务数 */
  subAgentsRunning?: number;
  /** 打开 ChatMain 持有的固定子代理运行检查器。 */
  onOpenSubAgentInspector?: () => void;
  /** 浏览器语音输入适配器；测试与后续 ASR Provider 接入可替换该适配器 */
  voiceInputAdapter?: BrowserVoiceInputAdapter;
  /** 浏览器语音输出适配器；测试与后续 TTS Provider 接入可替换该适配器 */
  voiceOutputAdapter?: BrowserVoiceOutputAdapter;
  /** 当前会话最新可朗读的助手回复文本。 */
  latestAssistantText?: string;
  /** 将本地语音输入状态投影到聊天运行时，例如头像/状态栏。 */
  onVoiceCaptureStatus?: (
    status: string,
    detail?: { sessionId?: string; error?: string },
  ) => void;
  /** 将本地语音输出状态投影到聊天运行时，例如头像/状态栏。 */
  onVoicePlaybackStatus?: (
    status: string,
    detail?: { deliveryId?: string; error?: string },
  ) => void;
  /** 浏览器摄像头输入适配器；测试与后续视频流 Provider 接入可替换该适配器 */
  cameraInputAdapter?: BrowserCameraInputAdapter;
    /** P1#4：权限模式（全局状态，经 ChatLayout → ChatMain 下传） */
  permissionMode?: PermissionMode;
  /** P1#4：权限模式变更回调 */
  onPermissionModeChange?: (mode: PermissionMode) => void;
  /** P2#9：Auto-review classifier 状态（回退手动审批指示器） */
  autoReviewState?: AutoReviewClassifierState;
  /** P2#9：Recently denied 面板条目 */
  recentlyDenied?: RecentlyDeniedItem[];
  /** P2#9：恢复自动模式（重置 blocked 计数） */
  onAutoReviewRestore?: () => void;
  /** P2#9：重试 Recently denied 条目 */
  onRetryDenied?: (item: RecentlyDeniedItem) => void;
  /** P2#9：移除 Recently denied 条目 */
  onRemoveDenied?: (id: string) => void;
  /** P2#9：清空 Recently denied */
  onClearDenied?: () => void;
  /** P2#10：Sandbox 边界信息 */
  sandboxBoundary?: SandboxBoundaryInfo | null;
  /** P2#10：网络模式变更回调 */
  onSandboxNetworkModeChange?: (mode: SandboxNetworkMode) => void;
}

const IntentConsole: React.FC<IntentConsoleProps> = ({
  inputValue,
  onInputChange,
  onKeyDown,
  loading,
  interactionQueue = [],
  onUpdateQueuedInteraction,
  onDeleteQueuedInteraction,
    onSendQueuedInteractionNow,
  onSteerQueuedInteraction,
  onReorderQueuedInteraction,
  onStopAll,
  onSend,
  onSendWithMetadata,
  onStop,
  onExport,
  onOpenDevDetails,
  disabled,
  tLimit,
  tUsed,
  tPct,
  status,
  sessionId,
  cacheHitTokens,
  cacheMissTokens,
  cacheHitRate,
  compactionStatus,
  subAgentsRunning = 0,
  onOpenSubAgentInspector,
  voiceInputAdapter = createDashScopeVoiceInputAdapter(),
  voiceOutputAdapter = defaultBrowserVoiceOutputAdapter,
  latestAssistantText,
  onVoiceCaptureStatus,
  onVoicePlaybackStatus,
  cameraInputAdapter = defaultBrowserCameraInputAdapter,
    workspaceId,
  permissionMode = 'auto',
  onPermissionModeChange = () => undefined,
  autoReviewState,
  recentlyDenied = [],
  onAutoReviewRestore,
  onRetryDenied,
  onRemoveDenied,
  onClearDenied,
  sandboxBoundary,
  onSandboxNetworkModeChange,
}) => {
  const { styles } = useChatStyles();
  const textAreaRef = useRef<HTMLTextAreaElement>(null);
  /** 输入叶子组件句柄（草稿态/IME 守卫/命令面板已下沉，外部改写走 setValue） */
  const textInputRef = useRef<ComposerTextInputHandle | null>(null);
  const handleComposerSendRef = useRef<() => void>(() => undefined);
  /** `+` 动作菜单 Popover */
  const [showComposerMenu, setShowComposerMenu] = useState(false);
  /** 运行状态详情 Popover */
  const [showStatusDetails, setShowStatusDetails] = useState(false);
  const [contextHealth, setContextHealth] =
    useState<ContextHealthSnapshot | null>(null);
  const [contextHealthLoading, setContextHealthLoading] = useState(false);
  const [contextHealthError, setContextHealthError] = useState<string | null>(
    null,
  );
  const [cacheDiagnostics, setCacheDiagnostics] =
    useState<CacheDiagnosticsReport | null>(null);
  /** 摄像头视觉输入弹窗 */
  const [showCameraInput, setShowCameraInput] = useState(false);
  /** 输入交互模式：键盘保留传统 composer，语音进入独立会话工作台。 */
  const [interactionMode, setInteractionMode] = useState<'keyboard' | 'voice'>(
    'keyboard',
  );
  /** Agent 执行偏好：仅影响前端显示，后续可接入后端策略。 */
  const [executionMode, setExecutionMode] = useState<'auto' | 'deep' | 'fast'>(
    'auto',
  );
  /** 已完成状态短暂显示后自动消失 */
  const [completedVisible, setCompletedVisible] = useState(false);
  /** Composer 输入容器焦点状态 */
  const [composerFocused, setComposerFocused] = useState(false);
  /** 输入是否有文本（叶子组件仅空↔非空翻转时上报；替代逐键 draftValue 门控） */
  const [composerHasText, setComposerHasText] = useState(
    inputValue.trim().length > 0,
  );
  /** 容器是否处于 active（focus 或 非空输入 或 正在录音） */
  const [recording, setRecording] = useState(false);
  const [recognizing, setRecognizing] = useState(false);
  const voiceHandleRef = useRef<BrowserVoiceInputHandle | null>(null);
  /** 图片上传隐藏文件选择器 */
  const imageFileInputRef = useRef<HTMLInputElement>(null);
  /** 图片上传进行中 */
  const [imageUploading, setImageUploading] = useState(false);
  /** 等待用户确认发送的本地图片；选择、粘贴、拖拽都只进入这里。 */
  const [pendingImages, setPendingImages] = useState<PendingComposerImage[]>(
    [],
  );
  const pendingImagesRef = useRef<PendingComposerImage[]>([]);
  /** 拖拽悬停高亮 */
  const [imageDragActive, setImageDragActive] = useState(false);
  const dragDepthRef = useRef(0);

  const refreshContextHealth = useCallback(async () => {
    if (!sessionId) return;
    setContextHealthLoading(true);
    setContextHealthError(null);
    try {
      const [contextResult, cacheResult] = await Promise.allSettled([
        getContextHealth(sessionId),
        getCacheDiagnostics(sessionId),
      ]);

      if (contextResult.status === 'fulfilled') {
        setContextHealth(contextResult.value);
      } else {
        setContextHealth(null);
        setContextHealthError(
          getRequestErrorMessage(contextResult.reason, '上下文窗口刷新失败'),
        );
      }

      if (cacheResult.status === 'fulfilled') {
        setCacheDiagnostics(cacheResult.value);
      }
    } catch (error) {
      setContextHealth(null);
      setContextHealthError(
        getRequestErrorMessage(error, '上下文窗口刷新失败'),
      );
    } finally {
      setContextHealthLoading(false);
    }
  }, [sessionId]);

  const handleStatusDetailsOpenChange = useCallback(
    (open: boolean) => {
      setShowStatusDetails(open);
      if (open) void refreshContextHealth();
    },
    [refreshContextHealth],
  );
  const voiceAdapterRef = useRef(createDashScopeVoiceInputAdapter());
  const composerActive =
    composerFocused ||
    composerHasText ||
    pendingImages.length > 0 ||
    recording;
  const cameraSupported = cameraInputAdapter.isSupported();
  const cameraEnabled = Boolean(
    cameraSupported &&
      workspaceId &&
      onSendWithMetadata &&
      !disabled &&
      !loading,
  );
  /** 图片上传可用条件：工作空间 + 发送通道 + 非禁用/生成中 */
  const imageEnabled = Boolean(
    workspaceId && onSendWithMetadata && !disabled && !loading,
  );
  const executionModeLabel =
    executionMode === 'deep'
      ? '深入'
      : executionMode === 'fast'
        ? '快速'
        : '自动';
  const uiTestMode =
    typeof window !== 'undefined' &&
    new URLSearchParams(window.location.search).get('uiTest') === '1';

  // 当 status 变为 completed 时，短暂显示后自动隐藏
  React.useEffect(() => {
    if (status === 'completed') {
      setCompletedVisible(true);
      const timer = setTimeout(() => setCompletedVisible(false), 2000);
      return () => clearTimeout(timer);
    }
    if (
      status === 'thinking' ||
      status === 'tool_executing' ||
      status === 'streaming' ||
      status === 'error'
    ) {
      setCompletedVisible(false);
    }
    return undefined;
  }, [status]);

  React.useEffect(() => {
    if (composerFocused || composerHasText) {
      setCompletedVisible(false);
    }
  }, [composerFocused, composerHasText]);

  React.useEffect(() => {
    pendingImagesRef.current = pendingImages;
  }, [pendingImages]);

  React.useEffect(
    () => () => {
      pendingImagesRef.current.forEach((item) =>
        URL.revokeObjectURL(item.previewUrl),
      );
    },
    [],
  );

  // 输入/组合/命令面板键盘导航已下沉 ComposerTextInput 叶子组件（按键只重渲染叶子）。
  // IntentConsole 仅接收低频事件：focus 变化与 hasText 空↔非空翻转。
  const handleTextAreaFocusChange = useCallback((focused: boolean) => {
    setComposerFocused(focused);
  }, []);
  const handleHasTextChange = useCallback((hasText: boolean) => {
    setComposerHasText(hasText);
  }, []);
  /** Enter+待发图片：走图片上传发送流（叶子内消费按键）。 */
  const handleEnterWithImages = useCallback(() => {
    handleComposerSendRef.current();
  }, []);
  const handleFillUiTestGreeting = useCallback(() => {
    setCompletedVisible(false);
    textInputRef.current?.setValue('你好');
    requestAnimationFrame(() => textAreaRef.current?.focus());
  }, []);

  const handleCameraSend = useCallback(
    async (content: string, metadata: Record<string, string>) => {
      if (!onSendWithMetadata) return;
      await onSendWithMetadata(content, metadata);
      textInputRef.current?.setValue('');
    },
    [onSendWithMetadata],
  );

  // ── 图片暂存与发送（菜单选择 / 粘贴 / 拖拽共用）──
  const stageImageFiles = useCallback((files: File[]) => {
    const images = files.filter((file) => file.type.startsWith('image/'));
    if (images.length !== files.length) message.warning('已忽略非图片文件');
    if (images.length === 0) return;

    setPendingImages((current) => {
      const available = Math.max(0, MAX_PENDING_IMAGES - current.length);
      if (images.length > available)
        message.warning(`每轮最多发送 ${MAX_PENDING_IMAGES} 张图片`);
      return [
        ...current,
        ...images.slice(0, available).map((file) => ({
          id: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
          file,
          previewUrl: URL.createObjectURL(file),
        })),
      ];
    });
  }, []);

  const handleRemovePendingImage = useCallback((id: string) => {
    setPendingImages((current) => {
      const removed = current.find((item) => item.id === id);
      if (removed) URL.revokeObjectURL(removed.previewUrl);
      return current.filter((item) => item.id !== id);
    });
  }, []);

  const handleComposerSend = useCallback(async () => {
    if (loading) {
      onStop();
      return;
    }
    if (pendingImages.length === 0) {
      onSend();
      return;
    }
    if (!workspaceId || !onSendWithMetadata || disabled || imageUploading)
      return;

    setImageUploading(true);
    try {
      const uploaded = await Promise.all(
        pendingImages.map(async (item) => {
          const dimensions = await readImageDimensions(item.file);
          const uploadFile = await normalizeVisionArtifactFile(item.file);
          const artifact = await uploadVisionArtifact(workspaceId, uploadFile, {
            width: dimensions?.width,
            height: dimensions?.height,
            capturedAt: Date.now(),
          });
          return {
            artifactId: artifact.artifactId,
            fileName: item.file.name,
            mimeType: artifact.mimeType || item.file.type,
            width: artifact.width ?? dimensions?.width,
            height: artifact.height ?? dimensions?.height,
          };
        }),
      );
      // ADR-077：图片事实以 typed content parts 提交；metadata 只保留投影事实。
      const imageParts = uploaded.map((item) => ({
        type: 'image' as const,
        artifactId: item.artifactId,
        detail: 'original' as const,
      }));
      const metadata: Record<string, string> = {
        inputMode: 'image',
        imageCount: String(uploaded.length),
      };
      const prompt =
        (textInputRef.current?.getValue() ?? '').trim() ||
        (uploaded.length > 1 ? '请分析这些图片。' : '请分析这张图片。');
      await onSendWithMetadata(prompt, metadata, imageParts);
      pendingImages.forEach((item) => URL.revokeObjectURL(item.previewUrl));
      setPendingImages([]);
      textInputRef.current?.setValue('');
    } catch (err) {
      message.error(getRequestErrorMessage(err, '图片上传失败'));
    } finally {
      setImageUploading(false);
    }
  }, [
    disabled,
    imageUploading,
    loading,
    onSend,
    onSendWithMetadata,
    onStop,
    pendingImages,
    workspaceId,
  ]);
  handleComposerSendRef.current = () => {
    void handleComposerSend();
  };

  const handleOpenImagePicker = useCallback(() => {
    imageFileInputRef.current?.click();
  }, []);

  const handleImageInputChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const files = Array.from(e.target.files ?? []);
      // 重置 value，允许连续选择同一文件
      e.target.value = '';
      stageImageFiles(files);
    },
    [stageImageFiles],
  );

  /** 粘贴图片（Ctrl+V）：优先检测剪贴板中的图片文件 */
  const handlePasteImage = useCallback(
    (e: React.ClipboardEvent) => {
      const files = Array.from(e.clipboardData?.files ?? []).filter((file) =>
        file.type.startsWith('image/'),
      );
      if (files.length > 0) {
        e.preventDefault();
        stageImageFiles(files);
      }
    },
    [stageImageFiles],
  );

  // ── 拖拽图片 ──
  const handleDragEnter = useCallback((e: React.DragEvent) => {
    if (!e.dataTransfer?.types?.includes('Files')) return;
    e.preventDefault();
    dragDepthRef.current += 1;
    setImageDragActive(true);
  }, []);

  const handleDragOver = useCallback((e: React.DragEvent) => {
    if (!e.dataTransfer?.types?.includes('Files')) return;
    e.preventDefault();
  }, []);

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    if (!e.dataTransfer?.types?.includes('Files')) return;
    e.preventDefault();
    dragDepthRef.current = Math.max(0, dragDepthRef.current - 1);
    if (dragDepthRef.current === 0) setImageDragActive(false);
  }, []);

  const handleDrop = useCallback(
    (e: React.DragEvent) => {
      if (!e.dataTransfer?.types?.includes('Files')) return;
      e.preventDefault();
      dragDepthRef.current = 0;
      setImageDragActive(false);
      stageImageFiles(Array.from(e.dataTransfer.files ?? []));
    },
    [stageImageFiles],
  );

  // ── Inline 语音录音 ──
  const handleToggleVoiceInput = useCallback(async () => {
    if (recording) {
      // 停止录音 → 识别
      voiceHandleRef.current?.stop();
      voiceHandleRef.current = null;
      setRecording(false);
      setRecognizing(true);
      return;
    }

    // 开始录音
    try {
      const adapter = voiceAdapterRef.current;
      if (!adapter.isSupported()) {
        alert('当前浏览器不支持语音输入');
        return;
      }

      let finalText = '';
      const handle = await adapter.start({
        onPermissionGranted: () => {
          setRecording(true);
        },
        onFinalTranscript: (text: string) => {
          finalText = text;
        },
        onError: (msg: string) => {
          console.error('Voice error:', msg);
        },
      });
      voiceHandleRef.current = handle;
      setRecording(true);

      // 监听 handle 的 stop — adapter 的 stop 是 async 的，但我们这里用同步 flag
      const origStop = handle.stop;
      handle.stop = () => {
        origStop.call(handle);
        // 延迟等待 finalText 被填充
        setTimeout(() => {
          setRecognizing(false);
          if (finalText.trim()) {
            textInputRef.current?.setValue(finalText);
          }
        }, 200);
      };
    } catch (err) {
      console.error('Voice start failed:', err);
      setRecording(false);
    }
  }, [recording, onInputChange]);

  /** 轻反馈带状态 */
  const refreshedContextPct = React.useMemo(() => {
    if (!contextHealth || contextHealth.effectiveWindowTokens <= 0)
      return undefined;
    const raw =
      contextHealth.usageRatio <= 1
        ? contextHealth.usageRatio * 100
        : contextHealth.usageRatio;
    return Math.max(0, Math.min(100, raw));
  }, [contextHealth]);

  const effectiveContextUsagePercentage =
    refreshedContextPct ?? (tLimit > 0 ? tPct : undefined);

  const feedbackState: FeedbackState = React.useMemo(
    () => ({
      context:
        status === 'thinking' ||
        status === 'tool_executing' ||
        status === 'streaming',
      contextUsagePercentage: effectiveContextUsagePercentage,
      contextLimitTokens:
        contextHealth?.contextWindowTokens ?? (tLimit > 0 ? tLimit : undefined),
      contextRemainingTokens:
        contextHealth?.remainingTokens ??
        (tLimit > 0 ? Math.max(tLimit - tUsed, 0) : undefined),
      memoryCount: 0,
      indexAvailable: false,
      subAgentsRunning,
      backgroundMemoryRunning: false,
    }),
    [
      status,
      subAgentsRunning,
      effectiveContextUsagePercentage,
      tLimit,
      tUsed,
      contextHealth,
    ],
  );

  /** 是否显示状态行 */
  const shouldShowStatus =
    status === 'thinking' ||
    status === 'tool_executing' ||
    status === 'streaming' ||
    status === 'error' ||
    (status === 'completed' && completedVisible);

  const displayStatusText =
    status === 'completed' && completedVisible
      ? STATUS_LABEL.completed
      : (STATUS_LABEL[status] ?? '');

  const refreshedContextToken =
    contextHealth && contextHealth.effectiveWindowTokens > 0
      ? {
          used: contextHealth.usedTokens,
          limit: contextHealth.contextWindowTokens,
          percentage: refreshedContextPct ?? 0,
          remaining: contextHealth.remainingTokens,
        }
      : undefined;

  const runtimeToken =
    refreshedContextToken ??
    (tLimit > 0 ? { used: tUsed, limit: tLimit, percentage: tPct } : undefined);

  const contextUsageStatus: ComposerRuntimeSummary['contextUsageStatus'] =
    contextHealthLoading
      ? 'loading'
      : contextHealthError
        ? 'error'
        : runtimeToken
          ? 'ready'
          : 'idle';

  const diagnosticsCacheHitRate = React.useMemo(() => {
    const rate = cacheDiagnostics?.averageCacheHitRate;
    if (rate === undefined || rate === null || !Number.isFinite(rate))
      return undefined;
    return rate <= 1 ? rate * 100 : rate;
  }, [cacheDiagnostics]);

  /** 组装运行摘要视图模型 */
  const runtimeSummary: ComposerRuntimeSummary = React.useMemo(
    () => ({
      status,
      statusLabel: displayStatusText,
      token: runtimeToken,
      contextUsageStatus,
      contextUsageError: contextHealthError ?? undefined,
      cacheHitRate: diagnosticsCacheHitRate ?? cacheHitRate,
      contextService: 'available',
      index: 'disabled',
      backgroundMemory: 'idle',
      subAgentsRunning,
      modelService: 'available',
    }),
    [
      status,
      displayStatusText,
      runtimeToken,
      contextUsageStatus,
      contextHealthError,
      diagnosticsCacheHitRate,
      cacheHitRate,
      subAgentsRunning,
    ],
    );

  return (
    <div
      className={`${styles.composerSurface} ${recording ? styles.composerRecording : ''}`}
      data-active={composerActive && !loading ? 'true' : undefined}
      data-error={status === 'error' ? 'true' : undefined}
      data-image-drag={imageDragActive ? 'true' : undefined}
      onDragEnter={handleDragEnter}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      {/* 隐藏的图片文件选择器：由 `+` 菜单触发 */}
      <input
        ref={imageFileInputRef}
        type="file"
        accept="image/*"
        multiple
        style={{ display: 'none' }}
        onChange={handleImageInputChange}
        aria-hidden="true"
        tabIndex={-1}
        data-testid="image-file-input"
      />

      <MessageQueueDropdown
        interactionQueue={interactionQueue}
        loading={loading}
        onUpdateQueuedInteraction={onUpdateQueuedInteraction}
        onDeleteQueuedInteraction={onDeleteQueuedInteraction}
        onSendQueuedInteractionNow={onSendQueuedInteractionNow}
        onSteerQueuedInteraction={onSteerQueuedInteraction}
        onReorderQueuedInteraction={onReorderQueuedInteraction}
        onStopAll={onStopAll}
      />

      {pendingImages.length > 0 && (
        <div
          className={styles.composerImagePreviewList}
          data-testid="image-preview-list"
          aria-label={`待发送图片 ${pendingImages.length} 张`}
        >
          {pendingImages.map((item, index) => (
            <div
              key={item.id}
              className={styles.composerImagePreviewItem}
              data-testid="image-preview-item"
            >
              <img
                src={item.previewUrl}
                alt={`待发送图片 ${index + 1}: ${item.file.name}`}
                className={styles.composerImagePreview}
              />
              <button
                type="button"
                className={styles.composerImageRemoveButton}
                onClick={() => handleRemovePendingImage(item.id)}
                aria-label={`移除图片 ${item.file.name}`}
                disabled={imageUploading}
              >
                <DeleteOutlined />
              </button>
            </div>
          ))}
        </div>
      )}

      <div className={styles.composerCapsuleBody}>
        <ComposerContextBar
          tLimit={contextHealth?.contextWindowTokens ?? tLimit}
          tUsed={contextHealth?.usedTokens ?? tUsed}
          tPct={effectiveContextUsagePercentage ?? 0}
          cacheHitTokens={cacheHitTokens}
          cacheMissTokens={cacheMissTokens}
          cacheHitRate={cacheHitRate}
          compactionStatus={compactionStatus}
        />
        <ComposerTextInput
          ref={textInputRef}
          inputValue={inputValue}
          onInputChange={onInputChange}
          onKeyDown={onKeyDown}
          onFocusChange={handleTextAreaFocusChange}
          onHasTextChange={handleHasTextChange}
          onEnterWithImages={handleEnterWithImages}
          hasPendingImages={pendingImages.length > 0}
          placeholder={
            loading
              ? '继续输入：Enter 排队，Ctrl/Cmd+Enter 插嘴当前 Agent…'
              : '输入你的问题或任务…'
          }
          disabled={disabled}
          className={styles.composerTextarea}
          onPaste={handlePasteImage}
          textareaRef={textAreaRef}
        />

        <div className={styles.composerToolbar}>
          <div className={styles.composerToolbarLeft}>
            <Popover
              content={
                <ComposerActionMenu
                  onExport={onExport}
                  onOpenCamera={() => setShowCameraInput(true)}
                  cameraEnabled={cameraEnabled}
                  onOpenImage={handleOpenImagePicker}
                  imageEnabled={imageEnabled}
                  onClose={() => setShowComposerMenu(false)}
                />
              }
              trigger="click"
              open={showComposerMenu}
              onOpenChange={setShowComposerMenu}
              placement="topLeft"
            >
              <button
                type="button"
                className={styles.composerToolbarButton}
                aria-label="打开输入动作菜单"
                data-testid="composer-menu"
              >
                <PlusOutlined />
              </button>
            </Popover>
            <Popover
              content={
                <ComposerStatusDetails
                  summary={runtimeSummary}
                  onOpenDevDetails={onOpenDevDetails}
                />
              }
              trigger="click"
              open={showStatusDetails}
              onOpenChange={handleStatusDetailsOpenChange}
              placement="topLeft"
            >
              <div className={styles.composerToolbarStatus}>
                <ComposerFeedbackStrip
                  state={feedbackState}
                  onClick={() => handleStatusDetailsOpenChange(true)}
                  onSubAgentsClick={onOpenSubAgentInspector}
                />
                {shouldShowStatus && (
                  <span
                    className={styles.composerStatusPill}
                    role="button"
                    tabIndex={0}
                    aria-label="查看运行状态详情"
                    onClick={() => handleStatusDetailsOpenChange(true)}
                  >
                    <span
                      className={styles.composerStatusDot}
                      style={{
                        background:
                          status === 'error'
                            ? 'var(--pudding-warning, #c4944c)'
                            : '#6f8f72',
                      }}
                    />
                    <span>{displayStatusText}</span>
                  </span>
                )}
                {uiTestMode && (
                  <button
                    type="button"
                    className={styles.composerUiTestButton}
                    onClick={handleFillUiTestGreeting}
                    aria-label="填入测试问候"
                    data-testid="composer-ui-test-greeting"
                  >
                    测试问候
                  </button>
                )}
              </div>
            </Popover>
          </div>

          <div
            className={styles.composerToolbarRight}
            data-testid="composer-action-area"
          >
            <Popover
              trigger="click"
              placement="topRight"
              content={
                <div className={styles.composerPreferenceMenu}>
                  {(['auto', 'deep', 'fast'] as const).map((mode) => (
                    <button
                      key={mode}
                      type="button"
                      className={styles.composerPreferenceItem}
                      data-active={executionMode === mode ? 'true' : undefined}
                      onClick={() => setExecutionMode(mode)}
                    >
                      {mode === 'auto'
                        ? '自动'
                        : mode === 'deep'
                          ? '深入'
                          : '快速'}
                    </button>
                  ))}
                </div>
              }
            >
              <button
                type="button"
                className={styles.composerPreferenceButton}
                aria-label="选择执行偏好"
              >
                <span>{executionModeLabel}</span>
                <DownOutlined />
              </button>
            </Popover>
                        <PermissionModeSelector
              value={permissionMode}
              onChange={onPermissionModeChange}
              disabled={disabled || loading}
            />
            {/* CU-11 §6.2：低频选项（Sandbox 边界 / Auto-review）收敛进设置 Popover，
                需要盯防的活动态通过角标浮出；高频的执行偏好/权限/语音/发送保持直达。 */}
            <Popover
              trigger="click"
              placement="topRight"
              content={
                <div
                  className={styles.composerSettingsPanel}
                  data-testid="composer-settings-panel"
                >
                  <SandboxBoundaryIndicator
                    boundary={sandboxBoundary ?? null}
                    disabled={disabled || !sandboxBoundary}
                    onNetworkModeChange={onSandboxNetworkModeChange}
                  />
                  <AutoReviewIndicator
                    state={
                      autoReviewState ?? {
                        enabled: permissionMode === 'auto',
                        consecutiveBlocks: 0,
                        totalBlocks: 0,
                        fallbackTriggered: false,
                        fallbackReason: null,
                        lastBlockedAt: null,
                        lastBlockRule: null,
                      }
                    }
                    recentlyDenied={recentlyDenied}
                    disabled={disabled || loading}
                    onRestoreAuto={onAutoReviewRestore}
                    onRetryDenied={onRetryDenied}
                    onRemoveDenied={onRemoveDenied}
                    onClearDenied={onClearDenied}
                  />
                </div>
              }
            >
              <button
                type="button"
                className={styles.composerToolbarButton}
                aria-label="沙箱与自动审查设置"
                data-testid="composer-settings"
              >
                <SettingOutlined />
                {Boolean(
                  (autoReviewState?.consecutiveBlocks ?? 0) > 0 ||
                    autoReviewState?.fallbackTriggered ||
                    recentlyDenied.length > 0,
                ) && <span className={styles.composerSettingsBadge} />}
              </button>
            </Popover>
            <Tooltip
              title={
                recording ? '停止录音' : recognizing ? '识别中...' : '语音输入'
              }
            >
              <button
                type="button"
                className={styles.composerToolbarButton}
                aria-label={recording ? '停止录音' : '开始语音输入'}
                onClick={handleToggleVoiceInput}
                disabled={disabled || loading || recognizing}
                style={
                  recording
                    ? { color: '#8b5cf6', animation: 'pulse 1s infinite' }
                    : undefined
                }
              >
                {recording ? (
                  <StopOutlined />
                ) : recognizing ? (
                  <LoadingOutlined spin />
                ) : (
                  <AudioOutlined />
                )}
              </button>
            </Tooltip>
            <Tooltip
              title={
                loading ? '停止生成' : imageUploading ? '图片上传中…' : '发送'
              }
            >
              <button
                type="button"
                className={styles.composerSendButton}
                data-loading={loading ? 'true' : undefined}
                onClick={() => void handleComposerSend()}
                disabled={
                  loading
                    ? false
                    : (!composerHasText && pendingImages.length === 0) ||
                      disabled ||
                      imageUploading
                }
                data-testid="chat-send"
                aria-label={loading ? '停止生成' : '发送'}
              >
                {loading ? (
                  <StopOutlined />
                ) : imageUploading ? (
                  <LoadingOutlined spin />
                ) : (
                  <SendOutlined />
                )}
              </button>
            </Tooltip>
          </div>
        </div>
      </div>

      {showCameraInput && (
        <React.Suspense fallback={null}>
          <CameraInputModal
            open
            workspaceId={workspaceId}
            disabled={disabled || loading}
            initialPrompt={textInputRef.current?.getValue() ?? ''}
            cameraInputAdapter={cameraInputAdapter}
            onCancel={() => setShowCameraInput(false)}
            onSend={handleCameraSend}
          />
        </React.Suspense>
      )}
    </div>
  );
};

export default IntentConsole;
