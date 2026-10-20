import {
  buildDocumentContentUrl,
  normalizeDocumentPath,
  parseDocPreviewQuery,
} from './documentPath';

/**
 * 这些用例守的是「URL 里的 path 是外部输入」这条边界。
 * 任何一条失效，都意味着工作区外的文件可能被读进浏览器。
 */
describe('normalizeDocumentPath', () => {
  it('接受普通工作区相对路径', () => {
    expect(normalizeDocumentPath('Docs/14_reports/方案.md')).toEqual({
      ok: true,
      path: 'Docs/14_reports/方案.md',
    });
  });

  it('把反斜杠统一成斜杠', () => {
    expect(normalizeDocumentPath('Docs\\14_reports\\方案.md')).toEqual({
      ok: true,
      path: 'Docs/14_reports/方案.md',
    });
  });

  it('丢弃 . 与空段，但保留顺序', () => {
    expect(normalizeDocumentPath('./Docs//a.md')).toEqual({ ok: true, path: 'Docs/a.md' });
  });

  it('拒绝 .. 穿越', () => {
    const result = normalizeDocumentPath('../../secrets.md');
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.reason).toContain('..');
  });

  it('拒绝夹在中间的 ..', () => {
    expect(normalizeDocumentPath('Docs/../../x.md').ok).toBe(false);
  });

  it('拒绝以 / 开头的绝对路径', () => {
    expect(normalizeDocumentPath('/etc/passwd.md').ok).toBe(false);
  });

  it('拒绝 UNC 路径', () => {
    expect(normalizeDocumentPath('\\\\server\\share\\a.md').ok).toBe(false);
  });

  it('拒绝带盘符的路径', () => {
    expect(normalizeDocumentPath('D:\\CodeProject\\a.md').ok).toBe(false);
  });

  it('拒绝控制字符', () => {
    expect(normalizeDocumentPath('Docs/a\u0000.md').ok).toBe(false);
  });

  it('拒绝超长路径', () => {
    expect(normalizeDocumentPath(`${'a'.repeat(600)}.md`).ok).toBe(false);
  });

  it('拒绝非预览扩展名', () => {
    for (const path of ['a.pdf', 'a.html', 'a.exe']) {
      expect(normalizeDocumentPath(path).ok).toBe(false);
    }
  });

  it('扩展名大小写不敏感', () => {
    expect(normalizeDocumentPath('Docs/README.MD')).toEqual({
      ok: true,
      path: 'Docs/README.MD',
    });
  });

  it('空串与非字符串都算缺参数', () => {
    expect(normalizeDocumentPath('   ').ok).toBe(false);
    expect(normalizeDocumentPath(null).ok).toBe(false);
    expect(normalizeDocumentPath(undefined).ok).toBe(false);
  });

  it('只有 ./ 也视为空路径', () => {
    expect(normalizeDocumentPath('././').ok).toBe(false);
  });
});

describe('parseDocPreviewQuery', () => {
  it('解析 path 与 ws', () => {
    expect(parseDocPreviewQuery('?path=Docs%2Fa.md&ws=default')).toEqual({
      rawPath: 'Docs/a.md',
      workspaceId: 'default',
    });
  });

  it('不带前导 ? 也能解析', () => {
    expect(parseDocPreviewQuery('path=Docs%2Fa.md').rawPath).toBe('Docs/a.md');
  });

  it('缺少 ws 时回落到 default', () => {
    expect(parseDocPreviewQuery('?path=a.md').workspaceId).toBe('default');
    expect(parseDocPreviewQuery('?path=a.md&ws=%20').workspaceId).toBe('default');
  });

  it('缺少 path 时 rawPath 为 null', () => {
    expect(parseDocPreviewQuery('').rawPath).toBeNull();
  });
});

describe('buildDocumentContentUrl', () => {
  it('把路径作为查询参数编码，而不是拼进路径段', () => {
    const url = buildDocumentContentUrl('default', 'Docs/带 空格/a.md');
    expect(url.startsWith('/api/workspaces/default/files/content?')).toBe(true);
    expect(url).toContain('path=Docs%2F%E5%B8%A6+%E7%A9%BA%E6%A0%BC%2Fa.md');
  });

  it('工作区 id 会被编码', () => {
    expect(buildDocumentContentUrl('a/b', 'x.md')).toContain('/api/workspaces/a%2Fb/');
  });
});
