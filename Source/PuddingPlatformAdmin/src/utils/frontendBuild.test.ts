import {
  FALLBACK_FRONTEND_BUILD,
  formatBuildTime,
  formatFrontendVersionDetail,
  formatFrontendVersionLabel,
  getFrontendBuildInfo,
  type FrontendBuildInfo,
} from './frontendBuild';

const base: FrontendBuildInfo = {
  version: '6.1.0',
  commit: 'abc1234def5678',
  commitShort: 'abc1234',
  commitTime: '2026-10-01T12:00:00.000Z',
  dirty: false,
  builtAt: '2026-10-01T14:57:00.000Z',
};

describe('前端构建信息格式化（AGENTS.md 版本号约定）', () => {
  it('formatBuildTime 输出本地 YYYY-MM-DD HH:mm，非法/空输入不留假时间', () => {
    expect(formatBuildTime('')).toBe('');
    expect(formatBuildTime('not-a-date')).toBe('');
    expect(formatBuildTime(base.builtAt)).toMatch(
      /^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$/,
    );
  });

  it('徽标短文本 = v版本 · 短哈希 · 构建时间', () => {
    expect(formatFrontendVersionLabel(base)).toMatch(
      /^v6\.1\.0 · abc1234 · \d{4}-\d{2}-\d{2} \d{2}:\d{2}$/,
    );
  });

  it('工作树不干净时标 +dirty', () => {
    expect(formatFrontendVersionLabel({ ...base, dirty: true })).toMatch(
      /^v6\.1\.0\+dirty · abc1234 · /,
    );
  });

  it('缺构建时间时退回提交时间；两者都缺则不带时间尾巴', () => {
    const noBuiltAt = formatFrontendVersionLabel({ ...base, builtAt: '' });
    expect(noBuiltAt).toMatch(/^v6\.1\.0 · abc1234 · \d{4}-\d{2}-\d{2} \d{2}:\d{2}$/);
    const noTime = formatFrontendVersionLabel({
      ...base,
      builtAt: '',
      commitTime: '',
    });
    expect(noTime).toBe('v6.1.0 · abc1234');
  });

  it('悬停详情含全哈希、提交时间、构建时间；脏树给出说明', () => {
    const detail = formatFrontendVersionDetail(base);
    expect(detail).toContain('前端版本 v6.1.0');
    expect(detail).toContain('提交 abc1234def5678');
    expect(detail).toContain('提交时间 ');
    expect(detail).toContain('构建时间 ');
    expect(formatFrontendVersionDetail({ ...base, dirty: true })).toContain(
      '+dirty',
    );
  });

  it('未注入构建信息时返回 unknown 占位，不用假版本号冒充构建', () => {
    // jest 环境没有 umi define，因此这里就是“未注入”的真实路径
    const info = getFrontendBuildInfo();
    expect(info).toEqual(FALLBACK_FRONTEND_BUILD);
    expect(formatFrontendVersionLabel(info)).toBe('v0.0.0 · unknown');
  });

  describe('注入值归一化（构建器可能给对象，也可能给 JSON 字符串）', () => {
    const injected = {
      version: '7.2.1',
      commit: 'ffeeddccbbaa9988',
      commitShort: 'ffeeddc',
      commitTime: '2026-10-01T10:00:00.000Z',
      dirty: true,
      builtAt: '2026-10-01T11:00:00.000Z',
    };
    const setGlobal = (value: unknown) => {
      (
        globalThis as unknown as { __PUDDING_FRONTEND__?: unknown }
      ).__PUDDING_FRONTEND__ = value;
    };

    afterEach(() => setGlobal(undefined));

    it('对象形态直接采用', () => {
      setGlobal(injected);
      expect(getFrontendBuildInfo()).toEqual(injected);
    });

    it('JSON 字符串形态也能解析（否则会被当成字符键）', () => {
      setGlobal(JSON.stringify(injected));
      expect(getFrontendBuildInfo()).toEqual(injected);
    });

    it('非法字符串或缺少 version 时退回占位，不抛错', () => {
      setGlobal('{not json');
      expect(getFrontendBuildInfo()).toEqual(FALLBACK_FRONTEND_BUILD);
      setGlobal(JSON.stringify({ commit: 'x' }));
      expect(getFrontendBuildInfo()).toEqual(FALLBACK_FRONTEND_BUILD);
      setGlobal(42);
      expect(getFrontendBuildInfo()).toEqual(FALLBACK_FRONTEND_BUILD);
    });
  });
});
