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

  it('appends the detail line inside the tooltip', async () => {
    render(
      <ProviderBalanceIndicator provider="DeepSeek" balance={110} currency="¥" detail="点击刷新" />,
    );
    fireEvent.mouseEnter(screen.getByText('¥110.00'));
    await waitFor(() =>
      expect(screen.getByText('DeepSeek 余额：¥110.00')).toBeTruthy(),
    );
    expect(screen.getByText('点击刷新')).toBeTruthy();
  });
});
