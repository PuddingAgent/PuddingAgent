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

    internal static void ValidateLocator(Locator locator, long pageVersion)
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
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var expected))
            throw new BrowserOperationException("browser_invalid_arguments", "Invalid browser ref format");
        if (expected != pageVersion)
        {
            throw new BrowserOperationException(
                "stale_element_reference",
                $"Browser ref belongs to page version {expected}; current version is {pageVersion}");
        }
    }

    internal static async Task<PageSnapshot> SnapshotAsync(
        CoreWebView2 webView,
        long pageVersion,
        SnapshotOptions options,
        CancellationToken ct)
    {
        var normalized = Normalize(options);
        var result = await ExecuteAsync<SnapshotScriptResult>(webView, SnapshotScript, new
        {
            pageVersion,
            normalized.IncludeDom,
            normalized.IncludeAccessibilityTree,
            normalized.IncludeHidden,
            normalized.IncludeIframes,
            normalized.IncludeShadowDom,
            normalized.IncludeHtml,
            normalized.MaxNodes,
            normalized.MaxTextLength,
            normalized.MaxDepth
        }, ct);

        return new PageSnapshot
        {
            DomText = result.DomText,
            AccessibilityTree = result.AccessibilityTree,
            Html = result.Html,
            Truncated = result.Truncated,
            NodeCount = result.NodeCount
        };
    }

    internal static async Task<IReadOnlyList<BrowserElementInfo>> LocateAsync(
        CoreWebView2 webView,
        long pageVersion,
        Locator locator,
        CancellationToken ct)
    {
        ValidateLocator(locator, pageVersion);
        var result = await ExecuteAsync<LocateScriptResult>(webView, LocateScript, new
        {
            pageVersion,
            locator = ToPayload(locator),
            maxResults = 101
        }, ct);
        EnsureSuccess(result.Ok, result.Code, result.Message);
        return result.Elements ?? [];
    }

    internal static async Task<BrowserElementInfo?> InteractAsync(
        CoreWebView2 webView,
        long pageVersion,
        string action,
        Locator? locator,
        object? value,
        double? deltaX,
        double? deltaY,
        CancellationToken ct)
    {
        if (locator is not null)
            ValidateLocator(locator, pageVersion);
        var result = await ExecuteAsync<InteractScriptResult>(webView, InteractScript, new
        {
            pageVersion,
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
        var raw = await webView.ExecuteScriptAsync(script).WaitAsync(ct);
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

    [GeneratedRegex("^v(\\d+)-n\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex BrowserRefRegex();

    private sealed record SnapshotScriptResult
    {
        public string? DomText { get; init; }
        public string? AccessibilityTree { get; init; }
        public string? Html { get; init; }
        public bool Truncated { get; init; }
        public int NodeCount { get; init; }
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

    private sealed record WaitScriptResult : ScriptResult
    {
        public bool Matched { get; init; }
    }

    private const string SnapshotScript = """
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
    const prefix = `v${input.pageVersion}-n`;
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
  const visit = (el, depth, framePrefix) => {
    if (truncated || !el || depth > input.maxDepth) { if (depth > input.maxDepth) truncated = true; return; }
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
  if (document.documentElement) visit(document.documentElement, 0, 'main');
  let html = null;
  if (input.includeHtml) {
    html = document.documentElement?.outerHTML || '';
    if (html.length > input.maxTextLength) { html = html.slice(0, input.maxTextLength); truncated = true; }
  }
  return { domText: input.includeDom ? dom.join('\n') : null,
    accessibilityTree: input.includeAccessibilityTree ? ax.join('\n') : null,
    html, truncated, nodeCount: Math.min(nodeCount, input.maxNodes) };
})()
""";

    private const string LocateScript = """
(() => {
  const input = __PUDDING_INPUT__;
  const locator = input.locator;
  const normalize = value => String(value ?? '').replace(/\s+/g, ' ').trim();
  const visible = el => { const s=getComputedStyle(el), r=el.getBoundingClientRect(); return s.display!=='none'&&s.visibility!=='hidden'&&Number(s.opacity||1)!==0&&r.width>0&&r.height>0; };
  const roleOf = el => el.getAttribute('role') || ({A:'link',BUTTON:'button',INPUT:el.type==='checkbox'?'checkbox':el.type==='radio'?'radio':'textbox',SELECT:'combobox',TEXTAREA:'textbox',IMG:'img'}[el.tagName] || null);
  const nameOf = el => normalize(el.getAttribute('aria-label')||el.getAttribute('alt')||el.getAttribute('title')||el.getAttribute('placeholder')||(el.labels&&[...el.labels].map(x=>x.innerText).join(' '))||el.innerText||el.textContent);
  const roots = [];
  const addRoots = root => { roots.push(root); for (const el of root.querySelectorAll('*')) { if (el.shadowRoot) addRoots(el.shadowRoot); if (el.tagName==='IFRAME') { try { if (el.contentDocument) addRoots(el.contentDocument); } catch {} } } };
  addRoots(document);
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
  if (locator.hasText) elements = elements.filter(el => matchText(el.innerText || el.textContent, locator.hasText));
  if (locator.nth !== null && locator.nth !== undefined) elements = locator.nth >= 0 && locator.nth < elements.length ? [elements[locator.nth]] : [];
  const refState = globalThis.__puddingRefState?.version === input.pageVersion
    ? globalThis.__puddingRefState
    : (globalThis.__puddingRefState = {version: input.pageVersion, next: 0});
  const prefix = `v${input.pageVersion}-n`;
  const describe = el => {
    let ref=el.getAttribute('data-pudding-ref'); if(!ref||!ref.startsWith(prefix)){ref=`${prefix}${++refState.next}`;el.setAttribute('data-pudding-ref',ref);}
    const r=el.getBoundingClientRect();
    return {ref,tag:el.tagName.toLowerCase(),role:roleOf(el),name:nameOf(el).slice(0,500),text:normalize(el.innerText||el.textContent).slice(0,1000),visible:visible(el),enabled:!el.disabled,checked:typeof el.checked==='boolean'?el.checked:null,boundingBox:{x:r.x,y:r.y,width:r.width,height:r.height}};
  };
  return {ok:true,elements:elements.slice(0,input.maxResults).map(describe)};
})()
""";

    private const string InteractScript = """
(() => {
  const input = __PUDDING_INPUT__;
  const normalize = value => String(value ?? '').replace(/\s+/g, ' ').trim();
  const visible = el => { const s=getComputedStyle(el), r=el.getBoundingClientRect(); return s.display!=='none'&&s.visibility!=='hidden'&&Number(s.opacity||1)!==0&&r.width>0&&r.height>0; };
  const roleOf = el => el.getAttribute('role') || ({A:'link',BUTTON:'button',INPUT:el.type==='checkbox'?'checkbox':el.type==='radio'?'radio':'textbox',SELECT:'combobox',TEXTAREA:'textbox',IMG:'img'}[el.tagName] || null);
  const nameOf = el => normalize(el.getAttribute('aria-label')||el.getAttribute('alt')||el.getAttribute('title')||el.getAttribute('placeholder')||(el.labels&&[...el.labels].map(x=>x.innerText).join(' '))||el.innerText||el.textContent);
  const roots=[]; const addRoots=root=>{roots.push(root);for(const el of root.querySelectorAll('*')){if(el.shadowRoot)addRoots(el.shadowRoot);if(el.tagName==='IFRAME'){try{if(el.contentDocument)addRoots(el.contentDocument);}catch{}}}}; addRoots(document);
  const all=()=>[...new Set(roots.flatMap(root=>[...root.querySelectorAll('*')]))];
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
    out=[...new Set(out)]; if(locator.hasText)out=out.filter(el=>match(el.innerText||el.textContent,locator.hasText));
    if(locator.nth!==null&&locator.nth!==undefined)out=locator.nth>=0&&locator.nth<out.length?[out[locator.nth]]:[];
    return out;
  };
  if(input.action==='scroll'&&!input.locator){window.scrollBy(Number(input.deltaX||0),Number(input.deltaY||0));return{ok:true,element:null};}
  const matches=locate(input.locator);
  if(matches.length===0)return{ok:false,code:'browser_element_not_found',message:'No element matched the locator'};
  if(matches.length>1)return{ok:false,code:'browser_locator_ambiguous',message:`Locator matched ${matches.length} elements`};
  const el=matches[0];
  if(input.action!=='scroll'&&!visible(el))return{ok:false,code:'browser_element_not_visible',message:'Matched element is not visible'};
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
  const refState=globalThis.__puddingRefState?.version===input.pageVersion?globalThis.__puddingRefState:(globalThis.__puddingRefState={version:input.pageVersion,next:0});let ref=el.getAttribute('data-pudding-ref');const prefix=`v${input.pageVersion}-n`;if(!ref||!ref.startsWith(prefix)){ref=`${prefix}${++refState.next}`;el.setAttribute('data-pudding-ref',ref);}const r=el.getBoundingClientRect();
  return{ok:true,element:{ref,tag:el.tagName.toLowerCase(),role:roleOf(el),name:nameOf(el).slice(0,500),text:normalize(el.innerText||el.textContent).slice(0,1000),visible:visible(el),enabled:!el.disabled,checked:typeof el.checked==='boolean'?el.checked:null,boundingBox:{x:r.x,y:r.y,width:r.width,height:r.height}}};
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
