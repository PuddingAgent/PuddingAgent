// 诊断：滚动到顶前后列表/行状态的时间序列，定位「总高塌缩」的真实原因。
import { newPage, delay } from './lib/cdp.mjs';
import { writeFileSync, mkdirSync } from 'node:fs';
import { buildLongMarkdown } from './lib/content.mjs';

const BASE = 'http://localhost';
const OUT = 'temp/perf/out';
mkdirSync(OUT, { recursive: true });

const page = await newPage('about:blank');
const timeline = [];

async function probe(label) {
  const state = await page.evaluate(`(() => {
    const list = document.querySelector('[data-testid="chat-message-list"]');
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    const items = Array.from(document.querySelectorAll('[data-viewport-item-id]'));
    const longHost = items.find(n => (n.textContent || '').includes('性能诊断长回复样本'));
    return {
      listScrollTop: list?.scrollTop ?? null,
      listScrollHeight: list?.scrollHeight ?? null,
      listClientHeight: list?.clientHeight ?? null,
      virtualized: content?.getAttribute('data-virtualized') ?? null,
      contentHeight: content ? Math.round(content.getBoundingClientRect().height) : null,
      contentStyleHeight: content?.style?.height ?? null,
      rows: items.length,
      rowTextLens: items.map(n => (n.textContent || '').length).sort((a, b) => b - a).slice(0, 4),
      longFound: !!longHost,
      longTextLen: longHost ? (longHost.textContent || '').length : 0,
      longH: longHost ? Math.round(longHost.getBoundingClientRect().height) : 0,
      bodyTextLen: (document.body.innerText || '').length,
      domNodes: document.getElementsByTagName('*').length,
    };
  })()`);
  timeline.push({ label, at: Date.now(), ...state });
  console.log(label, JSON.stringify(state));
  return state;
}

try {
  // 登录
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

  const baseline = (await page.evaluate(`(async () => {
    const token = localStorage.getItem('pudding_token');
    const r = await fetch('/api/workspaces/default/agents/default.audit-agent.001/conversation', { headers: { Authorization: 'Bearer ' + token } });
    return await r.json();
  })()`));

  // 合成 20 条 + 1 条 50KB 长回复
  const longText = buildLongMarkdown(50_000);
  const messages = [];
  for (let i = 0; i < 19; i += 1) {
    const isUser = i % 2 === 0;
    const isLong = i === 18;
    messages.push({
      messageId: `diag-msg-${i}`,
      turnId: `diag-turn-${i}`,
      runId: null,
      role: isUser ? 'user' : 'agent',
      sourceKind: isUser ? 'user' : 'agent',
      sourceId: isUser ? 'admin' : 'default.audit-agent.001',
      sourceName: isUser ? 'admin' : '审批审计员',
      messageType: isUser ? 'user_message' : 'agent_output',
      llmRole: isUser ? 'user' : 'agent',
      createdAt: new Date(Date.parse('2026-10-01T02:00:00Z') + i * 60000).toISOString(),
      content: isLong ? longText : (isUser ? `诊断提问 ${i}` : `诊断回复 ${i}`),
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

  await probe('after-mount');
  await delay(1000);
  await probe('after-mount+1s');

  // 滚到列表底部
  await page.evaluate(`(() => {
    const el = document.querySelector('[data-testid="chat-message-list"]');
    el.scrollTop = el.scrollHeight;
    return el.scrollTop;
  })()`);
  await delay(1500);
  await probe('at-bottom');

  // 滚到顶部（触发历史 prepend / 锚点恢复）
  await page.evaluate(`(() => {
    const el = document.querySelector('[data-testid="chat-message-list"]');
    el.scrollTop = 0;
    return el.scrollTop;
  })()`);
  for (let i = 0; i < 8; i += 1) {
    await delay(600);
    await probe(`after-top+${(i + 1) * 600}ms`);
  }

  // 再滚回底部：判断长行是否可恢复，以及用户是否被卡住
  await page.evaluate(`(() => {
    const el = document.querySelector('[data-testid="chat-message-list"]');
    el.scrollTop = el.scrollHeight;
    return { scrollTop: el.scrollTop, scrollHeight: el.scrollHeight };
  })()`);
  for (let i = 0; i < 5; i += 1) {
    await delay(600);
    await probe(`after-back-to-bottom+${(i + 1) * 600}ms`);
  }

  // 对照：同样结构但长回复换成 2000 字符，观察是否仍塌缩
  console.log('\n--- control: short replacement (2k chars) ---');
  const shortMessages = messages.map((m) =>
    m.content.length > 5000 ? { ...m, content: m.content.slice(0, 2000) } : m,
  );
  stub.messages = shortMessages;
  await page.send('Storage.clearDataForOrigin', { origin: BASE, storageTypes: 'indexeddb' }).catch(() => {});
  await page.navigate(`${BASE}/admin/chat?perf=1&workspaceId=default&agentId=default.audit-agent.001`, { waitUntil: 'load' });
  await delay(5000);
  await probe('control-after-mount');
  await page.evaluate(`(() => {
    const el = document.querySelector('[data-testid="chat-message-list"]');
    el.scrollTop = 0;
  })()`);
  await delay(1500);
  await probe('control-after-top');
  await page.evaluate(`(() => {
    const el = document.querySelector('[data-testid="chat-message-list"]');
    el.scrollTop = el.scrollHeight;
  })()`);
  await delay(1500);
  await probe('control-after-bottom');

  writeFileSync(`${OUT}/scroll-collapse-timeline.json`, JSON.stringify(timeline, null, 2), 'utf8');
  console.log('wrote scroll-collapse-timeline.json');
} catch (err) {
  console.error('FAILED:', err.message);
  console.error(page.errors.slice(-8).join('\n'));
  process.exitCode = 1;
} finally {
  page.close();
}
