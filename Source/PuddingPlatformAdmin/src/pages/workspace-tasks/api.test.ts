import {
  archiveTask,
  assignTask,
  cancelTask,
  createTask,
  deleteTask,
  getTask,
  listTasks,
  markFailedTask,
  reopenTask,
  requeueTask,
  resumeTask,
  runNowTask,
  updateTask,
} from '@/services/platform/api';

const mockRequest = jest.fn();

jest.mock('@umijs/max', () => ({
  request: (...args: unknown[]) => mockRequest(...args),
}));

describe('workspace task REST API（TB-04 §5.1，13 端点）', () => {
  beforeEach(() => {
    mockRequest.mockReset();
  });

  it('listTasks：GET + 路径编码 + boardColumn/cursor 参数', async () => {
    mockRequest.mockResolvedValueOnce({ items: [], nextCursor: null });
    await listTasks('ws/default', {
      boardColumn: 'Todo',
      limit: 100,
      cursor: '5|t1',
    });
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/ws%2Fdefault/tasks',
      {
        method: 'GET',
        params: { boardColumn: 'Todo', limit: 100, cursor: '5|t1' },
      },
    );
  });

  it('createTask：POST data', async () => {
    mockRequest.mockResolvedValueOnce({});
    await createTask('default', { title: 't', priority: 'p3' });
    expect(mockRequest).toHaveBeenCalledWith('/api/workspaces/default/tasks', {
      method: 'POST',
      data: { title: 't', priority: 'p3' },
    });
  });

  it('getTask：GET 路径编码 taskId', async () => {
    mockRequest.mockResolvedValueOnce({});
    await getTask('default', 'task/1');
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/default/tasks/task%2F1',
      { method: 'GET' },
    );
  });

  it('updateTask：PATCH + CAS expectedVersion', async () => {
    mockRequest.mockResolvedValueOnce({});
    await updateTask('default', 'task/1', {
      expectedVersion: 2,
      title: 'updated',
    });
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/default/tasks/task%2F1',
      { method: 'PATCH', data: { expectedVersion: 2, title: 'updated' } },
    );
  });

  it('deleteTask：DELETE', async () => {
    mockRequest.mockResolvedValueOnce(undefined);
    await deleteTask('default', 'task/1');
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/default/tasks/task%2F1',
      { method: 'DELETE' },
    );
  });

  it('assignTask：POST .../assign', async () => {
    mockRequest.mockResolvedValueOnce({});
    await assignTask('default', 't1', { agentId: 'a1', expectedVersion: 1 });
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/default/tasks/t1/assign',
      { method: 'POST', data: { agentId: 'a1', expectedVersion: 1 } },
    );
  });

  it('runNowTask：POST .../run-now + windowDecision', async () => {
    mockRequest.mockResolvedValueOnce({});
    await runNowTask('default', 't1', {
      agentId: 'a1',
      expectedVersion: 1,
      windowDecision: 'deferred_peak_window',
    });
    expect(mockRequest).toHaveBeenCalledWith(
      '/api/workspaces/default/tasks/t1/run-now',
      {
        method: 'POST',
        data: {
          agentId: 'a1',
          expectedVersion: 1,
          windowDecision: 'deferred_peak_window',
        },
      },
    );
  });

  it.each([
    ['cancelTask', cancelTask, 'cancel'],
    ['reopenTask', reopenTask, 'reopen'],
    ['archiveTask', archiveTask, 'archive'],
    ['markFailedTask', markFailedTask, 'mark-failed'],
    ['resumeTask', resumeTask, 'resume'],
    ['requeueTask', requeueTask, 'requeue'],
  ])('%s：POST .../%s + CommandTaskRequest', async (_name, fn, segment) => {
    mockRequest.mockResolvedValueOnce({});
    await (fn as (ws: string, id: string, body: unknown) => Promise<unknown>)(
      'default',
      't1',
      { expectedVersion: 1, reason: 'r' },
    );
    expect(mockRequest).toHaveBeenCalledWith(
      `/api/workspaces/default/tasks/t1/${segment}`,
      { method: 'POST', data: { expectedVersion: 1, reason: 'r' } },
    );
  });
});
