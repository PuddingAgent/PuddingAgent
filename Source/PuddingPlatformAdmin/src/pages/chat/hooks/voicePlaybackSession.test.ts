import {
  reduceVoicePlaybackSession,
  type VoicePlaybackSession,
} from './voicePlaybackSession';

describe('voicePlaybackSession', () => {
  it('tracks a DashScope TTS request without exposing provider secrets to the UI', () => {
    const session = reduceVoicePlaybackSession(undefined, {
      type: 'tts_requested',
      messageId: 'msg-1',
      deliveryId: 'delivery-1',
      provider: 'dashscope',
      model: 'qwen3-tts-flash',
      voice: 'Cherry',
      languageType: 'Chinese',
      now: 100,
    });

    expect(session).toMatchObject({
      messageId: 'msg-1',
      deliveryId: 'delivery-1',
      provider: 'dashscope',
      model: 'qwen3-tts-flash',
      voice: 'Cherry',
      languageType: 'Chinese',
      status: 'synthesizing',
      updatedAt: 100,
    });
    expect('apiKey' in session).toBe(false);
  });

  it('moves a non-realtime audio URL through playback and completion states', () => {
    const requested = reduceVoicePlaybackSession(undefined, {
      type: 'tts_requested',
      messageId: 'msg-2',
      now: 200,
    });

    const ready = reduceVoicePlaybackSession(requested, {
      type: 'audio_url_ready',
      messageId: 'msg-2',
      url: 'https://example.invalid/audio.wav',
      expiresAt: 86_600,
      now: 210,
    });
    expect(ready.status).toBe('buffering');
    expect(ready.source).toEqual({
      kind: 'url',
      url: 'https://example.invalid/audio.wav',
      expiresAt: 86_600,
    });

    const playing = reduceVoicePlaybackSession(ready, {
      type: 'play',
      messageId: 'msg-2',
      now: 220,
    });
    expect(playing.status).toBe('playing');

    const paused = reduceVoicePlaybackSession(playing, {
      type: 'pause',
      messageId: 'msg-2',
      now: 230,
    });
    expect(paused.status).toBe('paused');

    const completed = reduceVoicePlaybackSession(paused, {
      type: 'complete',
      messageId: 'msg-2',
      now: 240,
    });
    expect(completed.status).toBe('completed');
  });

  it('tracks streaming PCM chunks for low-latency playback', () => {
    const initial: VoicePlaybackSession = {
      messageId: 'msg-3',
      status: 'synthesizing',
      updatedAt: 300,
    };

    const firstChunk = reduceVoicePlaybackSession(initial, {
      type: 'pcm_chunk',
      messageId: 'msg-3',
      sampleRate: 24_000,
      now: 310,
    });
    const secondChunk = reduceVoicePlaybackSession(firstChunk, {
      type: 'pcm_chunk',
      messageId: 'msg-3',
      sampleRate: 24_000,
      now: 320,
    });

    expect(secondChunk.status).toBe('buffering');
    expect(secondChunk.source).toEqual({
      kind: 'pcm-stream',
      sampleRate: 24_000,
      chunksReceived: 2,
    });
  });

  it('marks expiring provider URLs and failures explicitly', () => {
    const ready = reduceVoicePlaybackSession(undefined, {
      type: 'audio_url_ready',
      messageId: 'msg-4',
      url: 'https://example.invalid/audio.wav',
      expiresAt: 1000,
      now: 400,
    });

    const expired = reduceVoicePlaybackSession(ready, {
      type: 'expire',
      messageId: 'msg-4',
      now: 1001,
    });
    expect(expired.status).toBe('expired');
    expect(expired.source).toEqual(ready.source);

    const failed = reduceVoicePlaybackSession(expired, {
      type: 'fail',
      messageId: 'msg-4',
      error: 'tts provider rejected the request',
      now: 1010,
    });
    expect(failed.status).toBe('failed');
    expect(failed.error).toBe('tts provider rejected the request');
  });
});
