// ── ApprovalCard：工具审批卡片（P0#1）───────────────────────────
import { Button, Input, Space, Tag, Typography } from 'antd';
import React, { useMemo, useState } from 'react';
import type { ApprovalCardData } from '../client/types';
import { RISK_COLORS, useApprovalStyles } from '../styles/approval.styles';

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

// 风险标签文案；颜色/背景集中在 approval.styles.ts 顶部 RISK_COLORS（语义=严重度，非状态色阶）
const RISK_META: Record<
  ApprovalCardData['riskLevel'],
  { label: string; color: string; background: string }
> = {
  low: { label: '低风险', ...RISK_COLORS.low },
  medium: { label: '中风险', ...RISK_COLORS.medium },
  high: { label: '高风险', ...RISK_COLORS.high },
  critical: { label: '严重风险', ...RISK_COLORS.critical },
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
  const { styles } = useApprovalStyles();
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
      className={`approval-card ${styles.cardContainer}`}
      data-testid="approval-card"
      data-approval-id={approvalId}
      data-status={status}
      data-risk={riskLevel}
      style={{ borderLeft: `3px solid ${risk.color}` }}
    >
      <div className={styles.header} data-testid="approval-card-header">
        <span className={styles.title} data-testid="approval-card-tool">
          🔧 {toolName || '工具调用'}
        </span>
        <Tag
          color={risk.color}
          className={styles.tag}
          style={{ background: risk.background }}
          data-testid="approval-card-risk"
        >
          {risk.label}
        </Tag>
        {isResolved && (
          <Tag
            color={status === 'approved' ? 'success' : 'error'}
            className={styles.tag}
            data-testid="approval-card-status"
          >
            {STATUS_LABEL[status]}
          </Tag>
        )}
      </div>

      {description && (
        <p className={styles.description} data-testid="approval-card-description">
          {description}
        </p>
      )}

      {argumentsText && (
        <pre className={styles.argumentsPre} data-testid="approval-card-arguments">
          {argumentsText}
        </pre>
      )}

      {isExpired && (
        <Typography.Text
          type="secondary"
          className={styles.metaText}
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
              className={styles.tag}
              data-testid="approval-card-decision-tag"
            >
              {decision ? DECISION_LABEL[decision] : STATUS_LABEL[status]}
            </Tag>
            {decision === 'always_allow' && (
              <Typography.Text type="secondary" className={styles.metaText}>
                已加入允许清单
              </Typography.Text>
            )}
          </Space>
          {reason && (
            <div
              className={styles.decisionReason}
              data-testid="approval-card-decision-reason"
            >
              理由：{reason}
            </div>
          )}
        </div>
      )}

      {isPending && (
        <div data-testid="approval-card-pending">
          <div className={styles.reasonRow}>
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
            className={styles.actions}
            data-testid="approval-card-actions"
          >
            {/* 飞书卡片规范：主操作「允许一次」最右、按钮 ≤3 个 */}
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
          </Space>
        </div>
      )}
    </div>
  );
};

export default React.memo(ApprovalCard);
