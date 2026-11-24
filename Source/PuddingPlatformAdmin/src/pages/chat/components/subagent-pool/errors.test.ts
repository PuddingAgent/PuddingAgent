// ── 子代理池错误文案测试 ────────────────────────────────────────
// 这些断言守的是「操作者看到什么」这件事本身：
// 后端 detail 必须透出、动作失败不得冒充「加载失败」。
import {
  describeSubAgentPoolActionError,
  describeSubAgentPoolError,
} from './errors';

describe('describeSubAgentPoolError', () => {
  it('后端 detail 优先于 message（控制器用的是 { error, detail }）', () => {
    expect(
      describeSubAgentPoolError({
        response: {
          data: {
            error: 'destroy_rejected',
            detail: '无法销毁：条目不在本进程池内或库拒绝了状态迁移。',
            message: 'request error, status: 409',
          },
        },
      }),
    ).toBe('无法销毁：条目不在本进程池内或库拒绝了状态迁移。');
  });

  it('没有 detail 时用 message（其它 admin 端点的形状）', () => {
    expect(
      describeSubAgentPoolError({ response: { data: { message: '无权访问子代理池' } } }),
    ).toBe('无权访问子代理池');
  });

  it('响应体里的空串不算文案，继续往下回落', () => {
    expect(
      describeSubAgentPoolError({ response: { data: { detail: '   ', message: '池未就绪' } } }),
    ).toBe('池未就绪');
  });

  it('没有响应体时用 Error.message（前端自检错误，如「期望数组」）', () => {
    expect(describeSubAgentPoolError(new Error('子代理池响应格式不正确（期望数组）'))).toBe(
      '子代理池响应格式不正确（期望数组）',
    );
  });

  it('什么都没有时用调用方给的兜底，缺省是「加载子代理池失败」', () => {
    expect(describeSubAgentPoolError(undefined)).toBe('加载子代理池失败');
    expect(describeSubAgentPoolError(undefined, '自定义兜底')).toBe('自定义兜底');
  });
});

describe('describeSubAgentPoolActionError', () => {
  it('有后端 detail 时按 detail 显示，兜底文案不参与', () => {
    expect(
      describeSubAgentPoolActionError('destroy', {
        response: { data: { detail: '库拒绝了状态迁移' } },
      }),
    ).toBe('库拒绝了状态迁移');
  });

  it('每个动作的兜底文案互不相同，且销毁不再显示「加载失败」', () => {
    const kinds = ['sleep', 'destroy', 'patch', 'model', 'role'] as const;
    const texts = kinds.map((kind) => describeSubAgentPoolActionError(kind, undefined));

    expect(new Set(texts).size).toBe(kinds.length);
    expect(texts).not.toContain('加载子代理池失败');
    expect(describeSubAgentPoolActionError('destroy', undefined)).toBe('销毁失败');
    expect(describeSubAgentPoolActionError('sleep', undefined)).toBe('休眠失败');
  });
});
