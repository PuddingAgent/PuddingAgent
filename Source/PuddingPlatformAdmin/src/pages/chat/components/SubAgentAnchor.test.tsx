import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import SubAgentAnchor from './SubAgentAnchor';

jest.mock('antd-style', () => ({
  createStyles: () => () => ({
    styles: new Proxy({}, { get: (_target, prop) => String(prop) }),
  }),
}));

describe('SubAgentAnchor', () => {
  it('summarizes a batch without embedding live run details in the message flow', () => {
    const onOpen = jest.fn();
    render(
      <SubAgentAnchor
        onOpen={onOpen}
        cards={[
          {
            turnId: 'one',
            runId: 'run-1',
            subSessionId: 'sub-1',
            originToolId: 'smart_plan',
            taskSummary: 'plan',
            status: 'completed',
            spawnedAt: 1,
            completedAt: 2,
          },
          {
            turnId: 'two',
            runId: 'run-2',
            subSessionId: 'sub-2',
            originToolId: 'smart_review',
            taskSummary: 'review',
            status: 'failed',
            spawnedAt: 1,
            completedAt: 3,
          },
        ]}
      />,
    );

    expect(screen.getByText('子代理执行结束 · 1 完成 · 1 异常')).toBeTruthy();
    expect(screen.getByText('smart_plan、smart_review')).toBeTruthy();
    fireEvent.click(screen.getByTestId('subagent-anchor'));
    expect(onOpen).toHaveBeenCalledWith('run-1');
  });
});
