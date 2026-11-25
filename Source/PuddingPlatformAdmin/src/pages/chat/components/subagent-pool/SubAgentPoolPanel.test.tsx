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
  ownerSessionId?: string | null;
  createdAt: string;
  spend?: {
    totalTokens: number;
    pricedCostUsd: number;
    pricedCalls: number;
    unpricedCalls: number;
  } | null;
};

interface PoolState {
  data?: PoolEntryFixture[] | null;
  loading?: boolean;
  error?: string | null;
  /** 池容量上限；默认 null = 未取到，与后端未响应时的返回值一致。 */
  capacity?: number | null;
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
  ownerSessionId: '3f52368a35ef49ab834991e897e8715f',
  createdAt: '2026-11-01T00:00:00Z',
  ...overrides,
});

const setPoolState = (state: PoolState): void => {
  mockedUseSubAgentPool.mockReturnValue({
    data: null,
    loading: false,
    error: null,
    // 显式给 null 而不是留空：留空会得到 undefined，而 undefined 与 null 在
    // `!== null` 类判据下表现不同 —— 默认值必须和生产 hook 的真实返回一致。
    capacity: null,
    reload: jest.fn(),
    ...state,
  });
};

interface RunsState {
  runs?: unknown[] | null;
  total?: number;
  truncated?: boolean;
  /** 花费汇总；默认 null = 从未产生账本，与后端无账本时的返回值一致。 */
  spend?: {
    totalTokens: number;
    pricedCostUsd: number;
    pricedCalls: number;
    unpricedCalls: number;
  } | null;
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
    spend: null,
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

  it('容量已知时在计数旁显示上限', () => {
    setPoolState({ data: [entry()], loading: false, error: null, capacity: 15 });
    render(<SubAgentPoolPanel />);

    expect(screen.getByTestId('subagent-pool-capacity').textContent).toContain(
      '上限 15',
    );
    // 未满时不应出现满额提示。
    expect(screen.queryByTestId('subagent-pool-full-warning')).toBeNull();
  });

  // 这条守卫的是「宁可不写也不要编」：拿不到上限时该栏直接不渲染，
  // 而不是留下一个「上限 undefined」。
  it('容量未知时不渲染上限，也不出现 undefined 字样', () => {
    setPoolState({ data: [entry()], loading: false, error: null, capacity: null });
    render(<SubAgentPoolPanel />);

    expect(screen.queryByTestId('subagent-pool-capacity')).toBeNull();
    expect(screen.getByTestId('subagent-pool-count').textContent).not.toContain(
      'undefined',
    );
  });

  it('列表条数达到上限时给出满额提示', () => {
    const many = Array.from({ length: 15 }, (_, index) =>
      entry({ poolId: `pool-${index}`, name: `成员${index}` }),
    );
    setPoolState({ data: many, loading: false, error: null, capacity: 15 });
    render(<SubAgentPoolPanel />);

    const warning = screen.getByTestId('subagent-pool-full-warning').textContent;
    expect(warning).toContain('15');
    // 措辞必须是「可能失败」：真实判据是内存池条数，而列表读的是库，
    // 两者在池未加载完时不等，断不能在这里写成「已满」。
    expect(warning).toContain('可能失败');
  });

  // 这条守卫的是 `typeof === 'number'` 而不是 `!== null`：
  // hook 如果漏返回 capacity（字段缺失、值为 undefined），`!== null` 仍然为 true，
  // 界面会直接写出「上限 undefined」。setPoolState 默认给 null 造不出这个场景，
  // 所以这里绕过它直接 mockReturnValue。
  it('容量字段缺失（undefined）时同样不渲染上限', () => {
    mockedUseSubAgentPool.mockReturnValue({
      data: [entry()],
      loading: false,
      error: null,
      // 刻意不给 capacity
      reload: jest.fn(),
    });
    render(<SubAgentPoolPanel />);

    expect(screen.queryByTestId('subagent-pool-capacity')).toBeNull();
    expect(screen.getByTestId('subagent-pool-count').textContent).not.toContain(
      'undefined',
    );
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

  it('累计派发次数写成「派发 N 次」，不写成「任务 N」', () => {
    setPoolState({
      data: [
        entry({
          poolId: 'p-count',
          name: '派发过三次',
          taskCount: 3,
          status: 'idle',
        }),
      ],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    // 左栏摘要：「任务 3」会被读成「有 3 个任务在跑」，而同屏的「空闲」也不反驳它。
    const summary =
      screen.getByTestId('subagent-pool-item-p-count').textContent ?? '';
    expect(summary).toContain('派发 3 次');
    expect(summary).not.toContain('任务 3');

    fireEvent.click(screen.getByTestId('subagent-pool-item-p-count'));
    const detail = screen.getByTestId('subagent-pool-detail').textContent ?? '';
    expect(detail).toContain('累计派发');
    expect(detail).not.toContain('任务数');
  });

  it('列表条目直接显示花费，无账本时不渲染（不给 $0）', () => {
    setPoolState({
      data: [
        entry({
          poolId: 'p-priced',
          name: '花过钱的',
          spend: {
            totalTokens: 17_128_349,
            pricedCostUsd: 1.555588,
            pricedCalls: 128,
            unpricedCalls: 0,
          },
        }),
        entry({
          poolId: 'p-unpriced',
          name: '价格没登记的',
          spend: {
            totalTokens: 49_000_000,
            pricedCostUsd: 0,
            pricedCalls: 0,
            unpricedCalls: 5,
          },
        }),
        entry({ poolId: 'p-noledger', name: '没跑过的', spend: null }),
      ],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    const spendOf = (poolId: string): HTMLElement[] =>
      within(screen.getByTestId(`subagent-pool-item-${poolId}`)).queryAllByTestId(
        'subagent-pool-item-spend',
      );

    // 有已计价：金额出现在列表本身，不必点开详情
    expect(spendOf('p-priced')[0]?.textContent).toBe('$1.56');

    // 全部未计价：说「全部未计价」而不是 $0——后者会把「价格没登记」说成「免费」
    expect(spendOf('p-unpriced')[0]?.textContent).toBe('全部未计价');
    expect(spendOf('p-unpriced')[0]?.textContent).not.toBe('$0');

    // 无账本：整列不渲染（不是 $0，也不是—）
    expect(spendOf('p-noledger')).toHaveLength(0);

    // 正对照：无账本条目仍保留状态徽标，排除「整个右栏都没渲染」这种假肢解释
    expect(
      within(screen.getByTestId('subagent-pool-item-p-noledger')).getByTestId(
        'subagent-status-idle',
      ),
    ).toBeTruthy();
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

  // ── 累计花费：后端早已返回，前端此前未声明、未渲染 ────────────
  // 这两条用例锁住「链路真的接到界面」：只改类型声明而不加渲染，或渲染到
  // 一个没人看得见的容器里，都应该红。

  it('有账本时右栏渲染累计花费、token 量与未计价告警', () => {
    setPoolState({
      data: [entry({ poolId: 'p1', name: '检索助手' })],
      loading: false,
      error: null,
    });
    setRunsState({
      runs: [],
      loading: false,
      error: null,
      spend: {
        totalTokens: 17_128_349,
        pricedCostUsd: 1.555588,
        pricedCalls: 125,
        unpricedCalls: 3,
      },
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p1'));

    expect(screen.getByTestId('subagent-spend-amount').textContent).toBe('$1.56');
    expect(screen.getByTestId('subagent-spend-detail').textContent).toContain('17.13M');
    expect(screen.getByTestId('subagent-spend-detail').textContent).toContain('128 次调用');
    expect(screen.getByTestId('subagent-spend-warning').textContent).toContain(
      '3 次没有价格',
    );
  });

  it('无账本时不渲染花费块，而不是渲染成 $0', () => {
    setPoolState({
      data: [entry({ poolId: 'p1', name: '检索助手' })],
      loading: false,
      error: null,
    });
    setRunsState({ runs: [], loading: false, error: null, spend: null });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p1'));

    expect(screen.queryByTestId('subagent-spend')).toBeNull();
    expect(screen.queryByText(/^\$/)).toBeNull();
  });

  it('右栏显示归属会话的短 id，并把完整值放进 title', () => {
    setPoolState({
      data: [
        entry({
          poolId: 'p-owned',
          name: '有主',
          ownerSessionId: '3f52368a35ef49ab834991e897e8715f',
        }),
      ],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p-owned'));

    const detail = screen.getByTestId('subagent-pool-detail');
    expect(detail.textContent).toContain('归属会话');
    // 短 id：截前 8 位；完整 32 位不进正文（右栏会被撑破）
    expect(detail.textContent).toContain('3f52368a…');
    expect(detail.textContent).not.toContain(
      '3f52368a35ef49ab834991e897e8715f',
    );

    // 正对照：完整值必须可获取（title），否则用户没法把条目对上具体会话
    const titled = Array.from(detail.querySelectorAll('[title]')).map(
      (el) => el.getAttribute('title') ?? '',
    );
    expect(
      titled.some((t) =>
        t.includes('3f52368a35ef49ab834991e897e8715f'),
      ),
    ).toBe(true);
  });

  it('未记录归属时写「未记录」，不拿名字或模板顶替', () => {
    setPoolState({
      data: [entry({ poolId: 'p-legacy', name: '老条目', ownerSessionId: null })],
      loading: false,
      error: null,
    });
    render(<SubAgentPoolPanel />);

    fireEvent.click(screen.getByTestId('subagent-pool-item-p-legacy'));

    const detail = screen.getByTestId('subagent-pool-detail');
    expect(detail.textContent).toContain('归属会话');
    expect(detail.textContent).toContain('未记录');
    // 不得用同屏其它字段顶替：模板名与条目名都不是归属会话
    const ownerRow = Array.from(detail.querySelectorAll('div')).find(
      (el) => (el.textContent ?? '').startsWith('归属会话'),
    );
    expect(ownerRow?.textContent).toContain('未记录');
    expect(ownerRow?.textContent).not.toContain('workspace-task-agent');
  });
});
