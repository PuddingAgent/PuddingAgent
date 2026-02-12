import { TestBrowser } from '@@/testBrowser';
import { fireEvent, render, waitFor } from '@testing-library/react';
import * as React from 'react';
import { act } from 'react';

const mockLogin = jest.fn();
const mockCurrentUser = jest.fn();

jest.mock('../../chat/components/WorkspaceStudioGameCanvas', () => () => null);

jest.mock('@/services/ant-design-pro/api', () => ({
  login: (...args: any[]) => mockLogin(...args),
  currentUser: (...args: any[]) => mockCurrentUser(...args),
}));

const waitTime = (time: number = 100) => {
  return new Promise((resolve) => {
    setTimeout(() => {
      resolve(true);
    }, time);
  });
};

describe('Login Page', () => {
  beforeEach(() => {
    window.HTMLElement.prototype.scrollIntoView = jest.fn();
    mockLogin.mockResolvedValue({
      status: 'ok',
      token: 'test-token',
      currentAuthority: 'admin',
    });
    mockCurrentUser.mockResolvedValue({
      data: {
        name: 'Admin',
        userid: 'admin',
        access: 'admin',
      },
    });
  });

  afterEach(() => {
    mockLogin.mockReset();
    mockCurrentUser.mockReset();
    localStorage.removeItem('pudding_token');
  });

  it('renders the Pudding runtime entry shell', async () => {
    const historyRef = React.createRef<any>();
    const rootContainer = render(
      <TestBrowser
        historyRef={historyRef}
        location={{
          pathname: '/user/login',
        }}
      />,
    );

    await rootContainer.findByText('登录');

    act(() => {
      historyRef.current?.push('/user/login');
    });

    expect(
      await rootContainer.findByTestId('runtime-entry-shell'),
    ).toBeTruthy();
    expect(
      await rootContainer.findByTestId('login-ambient-backdrop'),
    ).toBeTruthy();
    expect(
      rootContainer.queryByTestId('runtime-entry-visual'),
    ).not.toBeTruthy();
    expect(
      rootContainer.queryByTestId('runtime-entry-map'),
    ).not.toBeTruthy();
    const authCard = await rootContainer.findByTestId('auth-card-login');
    expect(authCard).toBeTruthy();
    expect(
      await rootContainer
        .findByTestId('auth-card-login')
        .then((node) => node.getAttribute('data-surface')),
    ).toBe('warm-paper');
    expect(authCard).toBeTruthy();
    expect(await rootContainer.findByLabelText('用户名')).toBeTruthy();
    expect(await rootContainer.findByLabelText('密码')).toBeTruthy();

    rootContainer.unmount();
  });

  it('uses an in-app transition before navigating to the workspace entry after login success', async () => {
    const historyRef = React.createRef<any>();
    const rootContainer = render(
      <TestBrowser
        historyRef={historyRef}
        location={{
          pathname: '/user/login',
        }}
      />,
    );

    await rootContainer.findByText('登录');

    const userNameInput = await rootContainer.findByLabelText('用户名');

    act(() => {
      fireEvent.change(userNameInput, { target: { value: 'admin' } });
    });

    const passwordInput = await rootContainer.findByLabelText('密码');

    act(() => {
      fireEvent.change(passwordInput, { target: { value: 'pudding.dev' } });
    });

    const submitButton = await rootContainer.findByText('进入工作台');

    await act(async () => {
      fireEvent.click(submitButton);
    });

    await waitFor(async () => {
      expect(
        (await rootContainer.findByTestId('runtime-entry-shell')).getAttribute(
          'data-transition',
        ),
      ).toBe('entering-workbench');
    });

    await act(async () => {
      await waitTime(400);
    });

    expect(historyRef.current?.location?.pathname).toBe('/pudding/workspaces');

    rootContainer.unmount();
  });
});
