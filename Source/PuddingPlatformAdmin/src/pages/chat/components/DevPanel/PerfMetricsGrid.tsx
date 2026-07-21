import React from 'react';
import { Typography } from 'antd';
import { useChatStyles } from '../../styles';

const { Text } = Typography;

/* ---- helpers (extracted from DevPanel.tsx) ---- */

const getNestedNumber = (
  obj: Record<string, unknown> | null | undefined,
  path: string,
): number | null => {
  if (!obj) return null;
  const parts = path.split('.');
  let current: unknown = obj;
  for (const part of parts) {
    if (current == null || typeof current !== 'object') return null;
    current = (current as Record<string, unknown>)[part];
  }
  if (typeof current === 'number') return current;
  if (current == null) return null;
  const n = Number(current);
  return Number.isFinite(n) ? n : null;
};

const formatMetric = (value: number | null, suffix = '') =>
  value == null ? '-' : `${value}${suffix}`;

const PerfMetric: React.FC<{
  label: string;
  value: string;
  tone?: 'normal' | 'warn' | 'ok';
}> = ({ label, value, tone = 'normal' }) => {
  const { styles } = useChatStyles();
  return (
    <div className={styles.devPerfMetric} data-tone={tone}>
      <Text type="secondary" className={styles.devPerfMetricLabel}>
        {label}
      </Text>
      <Text className={styles.devPerfMetricValue}>{value}</Text>
    </div>
  );
};

/* ---- exported component ---- */

interface PerfMetricsGridProps {
  perfSummary: Record<string, unknown> | null;
}

const PerfMetricsGrid: React.FC<PerfMetricsGridProps> = ({
  perfSummary,
}) => {
  const { styles } = useChatStyles();

  return (
    <div className={styles.devPerfGrid}>
      <PerfMetric
        label="到达速率"
        value={formatMetric(
          getNestedNumber(
            perfSummary,
            'stream.incomingCharsPerSecond',
          ),
          ' chars/s',
        )}
        tone="ok"
      />
      <PerfMetric
        label="可见速率"
        value={formatMetric(
          getNestedNumber(
            perfSummary,
            'output.activeCharsPerSecond',
          ),
          ' chars/s',
        )}
        tone="ok"
      />
      <PerfMetric
        label="DOM 字符"
        value={`${formatMetric(getNestedNumber(perfSummary, 'output.lastDomChars'))} / ${formatMetric(getNestedNumber(perfSummary, 'output.maxDomChars'))}`}
      />
      <PerfMetric
        label="Commit→Paint 平均"
        value={formatMetric(
          getNestedNumber(
            perfSummary,
            'output.avgCommitToPaintMs',
          ),
          'ms',
        )}
        tone={
          (getNestedNumber(
            perfSummary,
            'output.maxCommitToPaintMs',
          ) ?? 0) > 50
            ? 'warn'
            : 'normal'
        }
      />
      <PerfMetric
        label="Commit→Paint 峰值"
        value={formatMetric(
          getNestedNumber(
            perfSummary,
            'output.maxCommitToPaintMs',
          ),
          'ms',
        )}
        tone={
          (getNestedNumber(
            perfSummary,
            'output.maxCommitToPaintMs',
          ) ?? 0) > 50
            ? 'warn'
            : 'normal'
        }
      />
      <PerfMetric
        label="事件应用峰值"
        value={formatMetric(
          getNestedNumber(perfSummary, 'react.maxEventApplyMs'),
          'ms',
        )}
        tone={
          (getNestedNumber(
            perfSummary,
            'react.maxEventApplyMs',
          ) ?? 0) > 30
            ? 'warn'
            : 'normal'
        }
      />
      <PerfMetric
        label="Markdown 峰值"
        value={formatMetric(
          getNestedNumber(
            perfSummary,
            'react.maxMarkdownCommitMs',
          ),
          'ms',
        )}
        tone={
          (getNestedNumber(
            perfSummary,
            'react.maxMarkdownCommitMs',
          ) ?? 0) > 50
            ? 'warn'
            : 'normal'
        }
      />
      <PerfMetric
        label="长任务"
        value={`${formatMetric(getNestedNumber(perfSummary, 'browser.longTasks'))} · ${formatMetric(getNestedNumber(perfSummary, 'browser.maxLongTaskMs'), 'ms')}`}
        tone={
          (getNestedNumber(perfSummary, 'browser.longTasks') ??
            0) > 0
            ? 'warn'
            : 'normal'
        }
      />
      <PerfMetric
        label="活跃窗口"
        value={`${formatMetric(getNestedNumber(perfSummary, 'output.activeWindowMs'), 'ms')} / ${formatMetric(getNestedNumber(perfSummary, 'stream.incomingWindowMs'), 'ms')}`}
      />
      <PerfMetric
        label="流程步骤"
        value={`${formatMetric(getNestedNumber(perfSummary, 'workflow.steps'))} · ${formatMetric(getNestedNumber(perfSummary, 'workflow.traces'))} traces`}
      />
      <PerfMetric
        label="流程峰值"
        value={formatMetric(
          getNestedNumber(perfSummary, 'workflow.maxStepMs'),
          'ms',
        )}
        tone={
          (getNestedNumber(perfSummary, 'workflow.maxStepMs') ??
            0) > 800
            ? 'warn'
            : 'normal'
        }
      />
    </div>
  );
};

export default PerfMetricsGrid;
