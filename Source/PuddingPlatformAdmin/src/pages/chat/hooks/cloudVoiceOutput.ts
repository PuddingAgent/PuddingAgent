import type { BrowserVoiceOutputAdapter } from './browserVoiceOutput';
import { ttsPlayback } from './ttsPlaybackController';

export const defaultCloudVoiceOutputAdapter: BrowserVoiceOutputAdapter = {
  isSupported: () => Boolean(globalThis.AudioContext),
  speak(text, handlers = {}) {
    const owner = `voice-panel:${Date.now()}`;
    void ttsPlayback.speak(owner, text);
    let active = true;
    let started = false;
    const unsubscribe = ttsPlayback.subscribe(() => {
      const state = ttsPlayback.getSnapshot();
      if (state.owner === owner && state.status === 'playing' && !started) { started = true; handlers.onStart?.(); }
      if (state.owner === owner && state.status === 'failed') { handlers.onError?.(state.error || '朗读失败'); unsubscribe(); }
      if (state.owner !== owner && active) { active = false; handlers.onComplete?.(); unsubscribe(); }
    });
    return { stop: () => { unsubscribe(); ttsPlayback.stop(owner); } };
  },
};
