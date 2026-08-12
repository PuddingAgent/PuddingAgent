// ── ApprovalCard：工具审批卡片（P0#1）───────────────────────────
import { Button, Input, Space, Tag, Typography } from 'antd';
import React, { useMemo, useState } from 'react';
import type { ApprovalCardData } from '../client/types';

export type ApprovalDecision = NonNullable<ApprovalCardData['decision']>;

interface ApprovalCardProps {
  approvalId: string;
  toolName: string;
  description: string;
  riskLevel: ApprovalCardData['riskLevel'];
  arguments?: Record<string, unknown>;
  status: ApprovalCardData['status'];
  decision?: ApprovalCardData['decision'];
  reason?: string;
  requestedAt?: string;
  expiresAt?: string;
  /** 决策回调：由父组件负责调用 POST /api/sessions/{sessionId}/decide。 */
  onDecide?: (decision: ApprovalDecision, reason?: string) => void | Promise<void>;
}

const RISK_META: Record<
  ApprovalCardData['riskLevel'],
  { label: string; color: string; background: string }
> = {
  low: { label: '低风险', color: '#389e0d', background: '#f6ffed' },
  medium: { label: '中风险', color: '#d48806', background: '#fffbe6' },
  high: { label: '高风险', color: '#d46b08', background: '#fff7e6' },
  critical: { label: '严重风险', color: '#cf1322', background: '#fff1f0' },
};

const DECISION_LABEL: Record<ApprovalDecision, string> = {
  allow_once: '允许一次',
  always_allow: '始终允许',
  deny: '拒绝',
};

const STATUS_LABEL: Record<ApprovalCardData['status'], string> = {
  pending: '待审批',
  approved: '已批准',
  denied: '已拒绝',
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
};

const headerStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  flexWrap: 'wrap',
};

const titleStyle: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 600,
  lineHeight: '20px',
  color: 'var(--pudding-chat-text)',
  wordBreak: 'break-all',
};

const descriptionStyle: React.CSSProperties = {
  margin: 0,
  fontSize: 12,
  lineHeight: 1.6,
  color: 'var(--pudding-chat-text-muted)',
  wordBreak: 'break-word',
  whiteSpace: 'pre-wrap',
};

const argumentsPreStyle: React.CSSProperties = {
  margin: 0,
  maxHeight: 160,
  overflowY: 'auto',
  padding: '8px 10px',
  borderRadius: 6,
  background: 'color-mix(in srgb, var(--pudding-chat-border) 18%, transparent)',
  fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
  fontSize: 11,
  lineHeight: 1.55,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-all',
};

const reasonRowStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 6,
};

const ApprovalCard: React.FC<ApprovalCardProps> = ({
  approvalId,
  toolName,
  description,
  riskLevel,
  arguments: rawArguments,
  status,
  decision,
  reason,
  expiresAt,
  onDecide,
}) => {
  const [pendingReason, setPendingReason] = useState('');
  const [submitting, setSubmitting] = useState<ApprovalDecision | null>(null);

  const risk = RISK_META[riskLevel] ?? RISK_META.medium;
  const isExpired = useMemo(() => {
    if (status !== 'pending' || !expiresAt) return false;
    const parsed = Date.parse(expiresAt);
    return Number.isFinite(parsed) && Date.now() > parsed;
  }, [status, expiresAt]);

  const argumentsText = useMemo(() => {
    if (!rawArguments || Object.keys(rawArguments).length === 0) return '';
    try {
      return JSON.stringify(rawArguments, null, 2);
    } catch {
      return String(rawArguments);
    }
  }, [rawArguments]);

  const handleDecide = async (nextDecision: ApprovalDecision) => {
    if (!onDecide || submitting) return;
    setSubmitting(nextDecision);
    try {
      await onDecide(nextDecision, pendingReason.trim() || undefined);
    } finally {
      setSubmitting(null);
    }
  };

  const isPending = status === 'pending' && !isExpired;
  const isResolved = status === 'approved' || status === 'denied';

  return (
    <div
      className="approval-card"
      data-testid="approval-card"
      data-approval-id={approvalId}
      data-status={status}
      data-risk={riskLevel}
      style={{
        ...cardContainerStyle,
        borderLeft: `3px solid ${risk.color}`,
      }}
    >
      <div style={headerStyle} data-testid="approval-card-header">
        <span style={titleStyle} data-testid="approval-card-tool">
          🔧 {toolName || '工具调用'}
        </span>
        <Tag
          color={risk.color}
          style={{ marginInlineEnd: 0, background: risk.background }}
          data-testid="approval-card-risk"
        >
          {risk.label}
        </Tag>
        {isResolved && (
          <Tag
            color={status === 'approved' ? 'success' : 'error'}
            style={{ marginInlineEnd: 0 }}
            data-testid="approval-card-status"
          >
            {STATUS_LABEL[status]}
          </Tag>
        )}
      </div>

      {description && (
        <p style={descriptionStyle} data-testid="approval-card-description">
          {description}
        </p>
      )}

      {argumentsText && (
        <pre style={argumentsPreStyle} data-testid="approval-card-arguments">
          {argumentsText}
        </pre>
      )}

      {isExpired && (
        <Typography.Text
          type="secondary"
          style={{ fontSize: 12 }}
          data-testid="approval-card-expired"
        >
          ⏰ 审批已过期
        </Typography.Text>
      )}

      {isResolved && (
        <div data-testid="approval-card-decision">
          <Space size={8} wrap>
            <Tag
              color={
                decision === 'deny' || status === 'denied'
                  ? 'error'
                  : 'success'
              }
              style={{ marginInlineEnd: 0 }}
              data-testid="approval-card-decision-tag"
            >
              {decision ? DECISION_LABEL[decision] : STATUS_LABEL[status]}
            </Tag>
            {decision === 'always_allow' && (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                已加入允许清单
              </Typography.Text>
            )}
          </Space>
          {reason && (
            <div
              style={{ marginTop: 6, fontSize: 12, color: 'var(--pudding-chat-text-muted)' }}
              data-testid="approval-card-decision-reason"
            >
              理由：{reason}
            </div>
          )}
        </div>
      )}

      {isPending && (
        <div data-testid="approval-card-pending">
          <div style={reasonRowStyle}>
            <Input
              size="small"
              placeholder="可选：填写审批理由"
              value={pendingReason}
              onChange={(e) => setPendingReason(e.target.value)}
              maxLength={200}
              allowClear
              data-testid="approval-card-reason-input"
            />
          </div>
          <Space
            size={8}
            wrap
            style={{ marginTop: 8 }}
            data-testid="approval-card-actions"
          >
            <Button
              size="small"
              type="primary"
              loading={submitting === 'allow_once'}
              disabled={submitting !== null}
              onClick={() => handleDecide('allow_once')}
              data-testid="approval-card-allow-once"
            >
              允许一次
            </Button>
            <Button
              size="small"
              loading={submitting === 'always_allow'}
              disabled={submitting !== null}
              onClick={() => handleDecide('always_allow')}
              data-testid="approval-card-always-allow"
            >
              始终允许
            </Button>
            <Button
              size="small"
              danger
              loading={submitting === 'deny'}
              disabled={submitting !== null}
              onClick={() => handleDecide('deny')}
              data-testid="approval-card-deny"
            >
              拒绝
            </Button>
          </Space>
        </div>
      )}
    </div>
  );
};

export default React.memo(ApprovalCard);
