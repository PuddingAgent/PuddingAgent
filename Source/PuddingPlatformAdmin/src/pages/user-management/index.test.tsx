// ── 用户管理页面测试：编辑抽屉集成头像上传（受控组件） ──────────

import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import UserManagementPage from './index';

jest.mock('@umijs/max', () => ({
  useModel: jest.fn(() => ({
    initialState: { currentUser: { userid: 'admin', avatar: '/old.png' } },
    setInitialState: jest.fn(),
  })),
}));

jest.mock('@/services/platform/api', () => ({
  listUsers: jest.fn(),
  listRoles: jest.fn(async () => []),
  createUser: jest.fn(),
  updateUser: jest.fn(),
  deleteUser: jest.fn(),
  changeUserPassword: jest.fn(),
  assignUserRoles: jest.fn(),
}));

// 头像上传组件替换为轻量桩：暴露 userId/avatarUrl/onUploaded 契约
jest.mock('@/components/UserAvatarUpload', () => (props: any) => (
  <div>
    <div data-testid="avatar-user-id">{props.userId}</div>
    <div data-testid="avatar-url">{props.avatarUrl ?? ''}</div>
    <button
      data-testid="avatar-uploaded-trigger"
      onClick={() => props.onUploaded?.('/user-avatars/new.png')}
    >
      simulate
    </button>
  </div>
));

const { listUsers } = jest.requireMock('@/services/platform/api') as any;

const adminUser = {
  id: 1,
  userId: 'admin',
  username: 'Admin',
  email: 'admin@test.local',
  userType: 'Admin' as const,
  isEnabled: true,
  roleIds: [],
  createdAt: '2026-01-01T00:00:00Z',
  avatar: '/user-avatars/admin-1.png',
};

const otherUser = {
  id: 2,
  userId: 'bob',
  username: 'Bob',
  email: 'bob@test.local',
  userType: 'SimpleUser' as const,
  isEnabled: true,
  roleIds: [],
  createdAt: '2026-01-01T00:00:00Z',
};

describe('用户管理 — 编辑抽屉头像', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    listUsers.mockResolvedValue([adminUser, otherUser]);
  });

  it('编辑已有用户时显示头像区域，且携带该用户的 userId 与 avatar', async () => {
    render(<UserManagementPage />);
    await waitFor(() => screen.getByText('bob'));

    fireEvent.click(screen.getAllByRole('button', { name: /编辑/ })[1]);
    await waitFor(() => screen.getByTestId('avatar-user-id'));

    expect(screen.getByTestId('avatar-user-id').textContent).toBe('bob');
    expect(screen.getByTestId('avatar-url').textContent).toBe('');
  });

  it('新建用户时不显示头像上传', async () => {
    render(<UserManagementPage />);
    await waitFor(() => screen.getByText('bob'));

    fireEvent.click(screen.getByRole('button', { name: /新建用户/ }));
    // 新建抽屉出现 UserId 输入（仅新建模式显示），且不渲染头像上传
    await waitFor(() =>
      screen.getByText('UserId（英文唯一标识）'),
    );
    expect(screen.queryByTestId('avatar-user-id')).toBeNull();
  });
});
