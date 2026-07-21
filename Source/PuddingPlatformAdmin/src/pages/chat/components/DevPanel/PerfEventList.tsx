import React from 'react';
import {
  Empty,
  Tag,
  Typography,
} from 'antd';
import type { PuddingPerfEvent } from '@/utils/debug';
import { useChatStyles } from '../../styles';

const { Text } = Typography;

interface PerfEventListProps {
  perfEvents: PuddingPerfEvent[];
  getEventTone: (name: string) => string;
}

const PerfEventList: React.FC<PerfEventListProps> = ({
  perfEvents,
  getEventTone,
}) => {
  const { styles } = useChatStyles();

  if (perfEvents.length === 0) {
    return (
      <div className={styles.devEventList}>
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="等待性能事件"
        />
      </div>
    );
  }

  return (
    <div className={styles.devEventList}>
      {perfEvents.map((event) => (
        <div
          key={`${event.name}-${event.at}-${JSON.stringify(event.payload ?? {})}`}
          className={styles.devPerfEventItem}
        >
          <div className={styles.devPerfEventHeader}>
            <Tag color={getEventTone(event.name)}>
              {event.name}
            </Tag>
            <Text className={styles.devEventTime}>
              {Math.round(event.at)}ms
            </Text>
          </div>
          <pre className={styles.devEventPayload}>
            {JSON.stringify(event.payload ?? {}, null, 2)}
          </pre>
        </div>
      ))}
    </div>
  );
};

export default PerfEventList;
