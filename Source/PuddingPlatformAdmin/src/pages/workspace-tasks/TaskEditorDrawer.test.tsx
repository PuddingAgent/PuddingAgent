import { fireEvent, render, screen } from '@testing-library/react';
import { App } from 'antd';
import * as React from 'react';
import { TaskEditorDrawer } from './TaskEditorDrawer';

const mockCreateTask = jest.fn();
const mockUpdateTask = jest.fn();
const mockGetTask = jest.fn();

jest.mock('@/services/platform/api', () => ({
  createTask: (...args: unknown[]) => mockCreateTask(...args),
  updateTask: (...args: unknown[]) => mockUpdateTask(...args),
  getTask: (...args: unknown[]) => mockGetTask(...args),
}));

const task = {
  taskId: 'task-1',
  workspaceId: 'default',
  title: '原标题',
  status: 'Backlog',
  boardColumn: 'Backlog',
  priority: 'p3',
  executionWindow: 'inherit',
  sortOrder: 0,
  version: 2,
  createdAtUtc: '2026-08-16T00:00:00Z',
  updatedAtUtc: '2026-08-16T00:00:00Z',
} as const;

function renderEditor() {
  return render(
    <App>
      <TaskEditorDrawer
        open
        workspaceId="default"
        task={task}
        agents={[]}
        onClose={jest.fn()}
        onSaved={jest.fn()}
      />
    </App>,
  );
}

describe('TaskEditorDrawer（CAS 冲突保留草稿，ST-08A.3）', () => {
  beforeEach(() => {
    mockCreateTask.mockReset();
    mockUpdateTask.mockReset();
    mockGetTask.mockReset();
  });

  it('409 版本冲突时弹出冲突层并保留草稿', async () => {
    mockUpdateTask.mockRejectedValueOnce({
      response: {
        status: 409,
        data: {
          code: 'task.version_conflict',
          message: '任务已被更新',
          traceId: 'trace-1',
          actualVersion: 3,
        },
      },
    });

    renderEditor();

    const titleInput = await screen.findByLabelText('标题');
    fireEvent.change(titleInput, { target: { value: '我的草稿标题' } });

    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }));

    // 冲突弹层出现
    expect(await screen.findByText('版本冲突')).toBeTruthy();
    // 草稿仍保留（未被服务端版本覆盖）
    expect((screen.getByLabelText('标题') as HTMLInputElement).value).toBe(
      '我的草稿标题',
    );
    // 三个处理选项齐全
    expect(screen.getByText('保留我的草稿')).toBeTruthy();
    expect(screen.getByText('加载服务端版本')).toBeTruthy();
    expect(screen.getByText('以服务端版本为基底重试')).toBeTruthy();
  });

  it('新建模式下无冲突弹层时正常提交', async () => {
    mockCreateTask.mockResolvedValueOnce({ ...task, title: '新建标题' });

    render(
      <App>
        <TaskEditorDrawer
          open
          workspaceId="default"
          task={null}
          agents={[]}
          onClose={jest.fn()}
          onSaved={jest.fn()}
        />
      </App>,
    );

    const titleInput = await screen.findByLabelText('标题');
    fireEvent.change(titleInput, { target: { value: '新建标题' } });
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }));

    expect(await screen.findByText('已创建')).toBeTruthy();
    expect(mockCreateTask).toHaveBeenCalled();
  });
});
