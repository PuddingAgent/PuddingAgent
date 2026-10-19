// ── 右侧产物区状态 ─────────────────────────────────────────────
// 为什么要 context，而不是 prop drilling：
//
// 从 chat 页到长消息卡要穿过 4 层（index → ChatLayout → ChatMain →
// MessageList → AgentMessageBubble → MessageItem）。每层都加一个"透传用的
// prop"，会让 4 个组件的签名都被一个 UI 动作污染。
//
// 这是典型的「跨层级 UI 动作」，用 context 表达最直接：
// Provider 持有"当前右侧文档"，任何层级都能 openDocument。
//
// 没挂 Provider 时（单测、demo）hook 返回 null，调用方安全降级——
// 长消息卡因此不会出现"点了没反应"的死按钮。
// ─────────────────────────────────────────────────────────────

import React, { createContext, useContext, useMemo, useState } from 'react';

export interface SideDocument {
  name: string;
  markdown: string;
  /** 来源标识（消息 id / 文件路径），便于将来做"同一产物"联动。 */
  sourceId?: string;
}

export interface SideArtifactApi {
  open: boolean;
  document: SideDocument | null;
  openDocument: (doc: SideDocument) => void;
  close: () => void;
}

const SideArtifactContext = createContext<SideArtifactApi | null>(null);

export const SideArtifactProvider: React.FC<{ children: React.ReactNode }> = ({
  children,
}) => {
  const [state, setState] = useState<{ open: boolean; document: SideDocument | null }>({
    open: false,
    document: null,
  });

  const value = useMemo<SideArtifactApi>(
    () => ({
      open: state.open,
      document: state.document,
      openDocument: (doc) => setState({ open: true, document: doc }),
      // 关闭只收起面板，保留 document：再次打开时还在，符合"旁路预览"的预期。
      close: () => setState((prev) => ({ ...prev, open: false })),
    }),
    [state],
  );

  return (
    <SideArtifactContext.Provider value={value}>{children}</SideArtifactContext.Provider>
  );
};

/** 未挂 Provider 时返回 null——调用方必须显式降级，不能假设一定有。 */
export function useSideArtifact(): SideArtifactApi | null {
  return useContext(SideArtifactContext);
}
