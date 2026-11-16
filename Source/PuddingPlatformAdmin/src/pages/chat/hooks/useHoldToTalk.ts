import { useCallback, useEffect, useRef, useState } from 'react';

type Hold = { source: 'keyboard' | 'pointer'; pointerId?: number; started: number };

/**
 * 输入框/可编辑区域里的按键属于文字输入，不能当成语音快捷键。
 *
 * 硬伤背景：`Ctrl+Shift` 是 Windows 切换输入法的组合键。用户打中文时
 * 先按 `Ctrl+Shift` 切输入法、再按空格上屏，历史上会被这里当成
 * “按住说话 → 松开发送”，于是**草稿被提前发出去、输入被截断**。
 */
const isEditableTarget = (element: Element | null): boolean => {
  if (!element) return false;
  const tag = element.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
  return (element as HTMLElement).isContentEditable === true;
};
/** Chat-local gesture ownership; startup/finalization remain with the capture lifecycle. */
export function useHoldToTalk(options: { blocked: boolean; cancellable?: boolean; start: () => void; finish: () => void; cancel: () => void; shortPress: () => void }) {
  const current = useRef(options); current.current = options;
  const active = useRef<Hold | null>(null);
  const [held, setHeld] = useState(false);
  const end = useCallback((send: boolean) => {
    const hold = active.current; if (!hold) return;
    active.current = null; setHeld(false);
    if (!send) current.current.cancel();
    else if (Date.now() - hold.started < 250) { current.current.cancel(); current.current.shortPress(); }
    else current.current.finish();
  }, []);
  const beginPointer = useCallback((event: React.PointerEvent) => {
    if (current.current.blocked || active.current) return;
    event.preventDefault();
    active.current = { source: 'pointer', pointerId: event.pointerId, started: Date.now() };
    event.currentTarget.setPointerCapture?.(event.pointerId);
    setHeld(true); current.current.start();
  }, []);
  useEffect(() => {
    let suppressMenu = false;
    const down = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && (active.current || current.current.cancellable)) { event.preventDefault(); if (active.current) end(false); else current.current.cancel(); return; }
      if (event.code !== 'Space' || !event.ctrlKey || !event.shiftKey || event.altKey || event.metaKey || event.isComposing) return;
      if (document.activeElement?.closest('[role="dialog"], [aria-modal="true"]')) return;
      // 焦点在输入框内时一律不接管 Space：那里的按键是文字输入，不是语音手势。
      if (isEditableTarget(document.activeElement)) return;
      if (current.current.blocked || active.current || event.repeat) return;
      event.preventDefault();
      active.current = { source: 'keyboard', started: Date.now() }; setHeld(true); current.current.start();
    };
    const up = (event: KeyboardEvent) => { if (active.current?.source === 'keyboard' && event.code === 'Space') { event.preventDefault(); end(true); } };
    const pointerUp = (event: PointerEvent) => { if (active.current?.source === 'pointer' && event.pointerId === active.current.pointerId) end(true); };
    const pointerCancel = (event: PointerEvent) => { if (active.current?.source === 'pointer' && event.pointerId === active.current.pointerId) end(false); };
    const right = (event: MouseEvent) => { if (event.button === 2 && active.current) { event.preventDefault(); suppressMenu = true; end(false); } };
    const context = (event: MouseEvent) => { if (suppressMenu) { event.preventDefault(); suppressMenu = false; } };
    const blur = () => { if (active.current) end(false); else if (current.current.cancellable) current.current.cancel(); };
    const hidden = () => { if (document.hidden) blur(); };
    window.addEventListener('keydown', down, true); window.addEventListener('keyup', up, true);
    window.addEventListener('pointerup', pointerUp); window.addEventListener('pointercancel', pointerCancel);
    window.addEventListener('mousedown', right, true); window.addEventListener('contextmenu', context);
    window.addEventListener('blur', blur); document.addEventListener('visibilitychange', hidden);
    return () => {
      active.current = null;
      window.removeEventListener('keydown', down, true); window.removeEventListener('keyup', up, true);
      window.removeEventListener('pointerup', pointerUp); window.removeEventListener('pointercancel', pointerCancel);
      window.removeEventListener('mousedown', right, true); window.removeEventListener('contextmenu', context);
      window.removeEventListener('blur', blur); document.removeEventListener('visibilitychange', hidden);
    };
  }, [end]);
  return { held, beginPointer, cancelGesture: () => end(false) };
}
