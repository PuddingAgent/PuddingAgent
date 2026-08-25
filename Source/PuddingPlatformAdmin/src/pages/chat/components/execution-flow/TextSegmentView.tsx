// ── TextSegmentView：TurnContentStream 正文段（AgentTurnCard 重构）─────────
// 一个 TextBlock 的承载者：
//  - 已封闭段（后续出现活动/终态）→ 静态 MessageItem，绝不重新打字/重排；
//  - 当前尾部段且 terminal='none' → useTypewriterStreaming 平滑流式；
//  - key = node.key（稳定）：新正文段到达只 mount 新段，不触碰旧段。
// 正文排版沿用 agentBubbleNew 平铺规格（无白卡），与旧 answer bubble 同语言。
import React from 'react';
import type { MessageNode } from '../../projections/executionFlowProjector';
import { useTypewriterStreaming } from '../../hooks/useTypewriterStreaming';
import { useAgentStyles } from '../../styles/agent.styles';
import MessageItem from '../MessageItem';

interface StaticSegmentProps {
  text: string;
  workspaceId?: string;
  onContextMenu?: (e: React.MouseEvent) => void;
}

/** 已封闭正文段：静态渲染（不挂打字机定时器）。 */
const StaticTextSegment: React.FC<StaticSegmentProps> = ({
  text,
  workspaceId,
  onContextMenu,
}) => {
  const { styles, cx } = useAgentStyles();
  return (
    <div
      className={cx(styles.agentBubbleNew)}
      data-testid="turn-text-segment"
      onContextMenu={onContextMenu}
    >
      <MessageItem markdownText={text} isStreaming={false} workspaceId={workspaceId} />
    </div>
  );
};

interface StreamingSegmentProps extends StaticSegmentProps {
  isStreaming: boolean;
}

/** 流式尾部段：打字机平滑（B2 自适应速率，与旧 StreamingAnswer 同调度）。 */
const StreamingTextSegment: React.FC<StreamingSegmentProps> = ({
  text,
  isStreaming,
  workspaceId,
  onContextMenu,
}) => {
  const { styles, cx } = useAgentStyles();
  const typewriter = useTypewriterStreaming({
    text,
    isStreaming: Boolean(isStreaming),
  });
  return (
    <div
      className={cx(styles.agentBubbleNew, styles.agentBubbleStreaming)}
      data-testid="turn-text-segment"
      data-streaming="true"
      onContextMenu={onContextMenu}
    >
      <MessageItem
        markdownText={text}
        isStreaming={isStreaming}
        workspaceId={workspaceId}
        stableMarkdown={typewriter.stableMarkdown}
        liveText={typewriter.liveText}
        visibleLiveText={typewriter.visibleLiveText}
        visibleStartOffset={typewriter.visibleStartOffset}
      />
    </div>
  );
};

export interface TextSegmentViewProps {
  node: MessageNode;
  /** 尾部开放段（run 活跃 && terminal='none'）才走打字机。 */
  streaming: boolean;
  workspaceId?: string;
  onContextMenu?: (e: React.MouseEvent) => void;
}

export const TextSegmentView: React.FC<TextSegmentViewProps> = ({
  node,
  streaming,
  workspaceId,
  onContextMenu,
}) =>
  streaming ? (
    <StreamingTextSegment
      text={node.text}
      isStreaming
      workspaceId={workspaceId}
      onContextMenu={onContextMenu}
    />
  ) : (
    <StaticTextSegment
      text={node.text}
      workspaceId={workspaceId}
      onContextMenu={onContextMenu}
    />
  );

export default React.memo(
  TextSegmentView,
  (previous, next) =>
    previous.node.key === next.node.key &&
    previous.node.text === next.node.text &&
    previous.node.terminal === next.node.terminal &&
    previous.streaming === next.streaming &&
    previous.workspaceId === next.workspaceId &&
    previous.onContextMenu === next.onContextMenu,
);
