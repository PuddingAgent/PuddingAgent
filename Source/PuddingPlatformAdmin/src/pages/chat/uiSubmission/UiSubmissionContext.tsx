// ── 面板提交回传通道 ────────────────────────────────────────
//
// 交互面板的价值在"填完能交回来"。这条通道必须由**宿主**提供：
// 本层不知道提交内容该去哪（写输入框？直接发送？落盘？）。
//
// ## 为什么单独一个 context，而不是塞进 ArtifactDataContext
//
// `ArtifactDataContext` 的语义是"按 artifactId 取数据行"，是**读**；
// 这里是**写**（把用户填的东西送回对话）。两件事的失效方式不同：
// 读不到数据 → 图表显式提示；没有回传通道 → 面板必须禁用提交，
// 而不是给一个点了没反应的按钮。分开表达才不容易混淆。
//
// ## 为什么 value 必须引用稳定
//
// 这个 context 会被**每条消息里的每个面板**消费。如果 value 每次渲染
// 都新建对象，长对话里所有面板会跟着父组件一起重渲染——不报错，只是
// 越用越卡。所以回调用 ref 锁住，对外暴露的对象空依赖 memo。
// ─────────────────────────────────────────────────────────────

import React, { createContext, useContext, useMemo, useRef } from 'react';
import type { UiSubmissionPayload } from '../panel/submission';

export interface UiSubmissionActions {
  /** 把一次面板提交交给宿主。 */
  submit: (payload: UiSubmissionPayload) => void;
}

const UiSubmissionContext = createContext<UiSubmissionActions | null>(null);

export interface UiSubmissionProviderProps {
  /** 宿主实现：通常是把 buildUiSubmission(...) 的结果写回输入框。 */
  onSubmit: (payload: UiSubmissionPayload) => void;
  children: React.ReactNode;
}

export const UiSubmissionProvider: React.FC<UiSubmissionProviderProps> = ({
  onSubmit,
  children,
}) => {
  // 回调锁进 ref：调用方传内联箭头函数是常态，直接进依赖会让 value 每帧变。
  const latest = useRef(onSubmit);
  latest.current = onSubmit;

  const actions = useMemo<UiSubmissionActions>(
    () => ({
      submit: (payload) => latest.current(payload),
    }),
    [],
  );

  return <UiSubmissionContext.Provider value={actions}>{children}</UiSubmissionContext.Provider>;
};

/**
 * 读取回传通道。**没有 Provider 时返回 null** —— 面板据此禁用提交，
 * 绝不渲染一个点了没反应的按钮。
 */
export function useUiSubmission(): UiSubmissionActions | null {
  return useContext(UiSubmissionContext);
}
