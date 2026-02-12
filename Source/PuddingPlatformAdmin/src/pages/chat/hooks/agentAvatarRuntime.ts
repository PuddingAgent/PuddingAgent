export type AgentAvatarRuntimeKind = 'sprite' | 'live2d' | 'static';

export type AgentAvatarStatus =
  | 'idle'
  | 'listening'
  | 'seeing'
  | 'thinking'
  | 'speaking'
  | 'tool'
  | 'error'
  | 'sleeping';

export type AgentAvatarExpression =
  | 'neutral'
  | 'focused'
  | 'curious'
  | 'thinking'
  | 'talking'
  | 'working'
  | 'concerned'
  | 'sleepy';

export type AgentAvatarMotion =
  | 'idle'
  | 'listen'
  | 'look'
  | 'think'
  | 'talk'
  | 'tool'
  | 'error'
  | 'sleep';

export interface AgentAvatarRuntimeState {
  agentId: string;
  avatarId?: string;
  runtimeKind: AgentAvatarRuntimeKind;
  status: AgentAvatarStatus;
  expression: AgentAvatarExpression;
  motion: AgentAvatarMotion;
  visible: boolean;
  motionEnabled: boolean;
  spriteSheetUrl?: string;
  live2dModelUrl?: string;
  staticAvatarUrl?: string;
  activeVoiceSessionId?: string;
  activeDeliveryId?: string;
  activeCameraSessionId?: string;
  activeVisionSessionId?: string;
  latestVisionArtifactId?: string;
  activeToolCallId?: string;
  error?: string;
  updatedAt: number;
}

export type AgentAvatarRuntimeEvent =
  | {
      type: 'configure';
      agentId: string;
      avatarId?: string;
      runtimeKind: AgentAvatarRuntimeKind;
      spriteSheetUrl?: string;
      live2dModelUrl?: string;
      staticAvatarUrl?: string;
      reducedMotion?: boolean;
      visible?: boolean;
      now?: number;
    }
  | {
      type: 'voice_capture_status';
      agentId: string;
      status: string;
      sessionId?: string;
      now?: number;
    }
  | {
      type: 'voice_playback_status';
      agentId: string;
      status: string;
      deliveryId?: string;
      now?: number;
    }
  | {
      type: 'camera_capture_status';
      agentId: string;
      status: string;
      sessionId?: string;
      artifactId?: string;
      now?: number;
    }
  | {
      type: 'visual_reasoning_status';
      agentId: string;
      status: string;
      sessionId?: string;
      now?: number;
    }
  | {
      type: 'assistant_response_status';
      agentId: string;
      status: string;
      now?: number;
    }
  | {
      type: 'tool_status';
      agentId: string;
      status: string;
      toolCallId?: string;
      now?: number;
    }
  | {
      type: 'agent_availability_changed';
      agentId: string;
      enabled: boolean;
      now?: number;
    }
  | {
      type: 'visibility_changed';
      agentId: string;
      visible: boolean;
      now?: number;
    }
  | {
      type: 'error';
      agentId: string;
      message: string;
      now?: number;
    }
  | {
      type: 'reset';
      agentId: string;
      now?: number;
    };

export interface AgentAvatarRenderState {
  runtimeKind: AgentAvatarRuntimeKind;
  status: AgentAvatarStatus;
  expression: AgentAvatarExpression;
  motion: AgentAvatarMotion;
  visible: boolean;
  spriteSheetUrl?: string;
  live2dModelUrl?: string;
  staticAvatarUrl?: string;
  spriteRow?: number;
  spriteFrameCount?: number;
  ariaLabel: string;
}

const spriteRows: Record<AgentAvatarStatus, number> = {
  idle: 0,
  listening: 1,
  seeing: 2,
  speaking: 3,
  tool: 5,
  error: 6,
  sleeping: 6,
  thinking: 7,
};

const spriteFrameCounts: Record<AgentAvatarStatus, number> = {
  idle: 6,
  listening: 6,
  seeing: 6,
  thinking: 6,
  speaking: 4,
  tool: 6,
  error: 1,
  sleeping: 6,
};

const statusLabels: Record<AgentAvatarStatus, string> = {
  idle: '待命',
  listening: '正在听',
  seeing: '正在看',
  thinking: '正在思考',
  speaking: '正在说话',
  tool: '正在使用工具',
  error: '异常',
  sleeping: '休眠',
};

const nowOrCurrent = (now?: number) => now ?? Date.now();

const avatarPoseByStatus: Record<
  AgentAvatarStatus,
  Pick<AgentAvatarRuntimeState, 'expression' | 'motion'>
> = {
  idle: { expression: 'neutral', motion: 'idle' },
  listening: { expression: 'focused', motion: 'listen' },
  seeing: { expression: 'curious', motion: 'look' },
  thinking: { expression: 'thinking', motion: 'think' },
  speaking: { expression: 'talking', motion: 'talk' },
  tool: { expression: 'working', motion: 'tool' },
  error: { expression: 'concerned', motion: 'error' },
  sleeping: { expression: 'sleepy', motion: 'sleep' },
};

function createInitialState(
  event: AgentAvatarRuntimeEvent,
): AgentAvatarRuntimeState {
  if (event.type !== 'configure') {
    throw new Error('agent avatar runtime must start with configure');
  }

  return {
    agentId: event.agentId,
    avatarId: event.avatarId,
    runtimeKind: event.runtimeKind,
    status: 'idle',
    expression: 'neutral',
    motion: 'idle',
    visible: event.visible ?? true,
    motionEnabled: event.reducedMotion ? false : true,
    spriteSheetUrl: event.spriteSheetUrl,
    live2dModelUrl: event.live2dModelUrl,
    staticAvatarUrl: event.staticAvatarUrl,
    updatedAt: nowOrCurrent(event.now),
  };
}

function withStatus(
  state: AgentAvatarRuntimeState,
  status: AgentAvatarStatus,
  updatedAt: number,
): AgentAvatarRuntimeState {
  return {
    ...state,
    status,
    ...avatarPoseByStatus[status],
    error: status === 'error' ? state.error : undefined,
    updatedAt,
  };
}

function statusFromVoiceCapture(status: string): AgentAvatarStatus | undefined {
  if (status === 'recording' || status === 'transcribing') return 'listening';
  if (
    status === 'idle' ||
    status === 'completed' ||
    status === 'stopped' ||
    status === 'cancelled'
  )
    return 'idle';
  if (status === 'failed') return 'error';
  return undefined;
}

function statusFromVoicePlayback(
  status: string,
): AgentAvatarStatus | undefined {
  if (status === 'playing' || status === 'buffering') return 'speaking';
  if (status === 'synthesizing' || status === 'queued') return 'thinking';
  if (
    status === 'idle' ||
    status === 'completed' ||
    status === 'done' ||
    status === 'stopped' ||
    status === 'cancelled'
  )
    return 'idle';
  if (status === 'failed' || status === 'expired') return 'error';
  return undefined;
}

function statusFromCamera(status: string): AgentAvatarStatus | undefined {
  if (
    status === 'previewing' ||
    status === 'capturing' ||
    status === 'sampling'
  )
    return 'seeing';
  if (
    status === 'idle' ||
    status === 'completed' ||
    status === 'done' ||
    status === 'stopped' ||
    status === 'cancelled'
  )
    return 'idle';
  if (status === 'failed') return 'error';
  return undefined;
}

function statusFromVisualReasoning(
  status: string,
): AgentAvatarStatus | undefined {
  if (status === 'reasoning' || status === 'thinking') return 'thinking';
  if (status === 'answering') return 'speaking';
  if (
    status === 'idle' ||
    status === 'completed' ||
    status === 'done' ||
    status === 'cancelled'
  )
    return 'idle';
  if (status === 'failed') return 'error';
  return undefined;
}

function statusFromAssistantResponse(
  status: string,
): AgentAvatarStatus | undefined {
  if (status === 'thinking') return 'thinking';
  if (status === 'streaming') return 'speaking';
  if (status === 'tool_executing') return 'tool';
  if (status === 'error') return 'error';
  if (status === 'idle' || status === 'composing' || status === 'completed')
    return 'idle';
  return undefined;
}

function statusFromTool(status: string): AgentAvatarStatus | undefined {
  if (status === 'running' || status === 'executing') return 'tool';
  if (status === 'failed') return 'error';
  return undefined;
}

export function reduceAgentAvatarRuntime(
  state: AgentAvatarRuntimeState | undefined,
  event: AgentAvatarRuntimeEvent,
): AgentAvatarRuntimeState {
  if (state && state.agentId !== event.agentId) {
    return state;
  }

  const current = state ?? createInitialState(event);
  const updatedAt = nowOrCurrent(event.now);

  switch (event.type) {
    case 'configure':
      return {
        ...current,
        avatarId: event.avatarId ?? current.avatarId,
        runtimeKind: event.runtimeKind,
        spriteSheetUrl: event.spriteSheetUrl ?? current.spriteSheetUrl,
        live2dModelUrl: event.live2dModelUrl ?? current.live2dModelUrl,
        staticAvatarUrl: event.staticAvatarUrl ?? current.staticAvatarUrl,
        visible: event.visible ?? current.visible,
        motionEnabled: event.reducedMotion ? false : current.motionEnabled,
        updatedAt,
      };

    case 'voice_capture_status': {
      const nextStatus = statusFromVoiceCapture(event.status);
      const next = nextStatus
        ? withStatus(current, nextStatus, updatedAt)
        : current;
      return {
        ...next,
        activeVoiceSessionId: event.sessionId ?? current.activeVoiceSessionId,
        updatedAt,
      };
    }

    case 'voice_playback_status': {
      const nextStatus = statusFromVoicePlayback(event.status);
      const next = nextStatus
        ? withStatus(current, nextStatus, updatedAt)
        : current;
      return {
        ...next,
        activeDeliveryId: event.deliveryId ?? current.activeDeliveryId,
        updatedAt,
      };
    }

    case 'camera_capture_status': {
      const nextStatus = statusFromCamera(event.status);
      const next = nextStatus
        ? withStatus(current, nextStatus, updatedAt)
        : current;
      return {
        ...next,
        activeCameraSessionId: event.sessionId ?? current.activeCameraSessionId,
        latestVisionArtifactId:
          event.artifactId ?? current.latestVisionArtifactId,
        updatedAt,
      };
    }

    case 'visual_reasoning_status': {
      const nextStatus = statusFromVisualReasoning(event.status);
      const next = nextStatus
        ? withStatus(current, nextStatus, updatedAt)
        : current;
      return {
        ...next,
        activeVisionSessionId: event.sessionId ?? current.activeVisionSessionId,
        updatedAt,
      };
    }

    case 'assistant_response_status': {
      const nextStatus = statusFromAssistantResponse(event.status);
      return nextStatus ? withStatus(current, nextStatus, updatedAt) : current;
    }

    case 'tool_status': {
      const nextStatus = statusFromTool(event.status);
      const next = nextStatus
        ? withStatus(current, nextStatus, updatedAt)
        : current;
      return {
        ...next,
        activeToolCallId: event.toolCallId ?? current.activeToolCallId,
        updatedAt,
      };
    }

    case 'agent_availability_changed':
      return withStatus(
        current,
        event.enabled ? 'idle' : 'sleeping',
        updatedAt,
      );

    case 'visibility_changed':
      return {
        ...current,
        visible: event.visible,
        updatedAt,
      };

    case 'error':
      return withStatus(
        { ...current, error: event.message },
        'error',
        updatedAt,
      );

    case 'reset':
      return withStatus(current, 'idle', updatedAt);

    default:
      return current;
  }
}

export function selectAvatarRenderState(
  state: AgentAvatarRuntimeState,
): AgentAvatarRenderState {
  return {
    runtimeKind: state.runtimeKind,
    status: state.status,
    expression: state.expression,
    motion: state.motion,
    visible: state.visible,
    spriteSheetUrl: state.spriteSheetUrl,
    live2dModelUrl: state.live2dModelUrl,
    staticAvatarUrl: state.staticAvatarUrl,
    spriteRow:
      state.runtimeKind === 'sprite' ? spriteRows[state.status] : undefined,
    spriteFrameCount:
      state.runtimeKind === 'sprite'
        ? state.motionEnabled
          ? spriteFrameCounts[state.status]
          : 1
        : undefined,
    ariaLabel: `Agent ${state.agentId} 状态：${statusLabels[state.status]}`,
  };
}
