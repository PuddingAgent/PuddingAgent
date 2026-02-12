export type VoiceCaptureStatus =
  | 'idle'
  | 'requesting_permission'
  | 'ready'
  | 'recording'
  | 'transcribing'
  | 'awaiting_confirmation'
  | 'sending'
  | 'completed'
  | 'cancelled'
  | 'failed';

export type VoiceCapturePermission =
  | 'unknown'
  | 'prompt'
  | 'granted'
  | 'denied';

export interface VoiceCaptureSession {
  sessionId: string;
  workspaceId: string;
  roomId: string;
  participantId: string;
  status: VoiceCaptureStatus;
  permission: VoiceCapturePermission;
  deviceLabel?: string;
  provider?: string;
  model?: string;
  language?: string;
  emotion?: string;
  format?: string;
  sampleRate?: number;
  framesCaptured: number;
  totalDurationMs?: number;
  interimTranscript?: string;
  finalTranscript?: string;
  error?: string;
  updatedAt: number;
}

export type VoiceCaptureEvent =
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
      type: 'start_recording';
      sessionId: string;
      sampleRate: number;
      format: string;
      provider?: string;
      model?: string;
      language?: string;
      now?: number;
    }
  | {
      type: 'audio_frame';
      sessionId: string;
      sequence: number;
      durationMs?: number;
      now?: number;
    }
  | {
      type: 'interim_transcript';
      sessionId: string;
      text: string;
      now?: number;
    }
  | {
      type: 'final_transcript';
      sessionId: string;
      text: string;
      emotion?: string;
      language?: string;
      now?: number;
    }
  | {
      type: 'confirm_send' | 'sent' | 'cancel';
      sessionId: string;
      now?: number;
    }
  | {
      type: 'fail';
      sessionId: string;
      error: string;
      now?: number;
    };

export interface VoiceMessageDraft {
  roomId: string;
  content: string;
  metadata: Record<string, string>;
}

const nowOrCurrent = (now?: number) => now ?? Date.now();

const makeBaseSession = (event: VoiceCaptureEvent): VoiceCaptureSession => {
  if (event.type !== 'permission_requested') {
    throw new Error(
      'voice capture session must start with permission_requested',
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

export function reduceVoiceCaptureSession(
  session: VoiceCaptureSession | undefined,
  event: VoiceCaptureEvent,
): VoiceCaptureSession {
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

    case 'start_recording':
      return {
        ...current,
        status: 'recording',
        provider: event.provider ?? current.provider,
        model: event.model ?? current.model,
        language: event.language ?? current.language,
        sampleRate: event.sampleRate,
        format: event.format,
        framesCaptured: 0,
        totalDurationMs: 0,
        interimTranscript: undefined,
        finalTranscript: undefined,
        error: undefined,
        updatedAt,
      };

    case 'audio_frame':
      return {
        ...current,
        status: current.status === 'recording' ? 'recording' : 'transcribing',
        framesCaptured: Math.max(current.framesCaptured + 1, event.sequence),
        totalDurationMs:
          (current.totalDurationMs ?? 0) + (event.durationMs ?? 0),
        updatedAt,
      };

    case 'interim_transcript':
      return {
        ...current,
        status: 'transcribing',
        interimTranscript: event.text,
        error: undefined,
        updatedAt,
      };

    case 'final_transcript':
      return {
        ...current,
        status: 'awaiting_confirmation',
        finalTranscript: event.text,
        emotion: event.emotion ?? current.emotion,
        language: event.language ?? current.language,
        error: undefined,
        updatedAt,
      };

    case 'confirm_send':
      return {
        ...current,
        status: 'sending',
        updatedAt,
      };

    case 'sent':
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

export function toVoiceMessageDraft(
  session: VoiceCaptureSession,
): VoiceMessageDraft {
  const content = session.finalTranscript?.trim();
  if (!content) {
    throw new Error('voice message draft requires a final transcript');
  }

  const metadata: Record<string, string> = {
    inputMode: 'voice',
    voiceSessionId: session.sessionId,
  };

  if (session.provider) metadata.asrProvider = session.provider;
  if (session.model) metadata.asrModel = session.model;
  if (session.language) metadata.language = session.language;
  if (session.emotion) metadata.emotion = session.emotion;

  return {
    roomId: session.roomId,
    content,
    metadata,
  };
}
