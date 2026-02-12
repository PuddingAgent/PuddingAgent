// ── Composer Store ─────────────────────────────────────────────
// ADR-054 Step 6: 管理 inputValue、draftMetadata、submitting 状态。
// 输入变化只通知 composer 订阅者，不触发 MessageList 重渲染。

import type { ComposerState, RuntimeStore } from './types';

// ── Store 接口 ───────────────────────────────────────────

export interface ComposerStoreActions {
  setInputValue(value: string): void;
  startSubmit(): void;
  endSubmit(): void;
  setDraftMetadata(meta: Record<string, string>): void;
  clear(): void;
}

export type ComposerStore = RuntimeStore<ComposerState> & ComposerStoreActions;

// ── Store 创建 ───────────────────────────────────────────

export function createComposerStore(
  initial?: Partial<ComposerState>,
): ComposerStore {
  let state: ComposerState = {
    inputValue: '',
    disabled: false,
    submitting: false,
    ...initial,
  };
  const listeners = new Set<() => void>();

  function notify(): void {
    for (const l of listeners) {
      l();
    }
  }

  function setState(partial: Partial<ComposerState>): void {
    const next = { ...state, ...partial };
    state = next;
    notify();
  }

  function subscribe(listener: () => void): () => void {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  }

  function getSnapshot(): ComposerState {
    return state;
  }

  function setInputValue(value: string): void {
    if (state.inputValue === value) return;
    setState({ inputValue: value });
  }

  function startSubmit(): void {
    setState({ submitting: true });
  }

  function endSubmit(): void {
    setState({ submitting: false, inputValue: '' });
  }

  function setDraftMetadata(meta: Record<string, string>): void {
    if (JSON.stringify(state.draftMetadata) === JSON.stringify(meta)) return;
    setState({ draftMetadata: meta });
  }

  function clear(): void {
    setState({ inputValue: '', submitting: false, draftMetadata: undefined });
  }

  return {
    subscribe,
    getSnapshot,
    setInputValue,
    startSubmit,
    endSubmit,
    setDraftMetadata,
    clear,
  };
}
