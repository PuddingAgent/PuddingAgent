import React, { useMemo } from 'react';
import type { RichBlockProps } from './RichBlockRegistry';
import { validatePanelSpec } from '../../panel/spec';
import { PanelView } from '../../panel/PanelView';
import { useUiSubmission } from '../../uiSubmission/UiSubmissionContext';

/**
 * ```panel 围栏渲染器 —— 让交互面板在**对话里**可达。
 *
 * 之前面板子系统只有 `/chat/panel-demo` 能点到：渲染器、标注、提交协议都写好了，
 * 但对话里没有任何入口能把 spec 送进去。这个组件就是那个入口。
 *
 * ## 三条约束
 *
 * 1. **只吃声明式 JSON**：模型产出的是 spec，不是代码/HTML 片段；
 *    HTML 草稿仍走 `DraftPanel` 的 sandbox，不在这里放开。
 * 2. **校验失败显式降级**：列出前 3 条问题 + 保留原文，不渲染半成品面板
 *    （半成品会被误以为"面板坏了"）。
 * 3. **没有回传通道就禁用提交**：不给一个点了没反应的按钮。
 *    宿主没接 `UiSubmissionProvider` 时，界面明确说明原因。
 */
const MAX_LISTED_ISSUES = 3;

export const PanelBlock: React.FC<RichBlockProps> = ({ code }) => {
  const ui = useUiSubmission();

  const parsed = useMemo(() => {
    let raw: unknown;
    try {
      raw = JSON.parse(code);
    } catch (error) {
      return { ok: false as const, reason: `JSON 解析失败：${(error as Error).message}` };
    }
    const result = validatePanelSpec(raw);
    if (!result.ok || !result.spec) {
      const listed = result.issues
        .filter((issue) => issue.severity === 'error')
        .slice(0, MAX_LISTED_ISSUES)
        .map((issue) => `${issue.path}：${issue.message}`)
        .join('\n');
      return { ok: false as const, reason: listed || '面板 spec 校验未通过' };
    }
    return { ok: true as const, spec: result.spec };
  }, [code]);

  if (!parsed.ok) {
    return (
      <div
        data-testid="panel-block-error"
        style={{
          padding: 12,
          border: '1px dashed #f53f3f',
          borderRadius: 8,
          fontSize: 12,
          color: '#f53f3f',
          background: '#fff1f0',
          whiteSpace: 'pre-wrap',
        }}
      >
        面板 spec 无效：{parsed.reason}
      </div>
    );
  }

  if (!ui) {
    // 面板照常可见可填，但提交被禁用并说明原因 —— 比隐藏整块更有用：
    // 用户至少能先看清要填什么，也能知道"这不是磁盘错误，是通道没接"。
    return (
      <div data-testid="panel-block-no-channel">
        <div
          role="note"
          style={{
            marginBottom: 8,
            padding: '8px 10px',
            border: '1px dashed #ff7d00',
            borderRadius: 8,
            fontSize: 12,
            color: '#ad4e00',
            background: '#fff7e8',
          }}
        >
          面板可以填写，但当前没有回传通道（宿主未接入），提交已禁用。
        </div>
        <PanelView spec={parsed.spec} onSubmit={() => undefined} disabled />
      </div>
    );
  }

  return (
    <div data-testid="panel-block">
      <PanelView spec={parsed.spec} onSubmit={(payload) => ui.submit(payload)} />
    </div>
  );
};

export default PanelBlock;
