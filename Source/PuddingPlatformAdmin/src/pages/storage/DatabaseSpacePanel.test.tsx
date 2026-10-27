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

// 快照里的数据库元数据：reusableFreeBytes = pageSizeBytes × freePageCount。
// 这里不用 `Partial<T>` 之类的辅助签名 —— 类型由 prop 位置结构化校验，避免
// Babel 对「导入的类型同时出现在注解里」的转换限制（见 usageBreakdown.test.ts 的同类写法）。
const inventoryRow = {
  databaseId: 'platform',
  displayName: 'Pudding 平台数据库',
  relativePath: 'databases/pudding_platform.db',
  mainBytes: 4_800_000_000,
  walBytes: 0,
  sharedMemoryBytes: 0,
  totalBytes: 4_800_000_000,
  pageSizeBytes: 4096,
  pageCount: 1_200_000,
  freePageCount: 400_000,
  reusableFreeBytes: 4096 * 400_000,
};

const databases = () => [inventoryRow];

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
    // 库不存在要如实说，而不是显示 0。
    expect(screen.getByText('库不存在')).toBeTruthy();
    // 按钮文案会变成「重新测量」；此处不钉文案（外观细节由人工确认），只钉数据语义。
  });

  it('百分比以该库文件大小为分母（不是已归类之和）', async () => {
    getDatabaseSpace.mockResolvedValue(snapshot());

    render(<DatabaseSpacePanel />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await screen.findByText('Pudding 平台数据库');
    // 1.4GB / 9.9GB = 14.1%（旧口径 1.4/2.0 = 70%）
    expect(screen.getByText('14.1%')).toBeTruthy();
    expect(screen.getByText('6.1%')).toBeTruthy();
    expect(screen.queryByText('70%')).toBeNull();
    // 分母本身要写在界面上。
    expect(screen.getByText('各占比以该库文件大小为分母')).toBeTruthy();
  });

  it('补齐兜底行：数据类 + 兜底 + 空闲页 = 该库文件大小（按差值推算）', async () => {
    getDatabaseSpace.mockResolvedValue(snapshot());

    const { container } = render(<DatabaseSpacePanel databases={databases()} />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await screen.findByText('Pudding 平台数据库');
    // 9.9GB − 2.0GB(数据类) − 1.5GB(空闲页) = 5.8GB；按差值推算 ⇒ 带 ≈ 前缀与说明。
    expect(screen.getByText('其它（索引 / 未归类表外对象）')).toBeTruthy();
    expect(container.textContent ?? '').toContain('≈5.8 GB');
    expect(container.textContent ?? '').toContain('按差值推算');
    // 跨库不可比的说明必须出现在界面上。
    expect(screen.getByText(/跨库的进度条长度不能用来比较体量/)).toBeTruthy();
  });

  it('空闲页未知时不谎报 0：兜底行名字里说明它也含空闲页', async () => {
    getDatabaseSpace.mockResolvedValue(snapshot());

    render(<DatabaseSpacePanel />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await screen.findByText('Pudding 平台数据库');
    expect(screen.getByText('其它（索引 / 空闲页 / 未归类表外对象）')).toBeTruthy();
  });

  it('拿到快照元数据后空闲页单独成行（可复用页不是可清理额度）', async () => {
    getDatabaseSpace.mockResolvedValue(snapshot());

    render(<DatabaseSpacePanel databases={databases()} />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await screen.findByText('Pudding 平台数据库');
    expect(screen.getByText('空闲页（可复用）')).toBeTruthy();
    expect(screen.getByText('可复用')).toBeTruthy();
  });

  it('采样来源标「估算」与 ≈，精确的文件大小不标', async () => {
    getDatabaseSpace.mockResolvedValue(snapshot());

    const { container } = render(<DatabaseSpacePanel databases={databases()} />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await screen.findByText('Pudding 平台数据库');
    // 区块标题带「估算」徽标（旧实现的「不出现估算字样」已被本轮要求取代）。
    expect(screen.getByText('估算')).toBeTruthy();
    // 采样行的字节带 ≈。
    expect(container.textContent ?? '').toContain('≈1.3 GB');
    // 文件大小是精确值：标题行里的 9.2 GB 不带 ≈。
    expect(container.textContent ?? '').toContain('9.2 GB');
  });

  it('dbstat 来源不标估算', async () => {
    getDatabaseSpace.mockResolvedValue(
      snapshot().map((item) => ({ ...item, perTableAvailable: true, spaceSource: 'dbstat' })),
    );

    render(<DatabaseSpacePanel databases={databases()} />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await screen.findByText('Pudding 平台数据库');
    expect(screen.queryByText('估算')).toBeNull();
  });

  it('顶部合计与各库合计的差额要说清楚（不是静默差着）', async () => {
    getDatabaseSpace.mockResolvedValue(snapshot());

    render(<DatabaseSpacePanel databases={databases()} headlineTotalBytes={15_000_000_000} />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    await screen.findByText('Pudding 平台数据库');
    expect(screen.getByText(/差额/)).toBeTruthy();
    expect(screen.getByText(/非数据库文件/)).toBeTruthy();
  });

  it('读取失败要提示，而不是静默显示空表', async () => {
    getDatabaseSpace.mockRejectedValue(new Error('服务端返回 500'));

    render(<DatabaseSpacePanel />);
    fireEvent.click(screen.getByRole('button', { name: '查看数据库占用' }));

    expect(await screen.findByText('服务端返回 500')).toBeTruthy();
  });
});
