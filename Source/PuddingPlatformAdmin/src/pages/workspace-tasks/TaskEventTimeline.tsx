import { Collapse, Empty, Timeline, Typography } from 'antd';
import React from 'react';
import dayjs from 'dayjs';
import type { TaskEventWatchEvent } from './types';

const { Text } = Typography;

export interface TaskEventTimelineProps {
  events: TaskEventWatchEvent[];
}

function formatUtc(value: string): string {
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format('MM-DD HH:mm:ss') : value;
}

/** 任务事件时间线（Quiet UI 默认折叠）。 */
export const TaskEventTimeline: React.FC<TaskEventTimelineProps> = ({
  events,
}) => {
  const sorted = [...events].sort((a, b) => a.sequence - b.sequence);

  return (
    <Collapse
      size="small"
      items={[
        {
          key: 'timeline',
          label: `事件时间线（${sorted.length}）`,
          children:
            sorted.length === 0 ? (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="暂无事件"
              />
            ) : (
              <Timeline
                style={{ marginTop: 8 }}
                items={sorted.map((event) => ({
                  key: String(event.sequence),
                  children: (
                    <div>
                      <Text strong style={{ fontSize: 12 }}>
                        {event.eventType}
                      </Text>
                      <div>
                        <Text type="secondary" style={{ fontSize: 12 }}>
                          seq {event.sequence} · {formatUtc(event.createdAtUtc)}
                        </Text>
                      </div>
                      {event.agentId && (
                        <div>
                          <Text type="secondary" style={{ fontSize: 12 }}>
                            agent: {event.agentId}
                          </Text>
                        </div>
                      )}
                    </div>
                  ),
                }))}
              />
            ),
        },
      ]}
    />
  );
};

export default TaskEventTimeline;
