import type { TimelineItem } from '../types';
import { mergeSubAgentActivity } from './useSubAgentActivity';

describe('appendOrUpdateSubAgentActivity', () => {
  it('replaces a replayed terminal event instead of appending a duplicate key', () => {
    const terminal: TimelineItem = {
      id: 'subagent-completed-sub-1',
      type: 'subagent_completed',
      status: 'error',
      name: 'sub-1',
      message: 'first failure',
      output: 'first output',
      timestamp: 1,
      collapsed: false,
    };

    const result = mergeSubAgentActivity('sub-1', [terminal], {
      ...terminal,
      message: 'replayed failure',
      output: 'replayed output',
      timestamp: 2,
    });

    expect(result).toHaveLength(1);
    expect(result[0]).toMatchObject({
      id: 'subagent-completed-sub-1',
      message: 'replayed failure',
      output: 'replayed output',
      timestamp: 2,
    });
  });
});
