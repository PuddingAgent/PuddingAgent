import { App } from 'antd';
import { render } from '@testing-library/react';
import * as React from 'react';
import Bootstrap from './index';

jest.mock('@umijs/max', () => ({
  Helmet: ({ children }: any) => <>{children}</>,
}));

describe('Bootstrap Page', () => {
  beforeEach(() => {
    global.fetch = jest.fn().mockResolvedValue({
      status: 200,
      json: async () => ({ needsSetup: true, hasAdmin: false, userCount: 0 }),
    }) as jest.Mock;
  });

  afterEach(() => {
    jest.resetAllMocks();
  });

  it('shows the development default password hint on the admin step', async () => {
    const rootContainer = render(
      <App>
        <Bootstrap />
      </App>,
    );

    expect(await rootContainer.findByText('开发环境快速初始化')).toBeTruthy();
    expect(rootContainer.getByText(/Admin@123456/)).toBeTruthy();
    expect(rootContainer.getByText(/默认密码仅用于本地开发环境/)).toBeTruthy();

    rootContainer.unmount();
  });
});
