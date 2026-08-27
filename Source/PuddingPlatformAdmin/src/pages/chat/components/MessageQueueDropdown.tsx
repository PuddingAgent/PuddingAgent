// ── MessageQueueDropdown：服务端持久化队列的紧凑只读投影 ──
// 普通 Turn 由 chat_execution_commands + ChatExecutionWorker 执行，
// Agent-to-Agent 消息由 Message Fabric 投递，页面关闭不影响两者继续运行。
// Steering 是独立的当前 Turn 插嘴通道，不从普通队列项在前端转换。
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
import React, {
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
} from 'react';
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

/** 三态归类：待认领 / 引导注入 / 终态（仅显式诊断时出现） */
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
 * Phase 2：不再在组件内嗅探 busy —— busy deferral 由后端投影的 substate=waiting 权威驱动
 * （chatStateUtils 已将其映射为 waitReason='busy-wait' 兼容旧消费方）。
 * 旧后端（无 substate）由 chatStateUtils 的 isBusyWaitRetry 嗅探派生 waitReason 兜底。
 */
const getQueueStatusLabel = (item: ChatInteractionQueueItem): string => {
  // steering 状态不变
  if (item.status === 'steering_pending') return '引导待注入';
  if (item.status === 'steering_injected')
    return item.injectedRound
      ? `已注入 · 第 ${item.injectedRound} 轮`
      : '已注入';
  if (item.status === 'steering_failed') return '引导失败';

  // Phase 2：substate 驱动（优先）
  if (item.substate === 'waiting') return '排队中 · 等待 Agent 空闲';
  if (item.substate === 'retrying') {
    const attempt = item.metadata?.attemptCount;
    return attempt ? `重试中 · 第 ${attempt} 次` : '重试中';
  }
  if (item.substate === 'delivered') return '已送达';
  if (item.substate === 'dead_letter') return '死信';
  if (item.substate === 'failed') return '失败';
  if (item.substate === 'cancelled') return '已取消';
  if (item.substate === 'expired') return '已过期';

  // 兜底：无 substate 时回落 status 驱动（旧后端兼容）
  if (item.status === 'delivering')
    return item.metadata?.queueKind === 'chat_turn' ? '执行中' : '投递中';
  if (item.status === 'retrying') {
    if (item.waitReason === 'busy-wait') return '排队中 · 等待 Agent 空闲';
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
  // Phase 2：substate 驱动 meta 文案（不嵌原文 JSON）
  if (item.substate === 'waiting') return '等待当前回复完成后自动投递';
  if (item.substate === 'retrying') return '投递失败，正在重试';
  // 兜底：旧后端无 substate 时按 status + waitReason 判定
  if (item.status === 'retrying') {
    if (item.waitReason === 'busy-wait') return '等待当前回复完成后自动投递';
    return '投递失败，正在重试';
  }
  if (item.source === 'local_pending')
    return '等待当前回复完成后自动发送，可拖拽重排';
  if (item.source === 'backend_message_queue')
    return item.metadata?.queueKind === 'chat_turn'
      ? '已由 Core 受理，等待 Agent 认领'
      : '等待 Agent 认领；认领后转入会话时间线';
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
  /** 默认收起，避免活动队列长期挤占消息区；用户需要时再展开详情。 */
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const panelId = useId();
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

  /**
   * 详情是脱离文档流的轻量浮层：点击外部或按 Escape 关闭，避免浮层遮挡
   * 最近消息后还要再点一次触发器。Escape 关闭后把焦点还给触发器。
   */
  useEffect(() => {
    if (!open) return undefined;

    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target;
      if (target instanceof Node && !rootRef.current?.contains(target)) {
        setOpen(false);
      }
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      setOpen(false);
      triggerRef.current?.focus();
    };

    document.addEventListener('pointerdown', handlePointerDown);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('pointerdown', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [open]);

  if (count === 0) return null;

  return (
    <div
      ref={rootRef}
      className={styles.messageQueueDropdown}
      data-testid="interaction-queue"
      data-open={open ? 'true' : 'false'}
      data-count={count}
    >
      <button
        ref={triggerRef}
        type="button"
        className={styles.messageQueueTrigger}
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
        aria-controls={panelId}
        data-testid="message-queue-trigger"
      >
        <OrderedListOutlined className={styles.messageQueueTriggerIcon} />
        <span className={styles.messageQueueTriggerTitle}>待发消息</span>
        <span className={styles.messageQueueCount}>{count}</span>
        <span className={styles.messageQueuePhaseSummary}>
          {phaseCounts.queued} 待认领 · {phaseCounts.delivering} 引导中 ·{' '}
          {phaseCounts.terminal} 已结束
        </span>
        <DownOutlined
          className={styles.messageQueueChevron}
          rotate={open ? 180 : 0}
        />
      </button>

      <section
        id={panelId}
        className={styles.messageQueuePanel}
        data-open={open ? 'true' : undefined}
        aria-label="待发消息详情"
        aria-hidden={!open}
      >
        <div className={styles.messageQueuePanelHeader}>
          <span className={styles.messageQueuePanelHint}>
            仅显示未认领消息；认领后转入会话轨迹 · ⚡ 可插嘴当前 Agent
          </span>
          <Tooltip title="中止当前页面请求；已由 Core 受理的 Turn 不会在前端丢弃">
            <Button
              type="text"
              size="small"
              danger
              icon={<StopOutlined />}
              disabled={!hasLocalPending && !loading}
              onClick={() => onStopAll?.()}
              data-testid="message-queue-stop-all"
            >
              中止当前
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
              isLocalPending && item.status === 'queued' && loading;
            const phase = getQueuePhase(item);
            // Phase 2：substate 驱动 —— waiting（busy 挂起）不渲染错误、按普通排队展示；
            // retrying（真实失败重试）展示警示。旧后端（无 substate）回落 waitReason
            // （由 chatStateUtils 的 isBusyWaitRetry 嗅探派生）判定。
            const isWaiting =
              item.substate === 'waiting' ||
              (item.substate == null && item.waitReason === 'busy-wait');
            const isRealRetrying =
              item.substate === 'retrying' ||
              (item.substate == null &&
                item.status === 'retrying' &&
                item.waitReason !== 'busy-wait');

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
                        ? '插嘴：当前步骤结束后注入 Agent 上下文'
                        : isBackendQueueItem
                          ? '后端投递项不能直接转换，避免重复执行'
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
                      aria-label={isLocalPending ? '插嘴发送' : '引导 Agent'}
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
                    title 保留全量原文；waiting（busy 挂起）不显示错误；其余状态错误同样摘要化。 */}
                {item.error &&
                  item.status !== 'steering_failed' &&
                  !isWaiting && (
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
                {/* Phase 2：终态动作按钮占位 —— 后端端点（retry-now/discard/yield/cancel-all）
                    未实现，dead_letter/failed 按钮暂禁用；delivered 仅查看占位。 */}
                {(item.substate === 'delivered' ||
                  item.substate === 'dead_letter' ||
                  item.substate === 'failed') && (
                  <div
                    className={styles.composerQueueActions}
                    style={{
                      gridColumn: '1 / -1',
                      justifyContent: 'flex-start',
                      gap: 6,
                      marginTop: 2,
                    }}
                  >
                    {item.substate === 'delivered' && (
                      <Button
                        size="small"
                        onClick={() => {
                          // 占位：查看消息详情（后端端点待实现）
                        }}
                        data-testid="queue-action-delivered-view"
                      >
                        查看
                      </Button>
                    )}
                    {item.substate === 'dead_letter' && (
                      <>
                        <Tooltip title="后端端点待实现">
                          <Button
                            size="small"
                            disabled
                            data-testid="queue-action-dead-letter-requeue"
                          >
                            重入队
                          </Button>
                        </Tooltip>
                        <Tooltip title="后端端点待实现">
                          <Button
                            size="small"
                            disabled
                            data-testid="queue-action-dead-letter-discard"
                          >
                            丢弃
                          </Button>
                        </Tooltip>
                      </>
                    )}
                    {item.substate === 'failed' && (
                      <>
                        <Tooltip title="后端端点待实现">
                          <Button
                            size="small"
                            disabled
                            data-testid="queue-action-failed-retry"
                          >
                            重试
                          </Button>
                        </Tooltip>
                        <Tooltip title="后端端点待实现">
                          <Button
                            size="small"
                            disabled
                            data-testid="queue-action-failed-view-error"
                          >
                            查看错误
                          </Button>
                        </Tooltip>
                      </>
                    )}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </section>
    </div>
  );
};

export default MessageQueueDropdown;
