import {
  buildCreateGraphRequest,
  createSuggestedGraphValues,
  getGraphDeletionBlocker,
} from './graphManagement';

describe('orchestration graph management', () => {
  it('creates stable form defaults inside the current workspace', () => {
    expect(createSuggestedGraphValues('research', () => 'abc123')).toEqual({
      graphId: 'graph-abc123',
      workspaceId: 'research',
      rootSessionId: 'admin-orchestration-abc123',
      objective: '',
      maxConcurrency: 1,
    });
  });

  it('trims the create request before sending it to the server', () => {
    expect(
      buildCreateGraphRequest({
        graphId: ' graph-1 ',
        workspaceId: ' default ',
        rootSessionId: ' admin-editor ',
        objective: ' Review a design ',
        maxConcurrency: 3,
      }),
    ).toEqual({
      graphId: 'graph-1',
      workspaceId: 'default',
      rootSessionId: 'admin-editor',
      objective: 'Review a design',
      maxConcurrency: 3,
    });
  });

  it('blocks destructive deletion whenever durable runs exist', () => {
    expect(getGraphDeletionBlocker(undefined)).toBe('请先选择 Graph');
    expect(
      getGraphDeletionBlocker({ graphId: 'graph-1', runCount: 2 }),
    ).toBe('该 Graph 已有 2 个 Run，为保护运行历史不能删除');
    expect(getGraphDeletionBlocker({ graphId: 'graph-1', runCount: 0 })).toBeUndefined();
  });
});
