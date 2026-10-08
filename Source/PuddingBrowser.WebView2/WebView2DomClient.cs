using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using PuddingBrowser.Abstractions;

namespace PuddingBrowser.WebView2;

internal static partial class WebView2DomClient
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    internal static SnapshotOptions Normalize(SnapshotOptions options) => options with
    {
        MaxNodes = Math.Clamp(options.MaxNodes, 1, 10_000),
        MaxTextLength = Math.Clamp(options.MaxTextLength, 256, 500_000),
        MaxDepth = Math.Clamp(options.MaxDepth, 1, 64)
    };

    /// <summary>
    /// 校验 ref 是否属于**这个页面与这一版文档**（设计方案 §5）。
    ///
    /// 为什么必须带 <paramref name="pageToken"/>：ref 的序号是**每页独立**生成的，
    /// 所以「页面 A 的 v7-n3」与「页面 B 的 v7-n3」会同时存在。只比版本号时，
    /// 跨页使用同一版本号的引用会**静默命中另一个页面的元素**（点错目标），
    /// 这正是设计点名「即使两页都恰好处于 v7」的情形。
    /// token 由驱动侧生成、页面无法伪造，因此跨页/跨 frame 的引用在这里被拒绝。
    /// </summary>
    internal static void ValidateLocator(Locator locator, long pageVersion, string pageToken)
    {
        ArgumentNullException.ThrowIfNull(locator);
        if (string.IsNullOrWhiteSpace(locator.Value))
            throw new BrowserOperationException("browser_invalid_arguments", "Locator value is required");
        if (locator.Frame is not null || locator.Has is not null)
        {
            throw new BrowserOperationException(
                "browser_operation_not_supported",
                "Frame and compound Has locators are not available in Phase 2A-3");
        }

        if (locator.Kind != LocatorKind.Ref)
            return;

        var match = BrowserRefRegex().Match(locator.Value.Trim());
        if (!match.Success || !long.TryParse(match.Groups[2].Value, out var expected))
            throw new BrowserOperationException("browser_invalid_arguments", "Invalid browser ref format");

        // 作用域（页面 token）与文档世代任一不符 ⇒ 引用过期。
        // 提示只说「重新观察」，**不**回指任何节点（禁止自动重绑，设计 §1.1）。
        if (!string.Equals(match.Groups[1].Value, pageToken, StringComparison.OrdinalIgnoreCase)
            || expected != pageVersion)
        {
            throw new BrowserOperationException(
                "stale_element_reference",
                "Browser ref does not belong to this page's current document;"
                + " re-observe the page and locate a fresh ref (refs are never re-bound to another node)");
        }
    }

    internal static async Task<PageSnapshot> SnapshotAsync(
        CoreWebView2 webView,
        long pageVersion,
        string pageToken,
        SnapshotOptions options,
        CancellationToken ct)
    {
        var normalized = Normalize(options);

        // 作用域 ref 与普通引用同一条规则（§5）：**先**校验它属于这一页这一版，再进脚本。
        // 过期就明确拒绝——绝不"降级成整页扫描"，那会让调用方以为拿到的是子树。
        if (normalized.ScopeRef is { } scopeRef)
        {
            ValidateLocator(
                new Locator { Kind = LocatorKind.Ref, Value = scopeRef }, pageVersion, pageToken);
        }

        var result = await ExecuteAsync<SnapshotScriptResult>(webView, SnapshotScript, new
        {
            pageVersion,
            pageToken,
            normalized.IncludeDom,
            normalized.IncludeAccessibilityTree,
            normalized.IncludeHidden,
            normalized.IncludeIframes,
            normalized.IncludeShadowDom,
            normalized.IncludeHtml,
            normalized.MaxNodes,
            normalized.MaxTextLength,
            normalized.MaxDepth,
            scopeRef = options.ScopeRef,
            includeContentBlocks = options.IncludeContentBlocks
        }, ct);

        EnsureSuccess(result.Ok, result.Code, result.Message);

        return new PageSnapshot
        {
            DomText = result.DomText,
            AccessibilityTree = result.AccessibilityTree,
            Html = result.Html,
            ContentBlocks = result.ContentBlocks,
            Truncated = result.Truncated,
            NodeCount = result.NodeCount
        };
    }

    internal static async Task<IReadOnlyList<BrowserElementInfo>> LocateAsync(
        CoreWebView2 webView,
        long pageVersion,
        string pageToken,
        Locator locator,
        CancellationToken ct)
    {
        ValidateLocator(locator, pageVersion, pageToken);
        var result = await ExecuteAsync<LocateScriptResult>(webView, LocateScript, new
        {
            pageVersion,
            pageToken,
            locator = ToPayload(locator),
            maxResults = 101
        }, ct);
        EnsureSuccess(result.Ok, result.Code, result.Message);
        return result.Elements ?? [];
    }

    internal static async Task<BrowserElementInfo?> InteractAsync(
        CoreWebView2 webView,
        long pageVersion,
        string pageToken,
        string action,
        Locator? locator,
        object? value,
        double? deltaX,
        double? deltaY,
        CancellationToken ct)
    {
        if (locator is not null)
            ValidateLocator(locator, pageVersion, pageToken);
        var result = await ExecuteAsync<InteractScriptResult>(webView, InteractScript, new
        {
            pageVersion,
            pageToken,
            action,
            locator = locator is null ? null : ToPayload(locator),
            value,
            deltaX,
            deltaY
        }, ct);
        EnsureSuccess(result.Ok, result.Code, result.Message);
        return result.Element;
    }

    internal static async Task<bool> EvaluateSelectorConditionAsync(
        CoreWebView2 webView,
        string selector,
        bool hidden,
        CancellationToken ct)
    {
        var result = await ExecuteAsync<WaitScriptResult>(webView, WaitScript, new
        {
            selector,
            hidden
        }, ct);
        EnsureSuccess(result.Ok, result.Code, result.Message);
        return result.Matched;
    }

    internal static bool WildcardMatch(string input, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static object ToPayload(Locator locator) => new
    {
        kind = locator.Kind.ToString().ToLowerInvariant(),
        value = locator.Value,
        locator.Name,
        locator.Exact,
        locator.Nth,
        locator.HasText
    };

    private static async Task<T> ExecuteAsync<T>(
        CoreWebView2 webView,
        string template,
        object input,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var inputJson = JsonSerializer.Serialize(input, s_jsonOptions);
        var script = template.Replace("__PUDDING_INPUT__", inputJson, StringComparison.Ordinal);
#if WINUI_BROWSER
        var raw = await webView.ExecuteScriptAsync(script).AsTask(ct);
#else
        var raw = await webView.ExecuteScriptAsync(script).WaitAsync(ct);
#endif
        try
        {
            return JsonSerializer.Deserialize<T>(raw, s_jsonOptions)
                   ?? throw new JsonException("Script returned null");
        }
        catch (JsonException ex)
        {
            throw new BrowserOperationException(
                "browser_operation_failed",
                $"WebView2 returned an invalid DOM result: {ex.Message}");
        }
    }

    private static void EnsureSuccess(bool ok, string? code, string? message)
    {
        if (ok)
            return;
        throw new BrowserOperationException(
            string.IsNullOrWhiteSpace(code) ? "browser_operation_failed" : code,
            string.IsNullOrWhiteSpace(message) ? "Browser DOM operation failed" : message);
    }

    /// <summary>
    /// ref 线格式：<c>p{页面 token}-v{文档世代}-n{序号}</c>。
    /// 页面 token 由驱动生成（页面写不出来），因此跨页/跨 frame 的引用无法通过校验。
    /// </summary>
    [GeneratedRegex("^p([0-9a-fA-F]{32})-v(\\d+)-n\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex BrowserRefRegex();

    private sealed record SnapshotScriptResult : ScriptResult
    {
        public string? DomText { get; init; }
        public string? AccessibilityTree { get; init; }
        public string? Html { get; init; }
        public bool Truncated { get; init; }
        public int NodeCount { get; init; }

        /// <summary>正文语义块（每行一个 JSON 对象）；由 <c>ArticleMarkdown</c> 组装成 markdown。</summary>
        public string? ContentBlocks { get; init; }
    }

    private record ScriptResult
    {
        public bool Ok { get; init; }
        public string? Code { get; init; }
        public string? Message { get; init; }
    }

    private sealed record LocateScriptResult : ScriptResult
    {
        public IReadOnlyList<BrowserElementInfo>? Elements { get; init; }
    }

    private sealed record InteractScriptResult : ScriptResult
    {
        public BrowserElementInfo? Element { get; init; }
    }

    private sealed record ScrollScriptResult : ScriptResult
    {
        public ScrollScriptOutcome? Scroll { get; init; }
    }

    private sealed record ScrollScriptOutcome
    {
        public string? Container { get; init; }
        public double BeforeX { get; init; }
        public double BeforeY { get; init; }
        public double AfterX { get; init; }
        public double AfterY { get; init; }
        public double ConsumedDeltaX { get; init; }
        public double ConsumedDeltaY { get; init; }
        public bool AtBoundary { get; init; }
        public bool ContentChanged { get; init; }
        public bool? InViewport { get; init; }
    }

    // 滚动目标线名由 Abstractions 的 ScrollTargetWire 统一给出
    // （这里、Core 与 Desktop 曾各写一份 switch —— 那正是漂移的来源）。

    /// <summary>
    /// 执行滚动并返回**结果事实**（设计方案 §7）。元素类目标要求 ref 定位：
    /// 脚本按宿主 ref 精确匹配，不做二次定位（避免"滚了另一个区域"）。
    /// </summary>
    internal static async Task<ScrollOutcome> ScrollAsync(
        CoreWebView2 webView,
        long pageVersion,
        string pageToken,
        ScrollOptions options,
        CancellationToken ct)
    {
        var (target, locatorRef, deltaX, deltaY) = DescribeScroll(options);

        var result = await ExecuteAsync<ScrollScriptResult>(
            webView,
            ScrollScript,
            new { pageVersion, pageToken, target, locatorRef, deltaX, deltaY },
            ct).ConfigureAwait(false);

        EnsureSuccess(result.Ok, result.Code, result.Message);
        var scroll = result.Scroll;
        return new ScrollOutcome
        {
            Container = scroll?.Container ?? "document",
            BeforeX = scroll?.BeforeX ?? 0,
            BeforeY = scroll?.BeforeY ?? 0,
            AfterX = scroll?.AfterX ?? 0,
            AfterY = scroll?.AfterY ?? 0,
            ConsumedDeltaX = scroll?.ConsumedDeltaX ?? 0,
            ConsumedDeltaY = scroll?.ConsumedDeltaY ?? 0,
            AtBoundary = scroll?.AtBoundary ?? false,
            ContentChanged = scroll?.ContentChanged ?? false,
        };
    }

    /// <summary>
    /// 把 <see cref="ScrollOptions"/> 规范化成脚本能用的四元组。
    /// 元素类目标**必须是 ref**：脚本按宿主 ref 精确匹配；其它定位方式在这里明确拒绝，
    /// 而不是先滚一个"看起来像"的区域。
    /// </summary>
    internal static (string Target, string? LocatorRef, double? DeltaX, double? DeltaY) DescribeScroll(ScrollOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var needsElement = options.Target is ScrollTarget.Element or ScrollTarget.ToRef;
        if (needsElement)
        {
            if (options.Locator is not { Kind: LocatorKind.Ref } locator)
            {
                throw new BrowserOperationException(
                    "browser_invalid_arguments",
                    $"scroll target '{ScrollTargetWire.NameOf(options.Target)}' requires a ref locator (re-locate first)");
            }

            return (ScrollTargetWire.NameOf(options.Target), locator.Value, options.DeltaX, options.DeltaY);
        }

        // page/to_top/to_bottom：可以带 ref（表示"滚它最近的可滚动祖先"），也可以不带（滚文档）。
        var reference = options.Locator is { Kind: LocatorKind.Ref } refLocator ? refLocator.Value : null;
        return (ScrollTargetWire.NameOf(options.Target), reference, options.DeltaX, options.DeltaY);
    }

    private sealed record WaitScriptResult : ScriptResult
    {
        public bool Matched { get; init; }
    }

    internal const string SnapshotScript = """
(() => {
  const input = __PUDDING_INPUT__;
  const dom = [];
  const ax = [];
  let nodeCount = 0;
  let textLength = 0;
  const refState = globalThis.__puddingRefState?.version === input.pageVersion
    ? globalThis.__puddingRefState
    : (globalThis.__puddingRefState = {version: input.pageVersion, next: 0});
  let truncated = false;
  const normalize = value => String(value ?? '').replace(/\s+/g, ' ').trim();
  const visible = el => {
    const style = getComputedStyle(el);
    const rect = el.getBoundingClientRect();
    return style.display !== 'none' && style.visibility !== 'hidden' && Number(style.opacity || 1) !== 0
      && rect.width > 0 && rect.height > 0;
  };
  const roleOf = el => el.getAttribute('role') || ({
    A:'link', BUTTON:'button', INPUT: el.type === 'checkbox' ? 'checkbox' : el.type === 'radio' ? 'radio' : 'textbox',
    SELECT:'combobox', TEXTAREA:'textbox', IMG:'img', FORM:'form', NAV:'navigation', MAIN:'main'
  }[el.tagName] || null);
  const nameOf = el => normalize(el.getAttribute('aria-label') || el.getAttribute('alt')
    || el.getAttribute('title') || el.getAttribute('placeholder')
    || (el.labels && [...el.labels].map(x => x.innerText).join(' ')) || el.innerText || el.textContent);
  const interactive = el => el.matches('a[href],button,input,textarea,select,summary,[role],[contenteditable="true"],[tabindex]');
  const ensureRef = el => {
    const prefix = `p${input.pageToken}-v${input.pageVersion}-n`;
    let ref = el.getAttribute('data-pudding-ref');
    if (!ref || !ref.startsWith(prefix)) {
      ref = `${prefix}${++refState.next}`;
      el.setAttribute('data-pudding-ref', ref);
    }
    return ref;
  };
  const append = (target, line) => {
    if (!line) return;
    if (textLength + line.length + 1 > input.maxTextLength) { truncated = true; return; }
    target.push(line);
    textLength += line.length + 1;
  };
  // 正文语义块提取（设计 §6.1 mode=text）：**只回答"哪块是正文"**，markdown 由 C# 侧组装
  // （那边的结构/预算/分页是纯函数，可被完整验证）。
  // 顺序：任务明确选定的 scope > article > main > [role=main] > 普通块回退；
  // 不写站点专用选择器；去导航/页脚噪声；表格/代码空白/链接目的地址都保留。
  const extractBlocks = scopeRoot => {
    const article = scopeRoot === document.documentElement
      ? (document.querySelector('article') || document.querySelector('main')
         || document.querySelector('[role=main]') || document.documentElement)
      : scopeRoot;
    const noise = el => {
      const tag = el.tagName;
      if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'TEMPLATE'
          || tag === 'NAV' || tag === 'FOOTER' || tag === 'ASIDE') return true;
      if (el.hasAttribute('hidden') || el.getAttribute('aria-hidden') === 'true') return true;
      const role = el.getAttribute('role');
      return role === 'navigation' || role === 'banner' || role === 'contentinfo';
    };
    // 行内内容：链接**带上目的地址**、行内代码带反引号、<br> 保留换行。
    const inline = el => {
      let out = '';
      for (const node of el.childNodes) {
        if (node.nodeType === 3) { out += node.nodeValue; continue; }
        if (node.nodeType !== 1) continue;
        if (noise(node)) continue;
        const tag = node.tagName;
        if (tag === 'BR') { out += '\n'; continue; }
        if (tag === 'A' && node.getAttribute('href')) {
          out += `[${normalize(node.innerText || node.textContent)}](${node.href})`; continue;
        }
        if (tag === 'CODE') { out += '`' + (node.textContent || '') + '`'; continue; }
        out += inline(node);
      }
      return out;
    };
    const listDepth = el => {
      let depth = 1;
      for (let node = el.parentElement; node && node !== article; node = node.parentElement) {
        if (node.tagName === 'UL' || node.tagName === 'OL') depth++;
      }
      return depth;
    };
    const blocks = [];
    const push = value => { if (blocks.length < 5000) blocks.push(JSON.stringify(value)); };
    const walk = el => {
      for (const child of el.children) {
        if (noise(child)) continue;
        const tag = child.tagName;
        if (/^H[1-6]$/.test(tag)) { push({k: tag.toLowerCase(), t: normalize(inline(child))}); continue; }
        if (tag === 'P') { const t = normalize(inline(child)); if (t) push({k: 'p', t}); continue; }
        if (tag === 'LI') {
          const t = normalize(inline(child));
          if (t) push({k: 'li', t, d: listDepth(child)});
          walk(child); continue;
        }
        if (tag === 'PRE') { push({k: 'pre', t: child.textContent || ''}); continue; }
        if (tag === 'BLOCKQUOTE') {
          const t = normalize(inline(child));
          if (t) push({k: 'quote', t});
          walk(child); continue;
        }
        if (tag === 'HR') { push({k: 'hr'}); continue; }
        if (tag === 'TABLE') {
          push({k: 'table', r: [...child.querySelectorAll('tr')].slice(0, 200)
            .map(tr => [...tr.children].map(td => normalize(td.innerText || td.textContent)))});
          continue;
        }
        if (tag === 'UL' || tag === 'OL') { walk(child); continue; }
        if (tag === 'A' && child.getAttribute('href')) {
          push({k: 'a', t: normalize(child.innerText || child.textContent), h: child.href}); continue;
        }
        if (tag === 'DIV' || tag === 'SECTION' || tag === 'ARTICLE' || tag === 'MAIN'
            || tag === 'HEADER' || tag === 'FIGURE' || tag === 'SPAN' || tag === 'BODY') {
          walk(child); continue;
        }
        // 兜底：没有已知块级子元素 ⇒ 把它的文本当一个段落（**不丢内容**）。
        if (!child.querySelector('p,li,h1,h2,h3,h4,h5,h6,pre,table,blockquote,ul,ol,div,section')) {
          const t = normalize(inline(child));
          if (t) push({k: 'p', t});
        }
      }
    };
    walk(article);
    return blocks.join('\n');
  };
  const visit = (el, depth, framePrefix) => {    if (truncated || !el || depth > input.maxDepth) { if (depth > input.maxDepth) truncated = true; return; }
    if (++nodeCount > input.maxNodes) { truncated = true; return; }
    const isVisible = visible(el);
    if (input.includeHidden || isVisible) {
      const tag = el.tagName.toLowerCase();
      const role = roleOf(el);
      const name = nameOf(el);
      const ref = interactive(el) ? ensureRef(el) : null;
      const marker = ref ? ` ref=${ref}` : '';
      if (input.includeDom) append(dom, `${'  '.repeat(depth)}<${tag}${marker}> ${normalize(el.childNodes.length === 1 ? el.textContent : '')}`);
      if (input.includeAccessibilityTree && (role || name))
        append(ax, `${'  '.repeat(depth)}${role || tag}${marker}${name ? ` name="${name.slice(0, 500)}"` : ''}`);
    }
    for (const child of el.children) visit(child, depth + 1, framePrefix);
    if (input.includeShadowDom && el.shadowRoot)
      for (const child of el.shadowRoot.children) visit(child, depth + 1, `${framePrefix}/shadow`);
    if (input.includeIframes && el.tagName === 'IFRAME') {
      try {
        const root = el.contentDocument?.documentElement;
        if (root) visit(root, depth + 1, `${framePrefix}/iframe`);
      } catch { append(ax, `${'  '.repeat(depth + 1)}iframe cross-origin`); }
    }
  };
  // 作用域（设计 §151 的 scope）：给了 ref 就从那个节点（含自己）开始，而不是整页。
  // 引用过期 ⇒ **明确拒绝**，绝不退化成正页扫描（那会让调用方以为拿到的是子树）。
  let root = document.documentElement;
  if (input.scopeRef) {
    try { root = document.querySelector(`[data-pudding-ref="${input.scopeRef}"]`); } catch { root = null; }
    // 影子节点（脱离文档但仍在内存里）同样不算"这个作用域还成立"。
    if (!root || !root.isConnected) return {ok:false,code:'stale_element_reference',message:'Scope ref does not belong to this page\'s current document; re-observe and locate a fresh ref'};
  }
  if (root) visit(root, 0, 'main');
  let html = null;
  if (input.includeHtml && root) {
    html = root.outerHTML || '';
    if (html.length > input.maxTextLength) { html = html.slice(0, input.maxTextLength); truncated = true; }
  }
  // 正文语义块（设计 §6.1 mode=text）：驱动只负责"哪块是正文"，markdown 由 C# 侧组装。
  // 顺序：任务明确选定的 scope > article > main > [role=main] > 普通块回退；
  // **不写站点专用选择器**；去导航/页脚噪声；表格/代码空白/链接地址保持结构。
  let contentBlocks = null;
  if (input.includeContentBlocks && root) contentBlocks = extractBlocks(root);
  return { ok:true, domText: input.includeDom ? dom.join('\n') : null,
    accessibilityTree: input.includeAccessibilityTree ? ax.join('\n') : null,
    html, truncated, nodeCount: Math.min(nodeCount, input.maxNodes), contentBlocks};
})()
""";

    /// <summary>
    /// 定位脚本。除常规字段外还回读**表单当前值**（<c>value</c>/<c>selectedValues</c>）：
    /// <c>text</c> 不含表单值，只有它能回答「fill 到底写进去了没有」（设计方案 §4.2/§6.2）。
    /// 可见性仅供测试断言（真实运行需要 WebView2，见 <c>WebView2.Smoke</c>）。
    /// </summary>
    internal const string LocateScript = """
(() => {
  const input = __PUDDING_INPUT__;
  const locator = input.locator;
  const normalize = value => String(value ?? '').replace(/\s+/g, ' ').trim();
  const visible = el => { const s=getComputedStyle(el), r=el.getBoundingClientRect(); return s.display!=='none'&&s.visibility!=='hidden'&&Number(s.opacity||1)!==0&&r.width>0&&r.height>0; };
  const roleOf = el => el.getAttribute('role') || ({A:'link',BUTTON:'button',INPUT:el.type==='checkbox'?'checkbox':el.type==='radio'?'radio':'textbox',SELECT:'combobox',TEXTAREA:'textbox',IMG:'img'}[el.tagName] || null);
  const nameOf = el => normalize(el.getAttribute('aria-label')||el.getAttribute('alt')||el.getAttribute('title')||el.getAttribute('placeholder')||(el.labels&&[...el.labels].map(x=>x.innerText).join(' '))||el.innerText||el.textContent);
  // 输入是否写入成功的唯一可靠回执（设计方案 §4.2/§6.2）：text 不含表单值。
  const readValue = el => { if (el.isContentEditable) return el.textContent ?? ''; return typeof el.value === 'string' ? el.value : null; };
  const readSelectedValues = el => el.tagName === 'SELECT' && el.options ? [...el.options].filter(o => o.selected).map(o => o.value) : null;
  // 「在视口内」与「可见」是两个独立事实（设计 §6.2）：屏外但有尺寸 ⇒ visible 真、inViewport 假。
  const inViewport = el => { const r=el.getBoundingClientRect(); return r.width>0&&r.height>0&&r.bottom>0&&r.right>0&&r.top<(window.innerHeight||0)&&r.left<(window.innerWidth||0); };
  const roots = [];
  const addRoots = root => { roots.push(root); for (const el of root.querySelectorAll('*')) { if (el.shadowRoot) addRoots(el.shadowRoot); if (el.tagName==='IFRAME') { try { if (el.contentDocument) addRoots(el.contentDocument); } catch {} } } };  addRoots(document);
  const all = () => [...new Set(roots.flatMap(root => [...root.querySelectorAll('*')]))];
  const matchText = (actual, expected) => locator.exact ? normalize(actual) === normalize(expected) : normalize(actual).toLowerCase().includes(normalize(expected).toLowerCase());
  let elements = [];
  try {
    switch (locator.kind) {
      case 'ref': elements = all().filter(el => el.getAttribute('data-pudding-ref') === locator.value); break;
      case 'css': elements = roots.flatMap(root => [...root.querySelectorAll(locator.value)]); break;
      case 'xpath': {
        for (const root of roots) { const doc = root.ownerDocument || root; if (!doc.evaluate || root instanceof ShadowRoot) continue;
          const result=doc.evaluate(locator.value, root, null, XPathResult.ORDERED_NODE_SNAPSHOT_TYPE, null);
          for(let i=0;i<result.snapshotLength;i++){const node=result.snapshotItem(i);if(node instanceof Element)elements.push(node);} }
        break;
      }
      case 'text': elements = all().filter(el => matchText(el.innerText || el.textContent, locator.value)); break;
      case 'role': elements = all().filter(el => roleOf(el) === locator.value && (!locator.name || matchText(nameOf(el), locator.name))); break;
      case 'label': elements = all().filter(el => el.labels && [...el.labels].some(label => matchText(label.innerText, locator.value))); break;
      case 'placeholder': elements = all().filter(el => matchText(el.getAttribute('placeholder'), locator.value)); break;
      case 'alttext': elements = all().filter(el => matchText(el.getAttribute('alt'), locator.value)); break;
      case 'title': elements = all().filter(el => matchText(el.getAttribute('title'), locator.value)); break;
      case 'testid': elements = all().filter(el => matchText(el.getAttribute('data-testid'), locator.value)); break;
      default: return {ok:false,code:'browser_invalid_arguments',message:`Unsupported locator kind: ${locator.kind}`,elements:[]};
    }
  } catch (error) { return {ok:false,code:'browser_invalid_arguments',message:String(error),elements:[]}; }
  elements = [...new Set(elements)];
  // text 的**最小匹配节点**（设计 §6.2）：祖先也包含同一段文本，若留着它就会"返回整页"。
  // 只保留不包含其它匹配的节点；文档顺序保证祖先在前，因此这就是最内层的那一批。
  if (locator.kind === 'text') {
    const matched = elements;
    elements = matched.filter(el => !matched.some(other => other !== el && el.contains(other)));
  }
  const matchedBeforeFilters = elements.length;
  // 默认排除隐藏元素（设计方案 §6.2）：屏外但有尺寸的元素**仍然保留**（那是 inViewport，不是 hidden）。
  if (!locator.includeHidden) elements = elements.filter(el => visible(el));
  // inViewportOnly 是**正交**的另一个筛选：只有显式要求时才按视口过滤。
  if (locator.inViewportOnly) elements = elements.filter(el => inViewport(el));
  // 零命中要**分清原因**（设计 §6.2：不能把一次零命中推广成"这种定位方式坏了"）：
  // 命中了但全被隐藏/视口筛掉，与真的一个都没命中，是两件不同的事。
  if (elements.length === 0 && matchedBeforeFilters > 0) {
    return {ok:false,code:'browser_locator_matches_filtered',message:`Matched ${matchedBeforeFilters} element(s) but all were hidden or outside the viewport`,elements:[]};
  }
  if (locator.hasText) elements = elements.filter(el => matchText(el.innerText || el.textContent, locator.hasText));
  if (locator.nth !== null && locator.nth !== undefined) elements = locator.nth >= 0 && locator.nth < elements.length ? [elements[locator.nth]] : [];
  const refState = globalThis.__puddingRefState?.version === input.pageVersion
    && globalThis.__puddingRefState?.pageToken === input.pageToken
    ? globalThis.__puddingRefState
    : (globalThis.__puddingRefState = {version: input.pageVersion, pageToken: input.pageToken, next: 0});
  const prefix = `p${input.pageToken}-v${input.pageVersion}-n`;
  const describe = el => {
    let ref=el.getAttribute('data-pudding-ref'); if(!ref||!ref.startsWith(prefix)){ref=`${prefix}${++refState.next}`;el.setAttribute('data-pudding-ref',ref);}
    const r=el.getBoundingClientRect();
    return {ref,tag:el.tagName.toLowerCase(),role:roleOf(el),name:nameOf(el).slice(0,500),text:normalize(el.innerText||el.textContent).slice(0,1000),visible:visible(el),inViewport:inViewport(el),enabled:!el.disabled,checked:typeof el.checked==='boolean'?el.checked:null,value:readValue(el),selectedValues:readSelectedValues(el),boundingBox:{x:r.x,y:r.y,width:r.width,height:r.height}};
  };
  return {ok:true,elements:elements.slice(0,input.maxResults).map(describe)};
})()
""";

    /// <summary>
    /// 滚动脚本（设计方案 §7）：实现 <c>page/element/to_ref/to_top/to_bottom</c> 五种目标，
    /// 并如实报告**结果事实**（目标容器、前后位置、实际消耗位移、是否到边界、内容是否变化）。
    ///
    /// 「无 locator 时检查 <c>document.scrollingElement</c>」；<c>element</c> 优先滚元素自身的滚动盒，
    /// 必要时上溯**最近可滚动祖先**；**不盲目滚另一个区域**。内容没变化**不是失败**。
    /// </summary>
    internal const string ScrollScript = """
(() => {
  const input = __PUDDING_INPUT__;
  const doc = document.scrollingElement || document.documentElement;
  const all = () => { const roots=[document]; for(const el of document.querySelectorAll('*')){ if(el.shadowRoot)roots.push(el.shadowRoot); } return [...new Set(roots.flatMap(root=>[...root.querySelectorAll('*')]))]; };
  const byRef = ref => ref ? (all().find(el => el.getAttribute('data-pudding-ref') === ref) || null) : null;
  const isScrollable = el => { const s=getComputedStyle(el); return /(auto|scroll|overlay)/.test(s.overflowY + s.overflowX); };
  const nearestScrollable = el => { let node=el; while(node && node!==document.documentElement){ if(isScrollable(node) && (node.scrollHeight>node.clientHeight || node.scrollWidth>node.clientWidth)) return node; node=node.parentElement; } return doc; };
  const readPos = el => el===doc ? {x:Number(window.scrollX||0), y:Number(window.scrollY||0)} : {x:Number(el.scrollLeft||0), y:Number(el.scrollTop||0)};
  const readSize = el => el===doc ? {h:Number(doc.scrollHeight||0), w:Number(doc.scrollWidth||0)} : {h:Number(el.scrollHeight||0), w:Number(el.scrollWidth||0)};

  // 目标解析：不盲目滚另一个区域（§7）——只有明确给了 locator 才会去碰容器。
  const element = byRef(input.locatorRef);
  let container = 'document', box = doc;
  if (input.target === 'to_ref') {
    if (!element) return {ok:false,code:'browser_element_not_found',message:'No element matched the locator'};
    container = 'element';
  } else if (input.target === 'element') {
    if (!element) return {ok:false,code:'browser_element_not_found',message:'No element matched the locator'};
    box = isScrollable(element) ? element : nearestScrollable(element);
    container = box === element ? 'element' : 'scrollable-ancestor';
  } else if (input.target === 'to_top' || input.target === 'to_bottom') {
    if (input.locatorRef) {
      if (!element) return {ok:false,code:'browser_element_not_found',message:'No element matched the locator'};
      box = nearestScrollable(element);
      container = 'scrollable-ancestor';
    }
  } else if (input.target !== 'page') {
    // 未登记的目标 fail closed（不在驱动里"猜一个"）。
    return {ok:false,code:'browser_invalid_arguments',message:`unsupported scroll target '${input.target}'`};
  }

  try {
    if (input.target === 'to_ref') {
      element.scrollIntoView({block:'center',inline:'center'});
      const r = element.getBoundingClientRect();
      const visible = r.width>0 && r.height>0 && r.bottom>0 && r.right>0 && r.top<(window.innerHeight||0) && r.left<(window.innerWidth||0);
      return {ok:true,scroll:{container,beforeX:0,beforeY:0,afterX:0,afterY:0,consumedDeltaX:0,consumedDeltaY:0,atBoundary:false,contentChanged:false,inViewport:visible}};
    }

    const before = readPos(box), beforeSize = readSize(box);
    const dx = Number(input.deltaX || 0), dy = Number(input.deltaY || 0);
    if (input.target === 'to_top') { if (box===doc) window.scrollTo(0,0); else { box.scrollLeft=0; box.scrollTop=0; } }
    else if (input.target === 'to_bottom') { const maxY=readSize(box).h; if (box===doc) window.scrollTo(0, maxY); else { box.scrollLeft=box.scrollWidth; box.scrollTop=box.scrollHeight; } }
    else if (box===doc) window.scrollBy(dx, dy);
    else { box.scrollLeft += dx; box.scrollTop += dy; }

    const after = readPos(box), afterSize = readSize(box);
    const consumedX = after.x - before.x, consumedY = after.y - before.y;
    // 「滚不动」= 请求了位移但一个像素都没动（或方向相反）。不是"失败"，而是边界事实。
    const atBoundary = (dy !== 0 && Math.sign(consumedY) !== Math.sign(dy)) || (dx !== 0 && Math.sign(consumedX) !== Math.sign(dx));
    // 内容变化是**独立事实**：没有新增内容不等于滚动失败（§7）。
    const contentChanged = afterSize.h !== beforeSize.h || afterSize.w !== beforeSize.w;
    return {ok:true,scroll:{container,beforeX:before.x,beforeY:before.y,afterX:after.x,afterY:after.y,consumedDeltaX:consumedX,consumedDeltaY:consumedY,atBoundary,contentChanged}};
  } catch (error) { return {ok:false,code:'browser_operation_failed',message:String(error)}; }
})()
""";

    /// <summary>交互脚本：动作之后**在同一节点上**回读 value/selectedValues（不重定位），作为输入证据。</summary>
    internal const string InteractScript = """
(() => {
  const input = __PUDDING_INPUT__;
  const normalize = value => String(value ?? '').replace(/\s+/g, ' ').trim();
  const visible = el => { const s=getComputedStyle(el), r=el.getBoundingClientRect(); return s.display!=='none'&&s.visibility!=='hidden'&&Number(s.opacity||1)!==0&&r.width>0&&r.height>0; };
  const roleOf = el => el.getAttribute('role') || ({A:'link',BUTTON:'button',INPUT:el.type==='checkbox'?'checkbox':el.type==='radio'?'radio':'textbox',SELECT:'combobox',TEXTAREA:'textbox',IMG:'img'}[el.tagName] || null);
  const nameOf = el => normalize(el.getAttribute('aria-label')||el.getAttribute('alt')||el.getAttribute('title')||el.getAttribute('placeholder')||(el.labels&&[...el.labels].map(x=>x.innerText).join(' '))||el.innerText||el.textContent);
  // 交互回执的输入证据：fill/type/select/check 之后回读**同一节点**的值（不重定位）。
  const readValue = el => { if (el.isContentEditable) return el.textContent ?? ''; return typeof el.value === 'string' ? el.value : null; };
  const readSelectedValues = el => el.tagName === 'SELECT' && el.options ? [...el.options].filter(o => o.selected).map(o => o.value) : null;
  // 「在视口内」与「可见」是两个独立事实（设计 §6.2）：写操作要求前者为真（先滚入再复检）。
  const inViewport = el => { const r=el.getBoundingClientRect(); return r.width>0&&r.height>0&&r.bottom>0&&r.right>0&&r.top<(window.innerHeight||0)&&r.left<(window.innerWidth||0); };
  const roots=[]; const addRoots=root=>{roots.push(root);for(const el of root.querySelectorAll('*')){if(el.shadowRoot)addRoots(el.shadowRoot);if(el.tagName==='IFRAME'){try{if(el.contentDocument)addRoots(el.contentDocument);}catch{}}}}; addRoots(document);
  const all=()=>[...new Set(roots.flatMap(root=>[...root.querySelectorAll('*')]))];
  let matchedBeforeFilters=0;
  const locate = locator => {
    if (!locator) return [];
    const match=(a,e)=>locator.exact?normalize(a)===normalize(e):normalize(a).toLowerCase().includes(normalize(e).toLowerCase());
    let out=[];
    try { switch(locator.kind){
      case'ref':out=all().filter(el=>el.getAttribute('data-pudding-ref')===locator.value);break;
      case'css':out=roots.flatMap(root=>[...root.querySelectorAll(locator.value)]);break;
      case'xpath':for(const root of roots){const doc=root.ownerDocument||root;if(!doc.evaluate||root instanceof ShadowRoot)continue;const result=doc.evaluate(locator.value,root,null,XPathResult.ORDERED_NODE_SNAPSHOT_TYPE,null);for(let i=0;i<result.snapshotLength;i++){const node=result.snapshotItem(i);if(node instanceof Element)out.push(node);}}break;
      case'text':out=all().filter(el=>match(el.innerText||el.textContent,locator.value));break;
      case'role':out=all().filter(el=>roleOf(el)===locator.value&&(!locator.name||match(nameOf(el),locator.name)));break;
      case'label':out=all().filter(el=>el.labels&&[...el.labels].some(x=>match(x.innerText,locator.value)));break;
      case'placeholder':out=all().filter(el=>match(el.getAttribute('placeholder'),locator.value));break;
      case'alttext':out=all().filter(el=>match(el.getAttribute('alt'),locator.value));break;
      case'title':out=all().filter(el=>match(el.getAttribute('title'),locator.value));break;
      case'testid':out=all().filter(el=>match(el.getAttribute('data-testid'),locator.value));break;
      default:return [];
    }}catch{return [];}
    out=[...new Set(out)];
    // 与定位脚本同一套语义：text 取最小匹配节点，并记录筛除前的命中数（零命中要分清原因）。
    if(locator.kind==='text'){const m=out;out=m.filter(el=>!m.some(other=>other!==el&&el.contains(other)));}
    matchedBeforeFilters=out.length;
    if(!locator.includeHidden)out=out.filter(visible); if(locator.hasText)out=out.filter(el=>match(el.innerText||el.textContent,locator.hasText));
    if(locator.nth!==null&&locator.nth!==undefined)out=locator.nth>=0&&locator.nth<out.length?[out[locator.nth]]:[];
    return out;
  };
  if(input.action==='scroll'&&!input.locator){window.scrollBy(Number(input.deltaX||0),Number(input.deltaY||0));return{ok:true,element:null};}
  const matches=locate(input.locator);
  // 命中了但全被隐藏/视口筛掉 ≠ 没命中（设计 §6.2：不能把一次零命中推广成"这种定位方式坏了"）。
  if(matches.length===0&&matchedBeforeFilters>0)return{ok:false,code:'browser_locator_matches_filtered',message:`Matched ${matchedBeforeFilters} element(s) but all were hidden or outside the viewport`};
  if(matches.length===0)return{ok:false,code:'browser_element_not_found',message:'No element matched the locator'};
  if(matches.length>1)return{ok:false,code:'browser_locator_ambiguous',message:`Locator matched ${matches.length} elements`};
  const el=matches[0];
  // 引用绑在**节点自身**上（`data-pudding-ref`），因此节点从文档脱离后**照样能查到**，
  // 但动作再也到不了页面。这类"影子节点"必须明确判成引用过期 ——
  // 否则就是静默操作一个不在文档里的元素（设计 §1.1：禁止自动重绑，也绝不装作成功）。
  if(!el.isConnected)return{ok:false,code:'stale_element_reference',message:'Matched element is detached from the document; re-observe the page and locate a fresh ref'};
  if(input.action!=='scroll'&&!visible(el))return{ok:false,code:'browser_element_not_visible',message:'Matched element is not visible'};
  // 写操作要求元素**真的在视口内**（设计 §6.2：先滚入可见区域并复检）。
  // 只读观察（hover/press 之外的检查）不需要，但"看起来在屏外"绝不能当成写成功。
  const mutating=input.action==='click'||input.action==='fill'||input.action==='type'||input.action==='select'||input.action==='check'||input.action==='press'||input.action==='drag';
  if(mutating&&!inViewport(el)){try{el.scrollIntoView({block:'center',inline:'center'});}catch{}}
  if(mutating&&!inViewport(el))return{ok:false,code:'browser_element_not_in_viewport',message:'Matched element could not be scrolled into view'};
  // 遮挡判定（设计 §6.2「隐藏/禁用/**遮挡**目标不按成功处理」）：写操作前做命中测试。
  // 用元素所在根（含 shadow root）的 elementFromPoint，避免 Shadow DOM 里误判成"被挡住"。
  if(mutating){
    const r0=el.getBoundingClientRect();
    const root=(el.getRootNode&&el.getRootNode().elementFromPoint)?el.getRootNode():document;
    const hit=root.elementFromPoint(r0.left+r0.width/2,r0.top+r0.height/2);
    const reachable=hit===el||(hit&&el.contains(hit))||(hit&&hit.contains(el));
    if(!reachable)return{ok:false,code:'browser_element_obstructed',message:'The target is covered by another element'};
  }
  if(el.disabled)return{ok:false,code:'browser_element_disabled',message:'Matched element is disabled'};
  const fire=(name,options={})=>el.dispatchEvent(new Event(name,{bubbles:true,...options}));
  try { switch(input.action){
    case'click':el.focus();el.click();break;
    case'fill':el.focus();if(el.isContentEditable)el.textContent=String(input.value??'');else el.value=String(input.value??'');fire('input');fire('change');break;
    case'type':el.focus();if(el.isContentEditable)el.textContent=(el.textContent||'')+String(input.value??'');else el.value=String(el.value??'')+String(input.value??'');fire('input');break;
    case'press':{const key=String(input.value??'');el.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true}));el.dispatchEvent(new KeyboardEvent('keyup',{key,bubbles:true}));if(key==='Enter'&&el.form)el.form.requestSubmit();break;}
    case'hover':el.dispatchEvent(new MouseEvent('mouseenter',{bubbles:true}));el.dispatchEvent(new MouseEvent('mouseover',{bubbles:true}));break;
    case'scroll':el.scrollIntoView({block:'center',inline:'center'});if(input.deltaX||input.deltaY)el.scrollBy(Number(input.deltaX||0),Number(input.deltaY||0));break;
    case'select':{const values=Array.isArray(input.value)?input.value.map(String):[String(input.value??'')];for(const option of el.options||[])option.selected=values.includes(option.value);fire('input');fire('change');break;}
    case'check':el.checked=Boolean(input.value);fire('input');fire('change');break;
    default:return{ok:false,code:'browser_operation_not_supported',message:`Unsupported interaction: ${input.action}`};
  }} catch(error){return{ok:false,code:'browser_operation_failed',message:String(error)};}
  const refState=globalThis.__puddingRefState?.version===input.pageVersion&&globalThis.__puddingRefState?.pageToken===input.pageToken?globalThis.__puddingRefState:(globalThis.__puddingRefState={version:input.pageVersion,pageToken:input.pageToken,next:0});let ref=el.getAttribute('data-pudding-ref');const prefix=`p${input.pageToken}-v${input.pageVersion}-n`;if(!ref||!ref.startsWith(prefix)){ref=`${prefix}${++refState.next}`;el.setAttribute('data-pudding-ref',ref);}const r=el.getBoundingClientRect();
  return{ok:true,element:{ref,tag:el.tagName.toLowerCase(),role:roleOf(el),name:nameOf(el).slice(0,500),text:normalize(el.innerText||el.textContent).slice(0,1000),visible:visible(el),inViewport:inViewport(el),enabled:!el.disabled,checked:typeof el.checked==='boolean'?el.checked:null,value:readValue(el),selectedValues:readSelectedValues(el),boundingBox:{x:r.x,y:r.y,width:r.width,height:r.height}}};
})()
""";

    private const string WaitScript = """
(() => {
  const input = __PUDDING_INPUT__;
  try {
    const el = document.querySelector(input.selector);
    if (!el) return {ok:true,matched:Boolean(input.hidden)};
    const style=getComputedStyle(el),rect=el.getBoundingClientRect();
    const visible=style.display!=='none'&&style.visibility!=='hidden'&&Number(style.opacity||1)!==0&&rect.width>0&&rect.height>0;
    return {ok:true,matched:input.hidden?!visible:visible};
  } catch(error) { return {ok:false,code:'browser_invalid_arguments',message:String(error),matched:false}; }
})()
""";
}
