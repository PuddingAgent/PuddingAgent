// ── GoalStepsPanel：Goal 计划步骤 + 目标级校验只读面板 ────────────────
// 数据来自 GET /api/v1/goals/{goalId}/steps（W2 冻结契约）。该端点可能尚未
// 部署：404/501/网络失败必须降级为可读提示，绝不能白屏或崩溃。
// 步骤按 sequenceNo 展示；progress.currentStepId 对应步骤高亮为「当前步骤」；
// status 未知值按原始值中性降级（与 GoalBanner 未知 phase 兜底风格一致）。

import { ReloadOutlined } from '@ant-design/icons';
import { Button, Spin } from 'antd';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type {
  GoalCheckItem,
  GoalSnapshot,
  GoalStepItem,
  GoalStepsSnapshot,
} from '@/services/platform/api';
import { getGoalSteps } from '@/services/platform/api';

interface GoalStepsPanelProps {
  goal: GoalSnapshot;
}

interface StepTone {
  color: string;
  background: string;
  borderColor: string;
}

const TONE_NEUTRAL: StepTone = {
  color: 'var(--pudding-chat-text-subtle)',
  background: 'var(--pudding-chat-surface-muted)',
  borderColor: 'var(--pudding-chat-border)',
};
const TONE_BLUE: StepTone = {
  color: '#1677ff',
  background: 'rgba(22, 119, 255, 0.10)',
  borderColor: 'rgba(22, 119, 255, 0.36)',
};
const TONE_GREEN: StepTone = {
  color: '#389e0d',
  background: 'rgba(82, 196, 26, 0.10)',
  borderColor: 'rgba(82, 196, 26, 0.34)',
};
const TONE_RED: StepTone = {
  color: '#cf1322',
  background: 'rgba(255, 77, 79, 0.10)',
  borderColor: 'rgba(255, 77, 79, 0.40)',
};
const TONE_ORANGE: StepTone = {
  color: '#d46b08',
  background: 'rgba(250, 140, 22, 0.12)',
  borderColor: 'rgba(250, 140, 22, 0.40)',
};

/** 已知步骤/校验状态 → 中文文案 + 配色；未列出的状态一律中性降级显示原始值。 */
const STATUS_TEXT: Record<string, string> = {
  planned: '待开始',
  pending: '待开始',
  in_progress: '进行中',
  running: '进行中',
  passed: '通过',
  succeeded: '通过',
  completed: '完成',
  failed: '失败',
  blocked: '受阻',
  skipped: '已跳过',
  cancelled: '已取消',
};

const STATUS_TONE: Record<string, StepTone> = {
  planned: TONE_NEUTRAL,
  pending: TONE_NEUTRAL,
  in_progress: TONE_BLUE,
  running: TONE_BLUE,
  passed: TONE_GREEN,
  succeeded: TONE_GREEN,
  completed: TONE_GREEN,
  failed: TONE_RED,
  blocked: TONE_ORANGE,
  skipped: TONE_NEUTRAL,
  cancelled: TONE_NEUTRAL,
};

const normalizeStatus = (status: string | undefined | null) =>
  typeof status === 'string' ? status.trim().toLowerCase() : '';

/** 紧凑时间显示（MM/DD HH:mm，本地时区）；缺失或无效返回 null。 */
const formatStepTime = (iso: string | null | undefined) => {
  if (!iso) return null;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  return date.toLocaleString('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  });
};

/** 证据引用紧凑显示：单条超过 48 字符时截断，避免超长路径撑爆行宽。 */
const compactEvidenceRef = (ref: string) =>
  ref.length > 48 ? `${ref.slice(0, 45)}…` : ref;

const StatusPill: React.FC<{ status: string | undefined | null }> = ({
  status,
}) => {
  const key = normalizeStatus(status);
  const known = Object.hasOwn(STATUS_TEXT, key);
  const text = known ? STATUS_TEXT[key] : `状态：${status ?? '未知'}`;
  const tone = known ? STATUS_TONE[key] : TONE_NEUTRAL;
  return (
    <span
      data-step-status={key || 'unknown'}
      title={known ? undefined : `未知状态原始值：${status ?? '空'}`}
      style={{
        display: 'inline-block',
        padding: '0 8px',
        borderRadius: 999,
        fontSize: 11,
        lineHeight: '18px',
        fontWeight: 600,
        whiteSpace: 'nowrap',
        color: tone.color,
        background: tone.background,
        border: `1px solid ${tone.borderColor}`,
      }}
    >
      {text}
    </span>
  );
};

interface RequestFailure {
  status?: number;
  message: string;
}

const describeFailure = (err: unknown): RequestFailure => {
  if (err && typeof err === 'object') {
    const anyErr = err as {
      response?: { status?: number };
      data?: { message?: string } | string;
      message?: unknown;
    };
    const status = anyErr.response?.status;
    let detail: string | undefined;
    if (typeof anyErr.data === 'string') detail = anyErr.data;
    else if (
      anyErr.data &&
      typeof anyErr.data === 'object' &&
      typeof anyErr.data.message === 'string'
    ) {
      detail = anyErr.data.message;
    }
    const fallback =
      typeof anyErr.message === 'string' ? anyErr.message : undefined;
    return { status, message: detail || fallback || '请求失败' };
  }
  return { message: '请求失败' };
};

const failureToHint = (failure: RequestFailure): string => {
  if (failure.status === 404) {
    return '步骤数据不可用（404）：目标不存在，或当前后端尚未部署步骤查询端点（/goals/{id}/steps）。';
  }
  if (failure.status === 501) {
    return '步骤查询端点尚未实现（501）：当前后端版本暂不支持步骤查询。';
  }
  if (failure.status) {
    return `步骤读取失败（HTTP ${failure.status}）：${failure.message}`;
  }
  return `步骤读取失败（网络或服务器错误）：${failure.message}`;
};

const sectionLabelStyle: React.CSSProperties = {
  fontSize: 12,
  fontWeight: 650,
  color: 'var(--pudding-chat-text)',
  lineHeight: 1.5,
};

const GoalStepsPanel: React.FC<GoalStepsPanelProps> = ({ goal }) => {
  const [snapshot, setSnapshot] = useState<GoalStepsSnapshot | null>(null);
  const [failure, setFailure] = useState<RequestFailure | null>(null);
  const [loading, setLoading] = useState(false);
  const [reloadToken, setReloadToken] = useState(0);
  const requestSeqRef = useRef(0);

  const fetchSteps = useCallback(async () => {
    const seq = ++requestSeqRef.current;
    setLoading(true);
    try {
      const data = await getGoalSteps(goal.goalRunId);
      if (seq !== requestSeqRef.current) return; // 过期响应（目标已切换/刷新）
      setSnapshot(data);
      setFailure(null);
    } catch (err) {
      if (seq !== requestSeqRef.current) return;
      setSnapshot(null);
      setFailure(describeFailure(err));
    } finally {
      if (seq === requestSeqRef.current) setLoading(false);
    }
  }, [goal.goalRunId]);

  useEffect(() => {
    void fetchSteps();
  }, [fetchSteps, reloadToken, goal.aggregateVersion, goal.updatedAtUtc]);

  const steps = useMemo(() => {
    const raw = snapshot?.steps ?? [];
    return [...raw].sort(
      (a, b) => (Number(a.sequenceNo) || 0) - (Number(b.sequenceNo) || 0),
    );
  }, [snapshot]);

  const progress = snapshot?.progress;
  const currentStepId = progress?.currentStepId ?? null;

  const renderStepRow = (step: GoalStepItem) => {
    const isCurrent =
      currentStepId !== null &&
      currentStepId !== undefined &&
      step.nodeId === currentStepId;
    const startedText = formatStepTime(step.startedAtUtc);
    const completedText = formatStepTime(step.completedAtUtc);
    const evidenceRefs = step.evidenceRefs ?? [];
    return (
      <div
        key={step.nodeId}
        data-goal-step-id={step.nodeId}
        data-step-started={step.startedAtUtc ?? undefined}
        data-step-completed={step.completedAtUtc ?? undefined}
        style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 2,
          padding: '6px 8px',
          borderRadius: 8,
          border: isCurrent
            ? '1px solid rgba(22, 119, 255, 0.45)'
            : '1px solid transparent',
          background: isCurrent
            ? 'rgba(22, 119, 255, 0.06)'
            : 'transparent',
          fontSize: 12,
          color: 'var(--pudding-chat-text-secondary)',
          lineHeight: 1.5,
        }}
      >
        <div
          style={{
            display: 'flex',
            flexWrap: 'wrap',
            alignItems: 'center',
            gap: '4px 8px',
          }}
        >
        <span
          style={{
            color: 'var(--pudding-chat-text-subtle)',
            fontVariantNumeric: 'tabular-nums',
          }}
        >
          #{step.sequenceNo}
        </span>
        <span
          style={{
            padding: '0 6px',
            borderRadius: 4,
            fontSize: 11,
            border: '1px solid var(--pudding-chat-border)',
            background: 'var(--pudding-chat-surface-muted)',
            color: 'var(--pudding-chat-text-subtle)',
          }}
        >
          {step.kind || '未标注类型'}
        </span>
        <span style={{ color: 'var(--pudding-chat-text)', fontWeight: 550 }}>
          {step.title || step.nodeId}
        </span>
        <StatusPill status={step.status} />
        {isCurrent && (
          <span
            style={{
              padding: '0 8px',
              borderRadius: 999,
              fontSize: 11,
              lineHeight: '18px',
              fontWeight: 600,
              color: TONE_BLUE.color,
              background: TONE_BLUE.background,
              border: `1px solid ${TONE_BLUE.borderColor}`,
            }}
          >
            当前步骤
          </span>
        )}
        {step.blockerCode ? (
          <span style={{ color: TONE_ORANGE.color }}>
            阻塞码：{step.blockerCode}
          </span>
        ) : null}
        </div>
        {startedText || completedText || evidenceRefs.length > 0 ? (
          <div
            style={{
              display: 'flex',
              flexWrap: 'wrap',
              gap: '2px 10px',
              fontSize: 11,
              color: 'var(--pudding-chat-text-subtle)',
            }}
          >
            {startedText ? <span>开始 {startedText}</span> : null}
            {completedText ? <span>完成 {completedText}</span> : null}
            {evidenceRefs.length > 0 ? (
              <span
                title={evidenceRefs.join('\n')}
                style={{
                  maxWidth: 340,
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}
              >
                证据：
                {evidenceRefs.slice(0, 3).map(compactEvidenceRef).join(' · ')}
                {evidenceRefs.length > 3 ? ` 等 ${evidenceRefs.length} 项` : ''}
              </span>
            ) : null}
          </div>
        ) : null}
      </div>
    );
  };

  const renderCheckRow = (check: GoalCheckItem) => (
    <div
      key={check.checkId}
      style={{
        display: 'flex',
        flexWrap: 'wrap',
        alignItems: 'center',
        gap: '4px 8px',
        padding: '4px 8px',
        fontSize: 12,
        color: 'var(--pudding-chat-text-secondary)',
        lineHeight: 1.5,
      }}
    >
      <span style={{ color: 'var(--pudding-chat-text)', fontWeight: 550 }}>
        {check.criterionId || check.checkId}
      </span>
      <StatusPill status={check.status} />
      {check.exitCode !== null && check.exitCode !== undefined ? (
        <span>exit={check.exitCode}</span>
      ) : null}
      {check.summary ? <span>{check.summary}</span> : null}
    </div>
  );

  return (
    <section
      aria-label="Goal 步骤"
      style={{
        marginTop: 10,
        border: '1px solid var(--pudding-chat-border)',
        borderRadius: 8,
        padding: '8px 10px',
        background: 'var(--pudding-chat-surface-muted)',
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'space-between',
          gap: 8,
          marginBottom: 6,
        }}
      >
        <span style={sectionLabelStyle}>
          步骤
          {typeof snapshot?.planVersion === 'number' ? (
            <span
              style={{
                marginLeft: 8,
                fontWeight: 400,
                color: 'var(--pudding-chat-text-subtle)',
              }}
            >
              计划版本 v{snapshot.planVersion}
            </span>
          ) : null}
          {progress ? (
            <span
              style={{
                marginLeft: 8,
                fontWeight: 400,
                color: 'var(--pudding-chat-text-subtle)',
              }}
            >
              共 {progress.stepsTotal} 步 · 通过 {progress.stepsPassed} · 失败{' '}
              {progress.stepsFailed} · 进行中 {progress.stepsInProgress}
            </span>
          ) : null}
        </span>
        <Button
          size="small"
          type="text"
          icon={<ReloadOutlined />}
          aria-label="刷新 Goal 步骤"
          disabled={loading}
          onClick={() => setReloadToken((token) => token + 1)}
        />
      </div>

      {loading && steps.length === 0 && !failure ? (
        <div style={{ padding: '8px 0', textAlign: 'center' }}>
          <Spin size="small" />
        </div>
      ) : null}

      {failure ? (
        <div
          role="alert"
          style={{
            fontSize: 12,
            color: TONE_ORANGE.color,
            lineHeight: 1.55,
            padding: '4px 2px',
          }}
        >
          {failureToHint(failure)}
        </div>
      ) : null}

      {snapshot && !snapshot.hasPlan ? (
        <div
          style={{
            fontSize: 12,
            color: 'var(--pudding-chat-text-subtle)',
            padding: '4px 2px',
          }}
        >
          该目标尚未生成执行计划（暂无步骤）。
        </div>
      ) : null}

      {snapshot?.hasPlan ? (
        <div
          style={{
            maxHeight: 240,
            overflow: 'auto',
            display: 'flex',
            flexDirection: 'column',
            gap: 4,
          }}
        >
          {steps.length === 0 ? (
            <div
              style={{
                fontSize: 12,
                color: 'var(--pudding-chat-text-subtle)',
                padding: '4px 2px',
              }}
            >
              计划已生成，但暂无步骤明细。
            </div>
          ) : (
            steps.map(renderStepRow)
          )}
        </div>
      ) : null}

      {snapshot && snapshot.checks.length > 0 ? (
        <div style={{ marginTop: 8 }}>
          <div
            style={{
              ...sectionLabelStyle,
              marginBottom: 4,
              color: 'var(--pudding-chat-text-secondary)',
            }}
          >
            目标级校验（非逐步）
            <span
              style={{
                marginLeft: 8,
                fontWeight: 400,
                color: 'var(--pudding-chat-text-subtle)',
              }}
            >
              以下校验针对整个目标，不属于任何单个步骤
            </span>
          </div>
          <div>{snapshot.checks.map(renderCheckRow)}</div>
        </div>
      ) : null}
    </section>
  );
};

export default GoalStepsPanel;
