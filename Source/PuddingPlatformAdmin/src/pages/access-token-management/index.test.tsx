import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from 'antd';
import * as React from 'react';
import { useState } from 'react';
import AccessTokenManagementPage from './index';
import { SecretOnceModal } from './components/SecretOnceModal';

const mockGetExternalApiStatus = jest.fn();
const mockListAccessTokens = jest.fn();
const mockCreateAccessToken = jest.fn();
const mockRenameAccessToken = jest.fn();
const mockRevokeAccessToken = jest.fn();
const mockListWorkspaces = jest.fn();

jest.mock('@/services/platform/api', () => ({
  getExternalApiStatus: (...args: unknown[]) => mockGetExternalApiStatus(...args),
  listAccessTokens: (...args: unknown[]) => mockListAccessTokens(...args),
  createAccessToken: (...args: unknown[]) => mockCreateAccessToken(...args),
  renameAccessToken: (...args: unknown[]) => mockRenameAccessToken(...args),
  revokeAccessToken: (...args: unknown[]) => mockRevokeAccessToken(...args),
  listWorkspaces: (...args: unknown[]) => mockListWorkspaces(...args),
}));

function renderPage() {
  return render(
    <App>
      <AccessTokenManagementPage />
    </App>,
  );
}

const activeToken = {
  tokenId: 'pat_active',
  keyId: 'key-1',
  displayPrefix: 'pdt_v1_abcd1234…',
  name: 'codex-readonly',
  ownerUserId: 'admin',
  version: 1,
  createdAtUtc: '2026-08-20T00:00:00Z',
  expiresAtUtc: '2026-11-18T00:00:00Z',
  revokedAtUtc: null,
  revokedByUserId: null,
  revocationReason: null,
  lastUsedAtUtc: null,
  scopes: ['tasks.read'],
  workspaces: ['default'],
  status: 'Active' as const,
};

describe('Access Token 管理页（ADR-075 §15.3）', () => {
  const apiStatus = {
    enabled: false,
    publicBaseUrl: null,
    requireHttps: true,
    defaultTokenLifetimeDays: 90,
    maxTokenLifetimeDays: 365,
    maxActiveTokensPerOwner: 20,
    boundBaseUrl: 'http://127.0.0.1:8080',
  };
  beforeEach(() => {
    mockGetExternalApiStatus.mockReset();
    mockListAccessTokens.mockReset();
    mockCreateAccessToken.mockReset();
    mockRenameAccessToken.mockReset();
    mockRevokeAccessToken.mockReset();
    mockListWorkspaces.mockReset();

    mockGetExternalApiStatus.mockResolvedValue(apiStatus);
    mockListAccessTokens.mockResolvedValue({
      items: [activeToken],
      total: 1,
      page: 1,
      pageSize: 20,
    });
    mockListWorkspaces.mockResolvedValue([
      { workspaceId: 'default', name: '默认工作空间' },
    ]);
  });

  it('列表渲染 Token 元数据，不包含 Secret 明文', async () => {
    renderPage();

    expect(await screen.findByText('codex-readonly')).toBeTruthy();
    expect(screen.getByText('pdt_v1_abcd1234…')).toBeTruthy();
    expect(screen.getByText('Active')).toBeTruthy();
    // 元数据响应中没有 accessToken 字段，页面上不应出现任何 canonical token 明文。
    expect(screen.queryByText(/pdt_v1_[A-Za-z0-9_-]{20,}\./)).toBeNull();
  });

  it('External API 未启用时显示醒目提示', async () => {
    renderPage();

    await screen.findByText('codex-readonly');
    expect(screen.getByText(/External API 当前未启用/)).toBeTruthy();
  });

  it('创建抽屉默认最小 scope，workspace 为空不能提交', async () => {
    renderPage();
    await screen.findByText('codex-readonly');

    fireEvent.click(screen.getByRole('button', { name: /新建 Access Token/ }));

    // 默认只勾选 tasks.read。
    const readCheckbox = (await screen.findByRole('checkbox', {
      name: /tasks\.read/,
    })) as HTMLInputElement;
    expect(readCheckbox.checked).toBe(true);
    const commandCheckbox = screen.getByRole('checkbox', {
      name: /tasks\.command/,
    }) as HTMLInputElement;
    expect(commandCheckbox.checked).toBe(false);
    // 新增的高风险消息权限必须可见，且仍由 Checkbox.Group 的空缺省值保持未选中。
    const messagesLabel = screen.getByText('messages.send');
    const messagesCheckbox = messagesLabel.closest('label')?.querySelector('input[type="checkbox"]');
    expect(messagesCheckbox).toBeTruthy();
    expect((messagesCheckbox as HTMLInputElement).checked).toBe(false);

    // 只填名称，workspace 留空 → 校验失败，不发起创建请求。
    const nameInput = await screen.findByRole('textbox', { name: /名称/ });
    fireEvent.change(nameInput, { target: { value: 'no-workspace' } });
    fireEvent.click(screen.getByRole('button', { name: /创[s ]*建/ }));

    await waitFor(() => {
      expect(mockCreateAccessToken).not.toHaveBeenCalled();
    });
    // antd Form 就地渲染 workspace 必填错误。
    await waitFor(() => {
      const explainError = document.querySelector('.ant-form-item-explain-error');
      expect(explainError).toBeTruthy();
    });
  });

  it('撤销弹窗显示强确认警示，未确认不调用后端', async () => {
    renderPage();
    await screen.findByText('codex-readonly');

    fireEvent.click(screen.getByRole('button', { name: /撤销/ }));

    expect(await screen.findByText(/撤销立即生效且不可恢复/)).toBeTruthy();
    expect(mockRevokeAccessToken).not.toHaveBeenCalled();
  });

  it('撤销 Modal 填写原因后提交 expectedVersion', async () => {
    renderPage();
    await screen.findByText('codex-readonly');

    fireEvent.click(screen.getByRole('button', { name: /撤销/ }));
    expect(await screen.findByText(/撤销立即生效且不可恢复/)).toBeTruthy();

    const reasonInput = await screen.findByRole('textbox', { name: /撤销原因/ });
    fireEvent.change(reasonInput, { target: { value: '泄漏' } });

    mockRevokeAccessToken.mockResolvedValue({ ...activeToken, status: 'Revoked' });
    fireEvent.click(screen.getByRole('button', { name: /确认撤销/ }));

    await waitFor(() => {
      expect(mockRevokeAccessToken).toHaveBeenCalledWith('pat_active', {
        expectedVersion: 1,
        reason: '泄漏',
      });
    });
  });
});

describe('SecretOnceModal 一次性明文语义（ADR-075 §10.4）', () => {
  const plaintext = 'pdt_v1_QXdlcnR5dWlvMTIz.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA';

  const apiStatus = {
    enabled: false,
    publicBaseUrl: null,
    requireHttps: true,
    defaultTokenLifetimeDays: 90,
    maxTokenLifetimeDays: 365,
    maxActiveTokensPerOwner: 20,
    boundBaseUrl: 'http://127.0.0.1:8080',
  };

  const createdToken = {
    ...activeToken,
    tokenId: 'pat_new',
    accessToken: plaintext,
  };

  it('显示明文与不可恢复警示，未勾选确认前不能关闭', async () => {
    render(
      <App>
        <SecretOnceModal token={createdToken} apiStatus={apiStatus} onClose={jest.fn()} />
      </App>,
    );

    expect(await screen.findByText(plaintext)).toBeTruthy();
    expect(screen.getByText(/关闭此窗口后 Token 明文不可恢复/)).toBeTruthy();

    const ackButton = screen.getByRole('button', {
      name: /我已安全保存并关闭/,
    }) as HTMLButtonElement;
    expect(ackButton.disabled).toBe(true);
  });

  it('勾选确认后可关闭，父组件清空 token 后明文不可再现', async () => {
    function ModalHarness() {
      const [token, setToken] = useState<typeof createdToken | null>(createdToken);
      return (
        <App>
          <SecretOnceModal
            token={token}
            apiStatus={apiStatus}
            onClose={() => setToken(null)}
          />
        </App>
      );
    }

    render(<ModalHarness />);

    expect(await screen.findByText(plaintext)).toBeTruthy();

    fireEvent.click(screen.getByRole('checkbox', { name: /我已把 Token 安全保存/ }));
    const ackButton = screen.getByRole('button', {
      name: /我已安全保存并关闭/,
    }) as HTMLButtonElement;
    await waitFor(() => {
      expect(ackButton.disabled).toBe(false);
    });
    fireEvent.click(ackButton);

    // onClose 清空 token 后（页面 setSecretToken(null) 同语义），明文不可再现。
    await waitFor(() => {
      expect(screen.queryByText(plaintext)).toBeNull();
    });
  });

  it('token=null 时不渲染任何明文（刷新后不可恢复）', () => {
    render(
      <App>
        <SecretOnceModal token={null} apiStatus={apiStatus} onClose={jest.fn()} />
      </App>,
    );

    expect(screen.queryByText(plaintext)).toBeNull();
  });
});
