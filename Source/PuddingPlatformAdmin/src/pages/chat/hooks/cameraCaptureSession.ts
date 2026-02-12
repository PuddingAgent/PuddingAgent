export type CameraCaptureStatus =
  | 'idle'
  | 'requesting_permission'
  | 'ready'
  | 'previewing'
  | 'capturing'
  | 'sampling'
  | 'paused'
  | 'awaiting_confirmation'
  | 'sending'
  | 'completed'
  | 'cancelled'
  | 'failed';

export type CameraCapturePermission =
  | 'unknown'
  | 'prompt'
  | 'granted'
  | 'denied';

export interface CameraCaptureSession {
  sessionId: string;
  workspaceId: string;
  roomId: string;
  participantId: string;
  status: CameraCaptureStatus;
  permission: CameraCapturePermission;
  deviceLabel?: string;
  width?: number;
  height?: number;
  samplingIntervalMs?: number;
  framesCaptured: number;
  latestArtifactId?: string;
  mimeType?: string;
  capturedAt?: number;
  prompt?: string;
  error?: string;
  updatedAt: number;
}

export type CameraCaptureEvent =
  | {
      type: 'permission_requested';
      sessionId: string;
      workspaceId: string;
      roomId: string;
      participantId: string;
      now?: number;
    }
  | {
      type: 'permission_granted';
      sessionId: string;
      deviceLabel?: string;
      now?: number;
    }
  | {
      type: 'permission_denied';
      sessionId: string;
      error: string;
      now?: number;
    }
  | {
      type: 'preview_started';
      sessionId: string;
      width?: number;
      height?: number;
      now?: number;
    }
  | {
      type: 'snapshot_requested';
      sessionId: string;
      prompt?: string;
      now?: number;
    }
  | {
      type: 'snapshot_captured' | 'sample_captured';
      sessionId: string;
      artifactId: string;
      mimeType: string;
      width?: number;
      height?: number;
      capturedAt?: number;
      now?: number;
    }
  | {
      type: 'start_sampling';
      sessionId: string;
      intervalMs: number;
      now?: number;
    }
  | {
      type: 'pause_sampling' | 'confirm_send' | 'sent' | 'stop' | 'cancel';
      sessionId: string;
      now?: number;
    }
  | {
      type: 'fail';
      sessionId: string;
      error: string;
      now?: number;
    };

export interface CameraMessageDraft {
  roomId: string;
  content: string;
  metadata: Record<string, string>;
}

const nowOrCurrent = (now?: number) => now ?? Date.now();

const makeBaseSession = (event: CameraCaptureEvent): CameraCaptureSession => {
  if (event.type !== 'permission_requested') {
    throw new Error(
      'camera capture session must start with permission_requested',
    );
  }

  return {
    sessionId: event.sessionId,
    workspaceId: event.workspaceId,
    roomId: event.roomId,
    participantId: event.participantId,
    status: 'requesting_permission',
    permission: 'prompt',
    framesCaptured: 0,
    updatedAt: nowOrCurrent(event.now),
  };
};

export function reduceCameraCaptureSession(
  session: CameraCaptureSession | undefined,
  event: CameraCaptureEvent,
): CameraCaptureSession {
  if (session && session.sessionId !== event.sessionId) {
    return session;
  }

  const current = session ?? makeBaseSession(event);
  const updatedAt = nowOrCurrent(event.now);

  switch (event.type) {
    case 'permission_requested':
      return {
        ...current,
        status: 'requesting_permission',
        permission: 'prompt',
        error: undefined,
        updatedAt,
      };

    case 'permission_granted':
      return {
        ...current,
        status: 'ready',
        permission: 'granted',
        deviceLabel: event.deviceLabel,
        error: undefined,
        updatedAt,
      };

    case 'permission_denied':
      return {
        ...current,
        status: 'failed',
        permission: 'denied',
        error: event.error,
        updatedAt,
      };

    case 'preview_started':
      return {
        ...current,
        status: 'previewing',
        width: event.width ?? current.width,
        height: event.height ?? current.height,
        error: undefined,
        updatedAt,
      };

    case 'snapshot_requested':
      return {
        ...current,
        status: 'capturing',
        prompt: event.prompt ?? current.prompt,
        error: undefined,
        updatedAt,
      };

    case 'snapshot_captured':
      return {
        ...current,
        status: 'awaiting_confirmation',
        latestArtifactId: event.artifactId,
        mimeType: event.mimeType,
        width: event.width ?? current.width,
        height: event.height ?? current.height,
        capturedAt: event.capturedAt,
        framesCaptured: current.framesCaptured + 1,
        error: undefined,
        updatedAt,
      };

    case 'start_sampling':
      return {
        ...current,
        status: 'sampling',
        samplingIntervalMs: event.intervalMs,
        error: undefined,
        updatedAt,
      };

    case 'sample_captured':
      return {
        ...current,
        status: 'sampling',
        latestArtifactId: event.artifactId,
        mimeType: event.mimeType,
        width: event.width ?? current.width,
        height: event.height ?? current.height,
        capturedAt: event.capturedAt,
        framesCaptured: current.framesCaptured + 1,
        error: undefined,
        updatedAt,
      };

    case 'pause_sampling':
      return {
        ...current,
        status: 'paused',
        updatedAt,
      };

    case 'confirm_send':
      return {
        ...current,
        status: 'sending',
        updatedAt,
      };

    case 'sent':
    case 'stop':
      return {
        ...current,
        status: 'completed',
        updatedAt,
      };

    case 'cancel':
      return {
        ...current,
        status: 'cancelled',
        updatedAt,
      };

    case 'fail':
      return {
        ...current,
        status: 'failed',
        error: event.error,
        updatedAt,
      };

    default:
      return current;
  }
}

export function toCameraMessageDraft(
  session: CameraCaptureSession,
): CameraMessageDraft {
  if (!session.latestArtifactId) {
    throw new Error('camera message draft requires a captured artifact');
  }

  const metadata: Record<string, string> = {
    inputMode: 'camera',
    cameraSessionId: session.sessionId,
    visionArtifactId: session.latestArtifactId,
  };

  if (session.mimeType) metadata.mimeType = session.mimeType;
  if (session.width) metadata.width = String(session.width);
  if (session.height) metadata.height = String(session.height);

  return {
    roomId: session.roomId,
    content: session.prompt?.trim() || '请分析这张图像。',
    metadata,
  };
}
