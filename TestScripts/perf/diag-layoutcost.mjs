// 诊断 6：布局成本 A/B —— 单条超长消息 vs 多条高行，content-visibility 开/关。
import { newPage, delay } from './lib/cdp.mjs';
import { buildLongMarkdown } from './lib/content.mjs';
import { writeFileSync, mkdirSync } from 'node:fs';

const BASE = 'http://localhost';
const OUT = 'temp/perf/out';
mkdirSync(OUT, { recursive: true });
const page = await newPage('about:blank');
const out = {};

function msg(i, content, role) {
  return {
    messageId: `lay-msg-${i}`,
    turnId: `lay-turn-${i}`,
    runId: null,
    role,
    sourceKind: role === 'user' ? 'user' : 'agent',
    sourceId: role === 'user' ? 'admin' : 'default.audit-agent.001',
    sourceName: role === 'user' ? 'admin' : '审批审计员',
    messageType: role === 'user' ? 'user_message' : 'agent_output',
    llmRole: role === 'user' ? 'user' : 'agent',
    createdAt: new Date(Date.parse('2026-10-01T02:00:00Z') + i * 60000).toISOString(),
    content,
    status: 'succeeded',
    processItems: [],
    processSummary: null,
    metadata: null,
    contentParts: null,
    turnOutcome: { status: 'succeeded', errorCode: null, errorMessage: null },
  };
}

const perfsOf = (snap) => {
  const raw = snap?.summary?.raw?.perfEvents ?? [];
  const dur = (name) =>
    raw.filter((e) => e.name === name).map((e) => e.payload?.durationMs ?? e.payload?.renderToPaintMs).filter((n) => typeof n === 'number');
  const p95 = (arr) => (arr.length ? [...arr].sort((a, b) => a - b)[Math.min(arr.length - 1, Math.ceil(0.95 * arr.length) - 1)] : null);
  return {
    longTasks: dur('browser.longtask'),
    renderToPaint: dur('chat.output.paint'),
    markdownCommit: raw.filter((e) => e.name === 'chat.markdown.render').map((e) => e.payload?.commitMs).filter((n) => typeof n === 'number'),
    layoutShiftCount: raw.filter((e) => e.name === 'browser.layoutShift').length,
    longTaskP95: p95(dur('browser.longtask')),
    longTaskSum: dur('browser.longtask').reduce((a, b) => a + b, 0),
    renderToPaintP95: p95(dur('chat.output.paint')),
  };
};

async function measureScroll(label) {
  await page.evaluate(`window.__PUDDING_PERF__ && window.__PUDDING_PERF__.clear()`);
  const scroll = await page.evaluate(`(async () => {
    const el = document.querySelector('[data-testid="chat-message-list"]');
    if (!el) return { found: false };
    const frame = () => new Promise(r => requestAnimationFrame(r));
    const t0 = performance.now();
    const total = Math.max(0, el.scrollHeight - el.clientHeight);
    for (let pass = 0; pass < 2; pass += 1) {
      const down = pass % 2 === 0;
      for (let i = 0; i <= 50; i += 1) {
        const f = down ? i / 50 : 1 - i / 50;
        el.scrollTop = f * total;
        await frame();
      }
    }
    return { found: true, elapsed: Math.round(performance.now() - t0), total, scrollHeight: el.scrollHeight, rows: document.querySelectorAll('[data-viewport-item-id]').length };
  })()`);
  await delay(1200);
  const snap = await page.evaluate(`(() => {
    const api = window.__PUDDING_PERF__;
    return api ? { summary: api.snapshot({ workspaceId: 'default' }) } : null;
  })()`);
  return { label, scroll, perf: perfsOf(snap), domNodes: await page.evaluate(`document.getElementsByTagName('*').length`) };
}

async function runCase(name, messages) {
  const stub = { ...baseline, messages, activeRun: null, eventCursor: baseline.eventCursor + 77, updatedAt: new Date().toISOString() };
  stubRef.current = stub;
  await page.send('Storage.clearDataForOrigin', { origin: BASE, storageTypes: 'indexeddb' }).catch(() => {});
  await page.navigate(`${BASE}/admin/`, { waitUntil: 'load' }).catch(() => {});
  await delay(1500);
  await page.navigate(`${BASE}/admin/chat?perf=1&workspaceId=default&agentId=default.audit-agent.001`, { waitUntil: 'load' });
  await delay(5500);

  const result = { case: name, variants: {} };
  result.variants.off = await measureScroll(name + ' / content-visibility off');
  await page.evaluate(`(() => {
    let s = document.getElementById('cv-patch');
    if (!s) { s = document.createElement('style'); s.id = 'cv-patch'; document.head.appendChild(s); }
    s.textContent = '[data-viewport-item-id] > * { content-visibility: auto; contain-intrinsic-size: auto 800px; }';
  })()`);
  result.variants.on = await measureScroll(name + ' / content-visibility on');
  await page.evaluate(`(() => { const s = document.getElementById('cv-patch'); if (s) s.textContent = ''; })()`);
  return result;
}

let baseline = null;
const stubRef = { current: null };

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
  baseline = await page.evaluate(`(async () => {
    const token = localStorage.getItem('pudding_token');
    const r = await fetch('/api/workspaces/default/agents/default.audit-agent.001/conversation', { headers: { Authorization: 'Bearer ' + token } });
    return await r.json();
  })()`);

  await page.send('Fetch.enable', { patterns: [{ urlPattern: '*/conversation*', requestStage: 'Request' }] });
  page.on('Fetch.requestPaused', async (p) => {
    try {
      await page.send('Fetch.fulfillRequest', {
        requestId: p.requestId,
        responseCode: 200,
        responseHeaders: [{ name: 'content-type', value: 'application/json; charset=utf-8' }],
        body: Buffer.from(JSON.stringify(stubRef.current), 'utf8').toString('base64'),
      });
    } catch {
      /* ignore */
    }
  });

  // 用例 1：20 条短消息 + 1 条 50KB（画面上只有一条高行）
  const single = [];
  for (let i = 0; i < 19; i += 1) single.push(msg(i, i % 2 === 0 ? `单条提问 ${i}` : `单条回复 ${i}`, i % 2 === 0 ? 'user' : 'agent'));
  single.push(msg(19, buildLongMarkdown(50_000), 'agent'));
  out.singleLong = await runCase('single-50KB', single);

  // 用例 2：10 条 6KB 高行（多条高行，屏外内容占比高）
  const many = [];
  for (let i = 0; i < 10; i += 1) {
    many.push(msg(i * 2, i % 2 === 0 ? `多行提问 ${i}` : `多行回复 ${i}`, i % 2 === 0 ? 'user' : 'agent'));
    many.push(msg(i * 2 + 1, buildLongMarkdown(6000), 'agent'));
  }
  out.manyTall = await runCase('10x6KB-tall-rows', many);

  writeFileSync(`${OUT}/layout-cost.json`, JSON.stringify(out, null, 2), 'utf8');
  console.log(JSON.stringify(out, null, 2));
} catch (err) {
  console.error('FAILED:', err.message);
  console.error(page.errors.slice(-6).join('\n'));
  process.exitCode = 1;
} finally {
  page.close();
}
