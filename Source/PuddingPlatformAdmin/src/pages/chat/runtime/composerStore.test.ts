// ── Composer Store 单测 ────────────────────────────────────────
// ADR-054 Step 6: 验证输入隔离、submit 生命周期、相同值不重绘

import { createComposerStore } from './composerStore';

describe('ComposerStore', () => {
  it('initial state has empty inputValue', () => {
    const store = createComposerStore();
    expect(store.getSnapshot().inputValue).toBe('');
    expect(store.getSnapshot().submitting).toBe(false);
    expect(store.getSnapshot().disabled).toBe(false);
  });

  it('setInputValue updates inputValue', () => {
    const store = createComposerStore();
    store.setInputValue('hello');
    expect(store.getSnapshot().inputValue).toBe('hello');
  });

  it('same inputValue does not trigger notification', () => {
    const store = createComposerStore();
    const listener = jest.fn();
    store.subscribe(listener);

    store.setInputValue('hello');
    expect(listener).toHaveBeenCalledTimes(1);

    store.setInputValue('hello');
    expect(listener).toHaveBeenCalledTimes(1); // still 1
  });

  it('startSubmit sets submitting to true', () => {
    const store = createComposerStore();
    store.startSubmit();
    expect(store.getSnapshot().submitting).toBe(true);
  });

  it('endSubmit clears inputValue and sets submitting false', () => {
    const store = createComposerStore();
    store.setInputValue('send this');
    store.startSubmit();
    store.endSubmit();

    const snap = store.getSnapshot();
    expect(snap.submitting).toBe(false);
    expect(snap.inputValue).toBe('');
  });

  it('clear resets all state', () => {
    const store = createComposerStore();
    store.setInputValue('text');
    store.startSubmit();
    store.setDraftMetadata({ voice: 'enabled' });
    store.clear();

    const snap = store.getSnapshot();
    expect(snap.inputValue).toBe('');
    expect(snap.submitting).toBe(false);
  });

  it('subscribe and unsubscribe work correctly', () => {
    const store = createComposerStore();
    const listener = jest.fn();
    const unsub = store.subscribe(listener);

    store.setInputValue('a');
    expect(listener).toHaveBeenCalledTimes(1);

    unsub();
    store.setInputValue('b');
    expect(listener).toHaveBeenCalledTimes(1); // no longer called
  });

  it('setDraftMetadata stores metadata', () => {
    const store = createComposerStore();
    store.setDraftMetadata({ lang: 'zh', voice: 'enabled' });
    expect(store.getSnapshot().draftMetadata).toEqual({
      lang: 'zh',
      voice: 'enabled',
    });
  });
});
