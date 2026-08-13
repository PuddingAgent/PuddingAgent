// ── P1#6 MessageQueueDropdown：三态消息队列（排队 / 让位 / 取消）──
// 对齐 Copilot Send 三态（Add to Queue / Steer with Message / Stop and Send）：
// - 排队（queue）：busy 期间输入的多条消息由 useMessageInteractionQueue 自动进入本地待发队列；
// - 让位（steer）：本地待发项与下一条交换（最后一条轮转到队首）；后端排队项则注入下一次上下文；
// - 取消（stop）：取消全部 — 中止在途请求并清空本地待发队列。
// 本地待发项（source=local_pending）支持拖拽重排与单条删除；后端项为只读快照。
import {
  DeleteOutlined,
  DownOutlined,
  HolderOutlined,
  OrderedListOutlined,
  SendOutlined,
  StopOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { Button, Input, Tooltip } from 'antd';
import React, { useCallback, useMemo, useState } from 'react';
import type { ChatInteractionQueueItem } from '../hooks/useChatState';
import { useChatStyles } from '../styles';

export type QueuePhase = 'queued' | 'delivering' | 'terminal';

interface MessageQueueDropdownProps {
  interactionQueue: ChatInteractionQueueItem[];
  loading: boolean;
  onUpdateQueuedInteraction?: (id: string, text: string) => void;
  onDeleteQueuedInteraction?: (id: string) => void;
  onSendQueuedInteractionNow?: (id: string) => Promise<void>;
  onSteerQueuedInteraction?: (id: string) => Promise<void>;
  onReorderQueuedInteraction?: (fromId: string, toId: string) => void;
  onStopAll?: () => void;
}

/** 三态归类：排队中 / 投递中（含引导注入）/ 终态（完成、失败、取消、过期） */
const getQueuePhase = (item: ChatInteractionQueueItem): QueuePhase => {
  if (item.status === 'queued') return 'queued';
  // P1#10：retrying（含 busy-wait 假 retrying）归入排队计数 ——
  // 真实失败重试仍在等待下一轮投递；busy-wait 本质是 Agent 忙导致的消息挂起，
  // 后端部署后此类项将直接以 queued 到达（过渡规则见 toChatInteractionQueueItem）。
  if (item.status === 'retrying') return 'queued';
  if (
    item.status === 'delivering' ||
    item.status === 'steering_pending' ||
    item.status === 'steering_injected'
  ) {
    return 'delivering';
  }
  return 'terminal';
};

/**
 * busy deferral 判定：status=retrying 且目标 Agent 忙 —— 本质是「排队等待 agent 空闲」，
 * 不是失败重试。两种信号任一命中即视为 busy deferral：
 * 1) P1#10 过渡规则派生的 waitReason='busy-wait'（由 lastError 含 executionState=Busy 推导）；
 * 2) 后端 lastError 原文包含 "busy"（忽略大小写）—— 后端部署后的权威信号。
 */
const isBusyDeferred = (item: ChatInteractionQueueItem): boolean =>
  item.status === 'retrying' &&
  (item.waitReason === 'busy-wait' ||
    (!!item.error && /busy/i.test(item.error)));

const getQueueStatusLabel = (item: ChatInteractionQueueItem): string => {
  if (item.status === 'steering_pending') return '引导待注入';
  if (item.status === 'steering_injected')
    return item.injectedRound
      ? `已注入 · 第 ${item.injectedRound} 轮`
      : '已注入';
  if (item.status === 'steering_failed') return '引导失败';
  if (item.status === 'delivering') return '投递中';
  if (item.status === 'retrying') {
    // busy deferral（Agent 忙 → 排队等待空闲）不渲染为失败重试
    if (isBusyDeferred(item)) return '排队等待中';
    const attempt = item.metadata?.attemptCount;
    return attempt ? `重试中 · 第 ${attempt} 次` : '重试中';
  }
  if (item.status === 'dead_letter') return '死信';
  if (item.status === 'failed') return '失败';
  if (item.status === 'cancelled') return '已取消';
  if (item.status === 'expired') return '已过期';
  if (item.source === 'local_pending') return '排队中 · 待发送';
  return '排队中';
};

/** P1#10：retrying 警示色（amber），区别于终态错误红 */
const QUEUE_RETRY_WARNING_COLOR = '#b36b1e';

/** P1#10：lastError 摘要 —— JSON 优先提取 message 字段，否则截断 ≤80 字符；title 保留全量原文。 */
const summarizeQueueError = (error: string): string => {
  try {
    const parsed = JSON.parse(error) as { message?: unknown };
    if (
      parsed &&
      typeof parsed === 'object' &&
      typeof parsed.message === 'string' &&
      parsed.message.trim()
    ) {
      const msg = parsed.message.trim();
      return msg.length > 80 ? `${msg.slice(0, 80)}…` : msg;
    }
  } catch {
    // lastError 非 JSON：按纯文本截断处理
  }
  return error.length > 80 ? `${error.slice(0, 80)}…` : error;
};

const formatQueueLatency = (ms?: number): string | null => {
  if (typeof ms !== 'number' || !Number.isFinite(ms)) return null;
  if (ms < 1000) return `${Math.round(ms)}ms`;
  return `${(ms / 1000).toFixed(ms < 10000 ? 1 : 0)}s`;
};

const getQueueMetaText = (item: ChatInteractionQueueItem): string => {
  if (item.status === 'steering_injected') {
    const latency = formatQueueLatency(item.injectionLatencyMs);
    return latency
      ? `提交后 ${latency} 注入，稍后自动收起`
      : '运行时已消费并注入上下文，稍后自动收起';
  }
  if (item.status === 'steering_pending') return '等待下一次模型请求前注入';
  if (item.status === 'steering_failed') return item.error ?? '提交失败';
  if (item.status === 'retrying') {
    // busy deferral：agent 正在执行当前回复，投递会等其空闲后自动完成
    if (isBusyDeferred(item)) return '等待当前回复完成后自动投递';
    // 真实失败重试：不把原文 JSON 嵌入 meta（错误摘要由下方摘要区展示，title 为全量 tooltip）
    return '投递失败，正在重试';
  }
  if (item.source === 'local_pending')
    return '等待当前回复完成后自动发送，可拖拽重排';
  if (item.source === 'backend_message_queue')
    return '后端消息队列快照，调度由 Agent 服务管理';
  return '后端队列状态';
};

const MessageQueueDropdown: React.FC<MessageQueueDropdownProps> = ({
  interactionQueue,
  loading,
  onUpdateQueuedInteraction,
  onDeleteQueuedInteraction,
  onSendQueuedInteractionNow,
  onSteerQueuedInteraction,
  onReorderQueuedInteraction,
  onStopAll,
}) => {
  const { styles } = useChatStyles();
  /** 默认展开（与旧内联队列一致的可见性；可折叠） */
  const [open, setOpen] = useState(true);
  /** HTML5 拖拽重排状态 */
  const [draggedId, setDraggedId] = useState<string | null>(null);
  const [overId, setOverId] = useState<string | null>(null);

  const count = interactionQueue.length;
  const phaseCounts = useMemo(() => {
    let queued = 0;
    let delivering = 0;
    let terminal = 0;
    for (const item of interactionQueue) {
      const phase = getQueuePhase(item);
      if (phase === 'queued') queued += 1;
      else if (phase === 'delivering') delivering += 1;
      else terminal += 1;
    }
    return { queued, delivering, terminal };
  }, [interactionQueue]);

  const hasLocalPending = useMemo(
    () => interactionQueue.some((item) => item.source === 'local_pending'),
    [interactionQueue],
  );

  const handleDrop = useCallback(() => {
    if (draggedId && overId && draggedId !== overId) {
      onReorderQueuedInteraction?.(draggedId, overId);
    }
    setDraggedId(null);
    setOverId(null);
  }, [draggedId, onReorderQueuedInteraction, overId]);

  const handleDragEnd = useCallback(() => {
    setDraggedId(null);
    setOverId(null);
  }, []);

  if (count === 0) return null;

  return (
    <div
      className={styles.messageQueueDropdown}
      data-testid="interaction-queue"
      data-open={open ? 'true' : 'false'}
      data-count={count}
    >
      <button
        type="button"
        className={styles.messageQueueTrigger}
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        data-testid="message-queue-trigger"
      >
        <OrderedListOutlined className={styles.messageQueueTriggerIcon} />
        <span className={styles.messageQueueTriggerTitle}>消息队列</span>
        <span className={styles.messageQueueCount}>{count}</span>
        <span className={styles.messageQueuePhaseSummary}>
          排队 {phaseCounts.queued} · 执行 {phaseCounts.delivering} · 终态{' '}
          {phaseCounts.terminal}
        </span>
        <DownOutlined
          className={styles.messageQueueChevron}
          rotate={open ? 180 : 0}
        />
      </button>

      <div
        className={styles.messageQueuePanel}
        data-open={open ? 'true' : undefined}
      >
        <div className={styles.messageQueuePanelHeader}>
          <span className={styles.messageQueuePanelHint}>
            排队中的消息将在当前回复完成后按序发送
          </span>
          <Tooltip title="取消全部：中止当前请求并清空待发队列">
            <Button
              type="text"
              size="small"
              danger
              icon={<StopOutlined />}
              disabled={!hasLocalPending && !loading}
              onClick={() => onStopAll?.()}
              data-testid="message-queue-stop-all"
            >
              取消全部
            </Button>
          </Tooltip>
        </div>

        <div
          className={styles.composerQueueList}
          data-testid="interaction-queue-list"
        >
          {interactionQueue.map((item) => {
            const isBackendQueueItem = item.source === 'backend_message_queue';
            const isLocalPending = item.source === 'local_pending';
            const isSteering = item.source === 'steering';
            const isEditable = item.status === 'queued' && !isBackendQueueItem;
            const canDelete = isSteering || isLocalPending;
            const canSteer =
              (isLocalPending &&
                interactionQueue.filter(
                  (candidate) => candidate.source === 'local_pending',
                ).length > 1) ||
              (item.status === 'queued' && isBackendQueueItem && loading);
            const phase = getQueuePhase(item);
            // busy deferral 不渲染错误、按普通排队展示；真实失败重试展示警示。
            const isBusyWait = isBusyDeferred(item);
            const isRealRetrying = item.status === 'retrying' && !isBusyWait;

            return (
              <div
                key={item.id}
                className={styles.composerQueueItem}
                data-status={item.status}
                data-phase={phase}
                data-draggable={isLocalPending ? 'true' : undefined}
                data-dragging={draggedId === item.id ? 'true' : undefined}
                data-drop-target={overId === item.id ? 'true' : undefined}
                draggable={isLocalPending}
                onDragStart={(event) => {
                  if (!isLocalPending) return;
                  event.dataTransfer.effectAllowed = 'move';
                  event.dataTransfer.setData('text/plain', item.id);
                  setDraggedId(item.id);
                }}
                onDragOver={(event) => {
                  if (!isLocalPending) return;
                  event.preventDefault();
                  if (overId !== item.id) setOverId(item.id);
                }}
                onDrop={(event) => {
                  if (!isLocalPending) return;
                  event.preventDefault();
                  handleDrop();
                }}
                onDragEnd={handleDragEnd}
              >
                {isLocalPending && (
                  <span
                    className={styles.messageQueueDragHandle}
                    title="拖拽重排"
                    aria-hidden="true"
                  >
                    <HolderOutlined />
                  </span>
                )}
                {isEditable ? (
                  <Input.TextArea
                    value={item.text}
                    autoSize={{ minRows: 1, maxRows: 2 }}
                    className={styles.composerQueueInput}
                    onChange={(event) => {
                      onUpdateQueuedInteraction?.(item.id, event.target.value);
                    }}
                    aria-label="队列消息"
                  />
                ) : (
                  // biome-ignore lint/a11y/useSemanticElements: 只读队列预览需保持 DIV（兼容既有队列测试契约 tagName=DIV）
                  <div
                    className={styles.composerQueuePreview}
                    role="textbox"
                    aria-label="队列消息"
                    aria-readonly="true"
                    tabIndex={-1}
                    title={item.text}
                  >
                    {item.text}
                  </div>
                )}
                <div className={styles.composerQueueActions}>
                  <span
                    className={styles.composerQueueStatus}
                    data-status={item.status}
                    style={
                      isRealRetrying
                        ? { color: QUEUE_RETRY_WARNING_COLOR }
                        : undefined
                    }
                    title={getQueueMetaText(item)}
                  >
                    {getQueueStatusLabel(item)}
                  </span>
                  {isBackendQueueItem && (
                    <Tooltip title="由后端队列调度">
                      <Button
                        type="text"
                        size="small"
                        icon={<SendOutlined />}
                        disabled
                        onClick={() => {
                          void onSendQueuedInteractionNow?.(item.id);
                        }}
                        aria-label="发送队列消息"
                      />
                    </Tooltip>
                  )}
                  <Tooltip
                    title={
                      isLocalPending
                        ? '让位给下一条'
                        : isBackendQueueItem
                          ? '注入下一次上下文'
                          : '引导状态'
                    }
                  >
                    <Button
                      type="text"
                      size="small"
                      icon={<ThunderboltOutlined />}
                      disabled={!canSteer}
                      onClick={() => {
                        void onSteerQueuedInteraction?.(item.id);
                      }}
                      aria-label={
                        isLocalPending ? '让位给下一条' : '引导 Agent'
                      }
                    />
                  </Tooltip>
                  <Tooltip
                    title={
                      canDelete
                        ? '删除'
                        : '后端队列项由 Agent 服务调度，不可删除'
                    }
                  >
                    <Button
                      type="text"
                      size="small"
                      icon={<DeleteOutlined />}
                      disabled={!canDelete}
                      onClick={() => onDeleteQueuedInteraction?.(item.id)}
                      aria-label="删除队列消息"
                    />
                  </Tooltip>
                </div>
                <div className={styles.composerQueueMeta}>
                  {getQueueMetaText(item)}
                </div>
                {/* P1#10：不再渲染红色原文 JSON —— retrying 改为摘要式错误（≤80 字符或提取 message），
                    title 保留全量原文；busy-wait 不显示错误；其余状态错误同样摘要化。 */}
                {item.error &&
                  item.status !== 'steering_failed' &&
                  !isBusyWait && (
                    <div
                      className={styles.composerQueueError}
                      style={
                        isRealRetrying
                          ? { color: QUEUE_RETRY_WARNING_COLOR }
                          : undefined
                      }
                      title={item.error}
                    >
                      {summarizeQueueError(item.error)}
                    </div>
                  )}
              </div>
            );
          })}
        </div>

        <div className={styles.messageQueueFooter}>
          <span>让位 = 下一条优先</span>
          <span>·</span>
          <span>取消全部 = 中止当前并清空待发</span>
          {!hasLocalPending && (
            <span className={styles.messageQueueFooterMuted}>
              · 后端已受理消息由 Agent 服务调度
            </span>
          )}
        </div>
      </div>
    </div>
  );
};

export default MessageQueueDropdown;
