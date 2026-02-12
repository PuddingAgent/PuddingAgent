export type VoicePlaybackStatus =
  | 'idle'
  | 'queued'
  | 'synthesizing'
  | 'buffering'
  | 'playing'
  | 'paused'
  | 'completed'
  | 'failed'
  | 'expired';

export type VoiceProvider = 'dashscope' | 'local' | 'unknown' | (string & {});

export type VoicePlaybackSource =
  | {
      kind: 'url';
      url: string;
      expiresAt?: number;
    }
  | {
      kind: 'pcm-stream';
      sampleRate: number;
      chunksReceived: number;
    };

export interface VoicePlaybackSession {
  messageId: string;
  deliveryId?: string;
  provider?: VoiceProvider;
  model?: string;
  voice?: string;
  languageType?: string;
  source?: VoicePlaybackSource;
  status: VoicePlaybackStatus;
  error?: string;
  updatedAt: number;
}

export type VoicePlaybackEvent =
  | {
      type: 'tts_requested';
      messageId: string;
      deliveryId?: string;
      provider?: VoiceProvider;
      model?: string;
      voice?: string;
      languageType?: string;
      now?: number;
    }
  | {
      type: 'audio_url_ready';
      messageId: string;
      url: string;
      expiresAt?: number;
      now?: number;
    }
  | {
      type: 'pcm_chunk';
      messageId: string;
      sampleRate: number;
      now?: number;
    }
  | {
      type: 'play' | 'pause' | 'complete' | 'expire';
      messageId: string;
      now?: number;
    }
  | {
      type: 'fail';
      messageId: string;
      error: string;
      now?: number;
    };

const nowOrCurrent = (now?: number) => now ?? Date.now();

const makeBaseSession = (event: VoicePlaybackEvent): VoicePlaybackSession => ({
  messageId: event.messageId,
  status: 'idle',
  updatedAt: nowOrCurrent(event.now),
});

export function reduceVoicePlaybackSession(
  session: VoicePlaybackSession | undefined,
  event: VoicePlaybackEvent,
): VoicePlaybackSession {
  if (session && session.messageId !== event.messageId) {
    return session;
  }

  const current = session ?? makeBaseSession(event);
  const updatedAt = nowOrCurrent(event.now);

  switch (event.type) {
    case 'tts_requested':
      return {
        ...current,
        messageId: event.messageId,
        deliveryId: event.deliveryId ?? current.deliveryId,
        provider: event.provider ?? current.provider,
        model: event.model ?? current.model,
        voice: event.voice ?? current.voice,
        languageType: event.languageType ?? current.languageType,
        status: 'synthesizing',
        error: undefined,
        updatedAt,
      };

    case 'audio_url_ready':
      return {
        ...current,
        messageId: event.messageId,
        source: {
          kind: 'url',
          url: event.url,
          expiresAt: event.expiresAt,
        },
        status: 'buffering',
        error: undefined,
        updatedAt,
      };

    case 'pcm_chunk': {
      const chunksReceived =
        current.source?.kind === 'pcm-stream'
          ? current.source.chunksReceived + 1
          : 1;

      return {
        ...current,
        messageId: event.messageId,
        source: {
          kind: 'pcm-stream',
          sampleRate: event.sampleRate,
          chunksReceived,
        },
        status: 'buffering',
        error: undefined,
        updatedAt,
      };
    }

    case 'play':
      return {
        ...current,
        status: 'playing',
        error: undefined,
        updatedAt,
      };

    case 'pause':
      return {
        ...current,
        status: 'paused',
        updatedAt,
      };

    case 'complete':
      return {
        ...current,
        status: 'completed',
        error: undefined,
        updatedAt,
      };

    case 'expire':
      return {
        ...current,
        status: 'expired',
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
