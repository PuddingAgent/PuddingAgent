// 诊断 2：定位「scrollHeight 塌缩」的溢出包含关系（谁在裁剪滚动内容）。
import { newPage, delay } from './lib/cdp.mjs';
import { writeFileSync, mkdirSync } from 'node:fs';
import { buildLongMarkdown } from './lib/content.mjs';

const BASE = 'http://localhost';
const OUT = 'temp/perf/out';
mkdirSync(OUT, { recursive: true });
const page = await newPage('about:blank');

async function boxModel(label) {
  const state = await page.evaluate(`(() => {
    const list = document.querySelector('[data-testid="chat-message-list"]');
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    const chain = [];
    let node = list?.parentElement ?? null;
    const describe = (el, name) => {
      if (!el) return null;
      const cs = getComputedStyle(el);
      return {
        name,
        tag: el.tagName,
        cls: (el.className || '').toString().slice(0, 90),
        testid: el.getAttribute?.('data-testid') ?? null,
        scrollTop: el.scrollTop,
        scrollHeight: el.scrollHeight,
        clientHeight: el.clientHeight,
        offsetHeight: el.offsetHeight,
        rectH: Math.round(el.getBoundingClientRect().height),
        inlineH: el.style?.height ?? '',
        overflowY: cs.overflowY,
        position: cs.position,
        display: cs.display,
        flex: cs.flex,
        minHeight: cs.minHeight,
        maxHeight: cs.maxHeight,
        contain: cs.contain,
        contentVisibility: cs.contentVisibility,
      };
    };
    const items = Array.from(document.querySelectorAll('[data-viewport-item-id]'));
    return {
      list: describe(list, 'chat-message-list'),
      content: describe(content, 'viewport-content'),
      ancestors: chain,
      parent: describe(list?.parentElement, 'parent'),
      grandparent: describe(list?.parentElement?.parentElement, 'grandparent'),
      great: describe(list?.parentElement?.parentElement?.parentElement, 'great-grandparent'),
      itemCount: items.length,
      itemHeights: items.map((n) => Math.round(n.getBoundingClientRect().height)).slice(0, 6),
      maxContentInlineHeight: content?.style?.height ?? null,
    };
  })()`);
  console.log('\n### ' + label);
  console.log(JSON.stringify(state, null, 1));
  return state;
}

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
    const isLong = i === 18;
    messages.push({
      messageId: `box-msg-${i}`,
      turnId: `box-turn-${i}`,
      runId: null,
      role: isUser ? 'user' : 'agent',
      sourceKind: isUser ? 'user' : 'agent',
      sourceId: isUser ? 'admin' : 'default.audit-agent.001',
      sourceName: isUser ? 'admin' : '审批审计员',
      messageType: isUser ? 'user_message' : 'agent_output',
      llmRole: isUser ? 'user' : 'agent',
      createdAt: new Date(Date.parse('2026-10-01T02:00:00Z') + i * 60000).toISOString(),
      content: isLong ? longText : isUser ? `盒子提问 ${i}` : `盒子回复 ${i}`,
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
  const before = await boxModel('mounted (bottom, long row visible)');

  await page.evaluate(`(() => { const el = document.querySelector('[data-testid="chat-message-list"]'); el.scrollTop = 0; })()`);
  await delay(1500);
  const after = await boxModel('after scroll-to-top (long row unmounted)');

  writeFileSync(`${OUT}/box-model.json`, JSON.stringify({ before, after }, null, 2), 'utf8');
  console.log('\nwrote box-model.json');
} catch (err) {
  console.error('FAILED:', err.message);
  process.exitCode = 1;
} finally {
  page.close();
}
