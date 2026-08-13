// ── WaitingBubble 单行运行态（P1-3）最小测试 ──
// 覆盖：单行渲染（StateDot ongoing + agentName 正在运行）、≥15s 计时出现、
// 阶段文案折叠进 tooltip、reduced-motion 不崩、锚定起点不随重渲染归零。
//
// mock 约定与 AgentMessageBubble.test.tsx 一致：
// - antd：Tooltip 降级为 data-title 透传（断言阶段文案折叠进 tooltip）
// - style 模块：Proxy 类名直出，避免真实 antd-style（依赖 antd.theme）
// - StateDot：验证 WaitingBubble 以 state='ongoing' 复用运行指示
import { act, render, screen } from '@testing-library/react';
import * as React from 'react';
import { WaitingBubble } from './WaitingBubble';

jest.mock('antd', () => ({
  Tooltip: ({ children, title }: any) => (
    <span
      data-testid="antd-tooltip"
      data-title={typeof title === 'string' ? title : ''}
    >
      {children}
    </span>
  ),
}));

jest.mock('../styles/agent.styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target: unknown, prop: string) => String(prop),
    },
  );
  return {
    useAgentStyles: () => ({ styles }),
  };
});

jest.mock('../styles/waiting.styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target: unknown, prop: string) => String(prop),
    },
  );
  return {
    useWaitingStyles: () => ({ styles }),
  };
});

jest.mock('./StateDot', () => ({
  __esModule: true,
  default: ({ state }: any) => (
    <span data-testid="state-dot" data-state={state} aria-hidden="true" />
  ),
}));

describe('WaitingBubble', () => {
  it('renders a single running line: StateDot(ongoing) + agentName 正在运行', () => {
    const { container } = render(
      <WaitingBubble waitSeconds={0} agentName="Pudding" />,
    );

    expect(screen.getByTestId('agent-waiting-monitor')).toBeTruthy();
    expect(screen.getByText('Pudding 正在运行')).toBeTruthy();

    // 运行指示复用 StateDot ongoing（像素追逐）
    const dot = container.querySelector('[data-testid="state-dot"]');
    expect(dot).toBeTruthy();
    expect(dot?.getAttribute('data-state')).toBe('ongoing');

    // 降噪：开发者 hint / 轨道 / 阶段文案都不再直出主行
    expect(screen.queryByText(/这是主代理的等待占位/)).toBeNull();
    expect(screen.queryByText(/等待首个可见事件/)).toBeNull();
    expect(screen.queryByText('正在请求模型')).toBeNull();

    // <15s 不显示计时段
    expect(screen.queryByText(/已等待/)).toBeNull();
  });

  it('shows the elapsed clock only after 15 seconds', () => {
    const { rerender } = render(<WaitingBubble waitSeconds={5} />);

    expect(screen.queryByText(/已等待/)).toBeNull();

    rerender(<WaitingBubble waitSeconds={15} />);
    expect(screen.getByText('· 已等待 15s')).toBeTruthy();

    rerender(<WaitingBubble waitSeconds={75} />);
    expect(screen.getByText('· 已等待 1m')).toBeTruthy();
  });

  it('anchors the wait start and does not reset on re-render', () => {
    jest.useFakeTimers();
    jest.setSystemTime(new Date('2026-08-14T00:00:00.000Z'));
    try {
      const { rerender } = render(
        <WaitingBubble waitSeconds={0} agentName="Pudding" />,
      );
      expect(screen.queryByText(/已等待/)).toBeNull();

      // 组件内 1s tick：20s 后出现运行时钟（act 内推进，flush 间隔回调的 setState）
      act(() => {
        jest.advanceTimersByTime(20_000);
      });
      expect(screen.getByText('· 已等待 20s')).toBeTruthy();

      // 同 turn 重渲染（父级传入相同 waitSeconds）不归零，计时继续
      rerender(<WaitingBubble waitSeconds={0} agentName="Pudding" />);
      act(() => {
        jest.advanceTimersByTime(5_000);
      });
      expect(screen.getByText('· 已等待 25s')).toBeTruthy();
    } finally {
      jest.useRealTimers();
    }
  });

  it('folds phase copy into the tooltip while keeping the main line single', () => {
    const { rerender } = render(<WaitingBubble waitSeconds={0} />);
    expect(screen.getByTestId('antd-tooltip').getAttribute('data-title')).toBe(
      '正在请求模型',
    );

    rerender(<WaitingBubble waitSeconds={5} />);
    expect(screen.getByTestId('antd-tooltip').getAttribute('data-title')).toBe(
      '等待模型响应',
    );

    // ≥15s：tooltip 附带运行时钟
    rerender(<WaitingBubble waitSeconds={20} />);
    expect(screen.getByTestId('antd-tooltip').getAttribute('data-title')).toBe(
      '模型正在深入分析 · 已等待 20s',
    );
  });

  it('renders without crashing under prefers-reduced-motion', () => {
    const original = window.matchMedia;
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: jest.fn((query: string) => ({
        matches: query.includes('prefers-reduced-motion'),
        media: query,
        onchange: null,
        addListener: jest.fn(),
        removeListener: jest.fn(),
        addEventListener: jest.fn(),
        removeEventListener: jest.fn(),
        dispatchEvent: jest.fn(),
      })),
    });
    try {
      const { container } = render(
        <WaitingBubble waitSeconds={120} agentName="Pudding" />,
      );
      expect(screen.getByText('Pudding 正在运行')).toBeTruthy();
      expect(screen.getByText('· 已等待 2m')).toBeTruthy();
      expect(container.querySelector('[data-testid="state-dot"]')).toBeTruthy();
    } finally {
      Object.defineProperty(window, 'matchMedia', {
        writable: true,
        configurable: true,
        value: original,
      });
    }
  });
});
