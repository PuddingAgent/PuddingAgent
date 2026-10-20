import { buildAuthHeaders, documentNameFromPath, loadDocumentContent } from './documentSource';

/**
 * fetch 的最小替身。故意不依赖 jsdom 是否提供 Response：
 * 只实现本模块真正用到的 ok/status/text 三个成员。
 */
function fakeFetch(
  init: { ok?: boolean; status?: number; body?: string; throwError?: Error },
): {
  impl: typeof fetch;
  calls: string[];
  headers: Array<Record<string, string>>;
} {
  const calls: string[] = [];
  const headers: Array<Record<string, string>> = [];
  const impl = (async (url: string, requestInit?: RequestInit) => {
    calls.push(url);
    headers.push((requestInit?.headers as Record<string, string>) ?? {});
    if (init.throwError) throw init.throwError;
    return {
      ok: init.ok ?? true,
      status: init.status ?? 200,
      text: async () => init.body ?? '',
    };
  }) as unknown as typeof fetch;
  return { impl, calls, headers };
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

describe('认证头', () => {
  it('有令牌时加 Bearer 头', () => {
    expect(buildAuthHeaders(() => 'abc')).toEqual({ Authorization: 'Bearer abc' });
  });

  it('没有令牌时不加头，让后端自己返 401', () => {
    // 客户端不能自己编造「未登录」以外的结论：没令牌就是没头，
    // 状态码由服务端决定。
    expect(buildAuthHeaders(() => null)).toEqual({});
    expect(buildAuthHeaders(() => '')).toEqual({});
  });

  it('读取令牌抛异常时降级为无令牌，而不是让预览整体失败', () => {
    // 隐私模式 / 存储被禁时 localStorage.getItem 会抛。
    expect(
      buildAuthHeaders(() => {
        throw new Error('storage blocked');
      }),
    ).toEqual({});
  });

  it('请求真的带上了 Authorization（防「只写函数不接线」）', async () => {
    // 前三条只测纯函数：若忘了在 fetch 里展开 buildAuthHeaders()，
    // 它们仍然全绿，而真实环境永远 401。这条断言才是接线本身。
    const { impl, headers } = fakeFetch({ body: '# x' });

    await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
      readToken: () => 'abc',
    });

    expect(headers[0].Authorization).toBe('Bearer abc');
  });

  it('没有令牌时请求不带 Authorization（上一条的对照组）', async () => {
    const { impl, headers } = fakeFetch({ body: '# x' });

    await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
      readToken: () => null,
    });

    expect(headers[0].Authorization).toBeUndefined();
  });
});

describe('front matter 剥离', () => {
  it('响应体开头的 YAML 块不会渲染成元数据表格（接线断言）', async () => {
    // 前面的 frontMatter.test 只验证纯函数；忘了在 loadDocumentContent 里调用它，
    // 那些用例仍然全绿，而真实页面依旧把 YAML 表格顶在最上面。
    const raw = ['---', 'title: 部署操作手册', 'status: draft', '---', '# 部署操作手册'].join('\n');
    const { impl } = fakeFetch({ body: JSON.stringify({ content: raw }) });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'Docs/手册.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.document.markdown).toBe('# 部署操作手册');
    expect(result.document.markdown).not.toContain('status: draft');
  });

  it('没有 front matter 的文档正文不被改写（对照组）', async () => {
    const raw = '# 纯粹的正文\n\n段落';
    const { impl } = fakeFetch({ body: JSON.stringify({ content: raw }) });

    const result = await loadDocumentContent({
      workspaceId: 'default',
      path: 'a.md',
      fetchImpl: impl,
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.document.markdown).toBe(raw);
  });
});
