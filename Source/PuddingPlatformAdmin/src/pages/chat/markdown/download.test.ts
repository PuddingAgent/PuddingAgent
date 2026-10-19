import { createMarkdownDownload, sanitizeFilename, triggerDownload } from './download';

/**
 * 下载载荷：文件名清洗是重点（来源是消息内容，可能是路径或带非法字符）。
 */
describe('sanitizeFilename', () => {
  it('剥掉路径，只留文件名', () => {
    expect(sanitizeFilename('Docs/12_features/方案.md')).toBe('方案.md');
    expect(sanitizeFilename('temp\\out\\report.pdf')).toBe('report.pdf');
  });

  it('替换 Windows 非法字符', () => {
    expect(sanitizeFilename('a<b>c:d"e|f?g*h.md')).toBe('a_b_c_d_e_f_g_h.md');
  });

  it('空名回退到固定名', () => {
    expect(sanitizeFilename('')).toBe('document');
    expect(sanitizeFilename('   ')).toBe('document');
    expect(sanitizeFilename('路径/')).toBe('document');
  });
});

describe('createMarkdownDownload', () => {
  it('自动补 .md 后缀', () => {
    expect(createMarkdownDownload('报告', '# x').filename).toBe('报告.md');
  });

  it('已有 .md 不重复添加（大小写不敏感）', () => {
    expect(createMarkdownDownload('报告.MD', '# x').filename).toBe('报告.MD');
  });

  it('MIME 带 charset（否则中文可能按系统编码解释）', () => {
    const { blob } = createMarkdownDownload('a.md', '# 标题');
    expect(blob.type).toBe('text/markdown;charset=utf-8');
    // jsdom 的 Blob 没有 text()，只能查 size（中文按 UTF-8 多字节，必须 > 0）
    expect(blob.size).toBeGreaterThan(0);
  });

  it('undefined 内容不崩，产出空文件', () => {
    const { blob } = createMarkdownDownload('a.md', undefined as unknown as string);
    expect(blob.size).toBe(0);
  });
});

describe('triggerDownload', () => {
  it('点击后释放 objectURL（避免 Blob 常驻内存）', () => {
    const createSpy = jest.fn(() => 'blob:mock');
    const revokeSpy = jest.fn();
    const original = { create: URL.createObjectURL, revoke: URL.revokeObjectURL };
    URL.createObjectURL = createSpy as unknown as typeof URL.createObjectURL;
    URL.revokeObjectURL = revokeSpy as unknown as typeof URL.revokeObjectURL;

    const clickSpy = jest.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    try {
      triggerDownload(createMarkdownDownload('a.md', '# x'));
      expect(createSpy).toHaveBeenCalledTimes(1);
      expect(clickSpy).toHaveBeenCalledTimes(1);
      expect(revokeSpy).toHaveBeenCalledWith('blob:mock');
    } finally {
      clickSpy.mockRestore();
      URL.createObjectURL = original.create;
      URL.revokeObjectURL = original.revoke;
    }
  });
});
