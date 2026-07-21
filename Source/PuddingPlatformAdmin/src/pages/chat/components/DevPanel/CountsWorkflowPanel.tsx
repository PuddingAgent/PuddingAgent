import React from 'react';
import {
  Collapse,
  Empty,
  Tag,
  Typography,
} from 'antd';
import { useChatStyles } from '../../styles';

const { Text } = Typography;

interface CountsWorkflowPanelProps {
  perfSummary: Record<string, unknown> | null;
  topWorkflowSteps: Array<{
    payload?: Record<string, unknown>;
  }>;
  formatMetric: (value: number | null, suffix?: string) => string;
  getEventTone: (name: string) => string;
}

const CountsWorkflowPanel: React.FC<CountsWorkflowPanelProps> = ({
  perfSummary,
  topWorkflowSteps,
  formatMetric,
  getEventTone,
}) => {
  const { styles } = useChatStyles();

  return (
    <Collapse
      size="small"
      ghost
      items={[
        {
          key: 'counts',
          label: '事件计数',
          children: (
            <div className={styles.devPerfCounts}>
              {Object.entries(
                (perfSummary?.counts ?? {}) as Record<
                  string,
                  number
                >,
              )
                .sort((a, b) => b[1] - a[1])
                .map(([name, count]) => (
                  <Tag key={name} color={getEventTone(name)}>
                    {name}: {count}
                  </Tag>
                ))}
            </div>
          ),
        },
        {
          key: 'workflow',
          label: '最慢流程步骤',
          children:
            topWorkflowSteps.length === 0 ? (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="暂无流程步骤"
              />
            ) : (
              <div className={styles.devPerfCounts}>
                {topWorkflowSteps.map(
                  (event, index) => {
                    const payload = event.payload ?? {};
                    const workflow =
                      typeof payload.workflow === 'string'
                        ? payload.workflow
                        : 'workflow';
                    const step =
                      typeof payload.step === 'string'
                        ? payload.step
                        : 'step';
                    const durationMs =
                      typeof payload.durationMs === 'number'
                        ? payload.durationMs
                        : null;
                    const traceId =
                      typeof payload.traceId === 'string'
                        ? payload.traceId
                        : '';
                    const status =
                      typeof payload.status === 'string'
                        ? payload.status
                        : 'ok';
                    return (
                      <div
                        key={`${traceId}-${workflow}-${step}-${index}`}
                        className={styles.devPerfEventItem}
                      >
                        <div
                          className={styles.devPerfEventHeader}
                        >
                          <Tag
                            color={
                              status === 'error'
                                ? 'red'
                                : durationMs != null &&
                                    durationMs > 800
                                  ? 'orange'
                                  : 'blue'
                            }
                          >
                            {workflow}.{step}
                          </Tag>
                          <Text className={styles.devEventTime}>
                            {formatMetric(durationMs, 'ms')}
                          </Text>
                        </div>
                        <pre className={styles.devEventPayload}>
                          {JSON.stringify(payload, null, 2)}
                        </pre>
                      </div>
                    );
                  },
                )}
              </div>
            ),
        },
      ]}
    />
  );
};

export default CountsWorkflowPanel;
