// Chat 前端性能实测录制器
//
// 设计约束：
//   * 只替换「数据源」——conversation 响应（活动回合为 1200ms 轮询）；渲染/投影/缓存/布局全走真实代码
//   * 不写数据库、不调用 LLM、不改运行中的 Desktop
//   * 每条消息带 textContent 长度、行高、子节点数与 longtask 统计，用于量化「长消息内部渲染成本」
import { newPage, waitFor, delay } from './lib/cdp.mjs';
import { buildLongMarkdown } from './lib/content.mjs';
import { writeFileSync, mkdirSync } from 'node:fs';
export { buildLongMarkdown };

const BASE = process.env.PUDDING_BASE || 'http://localhost';
const PORT = process.env.CDP_PORT || '9333';
// 输出固定在仓库 temp/ 下（Agents-Hygiene.md：测试输出 → temp/test-out，临时产物 → temp/）
const OUT = process.env.PERF_OUT || 'temp/perf/out';
mkdirSync(OUT, { recursive: true });

const WS = 'default';
const OWNER = 'Administrator';

// ────────────────────────── 合成内容生成（见 lib/content.mjs）──────────────────────────


function makeMessage({
  id,
  role,
  content,
  createdAtMs,
  sourceName,
  status = 'succeeded',
  processItems = [],
  processSummary = null,
  turnId,
  runId,
}) {
  return {
    messageId: id,
    turnId: turnId ?? `turn-${id}`,
    runId: runId ?? null,
    role,
    sourceKind: role === 'user' ? 'user' : 'agent',
    sourceId: role === 'user' ? 'admin' : 'default.audit-agent.001',
    sourceName: role === 'user' ? 'admin' : sourceName ?? '审批审计员',
    messageType: role === 'user' ? 'user_message' : 'agent_output',
    llmRole: role === 'user' ? 'user' : 'agent',
    createdAt: new Date(createdAtMs).toISOString(),
    content,
    status,
    processItems,
    processSummary,
    metadata: null,
    contentParts: null,
    turnOutcome: { status: 'succeeded', errorCode: null, errorMessage: null },
  };
}

/** 场景 B/C：20 条历史，其中一条 50KB 长回复。 */
function buildConversationLongMessage(baseline, longChars, shortCount = 19) {
  const messages = [];
  const base = Date.parse('2026-10-01T02:00:00.000Z');
  // 长消息放在倒数第二条，使其进入视口需要滚动，但处于预取范围
  const longIndex = shortCount - 1;
  for (let i = 0; i < shortCount; i += 1) {
    const isLong = i === longIndex;
    const isUser = i % 2 === 0;
    const content = isLong
      ? buildLongMarkdown(longChars)
      : isUser
        ? `第 ${i + 1} 条短提问：请继续分析第 ${i + 1} 个模块的性能特征。`
        : `第 ${i + 1} 条短回复：该模块的结论已记录，详见上一节。`;
    messages.push(
      makeMessage({
        id: `synth-msg-${i}`,
        role: isUser ? 'user' : 'agent',
        content,
        createdAtMs: base + i * 60_000,
      }),
    );
  }
  // 最后再放一条短回复，模拟长回复之后仍有新内容
  messages.push(
    makeMessage({
      id: 'synth-msg-tail',
      role: 'agent',
      content: '补充结论：长回复之后的收尾段，用于验证贴底行为。',
      createdAtMs: base + shortCount * 60_000,
    }),
  );
  return {
    ...baseline,
    messages,
    activeRun: null,
    eventCursor: (baseline.eventCursor ?? 0) + 1000,
    updatedAt: new Date().toISOString(),
  };
}

/** 场景 D：200 条历史，用于上翻分页。 */
function buildConversationManyMessages(baseline, count = 200) {
  const messages = [];
  const base = Date.parse('2026-09-01T00:00:00.000Z');
  for (let i = 0; i < count; i += 1) {
    const isUser = i % 2 === 0;
    messages.push(
      makeMessage({
        id: `hist-msg-${i}`,
        role: isUser ? 'user' : 'agent',
        content: isUser
          ? `历史提问 ${i}：检查模块 ${i} 的边界条件。`
          : `历史回答 ${i}：模块 ${i} 的边界条件已确认，包含 \`case${i}\` 分支。\n\n- 结论 A\n- 结论 B\n`,
        createdAtMs: base + i * 30_000,
      }),
    );
  }
  return {
    ...baseline,
    messages,
    activeRun: null,
    eventCursor: (baseline.eventCursor ?? 0) + 5000,
    updatedAt: new Date().toISOString(),
  };
}

/** 场景 E：流式事件序列（canonical 信封 + 增量分页）。 */
function buildStreamEvents(
  baseline,
  {
    totalChars = 30_000,
    steps = 60,
    messageId = 'synth-stream-msg',
    turnId = 'synth-turn-stream',
    runId = 'synth-run-stream',
    userMessageId = 'synth-user-msg',
    sessionId,
    startSequence = 900001,
  } = {},
) {
  const sid = sessionId ?? baseline.mainSessionId;
  const text = buildLongMarkdown(totalChars);
  const chunkSize = Math.ceil(text.length / steps);
  const now = Date.now();
  const events = [];
  const deltas = [];

  events.push({
    eventId: 'synth-evt-start',
    sequence: startSequence,
    occurredAt: new Date(now).toISOString(),
    runId,
    turnId,
    type: 'turn.started',
    sessionId: sid,
    payload: { messageId, userMessageId },
  });

  for (let i = 1; i <= steps; i += 1) {
    const delta = text.slice((i - 1) * chunkSize, Math.min(text.length, i * chunkSize));
    if (!delta) continue;
    const seq = startSequence + i;
    events.push({
      eventId: `synth-evt-delta-${i}`,
      sequence: seq,
      occurredAt: new Date(now + i * 100).toISOString(),
      runId,
      turnId,
      type: 'message.content.appended',
      sessionId: sid,
      payload: { messageId, delta },
    });
    // 分页接口每次只返回一条增量，模拟「事件流按真实节奏到达」
    deltas.push({
      eventId: `synth-evt-delta-${i}`,
      sequence: seq,
      occurredAt: new Date(now + i * 100).toISOString(),
      runId,
      turnId,
      type: 'message.content.appended',
      sessionId: sid,
      messageId,
      delta,
      payload: { messageId, delta },
    });
  }

  events.push({
    eventId: 'synth-evt-complete',
    sequence: startSequence + steps + 1,
    occurredAt: new Date(now + (steps + 1) * 100).toISOString(),
    runId,
    turnId,
    type: 'turn.completed',
    sessionId: sid,
    payload: { messageId, status: 'succeeded' },
  });

  return { events, deltas };
}

// ────────────────────────── 录制基础能力 ──────────────────────────

function sseFrame(event) {
  return `id: ${event.sequence}\nevent: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`;
}

async function login(page) {
  await page.navigate(`${BASE}/admin/user/login`, { waitUntil: 'load' });
  await delay(1000);
  return page.evaluate(`(async () => {
    const res = await fetch('/api/login/account', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'admin', password: 'Admin@123', type: 'account', autoLogin: true }),
    });
    const json = await res.json();
    if (json?.token) localStorage.setItem('pudding_token', json.token);
    return { status: res.status, hasToken: !!json?.token };
  })()`);
}

async function fetchBaseline(page, agentId) {
  return page.evaluate(`(async () => {
    const token = localStorage.getItem('pudding_token');
    const r = await fetch('/api/workspaces/${WS}/agents/${agentId}/conversation', { headers: { Authorization: 'Bearer ' + token } });
    return { status: r.status, json: await r.json() };
  })()`);
}

/**
 * 安装 Fetch 拦截：替换 conversation GET 的响应体，并可把 events/stream 变成受控 SSE。
 */
async function installStubs(page, { conversationStub, conversationVariant, streamEvents, bootstrapStub, logUrls = false }) {
  await page.send('Fetch.enable', {
    patterns: [
      { urlPattern: '*conversation*', requestStage: 'Request' },
      { urlPattern: '*events*', requestStage: 'Request' },
      { urlPattern: '*bootstrap*', requestStage: 'Request' },
      { urlPattern: '*sessions*', requestStage: 'Request' },
    ],
  });

  const stats = {
    conversationFulfilled: 0,
    conversationPassed: 0,
    streamFulfilled: 0,
    streamPassed: 0,
    eventsFulfilled: 0,
    bootstrapFulfilled: 0,
    paused: {},
    conversationContentLens: [],
  };

  page.on('Fetch.requestPaused', async (p) => {
    const { requestId, request } = p;
    const url = request.url;
    if (logUrls) {
      const key = url.replace(/^https?:\/\/[^/]+/, '').split('?')[0];
      stats.paused[key] = (stats.paused[key] ?? 0) + 1;
    }
    try {
      if (url.includes('/events/stream')) {
        if (!streamEvents) {
          stats.streamPassed += 1;
          await page.send('Fetch.continueRequest', { requestId });
          return;
        }
        stats.streamFulfilled += 1;
        const body = streamEvents.events.map(sseFrame).join('');
        await page.send('Fetch.fulfillRequest', {
          requestId,
          responseCode: 200,
          responseHeaders: [
            { name: 'content-type', value: 'text/event-stream; charset=utf-8' },
            { name: 'cache-control', value: 'no-store' },
          ],
          body: Buffer.from(body, 'utf8').toString('base64'),
        });
        return;
      }
      // conversation（可变体：模拟活动回合每次轮询拿到更长的正文）
      if (/\/conversation(\?|$)/.test(url)) {
        let payload = conversationStub;
        if (conversationVariant) {
          stats.conversationFulfilled += 1;
          payload = conversationVariant(stats.conversationFulfilled);
          const lens = payload?.messages?.map((m) => m.content.length) ?? [];
          stats.conversationContentLens.push(Math.max(0, ...lens));
        } else if (conversationStub) {
          stats.conversationFulfilled += 1;
        } else {
          stats.conversationPassed += 1;
          await page.send('Fetch.continueRequest', { requestId });
          return;
        }
        await page.send('Fetch.fulfillRequest', {
          requestId,
          responseCode: 200,
          responseHeaders: [{ name: 'content-type', value: 'application/json; charset=utf-8' }],
          body: Buffer.from(JSON.stringify(payload), 'utf8').toString('base64'),
        });
        return;
      }
      // bootstrap（合成会话的权威快照）
      if (bootstrapStub && /\/bootstrap(\?|$)/.test(url)) {
        stats.bootstrapFulfilled += 1;
        await page.send('Fetch.fulfillRequest', {
          requestId,
          responseCode: 200,
          responseHeaders: [{ name: 'content-type', value: 'application/json; charset=utf-8' }],
          body: Buffer.from(JSON.stringify(bootstrapStub), 'utf8').toString('base64'),
        });
        return;
      }
      // 事件分页（合成会话的模拟以真实轮询节奏取回增量）
      if (streamEvents && /\/sessions\/[^/]+\/events(\?|$)/.test(url)) {
        stats.eventsFulfilled += 1;
        const nextIndex = stats.__cursor ?? 0;
        const deltas = streamEvents.deltas ?? [];
        const body =
          nextIndex < deltas.length
            ? (stats.__cursor = nextIndex + 1, { events: [deltas[nextIndex]], hasMore: false, has_more: false })
            : { events: [], hasMore: false, has_more: false };
        await page.send('Fetch.fulfillRequest', {
          requestId,
          responseCode: 200,
          responseHeaders: [{ name: 'content-type', value: 'application/json; charset=utf-8' }],
          body: Buffer.from(JSON.stringify(body), 'utf8').toString('base64'),
        });
        return;
      }
      stats.conversationPassed += 1;
      await page.send('Fetch.continueRequest', { requestId });
    } catch (err) {
      stats.lastError = err.message;
    }
  });

  return stats;
}

async function openChat(page, { agentId, query = 'perf=1' }) {
  const url = `${BASE}/admin/chat?${query}&workspaceId=${WS}&agentId=${agentId}`;
  await page.navigate(url, { waitUntil: 'load' });
  await waitFor(page, `!!document.querySelector('[data-testid="chat-message-list"]')`, {
    timeoutMs: 25000,
    label: 'chat-message-list',
  }).catch(() => null);
}

/** 轮询直到长回复真正进入 DOM（或超时），返回最终快照。 */
async function waitForLongRow(page, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < deadline) {
    last = await snapshot(page);
    if (last.longRowFound) return last;
    await delay(500);
  }
  return last;
}

/** 在页面里做一次「真实滚动」序列，并返回滚动前后位置。 */
async function scrollThrough(page, { passes = 1, durationMs = 1200 } = {}) {
  return page.evaluate(`(async () => {
    const el = document.querySelector('[data-testid="chat-message-list"]');
    if (!el) return { found: false };
    const total = Math.max(0, el.scrollHeight - el.clientHeight);
    const t0 = performance.now();
    const startTop = el.scrollTop;
    const frame = () => new Promise(r => requestAnimationFrame(r));
    for (let pass = 0; pass < ${passes}; pass += 1) {
      const down = pass % 2 === 0;
      const steps = 60;
      for (let i = 0; i <= steps; i += 1) {
        const f = down ? i / steps : 1 - i / steps;
        el.scrollTop = f * total;
        await frame();
      }
    }
    return {
      found: true,
      startTop,
      endTop: el.scrollTop,
      total,
      elapsed: Math.round(performance.now() - t0),
      scrollHeight: el.scrollHeight,
    };
  })()`);
}

async function snapshot(page) {
  return page.evaluate(`(() => {
    const api = window.__PUDDING_PERF__;
    const list = document.querySelector('[data-testid="chat-message-list"]');
    const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
    const items = Array.from(document.querySelectorAll('[data-viewport-item-id]'));
    const longHost = items.find(n => (n.textContent || '').includes('性能诊断长回复样本'));
    const sizes = items.map(n => (n.textContent || '').length).sort((a, b) => b - a);
    const contentCs = content ? getComputedStyle(content) : null;
    return {
      available: !!api,
      summary: api ? api.snapshot({ workspaceId: '${WS}' }) : null,
      domNodes: document.getElementsByTagName('*').length,
      bodyTextLen: (document.body.innerText || '').length,
      listFound: !!list,
      listScrollHeight: list?.scrollHeight ?? 0,
      listClientHeight: list?.clientHeight ?? 0,
      listScrollTop: list?.scrollTop ?? 0,
      virtualized: content?.getAttribute('data-virtualized') ?? null,
      contentStyleHeight: content?.style?.height ?? null,
      contentComputedHeight: contentCs?.height ?? null,
      contentFlexShrink: contentCs?.flexShrink ?? null,
      contentOffsetHeight: content?.offsetHeight ?? 0,
      contentHeight: Math.round(content?.getBoundingClientRect?.().height ?? 0),
      rowCount: items.length,
      rowSizesTop: sizes.slice(0, 5),
      longRowFound: !!longHost,
      longRowTextLen: longHost ? (longHost.textContent || '').length : 0,
      longRowHeight: longHost ? Math.round(longHost.getBoundingClientRect().height) : 0,
      longRowChildNodes: longHost ? longHost.getElementsByTagName('*').length : 0,
      longRowCanvas: longHost ? longHost.getElementsByTagName('canvas').length : 0,
      longRowTables: longHost ? longHost.getElementsByTagName('table').length : 0,
      longRowPre: longHost ? longHost.getElementsByTagName('pre').length : 0,
    };
  })()`);
}

function summarize(result) {
  const s = result.summary?.summary ?? result.summary ?? {};
  return {
    totalEvents: s.totalEvents,
    windowMs: s.windowMs,
    counts: s.counts,
    output: s.output,
    react: s.react,
    browser: s.browser,
    workflow: s.workflow,
    domNodes: result.domNodes,
    bodyTextLen: result.bodyTextLen,
    listScrollHeight: result.listScrollHeight,
    listClientHeight: result.listClientHeight,
    listScrollTop: result.listScrollTop,
    virtualized: result.virtualized,
    contentStyleHeight: result.contentStyleHeight,
    contentComputedHeight: result.contentComputedHeight,
    contentFlexShrink: result.contentFlexShrink,
    contentOffsetHeight: result.contentOffsetHeight,
    contentHeight: result.contentHeight,
    rowCount: result.rowCount,
    rowSizesTop: result.rowSizesTop,
    longRowFound: result.longRowFound,
    longRowTextLen: result.longRowTextLen,
    longRowHeight: result.longRowHeight,
    longRowChildNodes: result.longRowChildNodes,
    longRowTables: result.longRowTables,
    longRowPre: result.longRowPre,
  };
}

/** 取某个 perf 事件名的 payload 序列（用于 p50/p95）。 */
function eventsOf(snapshotResult, name) {
  const raw = snapshotResult?.summary?.raw?.perfEvents ?? [];
  return raw.filter((e) => e.name === name);
}

function percentile(values, p) {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const idx = Math.min(sorted.length - 1, Math.max(0, Math.ceil((p / 100) * sorted.length) - 1));
  return sorted[idx];
}

function numberStats(values) {
  const nums = values.filter((v) => typeof v === 'number' && Number.isFinite(v));
  if (!nums.length) return null;
  return {
    count: nums.length,
    p50: percentile(nums, 50),
    p95: percentile(nums, 95),
    max: Math.max(...nums),
    sum: nums.reduce((a, b) => a + b, 0),
  };
}

/** 从一次快照里抽出报告需要的分项指标。 */
function extractMetrics(snapshotResult) {
  const paints = eventsOf(snapshotResult, 'chat.output.paint');
  const commits = eventsOf(snapshotResult, 'chat.output.commit');
  const mdRenders = eventsOf(snapshotResult, 'chat.markdown.render');
  const applies = eventsOf(snapshotResult, 'chat.event.apply');
  const longTasks = eventsOf(snapshotResult, 'browser.longtask');
  const steps = eventsOf(snapshotResult, 'chat.workflow.step');
  const typewriterInputs = eventsOf(snapshotResult, 'chat.typewriter.input');
  const typewriterTicks = eventsOf(snapshotResult, 'chat.typewriter.tick');
  const queueSnapshots = eventsOf(snapshotResult, 'chat.queue.snapshot');
  const resourceLoads = eventsOf(snapshotResult, 'browser.resource');
  const layoutShifts = eventsOf(snapshotResult, 'browser.layoutShift');
  return {
    paint: {
      renderToPaintMs: numberStats(paints.map((e) => e.payload?.renderToPaintMs)),
      commitToPaintMs: numberStats(paints.map((e) => e.payload?.commitToPaintMs)),
      renderToCommitMs: numberStats(commits.map((e) => e.payload?.renderToCommitMs)),
      domTextCharsMax: Math.max(0, ...paints.map((e) => e.payload?.domTextChars ?? 0)),
      scrollHeightMax: Math.max(0, ...paints.map((e) => e.payload?.scrollHeight ?? 0)),
    },
    markdown: {
      commitMs: numberStats(mdRenders.map((e) => e.payload?.commitMs)),
      charsMax: Math.max(0, ...mdRenders.map((e) => e.payload?.chars ?? 0)),
      processedCharsMax: Math.max(0, ...mdRenders.map((e) => e.payload?.processedChars ?? 0)),
      preprocessMs: numberStats(mdRenders.map((e) => e.payload?.preprocessMs)),
    },
    eventApply: numberStats(applies.map((e) => e.payload?.applyMs)),
    typewriter: {
      inputs: typewriterInputs.length,
      ticks: typewriterTicks.length,
      incomingDeltaMax: Math.max(0, ...typewriterInputs.map((e) => e.payload?.incomingDelta ?? 0)),
    },
    queue: { snapshots: queueSnapshots.length },
    layoutShift: { count: layoutShifts.length, cls: Number(layoutShifts.reduce((s, e) => s + (e.payload?.value ?? 0), 0).toFixed(4)) },
    resources: { count: resourceLoads.length, totalBytes: resourceLoads.reduce((s, e) => s + (e.payload?.decodedBodySize ?? e.payload?.transferSize ?? 0), 0) },
    longTask: numberStats(longTasks.map((e) => e.payload?.durationMs)),
    workflow: steps.map((e) => ({
      at: e.at,
      workflow: e.payload?.workflow,
      step: e.payload?.step,
      status: e.payload?.status,
      durationMs: e.payload?.durationMs,
      messageCount: e.payload?.messageCount ?? null,
      cacheHit: e.payload?.cacheHit ?? null,
      eventCursor: e.payload?.eventCursor ?? null,
    })),
    dom: {
      nodes: snapshotResult.domNodes,
      rowCount: snapshotResult.rowCount,
      longRowChildNodes: snapshotResult.longRowChildNodes,
      longRowHeight: snapshotResult.longRowHeight,
      longRowTextLen: snapshotResult.longRowTextLen,
      bodyTextLen: snapshotResult.bodyTextLen,
      listScrollHeight: snapshotResult.listScrollHeight,
    },
  };
}

// ────────────────────────── 场景 ──────────────────────────
//
// 每个场景自成闭环：自己清缓存、自己装 stub、自己导航。
// 不做场景间状态串联（串联会污染后续场景的 stub 内容，导致实测失真）。

/** 场景通用入口：清 IndexedDB、装 stub、进入 chat。 */
async function enterScenario(page, ctx, { stub, variant = null, clearCache = true } = {}) {
  if (clearCache) {
    await page
      .send('Storage.clearDataForOrigin', { origin: BASE, storageTypes: 'indexeddb' })
      .catch(() => {});
  }
  ctx.stats = await installStubs(page, {
    conversationStub: stub,
    conversationVariant: variant,
    logUrls: true,
  });
  // 先落到应用根，确保令牌与工作台可用，再带 stub 进入 chat
  await page.navigate(`${BASE}/admin/`, { waitUntil: 'load' }).catch(() => {});
  await delay(2000);
  await page.evaluate(`window.__PUDDING_PERF__ && window.__PUDDING_PERF__.clear()`).catch(() => {});
  await openChat(page, { agentId: ctx.agentId });
}

const scenarios = {
  /** A：冷加载（清空 IndexedDB + 禁用 HTTP 缓存），真实数据 */
  async cold(page, ctx) {
    await page.send('Network.setCacheDisabled', { cacheDisabled: true });
    await page
      .send('Storage.clearDataForOrigin', { origin: BASE, storageTypes: 'indexeddb' })
      .catch(() => {});
    await page.navigate(`${BASE}/admin/`, { waitUntil: 'load' }).catch(() => {});
    await delay(1500);
    await page.evaluate(`window.__PUDDING_PERF__ && window.__PUDDING_PERF__.clear()`).catch(() => {});
    await openChat(page, { agentId: ctx.agentId });
    await delay(7000);
    const snap = await snapshot(page);
    return { metrics: extractMetrics(snap), summary: summarize(snap) };
  },

  /** B：长消息（50KB）进入 DOM 的真实首渲染成本 */
  async warmLong(page, ctx) {
    await page.send('Network.setCacheDisabled', { cacheDisabled: false });
    const stub = buildConversationLongMessage(ctx.baseline, ctx.longChars ?? 50_000);
    await enterScenario(page, ctx, { stub });
    const snap = await waitForLongRow(page, 30000);
    return { metrics: extractMetrics(snap), summary: summarize(snap) };
  },

  /** C：长消息在时间线内时，滚动整条时间线的成本 */
  async scrollLong(page, ctx) {
    const stub = buildConversationLongMessage(ctx.baseline, ctx.longChars ?? 50_000);
    await enterScenario(page, ctx, { stub });
    await waitForLongRow(page, 30000);
    const before = await snapshot(page);
    await page.evaluate(`window.__PUDDING_PERF__ && window.__PUDDING_PERF__.clear()`);
    const scrolled = await scrollThrough(page, { passes: 2 });
    await delay(1500);
    const after = await snapshot(page);
    return {
      scrolled,
      before: summarize(before),
      metrics: extractMetrics(after),
      summary: summarize(after),
    };
  },

  /** D：40 条历史 + 上翻到顶（prepend 与锚点行为） */
  async historyPage(page, ctx) {
    const stub = buildConversationManyMessages(ctx.baseline, Number(process.env.PERF_HISTORY_COUNT || 40));
    await enterScenario(page, ctx, { stub });
    await delay(5000);
    const loaded = await snapshot(page);
    await page.evaluate(`window.__PUDDING_PERF__ && window.__PUDDING_PERF__.clear()`);
    const prepend = await page.evaluate(`(async () => {
      const el = document.querySelector('[data-testid="chat-message-list"]');
      if (!el) return { found: false };
      const beforeTop = el.scrollTop;
      const beforeHeight = el.scrollHeight;
      const t0 = performance.now();
      el.scrollTop = 0;
      await new Promise(r => setTimeout(r, 3000));
      return {
        found: true,
        elapsed: Math.round(performance.now() - t0),
        beforeTop, beforeHeight,
        afterTop: el.scrollTop,
        afterHeight: el.scrollHeight,
        anchorShiftDelta: el.scrollTop - beforeTop,
      };
    })()`);
    const after = await snapshot(page);
    return {
      loaded: summarize(loaded),
      prepend,
      metrics: extractMetrics(after),
      summary: summarize(after),
    };
  },

  /**
   * E：流式输出。活动回合在本构建走 1200ms 轮询 conversation，
   * 每次返回更长正文，让「拉取 → 合并 → 投影 → 权重 → React 提交 → 缓存写」
   * 全链路反复执行；同时在注入期间向输入框打字（流式与输入并发）。
   */
  async streaming(page, ctx) {
    const streamMessageId = 'synth-stream-msg';
    const runId = 'synth-run-stream';
    const turnId = 'synth-turn-stream';
    const userMsg = makeMessage({
      id: 'synth-user-msg',
      role: 'user',
      content: '请生成一份长性能分析报告。',
      createdAtMs: Date.now() - 1000,
    });
    const streamingMsg = makeMessage({
      id: streamMessageId,
      role: 'agent',
      content: '',
      createdAtMs: Date.now(),
      status: 'streaming',
      turnId,
      runId,
    });
    const baseMessages = ctx.baseline.messages.slice(-6);
    const nowIso = new Date().toISOString();
    const stub = {
      ...ctx.baseline,
      messages: [...baseMessages, userMsg, streamingMsg],
      activeRun: {
        runId,
        workspaceId: WS,
        ownerUserId: OWNER,
        agentId: ctx.agentId,
        mainSessionId: ctx.baseline.mainSessionId,
        commandClientId: null,
        status: 'running',
        statusText: '生成中',
        summary: '合成流式回合',
        eventCursor: ctx.baseline.eventCursor,
        outputSnapshot: { markdown: '', processItems: [], processSummary: null, window: null },
        startedAt: nowIso,
        updatedAt: nowIso,
        completedAt: null,
      },
      eventCursor: ctx.baseline.eventCursor,
      updatedAt: nowIso,
    };
    const fullText = buildLongMarkdown(ctx.streamChars ?? 30_000);
    const stepChars = Math.max(400, Math.round(fullText.length / (ctx.streamSteps ?? 40)));
    const variant = (pollIndex) => {
      const upto = Math.min(fullText.length, pollIndex * stepChars);
      return {
        ...stub,
        messages: [
          ...baseMessages,
          userMsg,
          { ...streamingMsg, content: fullText.slice(0, upto), status: upto >= fullText.length ? 'succeeded' : 'streaming' },
        ],
        eventCursor: (ctx.baseline.eventCursor ?? 0) + pollIndex,
        updatedAt: new Date().toISOString(),
      };
    };
    await enterScenario(page, ctx, { stub: null, variant });
    await delay(6000);
    // 「流式输出同时输入」：在增量持续到达期间向输入框注入文本
    const typing = await page
      .evaluate(`(async () => {
        const el = document.querySelector('[data-testid="chat-input"]');
        if (!el) return { found: false };
        const proto = el.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
        const t0 = performance.now();
        let chars = 0;
        for (let i = 0; i < 60; i += 1) {
          const chunk = '正在输入的草稿文本 ' + i + '。';
          if (setter) setter.call(el, (el.value || '') + chunk); else el.value += chunk;
          el.dispatchEvent(new Event('input', { bubbles: true }));
          chars += chunk.length;
          await new Promise(r => requestAnimationFrame(r));
        }
        return { found: true, chars, elapsed: Math.round(performance.now() - t0) };
      })()`)
      .catch((e) => ({ error: e.message }));
    await delay(12000);
    const snap = await snapshot(page);
    return {
      metrics: extractMetrics(snap),
      summary: summarize(snap),
      typing,
      stubStats: ctx.stats,
    };
  },

  /**
   * F：A/B —— 两组补丁对「50KB 长回复时间线」的影响。
   * 测量的是滚动 + 回顶 + 程序化贴底后，长行是否仍可到达（可用性），
   * 以及该过程的渲染/长任务成本。
   */
  async abPatches(page, ctx) {
    const stub = buildConversationLongMessage(ctx.baseline, ctx.longChars ?? 50_000);
    await enterScenario(page, ctx, { stub });
    await waitForLongRow(page, 30000);

    const applyPatch = (kind, on) =>
      page.evaluate(`(() => {
        const id = 'perf-patch-' + ${JSON.stringify(kind)};
        let style = document.getElementById(id);
        if (!style) { style = document.createElement('style'); style.id = id; document.head.appendChild(style); }
        const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
        if (${JSON.stringify(kind)} === 'flexShrink') {
          if (content) content.style.flexShrink = ${JSON.stringify(on)} ? '0' : '';
          return content ? getComputedStyle(content).flexShrink : null;
        }
        style.textContent = ${JSON.stringify(on)}
          ? '[data-viewport-item-id] > * { content-visibility: auto; contain-intrinsic-size: auto 800px; }'
          : '';
        return style.textContent.length;
      })()`);

    const probe = () =>
      page.evaluate(`(async () => {
        const el = document.querySelector('[data-testid="chat-message-list"]');
        const content = document.querySelector('[data-testid="chat-message-viewport-content"]');
        const longRow = () => Array.from(document.querySelectorAll('[data-viewport-item-id]'))
          .find(n => (n.textContent || '').includes('性能诊断长回复样本'));
        const hasLong = () => !!longRow();
        const frame = () => new Promise(r => requestAnimationFrame(r));
        // 先确保长行在视口内
        el.scrollTop = el.scrollHeight;
        for (let i = 0; i < 20 && !hasLong(); i += 1) { el.scrollTop = el.scrollHeight; await frame(); }
        const mountedLong = hasLong();
        const mountedScrollHeight = el.scrollHeight;
        // 滚到顶（触发长行卸载）
        for (let i = 0; i <= 60; i += 1) { el.scrollTop = (mountedScrollHeight - el.clientHeight) * (1 - i / 60); await frame(); }
        await new Promise(r => setTimeout(r, 1200));
        const afterTopScrollHeight = el.scrollHeight;
        // 程序化贴底：能否回到长行
        el.scrollTop = el.scrollHeight;
        await new Promise(r => setTimeout(r, 1200));
        const recoveredTop = el.scrollTop;
        for (let i = 0; i < 20 && !hasLong(); i += 1) { el.scrollTop = el.scrollHeight; await frame(); }
        return {
          mountedLong,
          mountedScrollHeight,
          afterTopScrollHeight,
          recoveredTop,
          recoveredLong: hasLong(),
          maxScrollTop: el.scrollHeight - el.clientHeight,
          contentComputedHeight: content ? getComputedStyle(content).height : null,
          contentFlexShrink: content ? getComputedStyle(content).flexShrink : null,
        };
      })()`);

    const measure = async (label) => {
      await page.evaluate(`window.__PUDDING_PERF__ && window.__PUDDING_PERF__.clear()`);
      const availability = await probe();
      await delay(1200);
      const snap = await snapshot(page);
      return { label, availability, metrics: extractMetrics(snap), summary: summarize(snap) };
    };

    // 基线：无补丁
    const baseline = await measure('no patch');
    // 补丁 1：内容容器 flex-shrink:0
    await applyPatch('flexShrink', true);
    const withFlexShrink = await measure('flex-shrink: 0');
    await applyPatch('flexShrink', false);
    // 补丁 2：content-visibility（对冻结块减少屏外布局）
    await applyPatch('contentVisibility', true);
    const withContentVisibility = await measure('content-visibility: auto');
    await applyPatch('contentVisibility', false);
    return { baseline, withFlexShrink, withContentVisibility };
  },
};

// ────────────────────────── 主流程 ──────────────────────────

const which = (process.argv[2] || 'all').split(',').map((s) => s.trim()).filter(Boolean);

const page = await newPage('about:blank');
const results = { startedAt: new Date().toISOString(), base: BASE, scenarios: {} };
try {
  const loginResult = await login(page);
  results.login = loginResult;

  const agentId = process.env.PERF_AGENT || 'default.audit-agent.001';
  const baselineResp = await fetchBaseline(page, agentId);
  if (!baselineResp.json?.messages) throw new Error(`baseline fetch failed: ${JSON.stringify(baselineResp).slice(0, 300)}`);
  const baseline = baselineResp.json;
  writeFileSync(`${OUT}/conversation-baseline.json`, JSON.stringify(baseline, null, 2), 'utf8');

  const ctx = {
    agentId,
    baseline,
    longChars: Number(process.env.PERF_LONG_CHARS || 50_000),
    streamChars: Number(process.env.PERF_STREAM_CHARS || 30_000),
    streamSteps: Number(process.env.PERF_STREAM_STEPS || 60),
  };
  results.agentId = agentId;
  results.messageCount = baseline.messages.length;

  const order = ['cold', 'warmLong', 'scrollLong', 'abPatches', 'historyPage', 'streaming'];
  for (const name of order) {
    if (!(which.includes('all') || which.includes(name))) continue;
    const started = Date.now();
    process.stderr.write(`\n[scenario] ${name} ...\n`);
    try {
      const value = await scenarios[name](page, ctx);
      results.scenarios[name] = { ok: true, ms: Date.now() - started, value, stubStats: ctx.stats };
      // 原始事件单独落盘：分析阶段算分位数与时间线，不塞进结果 JSON
      const rawEvents = value?.summary?.raw?.perfEvents ?? value?.summary?.summary?.raw?.perfEvents;
      if (Array.isArray(rawEvents) && rawEvents.length) {
        writeFileSync(`${OUT}/events-${name}.json`, JSON.stringify(rawEvents, null, 1), 'utf8');
      }
      const brief = value?.value ?? value;
      console.log(`\n=== ${name} (${Date.now() - started}ms) ===`);
      console.log(JSON.stringify(brief, null, 2).slice(0, 4000));
    } catch (err) {
      results.scenarios[name] = { ok: false, error: err.message, ms: Date.now() - started };
      console.error(`[scenario] ${name} FAILED: ${err.message}`);
      console.error(page.errors.slice(-5).join('\n'));
    }
  }
  writeFileSync(`${OUT}/record-results.json`, JSON.stringify(results, null, 2), 'utf8');
  console.log(`\nwrote ${OUT}/record-results.json`);
} catch (err) {
  console.error('RECORD FAILED:', err.message);
  console.error('console:', page.consoleLines.slice(-25).join('\n'));
  console.error('errors:', page.errors.slice(-10).join('\n'));
  process.exitCode = 1;
} finally {
  page.close();
}
