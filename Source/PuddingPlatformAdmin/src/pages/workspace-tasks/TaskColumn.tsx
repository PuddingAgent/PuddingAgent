import { useVirtualizer } from '@tanstack/react-virtual';
import { Button, Empty, Spin, Typography } from 'antd';
import React, { useRef } from 'react';
import { TaskCard } from './TaskCard';
import type { TaskActions } from './TaskCard';
import {
  BOARD_COLUMN_LABELS,
  type BoardColumnWire,
  type ColumnSlice,
} from './types';

const { Text } = Typography;

const ESTIMATED_CARD_HEIGHT = 132;

export interface TaskColumnProps {
  column: BoardColumnWire;
  slice: ColumnSlice;
  actions: TaskActions;
  onLoadMore: (column: BoardColumnWire) => void;
}

/** 列内虚拟列表 + “加载更多” + 空态。 */
export const TaskColumn: React.FC<TaskColumnProps> = ({
  column,
  slice,
  actions,
  onLoadMore,
}) => {
  const parentRef = useRef<HTMLDivElement | null>(null);

  const virtualizer = useVirtualizer({
    count: slice.items.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => ESTIMATED_CARD_HEIGHT,
    overscan: 4,
    getItemKey: (index) => slice.items[index]?.taskId ?? index,
  });

  const virtualRows = virtualizer.getVirtualItems();

  return (
    <div
      style={{
        display: 'flex',
        flexDirection: 'column',
        width: 292,
        minWidth: 260,
        flexShrink: 0,
        height: '100%',
        background: 'var(--pudding-chat-panel-bg, #f5f5f5)',
        borderRadius: 8,
        padding: 8,
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          padding: '4px 4px 8px',
        }}
      >
        <Text strong>{BOARD_COLUMN_LABELS[column]}</Text>
        <span
          style={{
            minWidth: 20,
            textAlign: 'center',
            fontSize: 12,
            color: 'var(--pudding-chat-text-subtle, #999)',
            background: 'var(--pudding-chat-bg, #fff)',
            borderRadius: 10,
            padding: '1px 6px',
          }}
        >
          {slice.items.length}
        </span>
      </div>

      <div
        ref={parentRef}
        style={{ flex: 1, overflowY: 'auto', minHeight: 0 }}
        data-testid={`column-${column}`}
      >
        {slice.loading ? (
          <div style={{ textAlign: 'center', padding: 24 }}>
            <Spin />
          </div>
        ) : slice.items.length === 0 ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="暂无任务"
            style={{ marginTop: 24 }}
          />
        ) : (
          <div
            style={{
              height: virtualizer.getTotalSize(),
              position: 'relative',
              width: '100%',
            }}
          >
            {virtualRows.map((row) => {
              const task = slice.items[row.index];
              return (
                <div
                  key={row.key}
                  data-index={row.index}
                  ref={virtualizer.measureElement}
                  style={{
                    position: 'absolute',
                    top: 0,
                    left: 0,
                    width: '100%',
                    transform: `translateY(${row.start}px)`,
                  }}
                >
                  <TaskCard task={task} actions={actions} />
                </div>
              );
            })}
          </div>
        )}
      </div>

      {slice.hasMore && (
        <Button
          type="link"
          size="small"
          block
          loading={slice.loadingMore}
          onClick={() => onLoadMore(column)}
        >
          加载更多
        </Button>
      )}
    </div>
  );
};

export default TaskColumn;
