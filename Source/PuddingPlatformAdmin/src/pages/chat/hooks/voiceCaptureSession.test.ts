import {
  reduceVoiceCaptureSession,
  toVoiceMessageDraft,
  type VoiceCaptureSession,
} from './voiceCaptureSession';

describe('voiceCaptureSession', () => {
  it('tracks explicit microphone permission before recording starts', () => {
    const requested = reduceVoiceCaptureSession(undefined, {
      type: 'permission_requested',
      sessionId: 'voice-1',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      now: 100,
    });

    expect(requested).toMatchObject({
      sessionId: 'voice-1',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'requesting_permission',
      permission: 'prompt',
      updatedAt: 100,
    });

    const granted = reduceVoiceCaptureSession(requested, {
      type: 'permission_granted',
      sessionId: 'voice-1',
      deviceLabel: 'Built-in Microphone',
      now: 110,
    });

    expect(granted.status).toBe('ready');
    expect(granted.permission).toBe('granted');
    expect(granted.deviceLabel).toBe('Built-in Microphone');
  });

  it('records audio frame counts without retaining raw audio bytes in UI state', () => {
    const ready: VoiceCaptureSession = {
      sessionId: 'voice-2',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'ready',
      permission: 'granted',
      framesCaptured: 0,
      updatedAt: 200,
    };

    const recording = reduceVoiceCaptureSession(ready, {
      type: 'start_recording',
      sessionId: 'voice-2',
      sampleRate: 16_000,
      format: 'pcm',
      now: 210,
    });
    const framed = reduceVoiceCaptureSession(recording, {
      type: 'audio_frame',
      sessionId: 'voice-2',
      sequence: 1,
      durationMs: 100,
      now: 220,
    });

    expect(framed.status).toBe('recording');
    expect(framed.sampleRate).toBe(16_000);
    expect(framed.format).toBe('pcm');
    expect(framed.framesCaptured).toBe(1);
    expect('audioBytes' in framed).toBe(false);
  });

  it('keeps interim and final transcripts separate until the user confirms sending', () => {
    const recording: VoiceCaptureSession = {
      sessionId: 'voice-3',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'recording',
      permission: 'granted',
      framesCaptured: 1,
      updatedAt: 300,
    };

    const interim = reduceVoiceCaptureSession(recording, {
      type: 'interim_transcript',
      sessionId: 'voice-3',
      text: '打开',
      now: 310,
    });
    const final = reduceVoiceCaptureSession(interim, {
      type: 'final_transcript',
      sessionId: 'voice-3',
      text: '打开空调。',
      emotion: 'neutral',
      now: 320,
    });

    expect(final.status).toBe('awaiting_confirmation');
    expect(final.interimTranscript).toBe('打开');
    expect(final.finalTranscript).toBe('打开空调。');
    expect(final.emotion).toBe('neutral');

    const sending = reduceVoiceCaptureSession(final, {
      type: 'confirm_send',
      sessionId: 'voice-3',
      now: 330,
    });
    expect(sending.status).toBe('sending');
  });

  it('builds a voice-originated message draft only from a final transcript', () => {
    const session: VoiceCaptureSession = {
      sessionId: 'voice-4',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      status: 'awaiting_confirmation',
      permission: 'granted',
      framesCaptured: 4,
      finalTranscript: '整理今天的记录。',
      language: 'zh',
      provider: 'dashscope',
      model: 'qwen3-asr-flash-realtime',
      emotion: 'neutral',
      updatedAt: 400,
    };

    const draft = toVoiceMessageDraft(session);

    expect(draft).toEqual({
      roomId: 'room-default',
      content: '整理今天的记录。',
      metadata: {
        inputMode: 'voice',
        voiceSessionId: 'voice-4',
        asrProvider: 'dashscope',
        asrModel: 'qwen3-asr-flash-realtime',
        language: 'zh',
        emotion: 'neutral',
      },
    });
  });

  it('handles denied permission, cancellation, and provider failures explicitly', () => {
    const requested = reduceVoiceCaptureSession(undefined, {
      type: 'permission_requested',
      sessionId: 'voice-5',
      workspaceId: 'default',
      roomId: 'room-default',
      participantId: 'user-owner',
      now: 500,
    });
    const denied = reduceVoiceCaptureSession(requested, {
      type: 'permission_denied',
      sessionId: 'voice-5',
      error: 'microphone permission denied',
      now: 510,
    });
    expect(denied.status).toBe('failed');
    expect(denied.permission).toBe('denied');
    expect(denied.error).toBe('microphone permission denied');

    const cancelled = reduceVoiceCaptureSession(denied, {
      type: 'cancel',
      sessionId: 'voice-5',
      now: 520,
    });
    expect(cancelled.status).toBe('cancelled');

    const failed = reduceVoiceCaptureSession(cancelled, {
      type: 'fail',
      sessionId: 'voice-5',
      error: 'asr websocket closed',
      now: 530,
    });
    expect(failed.status).toBe('failed');
    expect(failed.error).toBe('asr websocket closed');
  });
});
