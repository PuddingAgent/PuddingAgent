import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import * as React from 'react';
import { TaskDetailsDrawer } from './TaskDetailsDrawer';

const mockUpdateTask = jest.fn();
const mockDeleteTask = jest.fn();
const mockListTaskComments = jest.fn();
const mockCreateTaskComment = jest.fn();

jest.mock('@/services/platform/api', () => ({
  updateTask: (...args: unknown[]) => mockUpdateTask(...args),
  deleteTask: (...args: unknown[]) => mockDeleteTask(...args),
  listTaskComments: (...args: unknown[]) => mockListTaskComments(...args),
  createTaskComment: (...args: unknown[]) => mockCreateTaskComment(...args),
}));

// antd Select 下拉在 jsdom 下依赖 rAF 推进过渡动画，mock 为 setTimeout 确保及时渲染。
const originalRaf = window.requestAnimationFrame;
const originalCancelRaf = window.cancelAnimationFrame;
beforeAll(() => {
  window.requestAnimationFrame = ((cb: FrameRequestCallback) =>
    window.setTimeout(() => cb(Date.now()), 0)) as typeof window.requestAnimationFrame;
  window.cancelAnimationFrame = ((id: number) =>
    window.clearTimeout(id)) as typeof window.cancelAnimationFrame;
});
afterAll(() => {
  window.requestAnimationFrame = originalRaf;
  window.cancelAnimationFrame = originalCancelRaf;
});

function makeTask(overrides: any = {}) {
  return {
    taskId: 'task-1',
    workspaceId: 'default',
    title: '示例任务',
    status: 'Backlog',
    boardColumn: 'Backlog',
    allowedTransitions: [],
    priority: 'p3',
    executionWindow: 'inherit',
    sortOrder: 0,
    version: 1,
    createdAtUtc: '2026-08-16T00:00:00Z',
    updatedAtUtc: '2026-08-16T00:00:00Z',
    ...overrides,
  };
}

function renderDrawer(task: any) {
  return render(
    <App>
      <TaskDetailsDrawer
        open
        workspaceId="default"
        task={task}
        events={[]}
        onClose={jest.fn()}
        onEdit={jest.fn()}
        onCommand={jest.fn()}
        onDeleted={jest.fn()}
        onChanged={jest.fn()}
      />
    </App>,
  );
}

/** 打开目标状态 Select 并选择指定中文标签（options 来自 allowedTransitions）。 */
async function chooseTransition(label: string) {
  const combobox = screen.getByRole('combobox');
  const selector = combobox.closest('.ant-select-selector') as HTMLElement;
  fireEvent.mouseDown(selector);

  let option: Element | undefined;
  await waitFor(
    () => {
      option = Array.from(
        document.querySelectorAll('.ant-select-item-option'),
      ).find((el) => el.textContent === label);
      expect(option).toBeTruthy();
    },
    { timeout: 10000 },
  );

  fireEvent.click(option as Element);
}

/** 点击指定 testid 的按钮。 */
function clickByTestId(testId: string) {
  fireEvent.click(screen.getByTestId(testId));
}

describe('TaskDetailsDrawer 状态流转区（TB-12 F-3）', () => {
  beforeEach(() => {
    mockUpdateTask.mockReset();
    mockDeleteTask.mockReset();
    mockListTaskComments.mockReset();
    mockCreateTaskComment.mockReset();
    mockListTaskComments.mockResolvedValue([]);
  });

  it('渲染 allowedTransitions 选项，点击「流转」触发 updateTask', async () => {
    mockUpdateTask.mockResolvedValue({ ...makeTask(), status: 'Ready' });

    renderDrawer(makeTask({ allowedTransitions: ['Ready', 'Deferred'] }));

    // 当前状态（中文标签 + wire）
    expect(await screen.findByText(/当前状态：待规划/)).toBeTruthy();

    // 打开目标状态下拉，选项来自 allowedTransitions
    const combobox = screen.getByRole('combobox');
    fireEvent.mouseDown(combobox.closest('.ant-select-selector') as HTMLElement);
    await waitFor(
      () => {
        const labels = Array.from(
          document.querySelectorAll('.ant-select-item-option'),
        ).map((el) => el.textContent);
        expect(labels).toEqual(expect.arrayContaining(['待办', '已推迟']));
      },
      { timeout: 10000 },
    );

    await chooseTransition('待办');
    clickByTestId('transition-submit');

    await waitFor(() => {
      expect(mockUpdateTask).toHaveBeenCalledWith('default', 'task-1', {
        expectedVersion: 1,
        status: 'Ready',
      });
    });
    // 未填备注时不调 createTaskComment
    expect(mockCreateTaskComment).not.toHaveBeenCalled();
  });

  it('填流转备注时，成功后追加「状态 from→to：备注」评论', async () => {
    mockUpdateTask.mockResolvedValue({ ...makeTask(), status: 'Ready' });
    mockCreateTaskComment.mockResolvedValue({});

    renderDrawer(makeTask({ allowedTransitions: ['Ready'] }));

    await chooseTransition('待办');

    const note = screen.getByPlaceholderText('流转备注（可选）');
    fireEvent.change(note, { target: { value: '补充说明' } });

    clickByTestId('transition-submit');

    await waitFor(() => {
      expect(mockCreateTaskComment).toHaveBeenCalledWith('default', 'task-1', {
        content: '状态 Backlog→Ready：补充说明',
        authorKind: 'user',
      });
    });
  });

  it('allowedTransitions 为空时 fail-closed：显示无可迁移状态且无目标可选', async () => {
    renderDrawer(makeTask({ allowedTransitions: [] }));

    expect(await screen.findByText('无可迁移状态')).toBeTruthy();
    expect(screen.queryByRole('combobox')).toBeNull();
  });

  it('版本冲突时不覆盖并提示刷新', async () => {
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

    renderDrawer(makeTask({ allowedTransitions: ['Ready'] }));

    await chooseTransition('待办');
    clickByTestId('transition-submit');

    expect(
      await screen.findByText('任务已被他人更新，请刷新后重试'),
    ).toBeTruthy();
    expect(mockCreateTaskComment).not.toHaveBeenCalled();
  });
});

describe('TaskDetailsDrawer 评论/备注区（TB-12 F-4）', () => {
  beforeEach(() => {
    mockUpdateTask.mockReset();
    mockDeleteTask.mockReset();
    mockListTaskComments.mockReset();
    mockCreateTaskComment.mockReset();
  });

  it('渲染评论列表，提交调 createTaskComment 并追加到列表', async () => {
    mockListTaskComments.mockResolvedValue([
      {
        commentId: 'c1',
        taskId: 'task-1',
        workspaceId: 'default',
        authorKind: 'user',
        authorId: 'alice',
        content: '第一条备注',
        createdAtUtc: '2026-08-16T00:00:00Z',
      },
    ]);
    mockCreateTaskComment.mockResolvedValue({
      commentId: 'c2',
      taskId: 'task-1',
      workspaceId: 'default',
      authorKind: 'user',
      content: '第二条备注',
      createdAtUtc: '2026-08-16T01:00:00Z',
    });

    renderDrawer(makeTask());

    expect(await screen.findByText('第一条备注')).toBeTruthy();

    const input = screen.getByPlaceholderText('添加备注…');
    fireEvent.change(input, { target: { value: '第二条备注' } });
    clickByTestId('comment-submit');

    await waitFor(() => {
      expect(mockCreateTaskComment).toHaveBeenCalledWith('default', 'task-1', {
        content: '第二条备注',
        authorKind: 'user',
      });
    });
    expect(await screen.findByText('第二条备注')).toBeTruthy();
  });

  it('评论为空时显示「暂无备注」空态', async () => {
    mockListTaskComments.mockResolvedValue([]);

    renderDrawer(makeTask());

    expect(await screen.findByText('暂无备注')).toBeTruthy();
  });
});
