// ── S3（看板卡 55435eb4）: TurnElapsedLabel 消息头实时「已处理 <时长>」测试 ──
// 禁止快照：全部用确定性注入时间断言（照抄 TurnStatus 的 nowProp 约定）。
import { render, screen } from '@testing-library/react';
import * as React from 'react';
import { TurnElapsedLabel } from './TurnStatus';

const NOW = Date.parse('2026-09-19T12:00:00.000Z');
const START = NOW - 60_000;

describe('TurnElapsedLabel（消息头实时已处理时长）', () => {
  it('运行 <60s 显示「已处理 Xs」', () => {
    render(<TurnElapsedLabel startedAt={START} now={START + 19_000} />);
    expect(screen.getByTestId('turn-elapsed-label').textContent).toBe(
      '已处理 19s',
    );
  });

  it('运行 ≥60s 取整分钟显示「已处理 Xm」（与 TurnStatus 同一套格式）', () => {
    render(<TurnElapsedLabel startedAt={START} now={START + 125_000} />);
    expect(screen.getByTestId('turn-elapsed-label').textContent).toBe(
      '已处理 2m',
    );
  });

  it('rerender 随注入 now 推进（确定性，不依赖真实时钟）', () => {
    const { rerender } = render(
      <TurnElapsedLabel startedAt={START} now={START + 5_000} />,
    );
    expect(screen.getByTestId('turn-elapsed-label').textContent).toBe(
      '已处理 5s',
    );
    rerender(<TurnElapsedLabel startedAt={START} now={START + 61_000} />);
    expect(screen.getByTestId('turn-elapsed-label').textContent).toBe(
      '已处理 1m',
    );
  });

  it('startedAt 非法（0/NaN）回落 0s，不抛错', () => {
    render(
      <>
        <TurnElapsedLabel startedAt={0} now={NOW} />
        <TurnElapsedLabel startedAt={Number.NaN} now={NOW} />
      </>,
    );
    const labels = screen.getAllByTestId('turn-elapsed-label');
    expect(labels).toHaveLength(2);
    expect(labels[0].textContent).toBe('已处理 0s');
    expect(labels[1].textContent).toBe('已处理 0s');
  });

  it('注入 now 时不启动 interval（tick 封装在叶子，确定性测试零时钟）', () => {
    const spy = jest.spyOn(window, 'setInterval');
    render(<TurnElapsedLabel startedAt={START} now={NOW} />);
    expect(spy).not.toHaveBeenCalled();
    spy.mockRestore();
  });

  it('未注入 now 时叶子内部每秒自 tick（1000ms）', () => {
    const spy = jest.spyOn(window, 'setInterval');
    const { unmount } = render(<TurnElapsedLabel startedAt={START} />);
    expect(spy).toHaveBeenCalledWith(expect.any(Function), 1000);
    unmount();
    spy.mockRestore();
  });
});
