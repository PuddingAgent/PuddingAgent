// ── 子代理池面板三态测试（任务 B）────────────────────────────────
// 三态互斥是本组件的核心契约：空池不得显示成错误，失败不得显示成空池，
// 加载中不得显示成任何终态。取数 hook 被替换为可控返回值，测的是渲染契约。
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { SubAgentPoolPanel } from './SubAgentPoolPanel';
import { useSubAgentPool } from './useSubAgentPool';
import { useSubAgentRuns } from './useSubAgentRuns';

jest.mock('./useSubAgentPool');
// 运行历史也是一个取数 hook，同样替掉：本文件测的是渲染契约，
// 不替会让「选中条目」的用例去发真实请求（api 模块已被 mock，会拿到 undefined）。
jest.mock('./useSubAgentRuns');
// 写动作走 api 模块；用 requireMock 而不是 import 绑定，
// 因为本仓 esbuild+babel 管道在含 jest.mock 的文件里会拒绝「导入绑定用于类型标注」。
jest.mock('./api');
const mockedApi = jest.requireMock('./api') as {
  updateSubAgentPoolEntry: jest.Mock;
};

// 本仓 @umijs/test 的 esbuild+babel 管道在含 jest.mock 的文件里会拒绝
// 「导入绑定用于类型标注」（Cannot transform the imported binding ...），
// 因此测试内的夹具形状用本地类型描述，不引模块类型。
type PoolEntryFixture = {
  poolId: string;
  name: string;
  role: string | null;
  responsibility: string | null;
  avatarKey: string | null;
  modelId: string | null;
  templateId: string | null;
  status: string;
  taskCount: number;
  lastUsedAt: string | null;
  lastSuccessAt: string | null;
  lastSuccess?: boolean | null;
  subSessionId: string;
  createdAt: string;
};

interface PoolState {
  data?: PoolEntryFixture[] | null;
  loading?: boolean;
  error?: string | null;
  reload?: () => void;
}

const mockedUseSubAgentPool = useSubAgentPool as unknown as jest.Mock;

const entry = (
  overrides: Partial<PoolEntryFixture> = {},
): PoolEntryFixture => ({
  poolId: 'pool-1',
  name: '检索助手',
  role: 'researcher',
  responsibility: '负责仓库检索与资料汇总',
  avatarKey: null,
  modelId: 'deepseek/deepseek-flash',
  templateId: 'workspace-task-agent',
  status: 'idle',
  taskCount: 3,
  lastUsedAt: '2026-11-20T10:00:00Z',
  lastSuccessAt: '2026-11-20T09:55:00Z',
  subSessionId: 'sub-session-1',
  createdAt: '2026-11-01T00:00:00Z',
  ...overrides,
});

const setPoolState = (state: PoolState): void => {
  mockedUseSubAgentPool.mockReturnValue({
    data: null,
    loading: false,
    error: null,
    reload: jest.fn(),
    ...state,
  });
};

interface RunsState {
  runs?: unknown[] | null;
  total?: number;
  truncated?: boolean;
  loading?: boolean;
  error?: string | null;
  reload?: () => void;
}

const mockedUseSubAgentRuns = useSubAgentRuns as unknown as jest.Mock;

/** 运行历史默认「从未执行」：已有用例全都不关心履历，必须让它们拿一个稳定值。 */
const setRunsState = (state: RunsState = {}): void => {
  mockedUseSubAgentRuns.mockReturnValue({
    runs: [],
    total: 0,
    truncated: false,
    loading: false,
    error: null,
    reload: jest.fn(),
    ...state,
  });
};

describe('SubAgentPoolPanel', () => {
  beforeEach(() => {
    mockedUseSubAgentPool.mockReset();
    mockedApi.updateSubAgentPoolEntry.mockReset();
    mockedUseSubAgentRuns.mockReset();
    setRunsState();
  });

  it('加载中只渲染 loading，不渲染空态或错误', () => {
    setPoolState({ loading: true });
    render(<SubAgentPoolPanel />);

    expect(screen.getByTestId('subagent-pool-loading')).toBeTruthy();
    expect(screen.queryByText('暂无子代理')).toBeNull();
    expect(screen.queryByTestId('subagent-pool-empty')).toBeNull();
    expect(screen.queryByTestId('subagent-pool-error')).toBeNull();
    expect(screen.queryByTestId('subagent-pool-list')).toBeNull();
  });

  it('请求失败渲染错误态与重试入口，不显示「暂无子代理」', () => {
    const reload = jest.fn();
    setPoolState({ data: null, loading: false, error: '后端 500', reload });
    render(<SubAgentPoolPanel />);

    expect(screen.getByTestId('subagent-pool-error')).toBeTruthy();
    expect(screen.getByText('后端 500')).toBeTruthy();
    // 空池文案绝不能在失败态出现
    expect(screen.queryByText('暂无子代理')).toBeNull();
    expect(screen.queryByTestId('subagent-pool-empty')).toBeNull();
    expect(screen.queryByTestId('subagent-pool-loading')).toBeNull();

    fireEvent.click(screen.getByTestId('subagent-pool-retry'));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('返回空数组渲染空态，不显示错误、不显示加载中', () => {
    setPoolState({ data: [], loading: false, error: null });
    render(<SubAgentPoolPanel />);

    expect(screen.getByTestId('subagent-pool-empty')).toBeTruthy();
    expect(screen.getByText('暂无子代理')).toBeTruthy();
    expect(screen.queryByTestId('subagent-pool-error')).toBeNull();
    expect(screen.queryByTestId('subagent-pool-loading')).toBeNull();
    expect(screen.queryByTestId('subagent-pool-list')).toBeNull();
  });

  it('返回两条时左栏两条，点击后右栏出现该条 name 与 responsibility', () => {
    setPoolState({
      data: [
        entry({ poolId: 'pool-1', name: '检索助手' }),
        entry({
          poolId: 'pool-2',
          name: '评审助手',
          role: 'reviewer',
          responsibility: '负责对 diff 做独立评审',
        }),
      ],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    expect(screen.getByTestId('subagent-pool-count').textContent).toContain(
      '共 2 个',
    );
    expect(screen.getByTestId('subagent-pool-item-pool-1')).toBeTruthy();
    expect(screen.getByTestId('subagent-pool-item-pool-2')).toBeTruthy();
    // 未选中时右栏是空态占位
    expect(screen.getByTestId('subagent-pool-detail-empty')).toBeTruthy();

    fireEvent.click(screen.getByTestId('subagent-pool-item-pool-2'));

    const detail = screen.getByTestId('subagent-pool-detail');
    expect(within(detail).getByTestId('subagent-detail-name').textContent).toBe(
      '评审助手',
    );
    expect(
      within(detail).getByTestId('subagent-detail-responsibility').textContent,
    ).toBe('负责对 diff 做独立评审');
  });

  it('未声明模型路由时右栏写「跟随派发方」，不写「—」', () => {
    // 池里的 model_id 只是声明的偏好；执行时未声明就走派发方当轮解析的路由。
    // 因此空值不能被渲染成「—」——那会被读成「字段缺失 / 坏了」，与真实行为相反。
    setPoolState({
      data: [entry({ poolId: 'p-inherit', name: '跟随者', modelId: null })],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p-inherit'));

    const detail = screen.getByTestId('subagent-pool-detail');
    expect(within(detail).getByText('跟随派发方')).toBeTruthy();
  });

  it('四个状态各自独立渲染文案，不合并成一个模糊状态', () => {
    setPoolState({
      data: [
        entry({ poolId: 'p-idle', name: '空闲者', status: 'idle' }),
        entry({ poolId: 'p-busy', name: '忙者', status: 'busy' }),
        entry({ poolId: 'p-sleep', name: '睡着', status: 'sleeping' }),
        entry({ poolId: 'p-dead', name: '已销毁者', status: 'dead' }),
      ],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    expect(screen.getByTestId('subagent-status-idle').textContent).toBe('空闲');
    expect(screen.getByTestId('subagent-status-busy').textContent).toBe(
      '运行中',
    );
    expect(screen.getByTestId('subagent-status-sleeping').textContent).toBe(
      '休眠',
    );
    expect(screen.getByTestId('subagent-status-dead').textContent).toBe(
      '已销毁',
    );

    const tones = [
      screen.getByTestId('subagent-status-idle').getAttribute('data-tone'),
      screen.getByTestId('subagent-status-busy').getAttribute('data-tone'),
      screen.getByTestId('subagent-status-sleeping').getAttribute('data-tone'),
      screen.getByTestId('subagent-status-dead').getAttribute('data-tone'),
    ];
    expect(new Set(tones).size).toBe(4);
  });

  it('「最近成功」按后端三态如实渲染，不把未知时间显示成从未成功', () => {
    setPoolState({
      data: [
        entry({ poolId: 'p-none', name: '从未执行者', lastSuccess: null }),
        entry({ poolId: 'p-ok', name: '成功者', lastSuccess: true }),
        entry({ poolId: 'p-fail', name: '失败者', lastSuccess: false }),
      ],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    const detailTextAfterClick = (poolId: string): string => {
      fireEvent.click(screen.getByTestId(`subagent-pool-item-${poolId}`));
      return screen.getByTestId('subagent-pool-detail').textContent ?? '';
    };

    expect(detailTextAfterClick('p-none')).toContain('从未执行');
    expect(detailTextAfterClick('p-ok')).toContain('上次成功');
    expect(detailTextAfterClick('p-fail')).toContain('上次失败');
  });

  it('任务数为 0 时「最近使用」显示从未使用，不把创建时间当成使用时间', () => {
    setPoolState({
      data: [
        entry({
          poolId: 'p-unused',
          name: '从未使用者',
          taskCount: 0,
          status: 'idle',
          // 后端建条目时把 LastUsedAt 初始化成 CreatedAt，两者时间完全一样
          lastUsedAt: '2026-11-24T18:15:00+08:00',
          createdAt: '2026-11-24T18:15:00+08:00',
        }),
        entry({
          poolId: 'p-used',
          name: '用过者',
          taskCount: 2,
          lastUsedAt: '2026-11-24T13:50:00+08:00',
          createdAt: '2026-11-10T08:00:00+08:00',
        }),
      ],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    // 左栏摘要：0 任务不能出现「最近使用 时间」这种与任务数自相矛盾的措辞
    const summaryUnused =
      screen.getByTestId('subagent-pool-item-p-unused').textContent ?? '';
    expect(summaryUnused).toContain('从未使用');
    expect(summaryUnused).not.toContain('最近使用');

    const detailTextAfterClick = (poolId: string): string => {
      fireEvent.click(screen.getByTestId(`subagent-pool-item-${poolId}`));
      return screen.getByTestId('subagent-pool-detail').textContent ?? '';
    };

    // 详情：最近使用一行说「从未使用」，而创建时间仍照实显示同一个时刻 ——
    // 因此该时间字串只应出现一次（证明不是把所有时间都藏了）。
    const unused = detailTextAfterClick('p-unused');
    expect(unused).toContain('从未使用');
    expect(unused.split('2026-11-24 18:15').length - 1).toBe(1);

    // 正对照：真用过的条目仍显示时刻
    const used = detailTextAfterClick('p-used');
    expect(used).toContain('2026-11-24 13:50');
    expect(used).not.toContain('从未使用');
  });

  it('搜索框按名称过滤，且不因过滤清空已选详情', () => {
    setPoolState({
      data: [
        entry({ poolId: 'pool-1', name: '检索助手', role: 'researcher' }),
        entry({ poolId: 'pool-2', name: '评审助手', role: 'reviewer' }),
      ],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-pool-1'));
    fireEvent.change(screen.getByTestId('subagent-pool-search'), {
      target: { value: '评审' },
    });

    expect(screen.queryByTestId('subagent-pool-item-pool-1')).toBeNull();
    expect(screen.getByTestId('subagent-pool-item-pool-2')).toBeTruthy();
    expect(screen.getByTestId('subagent-pool-count').textContent).toContain(
      '匹配 1 个',
    );
    // 过滤只收窄列表，右栏仍显示原先选中的条目
    expect(
      screen.getByTestId('subagent-detail-name').textContent,
    ).toBe('检索助手');

    fireEvent.change(screen.getByTestId('subagent-pool-search'), {
      target: { value: '不存在的关键字' },
    });
    expect(screen.getByTestId('subagent-pool-list-empty')).toBeTruthy();
  });

  it('「改角色」预填当前角色，保存时把新角色交给后端', async () => {
    setPoolState({
      data: [entry({ poolId: 'p-role', name: '评审', role: 'reviewer' })],
      loading: false,
      error: null,
    });
    mockedApi.updateSubAgentPoolEntry.mockResolvedValue(undefined);
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p-role'));
    fireEvent.click(screen.getByTestId('subagent-action-role'));

    const input = screen.getByTestId('subagent-edit-role') as HTMLInputElement;
    expect(input.value).toBe('reviewer');

    fireEvent.change(input, { target: { value: 'verifier' } });
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }));

    await waitFor(() =>
      expect(mockedApi.updateSubAgentPoolEntry).toHaveBeenCalledWith('评审', {
        role: 'verifier',
      }),
    );
  });

  it('「改模型」预填当前路由，保存时把新路由交给后端', async () => {
    setPoolState({
      data: [
        entry({ poolId: 'p-model', name: '顾问', modelId: 'deepseek/deepseek-flash' }),
      ],
      loading: false,
      error: null,
    });
    mockedApi.updateSubAgentPoolEntry.mockResolvedValue(undefined);
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p-model'));
    fireEvent.click(screen.getByTestId('subagent-action-model'));

    const input = screen.getByTestId('subagent-edit-model') as HTMLInputElement;
    // 预填当前值是关键：否则用户改模型时看不到现在用的是哪个，只能靠记忆。
    expect(input.value).toBe('deepseek/deepseek-flash');

    fireEvent.change(input, {
      target: { value: 'fastrouter-claude/claude-opus-5-5' },
    });
    // antd 在两个汉字的按钮文案里插了一个空格（「保 存」），
    // 所以用 role + 正则匹配，不用精确文本。
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }));

    await waitFor(() =>
      expect(mockedApi.updateSubAgentPoolEntry).toHaveBeenCalledWith('顾问', {
        modelId: 'fastrouter-claude/claude-opus-5-5',
      }),
    );
  });

  it('裸 modelId 在前端被拦下，一个请求也不发', async () => {
    setPoolState({
      data: [entry({ poolId: 'p-bad', name: '探针', modelId: null })],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p-bad'));
    fireEvent.click(screen.getByTestId('subagent-action-model'));
    fireEvent.change(screen.getByTestId('subagent-edit-model'), {
      target: { value: 'deepseek-flash' },
    });
    fireEvent.click(screen.getByRole('button', { name: /保\s*存/ }));

    // 拦住的是「写入」，所以断言两件事：没发请求，且弹窗没关（用户能继续改）。
    // 不断言 message 文案：antd message 走 portal + CSSMotion，在 jsdom 里
    // 需要额外 holder 才稳定渲染 —— 那是提醒方式，不是被验证的行为。
    await waitFor(() =>
      expect(mockedApi.updateSubAgentPoolEntry).not.toHaveBeenCalled(),
    );
    expect(
      (screen.getByTestId('subagent-edit-model') as HTMLInputElement).value,
    ).toBe('deepseek-flash');
  });

  // ── 运行历史：与花名册同源的三态纪律 ────────────────────────

  it('无履历时右栏显示「从未执行」，不显示错误也不显示加载中', () => {
    setPoolState({
      data: [entry({ poolId: 'p1', name: '检索助手' })],
      loading: false,
      error: null,
    });
    setRunsState({ runs: [], loading: false, error: null });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p1'));

    expect(screen.getByTestId('subagent-run-empty')).toBeTruthy();
    expect(screen.getByText('从未执行过任务')).toBeTruthy();
    expect(screen.queryByTestId('subagent-run-error')).toBeNull();
    expect(screen.queryByTestId('subagent-run-loading')).toBeNull();
    expect(screen.queryByTestId('subagent-run-row')).toBeNull();
  });

  it('有履历时逐行渲染，失败行带出错误文本且标出状态', () => {
    setPoolState({
      data: [entry({ poolId: 'p1', name: '检索助手' })],
      loading: false,
      error: null,
    });
    setRunsState({
      loading: false,
      error: null,
      runs: [
        {
          runId: 'run-2',
          status: 'failed',
          templateId: 'workspace-task-agent',
          startedAt: '2026-11-24T06:33:22+08:00',
          completedAt: '2026-11-24T06:34:03+08:00',
          totalRounds: 8,
          totalToolCalls: 23,
          totalDurationMs: 41_200,
          errorMessage: 'budget_exhausted',
        },
        {
          runId: 'run-1',
          status: 'running',
          templateId: 'workspace-task-agent',
          startedAt: '2026-11-24T05:00:00+08:00',
          completedAt: null,
          totalRounds: 2,
          totalToolCalls: 5,
          totalDurationMs: 0,
          errorMessage: null,
        },
      ],
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p1'));

    const rows = screen.getAllByTestId('subagent-run-row');
    expect(rows).toHaveLength(2);
    expect(rows[0].getAttribute('data-run-status')).toBe('failed');
    expect(rows[0].textContent).toContain('失败');
    expect(rows[0].textContent).toContain('8 轮');
    expect(rows[0].textContent).toContain('41.2s');
    expect(
      within(rows[0]).getByTestId('subagent-run-error-text').textContent,
    ).toContain('budget_exhausted');

    // 运行中的那条没有错误文本，且未结算的耗时显示破折号
    expect(within(rows[1]).queryByTestId('subagent-run-error-text')).toBeNull();
    expect(rows[1].textContent).toContain('运行中');
    expect(rows[1].textContent).not.toContain('0.0s');
  });

  it('履历加载失败时显示错误与刷新入口，不冒充「从未执行」', async () => {
    const reload = jest.fn();
    setPoolState({
      data: [entry({ poolId: 'p1', name: '检索助手' })],
      loading: false,
      error: null,
    });
    setRunsState({ runs: null, loading: false, error: '后端 500', reload });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p1'));

    expect(screen.getByTestId('subagent-run-error')).toBeTruthy();
    expect(screen.getByText('后端 500')).toBeTruthy();
    // 「从未执行」与「没读到」是两件事：后者要刷新，前者要建任务
    expect(screen.queryByText('从未执行过任务')).toBeNull();

    await waitFor(() =>
      fireEvent.click(screen.getByTestId('subagent-run-reload')),
    );
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('历史被 limit 截断时明确指出「不是全部」', async () => {
    setPoolState({
      data: [entry({ poolId: 'p1', name: '检索助手' })],
      loading: false,
      error: null,
    });
    // 库里 137 条，本次只取 2 条 —— 界面必须说清楚，
    // 否则看到 2 条就会得出「总共跑了 2 次」这个错结论。
    setRunsState({
      runs: [
        {
          runId: 'run-t2',
          status: 'completed',
          templateId: 'workspace-task-agent',
          startedAt: '2026-11-25T00:10:00+08:00',
          completedAt: '2026-11-25T00:12:00+08:00',
          totalRounds: 3,
          totalToolCalls: 7,
          totalDurationMs: 120000,
          errorMessage: null,
        },
        {
          runId: 'run-t1',
          status: 'completed',
          templateId: 'workspace-task-agent',
          startedAt: '2026-11-25T00:00:00+08:00',
          completedAt: '2026-11-25T00:01:00+08:00',
          totalRounds: 1,
          totalToolCalls: 2,
          totalDurationMs: 60000,
          errorMessage: null,
        },
      ],
      total: 137,
      truncated: true,
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p1'));

    const notice = screen.getByTestId('subagent-run-truncated').textContent ?? '';
    expect(notice).toContain('2');
    expect(notice).toContain('137');
  });

  it('未截断时不显示截断提示（恒显示等于谎报）', async () => {
    setPoolState({
      data: [entry({ poolId: 'p1', name: '检索助手' })],
      loading: false,
      error: null,
    });
    setRunsState({
      runs: [
        {
          runId: 'run-s1',
          status: 'completed',
          templateId: 'workspace-task-agent',
          startedAt: '2026-11-25T00:00:00+08:00',
          completedAt: '2026-11-25T00:01:00+08:00',
          totalRounds: 1,
          totalToolCalls: 2,
          totalDurationMs: 60000,
          errorMessage: null,
        },
      ],
      total: 1,
      truncated: false,
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p1'));

    expect(screen.queryByTestId('subagent-run-truncated')).toBeNull();
  });
});
