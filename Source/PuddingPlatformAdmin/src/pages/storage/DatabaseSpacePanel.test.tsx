import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import * as api from './api';
import { DatabaseSpacePanel } from './DatabaseSpacePanel';

jest.mock('./api', () => ({
  ...jest.requireActual('./api'),
  getDatabaseSpace: jest.fn(),
}));

const getDatabaseSpace = api.getDatabaseSpace as jest.Mock;

const snapshot = () => [
  {
    key: 'platform',
    displayName: 'Pudding 平台数据库',
    databaseFile: 'D:/data/databases/pudding_platform.db',
    exists: true,
    fileBytes: 9_900_000_000,
    pageSize: 4096,
    pageCount: 2_400_000,
    perTableAvailable: false,
    spaceSource: 'rowcount-sample',
    dataClasses: [
      {
        targetId: 'platform.session-evidence',
        displayName: '会话事件证据',
        safetyLevel: 'Evidence',
        manualCleanupAllowed: true,
        bytes: 1_400_000_000,
        pages: 0,
        tables: ['conversation_events'],
      },
      {
        targetId: 'unclassified',
        displayName: '未归类',
        safetyLevel: '',
        manualCleanupAllowed: false,
        bytes: 600_000_000,
        pages: 0,
        tables: ['mystery_table'],
      },
    ],
  },
  {
    key: 'memory',
    displayName: '记忆数据库',
    databaseFile: 'D:/data/databases/pudding_memory.db',
    exists: false,
    fileBytes: 0,
    pageSize: 0,
    pageCount: 0,
    perTableAvailable: false,
    spaceSource: 'unavailable',
    dataClasses: [],
  },
];

describe('DatabaseSpacePanel（数据库占用按数据类）', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('默认不测量：要点按钮才发起请求（避免每次进页面都付逐表行数的代价）', () => {
    render(<DatabaseSpacePanel />);

    expect(getDatabaseSpace).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: '查看数据库占用' })).toBeTruthy();
  });

  it('点击后展示各库与各数据类的占用与占比', async () => {
    getDatabaseSpace.mockResolvedValue(snapshot());

    render(<DatabaseSpacePanel />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await waitFor(() => expect(getDatabaseSpace).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('Pudding 平台数据库')).toBeTruthy();
    expect(screen.getByText('会话事件证据')).toBeTruthy();
    // 未归类必须原样出现（它是「目录没覆盖」的信号，不能被吞掉）。
    expect(screen.getByText('未归类')).toBeTruthy();
    // 占比：1.4GB / 2.0GB = 70%。
    expect(screen.getByText('70%')).toBeTruthy();
    // 库不存在要如实说，而不是显示 0。
    expect(screen.getByText('库不存在')).toBeTruthy();
    // 按钮文案会变成「重新测量」；此处不钉文案（外观细节由人工确认），只钉数据语义。
  });

  it('界面上不出现「估算」字样（用户明确要求）', async () => {
    getDatabaseSpace.mockResolvedValue(snapshot());

    const { container } = render(<DatabaseSpacePanel />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await screen.findByText('Pudding 平台数据库');
    expect(container.textContent ?? '').not.toContain('估算');
  });

  it('读取失败要提示，而不是静默显示空表', async () => {
    getDatabaseSpace.mockRejectedValue(new Error('服务端返回 500'));

    render(<DatabaseSpacePanel />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    expect(await screen.findByText('服务端返回 500')).toBeTruthy();
  });
});
