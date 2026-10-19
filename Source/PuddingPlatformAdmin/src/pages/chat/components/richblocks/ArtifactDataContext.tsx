// ── 图表数据解析器注入 ─────────────────────────────────────────
// ```chart 围栏里 artifact 来源的图表需要 rows，而"读数据"是宿主的能力：
// 读取受工作区边界约束，富媒体层不碰存储。
//
// 为什么用 context 而不是 props：
// 从 chat 页到 code 渲染处要穿过 MessageList → AgentMessageBubble →
// MessageItem → IncrementalMarkdown → MarkdownBlock 五层，而这些层的 props
// 已经很长。这是一个"能力注入"，不是业务数据，context 更合适。
//
// ## value 必须稳定
//
// MarkdownBlock 用 useMemo 缓存 react-markdown 的 components，依赖里包含
// 这个 resolver。如果它每次渲染都是新对象，components 就会重建，
// **每条消息的 markdown 都会跟着重解析**——长对话里代价很大。
//
// 所以 Provider 内部用 ref 保存最新的 resolver，对外暴露一个**引用恒定**的
// 包装对象。调用方即使每次传新函数也不会拖累下游。
// ─────────────────────────────────────────────────────────────

import React, { createContext, useContext, useMemo, useRef } from 'react';

export interface ArtifactDataResolver {
  /** 按 artifactId 取出行数据；取不到返回 null（调用方会显示"没有数据行"）。 */
  resolveArtifactRows: (artifactId: string) => Array<Record<string, unknown>> | null;
}

const ArtifactDataContext = createContext<ArtifactDataResolver | null>(null);

export const ArtifactDataProvider: React.FC<{
  resolver: ArtifactDataResolver;
  children: React.ReactNode;
}> = ({ resolver, children }) => {
  const latest = useRef(resolver);
  latest.current = resolver;

  // 空依赖 → 引用恒定。内部转调 ref，所以行为仍是最新 resolver。
  const stable = useMemo<ArtifactDataResolver>(
    () => ({
      resolveArtifactRows: (artifactId) => latest.current.resolveArtifactRows(artifactId),
    }),
    [],
  );

  return (
    <ArtifactDataContext.Provider value={stable}>{children}</ArtifactDataContext.Provider>
  );
};

/** 未挂 Provider 时返回 null——图表会显式提示"需要宿主解析"，不会画空图。 */
export function useArtifactData(): ArtifactDataResolver | null {
  return useContext(ArtifactDataContext);
}
