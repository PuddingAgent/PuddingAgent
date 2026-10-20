// ── 右侧产物区状态 ─────────────────────────────────────────────
// 为什么要 context，而不是 prop drilling：
//
// 从 chat 页到长消息卡要穿过 4 层（index → ChatLayout → ChatMain →
// MessageList → AgentMessageBubble → MessageItem）。每层都加一个"透传用的
// prop"，会让 4 个组件的签名都被一个 UI 动作污染。
//
// ## 为什么拆成两个 context（2026-10-19 外部评审后调整）
//
// 评审提出"context 承载跨层 UI 动作最容易造成隐蔽的性能回归"。具体机制是：
// 单 context 的 value 每次 state 变化都新建 → **所有消费者**都重渲染，
// 哪怕它只想要 `openDocument`、根本不关心当前打开的是哪个文档。
//
// 长消息卡只消费动作。如果它跟着文档内容一起重渲染，在长对话里就是
// 每条长消息都白跑一遍 —— 不报错、不崩溃，只是越来越卡。
//
// 所以拆成：
//   - ActionsCtx：引用**永久稳定**（useMemo 空依赖），只放 openDocument / close；
//   - StateCtx：会变的部分（open / document）。
//
// 只消费动作的组件从此与文档内容无关。
//
// 没挂 Provider 时（单测、demo）hook 返回 null，调用方安全降级——
// 长消息卡因此不会出现"点了没反应"的死按钮。
// ─────────────────────────────────────────────────────────────

import React, { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react';

export interface SideDocument {
  name: string;
  markdown: string;
  /**
   * 真实磁盘路径。**只有来自文件的文档才有**。
   *
   * 曾经踩过的坑：长消息用「第一个标题 + .md」伪造文件名并允许「复制路径」，
   * 复制出来的是磁盘上根本不存在的路径 —— 正好是这个功能要解决的痛点反面。
   * 判据：没有 sourcePath 就不提供复制路径，也不该给名字加扩展名。
   */
  sourcePath?: string;
  /** 来源标识（消息 id / 文件路径），便于将来做"同一产物"联动。 */
  sourceId?: string;
}

export interface SideArtifactState {
  open: boolean;
  document: SideDocument | null;
}

export interface SideArtifactActions {
  openDocument: (doc: SideDocument) => void;
  close: () => void;
}

/** 合并视图（兼容旧调用方）。注意：它会在 state 变化时返回新对象。 */
export interface SideArtifactApi extends SideArtifactState, SideArtifactActions {}

const ActionsContext = createContext<SideArtifactActions | null>(null);
const StateContext = createContext<SideArtifactState | null>(null);

export const SideArtifactProvider: React.FC<{ children: React.ReactNode }> = ({
  children,
}) => {
  const [state, setState] = useState<SideArtifactState>({ open: false, document: null });

  // 触发入口：关闭时把焦点还回去。不记录的话，键盘用户一关面板
  // 焦点就掉到 body，再想打开得从页面开头 Tab 一遍。
  const triggerRef = useRef<HTMLElement | null>(null);

  // ActionsCtx 的 value 必须引用稳定，所以这两个函数不能读 state，
  // 也不能依赖 triggerRef 之外的东西。
  const actions = useMemo<SideArtifactActions>(
    () => ({
      openDocument: (nextDoc) => {
        const active = typeof window === 'undefined' ? null : window.document.activeElement;
        triggerRef.current = active instanceof HTMLElement ? active : null;
        setState({ open: true, document: nextDoc });
      },
      // 关闭只收起面板，保留 document：再次打开时还在，符合"旁路预览"的预期。
      close: () => {
        setState((prev) => ({ ...prev, open: false }));
        const trigger = triggerRef.current;
        triggerRef.current = null;
        // isConnected：触发元素可能已经滚出列表被回收，那就别硬 focus 了。
        if (trigger?.isConnected) trigger.focus();
      },
    }),
    [],
  );

  // Esc 关闭。
  // 但**在输入框里不拦**：用户可能在面板内的文本框按 Esc 取消一次输入，
  // 那时把整个面板关掉是意外行为。
  useEffect(() => {
    if (!state.open) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      const target = event.target as HTMLElement | null;
      const tag = target?.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || target?.isContentEditable) return;
      actions.close();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [state.open, actions]);

  return (
    <ActionsContext.Provider value={actions}>
      <StateContext.Provider value={state}>{children}</StateContext.Provider>
    </ActionsContext.Provider>
  );
};

/** 只想要动作时用这个：不会因文档内容变化而重渲染。 */
export function useSideArtifactActions(): SideArtifactActions | null {
  return useContext(ActionsContext);
}

/** 只想读状态时用这个。 */
export function useSideArtifactState(): SideArtifactState | null {
  return useContext(StateContext);
}

/**
 * 合并视图，兼容旧调用方。
 * 新代码请按需选 `useSideArtifactActions` / `useSideArtifactState`——
 * 这个 hook 的返回值每次 state 变化都是新对象。
 */
export function useSideArtifact(): SideArtifactApi | null {
  const actions = useContext(ActionsContext);
  const state = useContext(StateContext);
  return useMemo(
    () => (actions && state ? { ...actions, ...state } : null),
    [actions, state],
  );
}
