// ── EditablePlanCard：Plan 模式可编辑计划卡片（P1#5）─────────
import { DeleteOutlined, HolderOutlined } from '@ant-design/icons';
import { Button, Input, Space, Tag, Typography } from 'antd';
import React, { useCallback, useMemo, useRef, useState } from 'react';
import type { PlanCardData, PlanDecision, PlanStepData } from '../client/types';

export type { PlanDecision, PlanStepData };

interface EditablePlanCardProps {
  planId: string;
  summary?: string;
  steps: PlanStepData[];
  status: PlanCardData['status'];
  decision?: PlanCardData['decision'];
  decidedAt?: string;
  requestedAt?: string;
  /** 决定回调：由父组件负责调用 POST /api/sessions/{sessionId}/plan-decide。 */
  onDecide?: (
    decision: PlanDecision,
    steps: PlanStepData[],
  ) => void | Promise<void>;
}

const DECISION_LABEL: Record<PlanDecision, string> = {
  approve_and_build: '已批准并构建',
  manual: '逐步执行',
  keep_planning: '继续完善计划',
};

const cardContainerStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  width: '100%',
  maxWidth: 'min(560px, 100%)',
  padding: '12px 14px',
  marginTop: 8,
  borderRadius: 10,
  border: '1px solid color-mix(in srgb, var(--pudding-chat-border) 80%, transparent)',
  background: 'var(--pudding-chat-surface-muted)',
  boxShadow: '0 2px 8px rgba(63, 38, 95, 0.05)',
  borderLeft: '3px solid #7c3aed',
};

const titleStyle: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 600,
  lineHeight: '20px',
  color: 'var(--pudding-chat-text)',
  wordBreak: 'break-all',
};

const summaryStyle: React.CSSProperties = {
  margin: 0,
  fontSize: 12,
  lineHeight: 1.6,
  color: 'var(--pudding-chat-text-muted)',
  wordBreak: 'break-word',
  whiteSpace: 'pre-wrap',
};

const stepRowStyle = (isDragging: boolean): React.CSSProperties => ({
  display: 'flex',
  alignItems: 'center',
  gap: 6,
  padding: '6px 8px',
  borderRadius: 6,
  border: '1px solid color-mix(in srgb, var(--pudding-chat-border) 55%, transparent)',
  background: isDragging
    ? 'color-mix(in srgb, #7c3aed 10%, var(--pudding-chat-surface-muted))'
    : 'var(--pudding-chat-surface-muted)',
  opacity: isDragging ? 0.7 : 1,
});

const stepIndexStyle: React.CSSProperties = {
  flex: '0 0 22px',
  fontSize: 12,
  fontWeight: 600,
  color: 'var(--pudding-chat-text-muted)',
  textAlign: 'center',
};

const stepEditorStyle: React.CSSProperties = {
  flex: 1,
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  minWidth: 0,
};

const stepInputStyle: React.CSSProperties = {
  fontSize: 12,
  background: 'transparent',
};

const dragHandleStyle: React.CSSProperties = {
  cursor: 'grab',
  color: 'var(--pudding-chat-text-muted)',
  fontSize: 14,
  padding: '2px 2px',
  userSelect: 'none',
  flex: '0 0 auto',
};

const footerStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 8,
  marginTop: 4,
  flexWrap: 'wrap',
};

/**
 * P1#5 Plan 模式可编辑计划卡片。
 * <ul>
 *  <li>步骤列表：每步 title/description 可内联编辑，可删除，可拖拽排序；</li>
 *  <li>底部三按钮：approve-and-build（批准并构建）/ manual（逐步执行）/
 *      keep-planning（继续完善计划）；</li>
 *  <li>收到 plan.finalized 后进入只读终态，展示用户决定。</li>
 * </ul>
 */
const EditablePlanCard: React.FC<EditablePlanCardProps> = ({
  planId,
  summary,
  steps: initialSteps,
  status,
  decision,
  decidedAt,
  onDecide,
}) => {
  const [steps, setSteps] = useState<PlanStepData[]>(() =>
    initialSteps.map((step) => ({ ...step })),
  );
  const [submitting, setSubmitting] = useState<PlanDecision | null>(null);
  const [draggingIndex, setDraggingIndex] = useState<number | null>(null);
  const [overIndex, setOverIndex] = useState<number | null>(null);
  const dragSourceRef = useRef<number | null>(null);

  const isFinalized = status === 'finalized';

  const updateStep = useCallback(
    (index: number, patch: Partial<PlanStepData>) => {
      setSteps((current) =>
        current.map((step, i) => (i === index ? { ...step, ...patch } : step)),
      );
    },
    [],
  );

  const removeStep = useCallback((index: number) => {
    setSteps((current) => current.filter((_, i) => i !== index));
  }, []);

  const reorderSteps = useCallback((from: number, to: number) => {
    setSteps((current) => {
      if (from === to || from < 0 || to < 0 || from >= current.length || to >= current.length)
        return current;
      const next = [...current];
      const [moved] = next.splice(from, 1);
      next.splice(to, 0, moved);
      return next;
    });
  }, []);

  const handleDecide = async (nextDecision: PlanDecision) => {
    if (!onDecide || submitting) return;
    setSubmitting(nextDecision);
    try {
      await onDecide(nextDecision, steps);
    } finally {
      setSubmitting(null);
    }
  };

  const summaryText = useMemo(() => summary?.trim(), [summary]);

  return (
    <div
      className="editable-plan-card"
      data-testid="editable-plan-card"
      data-plan-id={planId}
      data-status={status}
      style={cardContainerStyle}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <span style={titleStyle} data-testid="plan-card-title">
          📋 执行计划
        </span>
        <Tag color="purple" style={{ marginInlineEnd: 0 }} data-testid="plan-card-mode">
          Plan 模式
        </Tag>
        {isFinalized && (
          <Tag
            color={decision === 'keep_planning' ? 'warning' : 'success'}
            style={{ marginInlineEnd: 0 }}
            data-testid="plan-card-status"
          >
            {decision ? DECISION_LABEL[decision] : '已决定'}
          </Tag>
        )}
      </div>

      {summaryText && (
        <p style={summaryStyle} data-testid="plan-card-summary">
          {summaryText}
        </p>
      )}

      <div
        style={{ display: 'flex', flexDirection: 'column', gap: 6 }}
        data-testid="plan-card-steps"
      >
        {steps.length === 0 && (
          <Typography.Text
            type="secondary"
            style={{ fontSize: 12 }}
            data-testid="plan-card-empty"
          >
            计划已无步骤，请继续完善或删除本计划。
          </Typography.Text>
        )}
        {steps.map((step, index) => (
          <div
            key={step.id}
            className="plan-step-row"
            data-testid={`plan-step-row-${index}`}
            data-step-id={step.id}
            draggable={!isFinalized}
            onDragStart={(e) => {
              dragSourceRef.current = index;
              setDraggingIndex(index);
              if (e.dataTransfer) {
                e.dataTransfer.effectAllowed = 'move';
                try {
                  e.dataTransfer.setData('text/plain', String(index));
                } catch {
                  // 某些环境不允许写入 dataTransfer，拖拽仍可用（内部状态兜底）
                }
              }
            }}
            onDragOver={(e) => {
              if (isFinalized || dragSourceRef.current === null) return;
              e.preventDefault();
              if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';
              setOverIndex(index);
            }}
            onDragLeave={() => {
              setOverIndex((current) => (current === index ? null : current));
            }}
            onDrop={(e) => {
              e.preventDefault();
              const from = dragSourceRef.current;
              dragSourceRef.current = null;
              setDraggingIndex(null);
              setOverIndex(null);
              if (from === null || from === index) return;
              reorderSteps(from, index);
            }}
            onDragEnd={() => {
              dragSourceRef.current = null;
              setDraggingIndex(null);
              setOverIndex(null);
            }}
            style={{
              ...stepRowStyle(draggingIndex === index),
              outline: overIndex === index && draggingIndex !== index
                ? '1px dashed #7c3aed'
                : undefined,
            }}
          >
            <span style={dragHandleStyle} data-testid={`plan-step-drag-${index}`}>
              <HolderOutlined />
            </span>
            <span style={stepIndexStyle}>{index + 1}</span>
            <div style={stepEditorStyle}>
              <Input
                size="small"
                variant="borderless"
                style={stepInputStyle}
                value={step.title}
                disabled={isFinalized}
                onChange={(e) => updateStep(index, { title: e.target.value })}
                placeholder="步骤标题"
                data-testid={`plan-step-title-${index}`}
              />
              <Input
                size="small"
                variant="borderless"
                style={stepInputStyle}
                value={step.description ?? ''}
                disabled={isFinalized}
                onChange={(e) =>
                  updateStep(index, { description: e.target.value })
                }
                placeholder="步骤说明（可选）"
                data-testid={`plan-step-description-${index}`}
              />
            </div>
            {!isFinalized && (
              <Button
                type="text"
                size="small"
                danger
                aria-label="删除步骤"
                icon={<DeleteOutlined />}
                onClick={() => removeStep(index)}
                data-testid={`plan-step-delete-${index}`}
              />
            )}
          </div>
        ))}
      </div>

      {isFinalized ? (
        <div data-testid="plan-card-finalized">
          {decision && (
            <Tag
              color={decision === 'keep_planning' ? 'warning' : 'success'}
              style={{ marginInlineEnd: 0 }}
              data-testid="plan-card-decision-tag"
            >
              {DECISION_LABEL[decision]}
            </Tag>
          )}
          {decidedAt && (
            <Typography.Text
              type="secondary"
              style={{ fontSize: 12, marginLeft: 8 }}
            >
              已记录决定
            </Typography.Text>
          )}
        </div>
      ) : (
        <div style={footerStyle} data-testid="plan-card-actions">
          <Space size={8} wrap>
            <Button
              size="small"
              type="primary"
              loading={submitting === 'approve_and_build'}
              disabled={submitting !== null}
              onClick={() => handleDecide('approve_and_build')}
              data-testid="plan-card-approve-build"
            >
              批准并构建
            </Button>
            <Button
              size="small"
              loading={submitting === 'manual'}
              disabled={submitting !== null}
              onClick={() => handleDecide('manual')}
              data-testid="plan-card-manual"
            >
              逐步执行
            </Button>
            <Button
              size="small"
              loading={submitting === 'keep_planning'}
              disabled={submitting !== null}
              onClick={() => handleDecide('keep_planning')}
              data-testid="plan-card-keep-planning"
            >
              继续完善计划
            </Button>
          </Space>
          <Typography.Text
            type="secondary"
            style={{ fontSize: 11 }}
            data-testid="plan-card-hint"
          >
            可编辑步骤、删除或拖拽排序后再决定
          </Typography.Text>
        </div>
      )}
    </div>
  );
};

export default React.memo(EditablePlanCard);
