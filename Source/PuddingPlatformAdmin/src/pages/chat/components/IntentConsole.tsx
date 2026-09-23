// ── InputArea：安静胶囊 Composer + 轻反馈带 ────────
import {
  AudioOutlined,
  DeleteOutlined,
  DownOutlined,
  LoadingOutlined,
  PlusOutlined,
  SendOutlined,
  StopOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { Dropdown, message, Popover, Tooltip } from 'antd';
import React, { useCallback, useEffect, useRef, useState } from 'react';
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
import type { PermissionMode } from '../types/chatStateTypes';

import ComposerTextInput, {
  type ComposerTextInputHandle,
} from './ComposerTextInput';
import ComposerActionMenu from './ComposerActionMenu';

// 技能面板按需加载：低频交互，且 chat 首屏有硬性包预算（scripts/check-chat-bundle-budget.cjs）。
const SkillPalette =
  process.env.NODE_ENV === 'test'
    ? (require('./SkillPalette')
        .default as typeof import('./SkillPalette').default)
    : React.lazy(() => import('./SkillPalette'));
import ContextUsageRing from './ContextUsageRing';

import ComposerStatusDetails, {
  type ComposerRuntimeSummary,
} from './ComposerStatusDetails';
import PermissionModeSelector from './PermissionModeSelector';
import MessageQueueDropdown from './MessageQueueDropdown';

import { normalizeVisionArtifactFile } from './visionArtifactImage';

const CameraInputModal =
  process.env.NODE_ENV === 'test'
    ? (require('./CameraInputModal')
        .default as typeof import('./CameraInputModal').default)
    : React.lazy(() => import('./CameraInputModal'));

/** `+` 菜单与技能子面板横向并排（级联 flyout，参照 WorkBuddy）。 */
const composerMenuRowStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
};

/** 子面板与主菜单的分隔：细左边线，保留“同属一个菜单”的整体感。 */
const composerSkillFlyoutStyle: React.CSSProperties = {
  marginLeft: 4,
  paddingLeft: 8,
  borderLeft:
    '1px solid color-mix(in srgb, var(--earth-brown, #5c4a3a) 12%, transparent)',
};

const pendingSkillsRowStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 6,
  padding: '6px 4px 2px',
};

const skillChipStyle: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 2,
  padding: '2px 4px 2px 8px',
  fontSize: 12,
  borderRadius: 999,
  background: 'color-mix(in srgb, var(--text-primary) 8%, transparent)',
  color: 'var(--text-primary)',
};

const skillChipRemoveStyle: React.CSSProperties = {
  border: 'none',
  background: 'transparent',
  cursor: 'pointer',
  fontSize: 13,
  lineHeight: 1,
  padding: '0 2px',
  color: 'inherit',
};

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
  // 措辞不得与「压缩上下文」撞车：用户会把「整理上下文」读成压缩，
  // 从而以为系统在压缩（用户反馈 2026-09-19）。
  thinking: '· 正在思考…',
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

const MAX_PENDING_IMAGES = 600;

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
    imageParts?: { type: 'image'; artifactId: string; detail?: 'original' | 'low' | 'high' | 'auto' }[],
  ) => Promise<void> | void;
  onStop: () => void;
  /**
   * 运行中把当前草稿补充给正在执行的 Turn（Steering）。
   * 返回是否被受理；失败时由调用方保留草稿。
   * 与 Ctrl/Cmd+Enter 走同一提交链，鼠标键盘语义一致。
   */
  onSteerCurrent?: (text: string) => Promise<boolean> | boolean;
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
  /**
   * 可 @ 的 Agent 列表（仅供输入框 @ 补全；不参与发送路由决策）。
   * 缺省时不启用补全，输入 @ 行为与改动前一致。
   */
  agents?: readonly {
    agentId: string;
    name?: string | null;
    displayName?: string | null;
    isEnabled?: boolean;
    isFrozen?: boolean;
  }[];
  /** 本轮待附加技能（以 chip 展示在输入框上方；发送时转为文本）。 */
  pendingSkills?: { skillId: string; name: string }[];
  onPendingSkillsChange?: (skills: { skillId: string; name: string }[]) => void;
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
  onSteerCurrent,
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
  agents,
  pendingSkills,
  onPendingSkillsChange,
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
}) => {
  const { styles } = useChatStyles();
  const textAreaRef = useRef<HTMLTextAreaElement>(null);
  /** 输入叶子组件句柄（草稿态/IME 守卫/命令面板已下沉，外部改写走 setValue） */
  const textInputRef = useRef<ComposerTextInputHandle | null>(null);

  /**
   * 技能选择 → 挂为 chip（用户定调 2026-09-23：展示为 chip，发送时才转文本）。
   * 状态提升到 ChatPage 而非本组件：Enter 发送逻辑在 ChatPage 的 handleSend，
   * 若状态停在本组件内，按 Enter 发送会丢掉技能、按发送按钮却带着技能。
   */
  const handleSelectSkill = useCallback(
    (skill: { skillId: string; name: string }) => {
      const next = [
        ...(pendingSkills ?? []).filter((s) => s.skillId !== skill.skillId),
        { skillId: skill.skillId, name: skill.name },
      ];
      onPendingSkillsChange?.(next);
      setShowComposerMenu(false);
      setShowSkillFlyout(false);
    },
    [pendingSkills, onPendingSkillsChange],
  );

  const handleRemoveSkill = useCallback(
    (skillId: string) => {
      onPendingSkillsChange?.(
        (pendingSkills ?? []).filter((s) => s.skillId !== skillId),
      );
    },
    [pendingSkills, onPendingSkillsChange],
  );
  const handleComposerSendRef = useRef<() => void>(() => undefined);
  /** `+` 动作菜单 Popover */
  const [showComposerMenu, setShowComposerMenu] = useState(false);
  /** 技能子面板（级联 flyout）：hover「技能」项时在右侧并排展开。 */
  const [showSkillFlyout, setShowSkillFlyout] = useState(false);
  /** 运行状态详情 Popover */

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

  const contextRequestRef = useRef(0);
  const contextSessionRef = useRef(sessionId);
  contextSessionRef.current = sessionId;
  const refreshContextHealth = useCallback(async () => {
    if (!sessionId) return;
    const requestId = ++contextRequestRef.current;
    setContextHealthLoading(true);
    setContextHealthError(null);
    try {
      const [contextResult, cacheResult] = await Promise.allSettled([
        getContextHealth(sessionId),
        getCacheDiagnostics(sessionId),
      ]);

      if (requestId !== contextRequestRef.current || contextSessionRef.current !== sessionId) return;
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
      if (requestId === contextRequestRef.current && contextSessionRef.current === sessionId) setContextHealthLoading(false);
    }
  }, [sessionId]);



  // 用户反馈（2026-09-19）：圆环始终显示「未配置」—— 原实现只在「运行状态详情」
  // 弹层打开时才拉 context-health，普通使用路径下 contextHealth 恒为 null，
  // 圆环因此拿不到任何数值。改为：会话切换即拉取；一轮结束后再刷新。
  useEffect(() => {
    if (!sessionId) return;
    void refreshContextHealth();
  }, [sessionId, refreshContextHealth]);

  useEffect(() => {
    if (status !== 'completed') return;
    void refreshContextHealth();
  }, [status, refreshContextHealth]);
  useEffect(() => {
    if (!compactionStatus?.startsWith('上次压缩：')) return;
    void refreshContextHealth();
  }, [compactionStatus, refreshContextHealth]);
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
      if (images.length > available) {
        message.warning(
          `每轮最多发送 ${MAX_PENDING_IMAGES} 张图片，还可添加 ${available} 张。本批图片未添加，请分批选择；仍可发送文字消息。`,
        );
        return current;
      }
      return [
        ...current,
        ...images.map((file) => ({
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
    // 运行中鼠标与键盘语义一致：有草稿即走同一提交链（Agent 忙碌时由服务端排队）。
    // “停止”由独立的停止按钮承担，不再挪用发送按钮。
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
    onSend,
    onSendWithMetadata,
    pendingImages,
    workspaceId,
  ]);
  handleComposerSendRef.current = () => {
    void handleComposerSend();
  };

  /** 运行中把当前草稿补充给正在执行的 Turn；受理与否的草稿处理由上层完成。 */
  const handleSteerFromDraft = useCallback(() => {
    const draft = (textInputRef.current?.getValue() ?? '').trim();
    if (!draft || !onSteerCurrent) return;
    void Promise.resolve(onSteerCurrent(draft));
  }, [onSteerCurrent]);

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





  const displayStatusText =
    status === 'completed' && completedVisible
      ? STATUS_LABEL.completed
      : (STATUS_LABEL[status] ?? '');

  const refreshedContextToken =
    contextHealth && contextHealth.effectiveWindowTokens > 0
      ? {
          used: contextHealth.usedTokens,
          limit: contextHealth.contextWindowTokens,
          effectiveLimit: contextHealth.effectiveWindowTokens,
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

  /** 发送区状态机：空闲=发送；运行中有草稿=加入队列；“停止当前执行”独立显示。 */
  const hasComposerDraft = composerHasText || pendingImages.length > 0;
  /** 补充入口仅在有可注入的文本草稿且当前有运行中 Turn 时出现（图片无法插嘴）。 */
  const steerEntryAvailable =
    loading && composerHasText && Boolean(onSteerCurrent);


  return (
    <div className={styles.composerAligner}>
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
        {(pendingSkills?.length ?? 0) > 0 && (
          <div style={pendingSkillsRowStyle} data-testid="pending-skills">
            {(pendingSkills ?? []).map((s) => (
              <span
                key={s.skillId}
                style={skillChipStyle}
                title={s.skillId}
                data-testid={`pending-skill-${s.skillId}`}
              >
                <span>{s.name}</span>
                <button
                  type="button"
                  style={skillChipRemoveStyle}
                  onClick={() => handleRemoveSkill(s.skillId)}
                  aria-label={`移除技能 ${s.name}`}
                  data-testid={`pending-skill-remove-${s.skillId}`}
                >
                  ×
                </button>
              </span>
            ))}
          </div>
        )}
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
              : '输入你的问题或任务… 支持 @ 指派 Agent、/ 调用系统指令'
          }
          disabled={disabled}
          className={styles.composerTextarea}
          onPaste={handlePasteImage}
          textareaRef={textAreaRef}
          mentionAgents={agents}
        />

        <div className={styles.composerToolbar}>
          <div className={styles.composerToolbarLeft}>
            <Popover
              content={
                <div
                  style={composerMenuRowStyle}
                  onMouseLeave={() => setShowSkillFlyout(false)}
                >
                  <ComposerActionMenu
                    onExport={onExport}
                    onOpenSkills={() => setShowSkillFlyout(true)}
                    onHoverSkills={() => setShowSkillFlyout(true)}
                    skillsActive={showSkillFlyout}
                    onOpenCamera={() => setShowCameraInput(true)}
                    cameraEnabled={cameraEnabled}
                    onOpenImage={handleOpenImagePicker}
                    imageEnabled={imageEnabled}
                    onClose={() => setShowComposerMenu(false)}
                  />
                  {showSkillFlyout && (
                    <div style={composerSkillFlyoutStyle}>
                      <React.Suspense fallback={null}>
                        <SkillPalette
                          open
                          onSelect={handleSelectSkill}
                          selectedSkillIds={(pendingSkills ?? []).map(
                            (s) => s.skillId,
                          )}
                        />
                      </React.Suspense>
                    </div>
                  )}
                </div>
              }
              trigger="click"
              open={showComposerMenu}
              onOpenChange={(next) => {
                setShowComposerMenu(next);
                if (!next) setShowSkillFlyout(false);
              }}
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
            {/* 用户诉求（2026-09-19）：旧上下文指示条（ComposerContextBar）已移除，
                改为工具栏内的圆环控件 —— 悬浮出「已使用 X/Y」摘要，点击出上下文明细面板。 */}
            <ContextUsageRing
              tLimit={contextHealth?.contextWindowTokens ?? tLimit}
              tUsed={contextHealth?.usedTokens ?? tUsed}
              tPct={effectiveContextUsagePercentage ?? 0}
              tEffective={contextHealth?.effectiveWindowTokens}
              tBreakdown={
                contextHealth
                  ? {
                      systemPrompt: contextHealth.systemPromptTokens ?? 0,
                      toolDefinitions: contextHealth.toolDefinitionTokens ?? 0,
                      compactionSummary:
                        contextHealth.compactionSummaryTokens ?? 0,
                      conversation: contextHealth.conversationTokens ?? 0,
                      reasoning: contextHealth.reasoningTokens ?? 0,
                      toolResults: contextHealth.toolResultTokens ?? 0,
                    }
                  : undefined
              }
              usageRecordedAt={contextHealth?.usageRecordedAtUtc}
              usageSource={contextHealth?.usageSource}
              usageConfidence={contextHealth?.usageConfidence}
              tMessageCount={contextHealth?.messageCount}
              onOpenChange={(next) => {
                // 打开即刷新：此前只在会话切换 / 一轮结束时拉取，
                // 用户点开面板看到的是过期快照（用户反馈 2026-09-19）。
                if (next) void refreshContextHealth();
              }}
              compactionStatus={compactionStatus}
              error={contextHealthError}
              subAgentsRunning={subAgentsRunning}
              onOpenSubAgents={onOpenSubAgentInspector}
              runtimeDetails={
                <ComposerStatusDetails
                  summary={runtimeSummary}
                  onOpenDevDetails={onOpenDevDetails}
                />
              }
            />

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
            {loading && (
              <Tooltip title="停止当前执行；已排队消息不受影响">
                <button
                  type="button"
                  className={styles.composerStopButton}
                  onClick={onStop}
                  disabled={disabled}
                  data-testid="chat-stop"
                  aria-label="停止当前执行"
                >
                  <StopOutlined />
                </button>
              </Tooltip>
            )}
            {steerEntryAvailable && (
              <Dropdown
                trigger={['click']}
                placement="topRight"
                menu={{
                  items: [
                    {
                      key: 'steer-current',
                      icon: <ThunderboltOutlined />,
                      label: '补充给当前任务',
                    },
                  ],
                  onClick: ({ key }) => {
                    if (key === 'steer-current') handleSteerFromDraft();
                  },
                }}
              >
                <Tooltip title="补充给当前任务：在当前步骤结束后注入运行中的 Agent（同 Ctrl/Cmd+Enter）">
                  <button
                    type="button"
                    className={styles.composerToolbarButton}
                    data-testid="chat-steer-menu"
                    aria-label="补充给当前任务"
                    aria-haspopup="menu"
                  >
                    <ThunderboltOutlined />
                  </button>
                </Tooltip>
              </Dropdown>
            )}
            <Tooltip
              title={
                imageUploading
                  ? '图片上传中…'
                  : loading
                    ? hasComposerDraft
                      ? '加入队列：当前回复完成后自动投递（同 Enter）'
                      : '输入内容后可加入队列'
                    : '发送'
              }
            >
              <button
                type="button"
                className={styles.composerSendButton}
                data-queued={loading && hasComposerDraft ? 'true' : undefined}
                onClick={() => void handleComposerSend()}
                disabled={
                  !hasComposerDraft || disabled || imageUploading
                }
                data-testid="chat-send"
                aria-label={
                  loading && hasComposerDraft ? '加入队列' : '发送'
                }
              >
                {imageUploading ? (
                  <LoadingOutlined spin />
                ) : (
                  <SendOutlined />
                )}
                {loading && hasComposerDraft && <span>加入队列</span>}
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
    </div>
  );
};

export default IntentConsole;
