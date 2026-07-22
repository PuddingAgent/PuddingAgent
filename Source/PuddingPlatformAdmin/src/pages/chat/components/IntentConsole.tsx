// ── InputArea：安静胶囊 Composer + 轻反馈带 ────────
import {
  AudioMutedOutlined,
  AudioOutlined,
  DeleteOutlined,
  DownOutlined,
  LoadingOutlined,
  PlusOutlined,
  SendOutlined,
  StopOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { Button, Input, Popover, Tooltip, message } from 'antd';
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
import { useChatStyles } from '../styles';
import CameraInputModal from './CameraInputModal';
import CommandPalette, { type Command, filterCommands } from './CommandPalette';
import ComposerActionMenu from './ComposerActionMenu';
import ComposerFeedbackStrip, {
  type FeedbackState,
} from './ComposerFeedbackStrip';
import ComposerStatusDetails, {
  type ComposerRuntimeSummary,
} from './ComposerStatusDetails';

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
  onSend: () => void;
  onSendWithMetadata?: (
    content: string,
    metadata: Record<string, string>,
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
  subAgentsRunning = 0,
  onOpenSubAgentInspector,
  voiceInputAdapter = createDashScopeVoiceInputAdapter(),
  voiceOutputAdapter = defaultBrowserVoiceOutputAdapter,
  latestAssistantText,
  onVoiceCaptureStatus,
  onVoicePlaybackStatus,
  cameraInputAdapter = defaultBrowserCameraInputAdapter,
  workspaceId,
}) => {
  const { styles } = useChatStyles();
  const textAreaRef = useRef<HTMLTextAreaElement>(null);
  const [paletteVisible, setPaletteVisible] = useState(false);
  const [selectedIdx, setSelectedIdx] = useState(0);
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
  /** 输入框本地草稿；IME 组合输入期间避免把拼音中间态提升到整页状态。 */
  const [draftValue, setDraftValue] = useState(inputValue);
  const isTextComposingRef = useRef(false);
  /** 容器是否处于 active（focus 或 非空输入 或 正在录音） */
    const [recording, setRecording] = useState(false);
  const [recognizing, setRecognizing] = useState(false);
  const voiceHandleRef = useRef<BrowserVoiceInputHandle | null>(null);
  /** 图片上传隐藏文件选择器 */
  const imageFileInputRef = useRef<HTMLInputElement>(null);
  /** 图片上传进行中 */
  const [imageUploading, setImageUploading] = useState(false);
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
    composerFocused || draftValue.trim().length > 0 || recording;
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
    if (composerFocused || draftValue.length > 0) {
      setCompletedVisible(false);
    }
  }, [composerFocused, draftValue]);

  React.useEffect(() => {
    if (!isTextComposingRef.current && inputValue !== draftValue) {
      setDraftValue(inputValue);
      if (!inputValue.trim()) {
        setPaletteVisible(false);
      }
    }
  }, [draftValue, inputValue]);

  const updateCommandPaletteState = useCallback(
    (value: string, selectionStart?: number | null) => {
      const pos = selectionStart ?? value.length;
      const before = value.slice(0, pos);
      const slashMatch = before.match(/(?:^|\s)\/([^\s]*)$/);
      setPaletteVisible(!!slashMatch);
      if (slashMatch) {
        setSelectedIdx(0);
      }
    },
    [],
  );

  /** 当前文本中最后一个 / 后的内容（用于过滤命令） */
  const slashFilterText = React.useMemo(() => {
    if (!paletteVisible) return '';
    const cursorPos = textAreaRef.current?.selectionStart ?? draftValue.length;
    const beforeCursor = draftValue.slice(0, cursorPos);
    const match = beforeCursor.match(/(?:^|\s)\/([^\s]*)$/);
    return match ? match[1] : '';
  }, [draftValue, paletteVisible]);

  /** 根据过滤文本匹配的命令列表 */
  const filteredCommands = React.useMemo(() => {
    return filterCommands(slashFilterText);
  }, [slashFilterText]);

  const handleInputChange = useCallback(
    (e: React.ChangeEvent<HTMLTextAreaElement>) => {
      const v = e.target.value;
      setDraftValue(v);
      setCompletedVisible(false);

      if (!isTextComposingRef.current) {
        onInputChange(v);
      }

      updateCommandPaletteState(v, e.target.selectionStart);
    },
    [onInputChange, updateCommandPaletteState],
  );

  const handleCompositionStart = useCallback(() => {
    isTextComposingRef.current = true;
  }, []);

  const handleCompositionEnd = useCallback(
    (e: React.CompositionEvent<HTMLTextAreaElement>) => {
      isTextComposingRef.current = false;
      const v = e.currentTarget.value;
      setDraftValue(v);
      setCompletedVisible(false);
      onInputChange(v);
      updateCommandPaletteState(v, e.currentTarget.selectionStart);
    },
    [onInputChange, updateCommandPaletteState],
  );

  const handleCommandSelect = useCallback(
    (cmd: Command) => {
      const pos = textAreaRef.current?.selectionStart ?? draftValue.length;
      const before = draftValue.slice(0, pos);
      const after = draftValue.slice(pos);
      const newBefore = before.replace(/\/([^\s]*)$/, cmd.shortcut + ' ');
      const newValue = newBefore + after;
      setDraftValue(newValue);
      onInputChange(newValue);
      setPaletteVisible(false);
      requestAnimationFrame(() => {
        if (textAreaRef.current) {
          const newPos = newBefore.length;
          textAreaRef.current.selectionStart = newPos;
          textAreaRef.current.selectionEnd = newPos;
          textAreaRef.current.focus();
        }
      });
    },
    [draftValue, onInputChange],
  );

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      if (e.nativeEvent.isComposing || isTextComposingRef.current) {
        return;
      }

      if (paletteVisible) {
        if (e.key === 'ArrowDown') {
          e.preventDefault();
          setSelectedIdx((prev) =>
            Math.min(filteredCommands.length - 1, prev + 1),
          );
          return;
        }
        if (e.key === 'ArrowUp') {
          e.preventDefault();
          setSelectedIdx((prev) => Math.max(0, prev - 1));
          return;
        }
        if (e.key === 'Escape') {
          e.preventDefault();
          setPaletteVisible(false);
          return;
        }
        if (e.key === 'Enter' && !e.shiftKey) {
          e.preventDefault();
          const cmd = filteredCommands[selectedIdx];
          if (cmd) handleCommandSelect(cmd);
          return;
        }
      }
      onKeyDown(e);
    },
    [
      paletteVisible,
      filteredCommands,
      selectedIdx,
      onKeyDown,
      handleCommandSelect,
    ],
  );

  const handleClosePalette = useCallback(() => {
    setPaletteVisible(false);
  }, []);

  const handleTextAreaFocus = useCallback(() => {
    setComposerFocused(true);
  }, []);
  const handleTextAreaBlur = useCallback(() => {
    setComposerFocused(false);
  }, []);
  const handleFillUiTestGreeting = useCallback(() => {
    setCompletedVisible(false);
    setPaletteVisible(false);
    setDraftValue('你好');
    onInputChange('你好');
    requestAnimationFrame(() => textAreaRef.current?.focus());
  }, [onInputChange]);

    const handleCameraSend = useCallback(
    async (content: string, metadata: Record<string, string>) => {
      if (!onSendWithMetadata) return;
      await onSendWithMetadata(content, metadata);
      setDraftValue('');
      onInputChange('');
    },
    [onInputChange, onSendWithMetadata],
  );

  // ── 图片上传（菜单选择 / 粘贴 / 拖拽共用）──
  const handleImageFile = useCallback(
    async (file: File) => {
      if (!workspaceId || !onSendWithMetadata) {
        message.warning('请先选择工作空间和 Agent');
        return;
      }
      if (!file.type.startsWith('image/')) {
        message.warning('仅支持图片文件');
        return;
      }
      setImageUploading(true);
      try {
        const dimensions = await readImageDimensions(file);
        const uploaded = await uploadVisionArtifact(
          workspaceId,
          file,
          {
            width: dimensions?.width,
            height: dimensions?.height,
            capturedAt: Date.now(),
          },
        );
        const prompt = draftValue.trim() || '请分析这张图片。';
        const metadata: Record<string, string> = {
          inputMode: 'image',
          visionArtifactId: uploaded.artifactId,
          fileName: file.name,
          mimeType: uploaded.mimeType || file.type,
        };
        if (uploaded.width ?? dimensions?.width)
          metadata.width = String(uploaded.width ?? dimensions?.width);
        if (uploaded.height ?? dimensions?.height)
          metadata.height = String(uploaded.height ?? dimensions?.height);
        await onSendWithMetadata(prompt, metadata);
        setDraftValue('');
        onInputChange('');
      } catch (err) {
        message.error(getRequestErrorMessage(err, '图片上传失败'));
      } finally {
        setImageUploading(false);
      }
    },
    [draftValue, onInputChange, onSendWithMetadata, workspaceId],
  );

  const handleOpenImagePicker = useCallback(() => {
    imageFileInputRef.current?.click();
  }, []);

  const handleImageInputChange = useCallback(
    (e: React.ChangeEvent<HTMLInputElement>) => {
      const file = e.target.files?.[0];
      // 重置 value，允许连续选择同一文件
      e.target.value = '';
      if (file) void handleImageFile(file);
    },
    [handleImageFile],
  );

  /** 粘贴图片（Ctrl+V）：优先检测剪贴板中的图片文件 */
  const handlePasteImage = useCallback(
    (e: React.ClipboardEvent) => {
      const file = e.clipboardData?.files?.[0];
      if (file && file.type.startsWith('image/')) {
        e.preventDefault();
        void handleImageFile(file);
      }
    },
    [handleImageFile],
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
      const file = e.dataTransfer.files?.[0];
      if (file && file.type.startsWith('image/')) {
        void handleImageFile(file);
      } else if (file) {
        message.warning('仅支持拖入图片文件');
      }
    },
    [handleImageFile],
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
            setDraftValue(finalText);
            onInputChange(finalText);
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
        contextHealth?.contextWindowTokens ??
        (tLimit > 0 ? tLimit : undefined),
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

  const getQueueStatusLabel = useCallback((item: ChatInteractionQueueItem) => {
    if (item.status === 'steering_pending') return '引导待注入';
    if (item.status === 'steering_injected')
      return item.injectedRound
        ? `已注入 · 第 ${item.injectedRound} 轮`
        : '已注入';
    if (item.status === 'steering_failed') return '引导失败';
    if (item.status === 'delivering') return '投递中';
    if (item.status === 'retrying') return '重试中';
    if (item.status === 'dead_letter') return '死信';
    if (item.status === 'failed') return '失败';
    if (item.status === 'cancelled') return '已取消';
    if (item.status === 'expired') return '已过期';
    return '排队中';
  }, []);

  const formatQueueLatency = useCallback((ms?: number) => {
    if (typeof ms !== 'number' || !Number.isFinite(ms)) return null;
    if (ms < 1000) return `${Math.round(ms)}ms`;
    return `${(ms / 1000).toFixed(ms < 10000 ? 1 : 0)}s`;
  }, []);

  const getQueueMetaText = useCallback(
    (item: ChatInteractionQueueItem) => {
      if (item.status === 'steering_injected') {
        const latency = formatQueueLatency(item.injectionLatencyMs);
        return latency
          ? `提交后 ${latency} 注入，稍后自动收起`
          : '运行时已消费并注入上下文，稍后自动收起';
      }
      if (item.status === 'steering_pending') return '等待下一次模型请求前注入';
      if (item.status === 'steering_failed') return item.error ?? '提交失败';
      if (item.source === 'backend_message_queue')
        return '后端消息队列快照，调度由 Agent 服务管理';
      return '后端队列状态';
    },
    [formatQueueLatency],
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
        style={{ display: 'none' }}
        onChange={handleImageInputChange}
        aria-hidden="true"
        tabIndex={-1}
        data-testid="image-file-input"
      />
      <CommandPalette
        visible={paletteVisible}
        filterText={slashFilterText}
        selectedIdx={selectedIdx % Math.max(1, filteredCommands.length)}
        onSelectIndex={setSelectedIdx}
        onSelect={handleCommandSelect}
        onClose={handleClosePalette}
      />

      {interactionQueue.length > 0 && (
        <div className={styles.composerQueue} data-testid="interaction-queue">
          <div className={styles.composerQueueHeader}>
            <span>交互队列</span>
            <span>{interactionQueue.length}</span>
          </div>
          <div className={styles.composerQueueList}>
            {interactionQueue.map((item) => {
              const isBackendQueueItem =
                item.source === 'backend_message_queue';
              const isEditable =
                item.status === 'queued' && !isBackendQueueItem;
              const canDelete = item.source === 'steering';
              const canSteer =
                item.status === 'queued' && isBackendQueueItem && loading;
              return (
                <div
                  key={item.id}
                  className={styles.composerQueueItem}
                  data-status={item.status}
                >
                  {isEditable ? (
                    <Input.TextArea
                      value={item.text}
                      autoSize={{ minRows: 1, maxRows: 2 }}
                      className={styles.composerQueueInput}
                      onChange={(event) => {
                        onUpdateQueuedInteraction?.(
                          item.id,
                          event.target.value,
                        );
                      }}
                      aria-label="队列消息"
                    />
                  ) : (
                    <div
                      className={styles.composerQueuePreview}
                      role="textbox"
                      aria-label="队列消息"
                      aria-readonly="true"
                      title={item.text}
                    >
                      {item.text}
                    </div>
                  )}
                  <div className={styles.composerQueueActions}>
                    <span
                      className={styles.composerQueueStatus}
                      data-status={item.status}
                      title={getQueueMetaText(item)}
                    >
                      {getQueueStatusLabel(item)}
                    </span>
                    <Tooltip title="由后端队列调度">
                      <Button
                        type="text"
                        size="small"
                        icon={<SendOutlined />}
                        disabled
                        onClick={() => {
                          void onSendQueuedInteractionNow?.(item.id);
                        }}
                        aria-label="发送队列消息"
                      />
                    </Tooltip>
                    <Tooltip title="注入下一次上下文">
                      <Button
                        type="text"
                        size="small"
                        icon={<ThunderboltOutlined />}
                        disabled={!canSteer}
                        onClick={() => {
                          void onSteerQueuedInteraction?.(item.id);
                        }}
                        aria-label="引导 Agent"
                      />
                    </Tooltip>
                    <Tooltip title="删除">
                      <Button
                        type="text"
                        size="small"
                        icon={<DeleteOutlined />}
                        disabled={!canDelete}
                        onClick={() => onDeleteQueuedInteraction?.(item.id)}
                        aria-label="删除队列消息"
                      />
                    </Tooltip>
                  </div>
                  <div className={styles.composerQueueMeta}>
                    {getQueueMetaText(item)}
                  </div>
                  {item.error && item.status !== 'steering_failed' && (
                    <div className={styles.composerQueueError}>
                      {item.error}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}

      <div className={styles.composerCapsuleBody}>
                <Input.TextArea
          ref={textAreaRef as any}
          value={draftValue}
          onChange={handleInputChange}
          onKeyDown={handleKeyDown}
          onPaste={handlePasteImage}
          onCompositionStart={handleCompositionStart}
          onCompositionEnd={handleCompositionEnd}
          onFocus={handleTextAreaFocus}
          onBlur={handleTextAreaBlur}
          placeholder={loading ? '正在生成回复…' : '输入你的问题或任务…'}
          disabled={disabled}
          autoSize={{ minRows: 1, maxRows: 5 }}
          className={styles.composerTextarea}
          data-testid="chat-input"
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
            <Tooltip title={loading ? '停止生成' : imageUploading ? '图片上传中…' : '发送'}>
              <button
                type="button"
                className={styles.composerSendButton}
                data-loading={loading ? 'true' : undefined}
                onClick={loading ? onStop : onSend}
                disabled={
                  loading ? false : !draftValue.trim() || disabled || imageUploading
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

      <CameraInputModal
        open={showCameraInput}
        workspaceId={workspaceId}
        disabled={disabled || loading}
        initialPrompt={draftValue}
        cameraInputAdapter={cameraInputAdapter}
        onCancel={() => setShowCameraInput(false)}
        onSend={handleCameraSend}
      />
    </div>
  );
};

export default IntentConsole;
