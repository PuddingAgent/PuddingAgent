// 诊断 4：为什么 viewport-content 的内联像素高度没有变成 offsetHeight。
import { newPage, delay } from './lib/cdp.mjs';
import { buildLongMarkdown } from './lib/content.mjs';
import { writeFileSync, mkdirSync } from 'node:fs';

const BASE = 'http://localhost';
const OUT = 'temp/perf/out';
mkdirSync(OUT, { recursive: true });
const page = await newPage('about:blank');
const out = {};

const inspect = () =>
  page.evaluate(`(() => {
    const list = document.querySelector('[data-testid="chat-message-list"]');
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    const pick = (el, name) => {
      if (!el) return null;
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      return {
        name,
        tag: el.tagName,
        className: (el.className || '').toString(),
        styleHeight: el.style.height || null,
        styleMinHeight: el.style.minHeight || null,
        styleOther: Array.from(el.style).filter((k) => k !== 'height').map((k) => k + ':' + el.style.getPropertyValue(k)),
        computed: {
          height: cs.height,
          minHeight: cs.minHeight,
          maxHeight: cs.maxHeight,
          flexGrow: cs.flexGrow,
          flexShrink: cs.flexShrink,
          flexBasis: cs.flexBasis,
          alignSelf: cs.alignSelf,
          position: cs.position,
          overflow: cs.overflow,
          contain: cs.contain,
          boxSizing: cs.boxSizing,
          display: cs.display,
          paddingTop: cs.paddingTop,
          paddingBottom: cs.paddingBottom,
        },
        rect: { w: Math.round(r.width), h: Math.round(r.height) },
        clientHeight: el.clientHeight,
        offsetHeight: el.offsetHeight,
        scrollHeight: el.scrollHeight,
        rectHeightHundredths: Math.round(r.height * 100) / 100,
      };
    };
    const chain = [];
    let n = content;
    for (let i = 0; i < 6 && n; i += 1) {
      chain.push(pick(n, i === 0 ? 'content' : 'ancestor-' + i));
      n = n.parentElement;
    }
    return {
      content: pick(content, 'content'),
      list: pick(list, 'list'),
      chain,
      rows: Array.from(document.querySelectorAll('[data-viewport-item-id]')).map((el) => ({
        id: el.getAttribute('data-viewport-item-id'),
        inlineTransform: el.style.transform,
        inlinePosition: el.style.position,
        rectH: Math.round(el.getBoundingClientRect().height),
        offsetTop: el.offsetTop,
        scrollHeight: el.scrollHeight,
      })).slice(0, 8),
    };
  })()`);

try {
  await page.navigate(`${BASE}/admin/user/login`, { waitUntil: 'load' });
  await delay(1000);
  await page.evaluate(`(async () => {
    const res = await fetch('/api/login/account', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'admin', password: 'Admin@123', type: 'account', autoLogin: true }),
    });
    const json = await res.json();
    if (json?.token) localStorage.setItem('pudding_token', json.token);
  })()`);

  const baseline = await page.evaluate(`(async () => {
    const token = localStorage.getItem('pudding_token');
    const r = await fetch('/api/workspaces/default/agents/default.audit-agent.001/conversation', { headers: { Authorization: 'Bearer ' + token } });
    return await r.json();
  })()`);
  const longText = buildLongMarkdown(50_000);
  const messages = [];
  for (let i = 0; i < 19; i += 1) {
    const isUser = i % 2 === 0;
    messages.push({
      messageId: `css-msg-${i}`,
      turnId: `css-turn-${i}`,
      runId: null,
      role: isUser ? 'user' : 'agent',
      sourceKind: isUser ? 'user' : 'agent',
      sourceId: isUser ? 'admin' : 'default.audit-agent.001',
      sourceName: isUser ? 'admin' : '审批审计员',
      messageType: isUser ? 'user_message' : 'agent_output',
      llmRole: isUser ? 'user' : 'agent',
      createdAt: new Date(Date.parse('2026-10-01T02:00:00Z') + i * 60000).toISOString(),
      content: i === 18 ? longText : isUser ? `CSS 提问 ${i}` : `CSS 回复 ${i}`,
      status: 'succeeded',
      processItems: [],
      processSummary: null,
      metadata: null,
      contentParts: null,
      turnOutcome: { status: 'succeeded', errorCode: null, errorMessage: null },
    });
  }
  const stub = { ...baseline, messages, activeRun: null, eventCursor: baseline.eventCursor + 50, updatedAt: new Date().toISOString() };
  await page.send('Fetch.enable', { patterns: [{ urlPattern: '*/conversation*', requestStage: 'Request' }] });
  page.on('Fetch.requestPaused', async (p) => {
    try {
      await page.send('Fetch.fulfillRequest', {
        requestId: p.requestId,
        responseCode: 200,
        responseHeaders: [{ name: 'content-type', value: 'application/json; charset=utf-8' }],
        body: Buffer.from(JSON.stringify(stub), 'utf8').toString('base64'),
      });
    } catch {
      /* ignore */
    }
  });
  await page.send('Storage.clearDataForOrigin', { origin: BASE, storageTypes: 'indexeddb' }).catch(() => {});
  await page.navigate(`${BASE}/admin/chat?perf=1&workspaceId=default&agentId=default.audit-agent.001`, { waitUntil: 'load' });
  await delay(6000);
  out.mounted = await inspect();

  // 匹配规则：找出把 height 压到 662px 的 CSS 来源
  out.matchedRules = await page.evaluate(`(() => {
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    const list = document.querySelector('[data-testid="chat-message-list"]');
    const res = {};
    for (const [name, el] of [['content', content], ['list', list]]) {
      const rules = [];
      for (const sheet of Array.from(document.styleSheets)) {
        let cssRules;
        try { cssRules = sheet.cssRules; } catch { continue; }
        for (const rule of Array.from(cssRules || [])) {
          if (!rule.selectorText) continue;
          let matches = false;
          try { matches = el.matches(rule.selectorText); } catch { matches = false; }
          if (!matches) continue;
          const t = rule.style;
          const interesting = ['height', 'min-height', 'max-height', 'flex', 'flex-grow', 'flex-shrink', 'flex-basis', 'display', 'position', 'overflow', 'overflow-y', 'contain', 'align-self'].filter(k => t.getPropertyValue(k));
          if (!interesting.length) continue;
          rules.push({ selector: rule.selectorText, decls: interesting.map(k => k + ':' + t.getPropertyValue(k)) });
        }
      }
      res[name] = { className: (el.className||'').toString(), rules };
    }
    return res;
  })()`);

  writeFileSync(`${OUT}/css-box.json`, JSON.stringify(out, null, 2), 'utf8');
  console.log('content computed:', JSON.stringify(out.mounted.content.computed));
  console.log('content rect:', JSON.stringify(out.mounted.content.rect), 'offsetHeight:', out.mounted.content.offsetHeight, 'styleHeight:', out.mounted.content.styleHeight);
  console.log('ancestors:');
  for (const a of out.mounted.chain) console.log(' ', a.name, a.className.slice(0, 60), 'computedH=' + a.computed.height, 'rect=' + JSON.stringify(a.rect), 'flex=' + a.computed.flexGrow + '/' + a.computed.flexShrink + '/' + a.computed.flexBasis, 'overflow=' + a.computed.overflow, 'position=' + a.computed.position);
  console.log('\nrows:', JSON.stringify(out.mounted.rows.slice(0, 4), null, 1));
} catch (err) {
  console.error('FAILED:', err.message);
  process.exitCode = 1;
} finally {
  page.close();
}
