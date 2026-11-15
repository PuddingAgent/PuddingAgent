import { useCallback, useEffect, useId, useSyncExternalStore } from 'react';
import { ttsPlayback } from './ttsPlaybackController';

/** All message, panel and automatic speech share one cancellable playback owner. */
export function useTtsPlayer(scope?: string) {
  const id = useId();
  const owner = `${scope ?? 'speech'}:${id}`;
  const state = useSyncExternalStore(ttsPlayback.subscribe, ttsPlayback.getSnapshot);
  useEffect(() => () => ttsPlayback.stop(owner), [owner]);
  const stop = useCallback(() => ttsPlayback.stop(owner), [owner]);
  const speak = useCallback((text: string, _voice?: string) => ttsPlayback.speak(owner, text), [owner]);
  return { speak, stop, playing: state.owner === owner && state.status === 'playing',
    loading: state.owner === owner && state.status === 'loading', error: state.owner === owner ? state.error : undefined };
}
