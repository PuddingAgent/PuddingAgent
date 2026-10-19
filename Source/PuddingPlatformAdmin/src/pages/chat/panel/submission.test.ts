import {
  buildUiSubmission,
  parseUiSubmission,
  submissionSignature,
  UI_SUBMISSION_VERSION,
} from './submission';

/**
 * 提交协议：往返一致 + 稳健拒绝 + 幂等键稳定。
 *
 * 这里的"拒绝"用例比"成功"更重要：解析器宁可丢一条提交，
 * 也不能把半截 JSON 或旧版本数据当有效输入喂给模型。
 */
describe('buildUiSubmission', () => {
  it('生成带版本号与类型头部的包裹块', () => {
    const text = buildUiSubmission({
      panelId: 'panel-1',
      kind: 'survey',
      values: { scope: 'a', note: '补充' },
    });

    expect(text.startsWith(`[UI_SUBMISSION v=${UI_SUBMISSION_VERSION} id=panel-1 type=survey]`)).toBe(true);
    expect(text.trimEnd().endsWith('[/UI_SUBMISSION]')).toBe(true);
    expect(text).toContain('"scope": "a"');
  });

  it('省略空字段（不让模型读到一堆 undefined）', () => {
    const text = buildUiSubmission({ panelId: 'p', kind: 'draft' });
    expect(text).not.toContain('annotations');
    expect(text).not.toContain('selected');
    expect(text).not.toContain('values');
  });
});

describe('parseUiSubmission', () => {
  it('build → parse 往返一致', () => {
    const payload = {
      panelId: 'panel-1',
      kind: 'variants' as const,
      selected: ['B'],
      annotations: [{ type: 'rect' as const, x: 0.1, y: 0.2, w: 0.3, h: 0.1, text: '太挤' }],
      note: '选 B',
    };

    const parsed = parseUiSubmission(buildUiSubmission(payload));
    expect(parsed).toEqual(payload);
  });

  it('允许包裹块前后有其它文字', () => {
    const body = buildUiSubmission({ panelId: 'p', kind: 'survey', values: { a: 1 } });
    const parsed = parseUiSubmission(`顺带说一句\n${body}\n谢谢`);

    expect(parsed?.panelId).toBe('p');
    expect(parsed?.values).toEqual({ a: 1 });
  });

  it('版本不符直接返回 null', () => {
    const text = buildUiSubmission({ panelId: 'p', kind: 'survey' }).replace(
      'v=1',
      'v=99',
    );
    expect(parseUiSubmission(text)).toBeNull();
  });

  it('未知 type 返回 null', () => {
    const text = buildUiSubmission({ panelId: 'p', kind: 'survey' }).replace(
      'type=survey',
      'type=iframe',
    );
    expect(parseUiSubmission(text)).toBeNull();
  });

  it('缺少闭合标记返回 null', () => {
    const text = buildUiSubmission({ panelId: 'p', kind: 'survey' }).replace(
      '[/UI_SUBMISSION]',
      '',
    );
    expect(parseUiSubmission(text)).toBeNull();
  });

  it('坏 JSON 返回 null（不抛异常）', () => {
    const text = ['[UI_SUBMISSION v=1 id=p type=survey]', '{ not json ', '[/UI_SUBMISSION]'].join('\n');
    expect(parseUiSubmission(text)).toBeNull();
  });

  it('普通文本返回 null', () => {
    expect(parseUiSubmission('这只是一句普通的话')).toBeNull();
    expect(parseUiSubmission('')).toBeNull();
  });

  it('超长文本直接拒绝', () => {
    const text = buildUiSubmission({
      panelId: 'p',
      kind: 'survey',
      values: { a: 'x'.repeat(70 * 1024) },
    });
    expect(parseUiSubmission(text)).toBeNull();
  });
});

describe('submissionSignature（幂等键）', () => {
  it('相同内容产生相同签名（键顺序无关）', () => {
    const a = submissionSignature({
      panelId: 'p',
      kind: 'survey',
      values: { a: 1, b: 2 },
    });
    const b = submissionSignature({
      panelId: 'p',
      kind: 'survey',
      values: { b: 2, a: 1 },
    });
    expect(a).toBe(b);
  });

  it('值变化 / 面板变化会改变签名', () => {
    const base = submissionSignature({ panelId: 'p', kind: 'survey', values: { a: 1 } });
    expect(submissionSignature({ panelId: 'p', kind: 'survey', values: { a: 2 } })).not.toBe(base);
    expect(submissionSignature({ panelId: 'q', kind: 'survey', values: { a: 1 } })).not.toBe(base);
  });

  it('选中项顺序不影响签名（防止连点被算两次）', () => {
    const a = submissionSignature({ panelId: 'p', kind: 'variants', selected: ['A', 'B'] });
    const b = submissionSignature({ panelId: 'p', kind: 'variants', selected: ['B', 'A'] });
    expect(a).toBe(b);
  });
});
