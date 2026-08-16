import React from 'react';
import { TaskColumn } from './TaskColumn';
import type { TaskActions } from './TaskCard';
import {
  BOARD_COLUMN_ORDER,
  type BoardColumnWire,
  type TaskColumns,
} from './types';

export interface TaskBoardProps {
  columns: TaskColumns;
  actions: TaskActions;
  onLoadMore: (column: BoardColumnWire) => void;
}

/** 五列容器（固定顺序 Backlog → Todo → InProgress → Done → Failed）。 */
export const TaskBoard: React.FC<TaskBoardProps> = ({
  columns,
  actions,
  onLoadMore,
}) => (
  <div
    style={{
      display: 'flex',
      gap: 12,
      alignItems: 'stretch',
      overflowX: 'auto',
      height: '100%',
      minHeight: 0,
      paddingBottom: 8,
    }}
    data-testid="task-board"
  >
    {BOARD_COLUMN_ORDER.map((column) => (
      <TaskColumn
        key={column}
        column={column}
        slice={columns[column]}
        actions={actions}
        onLoadMore={onLoadMore}
      />
    ))}
  </div>
);

export default TaskBoard;
