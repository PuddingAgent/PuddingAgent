import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import ComposerFeedbackStrip from './ComposerFeedbackStrip';

jest.mock('../styles', () => ({
  useChatStyles: () => ({
    styles: new Proxy({}, { get: (_target, prop) => String(prop) }),
  }),
}));

describe('ComposerFeedbackStrip', () => {
  it('renders sub-task state as a compact progressive-disclosure indicator', () => {
    render(
      <ComposerFeedbackStrip
        state={{
          context: true,
          memoryCount: 0,
          indexAvailable: false,
          subAgentsRunning: 3,
          backgroundMemoryRunning: false,
        }}
      />,
    );

    expect(
      screen.getByLabelText('3 个子代理运行中，打开子代理管理器'),
    ).toBeTruthy();
    expect(screen.getByText('子代理 3')).toBeTruthy();
  });

  it('keeps the idle sub-agent indicator named and clickable', () => {
    const onSubAgentsClick = jest.fn();
    render(
      <ComposerFeedbackStrip
        state={{
          context: false,
          memoryCount: 0,
          indexAvailable: false,
          subAgentsRunning: 0,
          backgroundMemoryRunning: false,
        }}
        onSubAgentsClick={onSubAgentsClick}
      />,
    );

    const contextIndicator = screen.getByLabelText('上下文待机');
    const subAgentIndicator = screen.getByLabelText(
      '没有子代理运行，打开子代理管理器',
    );
    expect(screen.getByText('上下文')).toBeTruthy();
    expect(screen.getByText('子代理')).toBeTruthy();
    expect(contextIndicator.getAttribute('data-active')).toBeNull();
    expect(subAgentIndicator.getAttribute('data-active')).toBeNull();

    fireEvent.click(subAgentIndicator);

    expect(onSubAgentsClick).toHaveBeenCalledTimes(1);
  });

  it('uses context usage percentage only for the context border aura and fill', () => {
    render(
      <ComposerFeedbackStrip
        state={{
          context: true,
          contextUsagePercentage: 72,
          memoryCount: 1,
          indexAvailable: false,
          subAgentsRunning: 0,
          backgroundMemoryRunning: false,
        }}
      />,
    );

    const contextIndicator = screen.getByLabelText('上下文已启用');
    const memoryIndicator = screen.getByLabelText('1 条记忆参考');
    const progress = contextIndicator.querySelector(
      '.composerFeedbackProgress',
    ) as HTMLElement | null;

    expect((contextIndicator as HTMLElement).style.borderColor).toBe('#d84a3a');
    expect(contextIndicator.getAttribute('style')).toContain('box-shadow');
    expect(progress?.style.width).toBe('72%');
    expect(
      progress?.style.getPropertyValue('--composer-feedback-fill'),
    ).toContain('linear-gradient');
    expect(memoryIndicator.getAttribute('style') ?? '').not.toContain(
      '#d84a3a',
    );
    expect(
      memoryIndicator.querySelector('.composerFeedbackProgress'),
    ).toBeNull();
  });

  it('keeps context usage fill visible while the context service is idle', () => {
    render(
      <ComposerFeedbackStrip
        state={{
          context: false,
          contextUsagePercentage: 42,
          memoryCount: 0,
          indexAvailable: false,
          subAgentsRunning: 0,
          backgroundMemoryRunning: false,
        }}
      />,
    );

    const contextIndicator = screen.getByLabelText('上下文待机');
    const progress = contextIndicator.querySelector(
      '.composerFeedbackProgress',
    ) as HTMLElement | null;

    expect(contextIndicator.getAttribute('data-active')).toBeNull();
    expect(progress?.style.width).toBe('42%');
    expect(
      progress?.style.getPropertyValue('--composer-feedback-fill'),
    ).toContain('linear-gradient');
  });

  it('includes context capacity in the context capsule accessible label', () => {
    render(
      <ComposerFeedbackStrip
        state={{
          context: false,
          contextUsagePercentage: 42,
          contextRemainingTokens: 58_000,
          contextLimitTokens: 100_000,
          memoryCount: 0,
          indexAvailable: false,
          subAgentsRunning: 0,
          backgroundMemoryRunning: false,
        }}
      />,
    );

    expect(
      screen.getByLabelText('上下文待机 · 剩余 58.0k / 100.0k'),
    ).toBeTruthy();
  });
});
