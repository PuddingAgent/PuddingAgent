import { Typography } from 'antd';
import React from 'react';
import dayjs from 'dayjs';
import type { TaskDto } from './types';

const { Text } = Typography;

export interface TaskExecutionLinkProps {
  task: TaskDto;
}

/**
 * 从 activeAssignmentId / 绑定 ID 跳执行会话（稳定 ID，ADR-073 §4.3）。
 * TB-05/TB-07 打通后启用深链；当前仅展示稳定 ID，不产生错误跳转。
 */
export const TaskExecutionLink: React.FC<TaskExecutionLinkProps> = ({
  task,
}) => {
  if (!task.activeAssignmentId) {
    return (
      <Text type="secondary" style={{ fontSize: 12 }}>
        尚未执行（无活跃 Assignment）
      </Text>
    );
  }
  return (
    <div>
      <Text type="secondary" style={{ fontSize: 12 }}>
        活跃 Assignment：{task.activeAssignmentId}
      </Text>
      <div>
        <Text type="secondary" style={{ fontSize: 12 }}>
          执行会话深链待 TB-05/TB-07 打通后启用。
        </Text>
      </div>
    </div>
  );
};

export default TaskExecutionLink;
