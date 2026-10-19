import React from 'react';

/**
 * 富媒体块的错误边界。
 *
 * 为什么必须单独包一层：`React.lazy` 加载失败（chunk 404、网络中断、
 * 版本不匹配）会 **throw 到最近的 ErrorBoundary**。若没有边界，一次
 * 动态 import 失败会把整条消息甚至整个消息列表打白——而"留白"正是
 * 设计文档里明令禁止的降级形态。
 *
 * 降级目标：显示原因 + 原始代码块，用户至少还能复制走。
 */
interface RichBlockBoundaryProps {
  code: string;
  children: React.ReactNode;
}

interface RichBlockBoundaryState {
  reason: string | null;
}

export class RichBlockBoundary extends React.Component<
  RichBlockBoundaryProps,
  RichBlockBoundaryState
> {
  state: RichBlockBoundaryState = { reason: null };

  static getDerivedStateFromError(error: unknown): RichBlockBoundaryState {
    return {
      reason: error instanceof Error ? error.message : String(error ?? 'unknown'),
    };
  }

  render(): React.ReactNode {
    if (this.state.reason) {
      return (
        <div data-testid="richblock-fallback" role="note">
          <p style={{ margin: '0 0 6px', fontSize: 12, opacity: 0.7 }}>
            可视化组件加载失败，已回退为原文：{this.state.reason}
          </p>
          <pre style={{ margin: 0, overflow: 'auto' }}>
            <code>{this.props.code}</code>
          </pre>
        </div>
      );
    }

    return this.props.children;
  }
}

export default RichBlockBoundary;
