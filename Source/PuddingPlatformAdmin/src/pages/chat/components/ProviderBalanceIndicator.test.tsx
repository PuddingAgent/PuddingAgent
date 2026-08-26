import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import ProviderBalanceIndicator from './ProviderBalanceIndicator';

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({
      styles,
    }),
  };
});

describe('ProviderBalanceIndicator', () => {
  it('shows an em dash when balance is missing', () => {
    render(<ProviderBalanceIndicator />);
    expect(screen.getByText('—')).toBeTruthy();
  });

  it('formats the balance with the currency symbol', () => {
    render(<ProviderBalanceIndicator provider="DeepSeek" balance={110} currency="¥" />);
    expect(screen.getByText('¥110.00')).toBeTruthy();
  });

  it('supports a custom currency symbol', () => {
    render(<ProviderBalanceIndicator balance={5} currency="$" />);
    expect(screen.getByText('$5.00')).toBeTruthy();
  });

  it('appends the detail line inside the hover card', async () => {
    render(
      <ProviderBalanceIndicator provider="DeepSeek" balance={110} currency="¥" detail="点击刷新" />,
    );
    fireEvent.mouseEnter(screen.getByText('¥110.00'));
    await waitFor(() =>
      expect(screen.getByText('DeepSeek 余额：¥110.00')).toBeTruthy(),
    );
    expect(screen.getByText('点击刷新')).toBeTruthy();
  });

  it('formats large balances with thousands separators', () => {
    render(<ProviderBalanceIndicator balance={1234567.891} currency="¥" />);
    expect(screen.getByText('¥1,234,567.89')).toBeTruthy();
  });

  it('renders a rich hover card with a prominent balance amount', async () => {
    const { container } = render(
      <ProviderBalanceIndicator provider="DeepSeek" balance={110} currency="¥" />,
    );
    // 未悬浮：卡片不渲染
    expect(container.querySelector('.balanceCard')).toBeNull();

    fireEvent.mouseEnter(screen.getByText('¥110.00'));
    await waitFor(() =>
      expect(screen.getByText('DeepSeek 余额：¥110.00')).toBeTruthy(),
    );
    const amount = document.querySelector('.balanceCardAmount');
    expect(amount).toBeTruthy();
    expect(amount?.textContent).toBe('¥110.00');
  });

  it('shows granted/topped-up breakdown and query time in the hover card', async () => {
    render(
      <ProviderBalanceIndicator
        provider="DeepSeek"
        balance={110}
        currency="¥"
        grantedBalance={10}
        toppedUpBalance={100}
        queriedAt="2026-08-26T10:30:00Z"
      />,
    );
    fireEvent.mouseEnter(screen.getByText('¥110.00'));
    await waitFor(() =>
      expect(screen.getByText('DeepSeek 余额：¥110.00')).toBeTruthy(),
    );
    // 明细行：赠送/充值/查询时间，label 与 value 分行；时间部分用正则匹配以兼容不同时区
    expect(screen.getByText('赠送余额')).toBeTruthy();
    expect(screen.getByText('¥10.00')).toBeTruthy();
    expect(screen.getByText('充值余额')).toBeTruthy();
    expect(screen.getByText('¥100.00')).toBeTruthy();
    expect(screen.getByText('查询时间')).toBeTruthy();
    expect(
      screen.getByText(/\d{4}-\d{2}-\d{2} \d{2}:\d{2}/),
    ).toBeTruthy();
  });

  it('omits the meta row entirely when no breakdown or query time is given', async () => {
    render(
      <ProviderBalanceIndicator provider="DeepSeek" balance={110} currency="¥" />,
    );
    fireEvent.mouseEnter(screen.getByText('¥110.00'));
    await waitFor(() =>
      expect(screen.getByText('DeepSeek 余额：¥110.00')).toBeTruthy(),
    );
    expect(screen.queryByText(/赠送/)).toBeNull();
    expect(screen.queryByText(/充值/)).toBeNull();
    expect(screen.queryByText(/查询/)).toBeNull();
  });

  it('keeps partial breakdown rows when only some fields are present', async () => {
    render(
      <ProviderBalanceIndicator
        balance={110}
        currency="¥"
        toppedUpBalance={100}
      />,
    );
    fireEvent.mouseEnter(screen.getByText('¥110.00'));
    await waitFor(() =>
      expect(screen.getByText(/余额：¥110\.00/)).toBeTruthy(),
    );
    expect(screen.getByText('充值余额')).toBeTruthy();
    expect(screen.getByText('¥100.00')).toBeTruthy();
    expect(screen.queryByText(/赠送/)).toBeNull();
  });

  it('marks the label with the error style on failure state', () => {
    render(<ProviderBalanceIndicator detail="认证失败" error />);
    expect(screen.getByText('—').className).toContain('balanceBadgeLabelError');
  });

  it('does not mark the label as error during normal idle degrade', () => {
    render(<ProviderBalanceIndicator detail="点击刷新" />);
    expect(screen.getByText('—').className).not.toContain(
      'balanceBadgeLabelError',
    );
  });

  it('marks the hover card amount with the error style on failure', async () => {
    render(<ProviderBalanceIndicator detail="认证失败" error />);
    fireEvent.mouseEnter(screen.getByText('—'));
    await waitFor(() =>
      expect(screen.getByText('DeepSeek 余额：—')).toBeTruthy(),
    );
    const amount = document.querySelector('.balanceCardAmount');
    expect(amount?.className).toContain('balanceCardAmountError');
  });

  it('applies a spin indicator while refreshing', () => {
    const { container } = render(
      <ProviderBalanceIndicator balance={110} currency="¥" loading />,
    );
    expect(container.querySelector('.balanceBadgeSpin')).toBeTruthy();
  });

  it('has no spin indicator when idle', () => {
    const { container } = render(
      <ProviderBalanceIndicator balance={110} currency="¥" />,
    );
    expect(container.querySelector('.balanceBadgeSpin')).toBeNull();
  });
});
