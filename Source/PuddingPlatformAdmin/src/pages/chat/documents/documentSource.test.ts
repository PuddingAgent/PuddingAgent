import { documentNameFromPath, loadDocumentContent } from './documentSource';

/**
 * fetch 的最小替身。故意不依赖 jsdom 是否提供 Response：
 * 只实现本模块真正用到的 ok/status/text 三个成员。
 */
function fakeFetch(
  init: { ok?: boolean; status?: number; body?: string; throwError?: Error },
): { impl: typeof fetch; calls: string[] } {
  const calls: string[] = [];
  const impl = (async (url: string) => {
    calls.push(url);
    if (init.throwError) throw init.throwError;
    return {
      ok: init.ok ?? true,
      status: init.status ?? 200,
      text: async () => init.body ?? '',
    };
  }) as unknown as typeof fetch;
  return { impl, calls };
}

describe('loadDocumentContent', () => {
  it('解析 JSON 信封并带出大小', async () => {
    const { impl } = fakeFetch({
      body: JSON.stringify({ content: '# 标题\n\n正文', size: 2048 }),
    });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'Docs/方案.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.document.name).toBe('方案.md');
    expect(result.document.markdown).toBe('# 标题\n\n正文');
    expect(result.document.meta).toBe('2.0 KB');
  });

  it('纯文本响应也能收（后端可能直接吐 markdown）', async () => {
    const { impl } = fakeFetch({ body: '# 直接是 markdown' });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.document.markdown).toBe('# 直接是 markdown');
    // 没有 size 信封时回落到路径，便于用户确认读的是哪个文件
    expect(result.document.meta).toBe('a.md');
  });

  it('JSON 解析失败不会当成错误（那是合法响应形态）', async () => {
    const { impl } = fakeFetch({ body: '{ 这不是合法 JSON' });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(true);
  });

  it('truncated 会显示在 meta 上，而不是静默截断', async () => {
    const { impl } = fakeFetch({
      body: JSON.stringify({ content: 'x', size: 100, truncated: true }),
    });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.document.meta).toContain('内容已截断');
  });

  it('404 说是找不到文件或超出工作区，而不是断言接口未提供', async () => {
    const { impl } = fakeFetch({ ok: false, status: 404 });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toContain('找不到该文件');
    // 后端对「根外」与「不存在」同为 404，所以不能把 404 解读成「接口缺失」。
    expect(result.reason).not.toContain('接口未提供');
    expect(result.hint).toContain('相对路径');
  });

  it('401 明确说是未登录，与 403 的越权区分开', async () => {
    const { impl } = fakeFetch({ ok: false, status: 401 });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toContain('需要登录');
    expect(result.reason).not.toContain('无权');
    expect(result.hint).toContain('登录');
  });

  it('501 明确说是宿主能力缺失，而不是渲染空文档', async () => {
    const { impl } = fakeFetch({ ok: false, status: 501 });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toContain('宿主未提供');
  });

  it('403 说是越权，不混同于文件不存在', async () => {
    const { impl } = fakeFetch({ ok: false, status: 403 });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toContain('无权');
  });

  it('网络异常给出可读原因', async () => {
    const { impl } = fakeFetch({ throwError: new Error('socket hang up') });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toContain('socket hang up');
  });

  it('非法路径在发请求前就被拦下', async () => {
    const { impl, calls } = fakeFetch({ body: 'x' });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: '../../etc/passwd.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(false);
    // 关键：一次请求都没发出去
    expect(calls).toHaveLength(0);
  });

  it('请求的是规整后的路径（反斜杠已统一）', async () => {
    const { impl, calls } = fakeFetch({ body: 'x' });

    await loadDocumentContent({
      workspaceId: 'default',
      path: 'Docs\\a.md',
      fetchImpl: impl,
    });

    expect(calls[0]).toContain('path=Docs%2Fa.md');
  });
});

describe('documentNameFromPath', () => {
  it('取最后一段', () => {
    expect(documentNameFromPath('Docs/14_reports/方案.md')).toBe('方案.md');
  });

  it('无分隔符时原样返回', () => {
    expect(documentNameFromPath('a.md')).toBe('a.md');
  });
});
