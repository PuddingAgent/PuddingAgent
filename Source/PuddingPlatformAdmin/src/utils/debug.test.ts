import {
  buildPerfDiagnosticSnapshot,
  clearPerfEvents,
  getPerfEvents,
  installPerfDiagnostics,
  isPerfDiagnosticsEnabled,
  recordPerfEvent,
  recordPerfStep,
  setPerfDiagnosticsEnabled,
  summarizePerfEvents,
} from './debug';

describe('perf diagnostics summary', () => {
  beforeEach(() => {
    clearPerfEvents();
    localStorage.setItem('pudding_perf', '1');
  });

  afterEach(() => {
    localStorage.removeItem('pudding_perf');
  });

  it('does not record diagnostics until the mode is explicitly enabled', () => {
    localStorage.removeItem('pudding_perf');
    setPerfDiagnosticsEnabled(false);

    expect(isPerfDiagnosticsEnabled()).toBe(false);
    installPerfDiagnostics();
    recordPerfEvent('chat.output.paint', { domTextChars: 30 });
    expect(summarizePerfEvents()).toMatchObject({ totalEvents: 0 });

    setPerfDiagnosticsEnabled(true);
    installPerfDiagnostics();
    recordPerfEvent('chat.output.paint', { domTextChars: 30 });
    expect(summarizePerfEvents()).toMatchObject({
      totalEvents: expect.any(Number),
      output: expect.objectContaining({ lastDomChars: 30 }),
    });
  });

  it('summarizes safely before any paint events exist', () => {
    recordPerfEvent('chat.sse.start', { sessionId: 'session-1' });

    expect(() => summarizePerfEvents()).not.toThrow();
    expect(summarizePerfEvents()).toMatchObject({
      totalEvents: 1,
      output: {
        paints: 0,
        firstDomChars: 0,
        lastDomChars: 0,
      },
    });
  });

  it('reports active visible and incoming rates without counting idle gaps', () => {
    let now = 0;
    const perfSpy = jest.spyOn(performance, 'now').mockImplementation(() => now);

    recordPerfEvent('chat.output.paint', { domTextChars: 100 });
    now = 500;
    recordPerfEvent('chat.output.paint', { domTextChars: 200 });
    now = 10_000;
    recordPerfEvent('chat.output.paint', { domTextChars: 210 });

    now = 11_000;
    recordPerfEvent('chat.typewriter.input', { incomingDelta: 0 });
    now = 11_500;
    recordPerfEvent('chat.typewriter.input', { incomingDelta: 50 });
    now = 30_000;
    recordPerfEvent('chat.typewriter.input', { incomingDelta: 100 });

    expect(summarizePerfEvents()).toMatchObject({
      output: {
        charsPerSecond: 11,
        activeChars: 100,
        activeWindowMs: 500,
        activeCharsPerSecond: 200,
      },
      stream: {
        incomingChars: 50,
        incomingWindowMs: 500,
        incomingCharsPerSecond: 100,
      },
    });

    perfSpy.mockRestore();
  });

  it('summarizes layout shifts and resource loads for chat diagnostics', () => {
    recordPerfEvent('browser.layoutShift', {
      value: 0.18,
      hadRecentInput: false,
      sources: ['body', 'textarea.ant-input'],
    });
    recordPerfEvent('browser.layoutShift', {
      value: 0.03,
      hadRecentInput: true,
      sources: ['button'],
    });
    recordPerfEvent('browser.resource', {
      name: '/admin/assets/pets/pudding/spritesheet.png',
      initiatorType: 'css',
      decodedBodySize: 2_140_000,
      transferSize: 2_140_000,
      durationMs: 830,
    });

    expect(summarizePerfEvents()).toMatchObject({
      browser: {
        layoutShifts: 2,
        cumulativeLayoutShift: 0.18,
        maxLayoutShift: 0.18,
        resources: 1,
        maxResourceBytes: 2_140_000,
      },
    });
  });

  it('builds a copyable diagnostic snapshot with bottleneck hints and raw records', () => {
    let now = 0;
    const perfSpy = jest.spyOn(performance, 'now').mockImplementation(() => now);

    recordPerfEvent('chat.typewriter.input', { incomingDelta: 20 });
    now = 1_500;
    recordPerfEvent('chat.typewriter.input', { incomingDelta: 40 });
    now = 1_620;
    recordPerfEvent('chat.output.paint', { domTextChars: 20, commitToPaintMs: 12 });
    now = 2_240;
    recordPerfEvent('chat.output.paint', { domTextChars: 60, commitToPaintMs: 95 });
    now = 2_260;
    recordPerfEvent('chat.markdown.render', { chars: 900, commitMs: 88 });
    now = 2_360;
    recordPerfEvent('browser.longtask', { durationMs: 140, startTime: 2_220 });
    now = 2_460;
    recordPerfEvent('browser.resource', {
      name: '/assets/pudding/spritesheet.png',
      decodedBodySize: 2_100_000,
      durationMs: 820,
    });

    const snapshot = buildPerfDiagnosticSnapshot({
      sessionId: 'session-1',
      workspaceId: 'default',
      rawEvents: [{ event: 'delta', timestamp: 1, payload: '{"delta":"hello"}' }],
    });

    expect(snapshot.context).toMatchObject({
      sessionId: 'session-1',
      workspaceId: 'default',
    });
    expect(snapshot.diagnosis.map(item => item.code)).toEqual(
      expect.arrayContaining([
        'stream-input-gap',
        'paint-gap',
        'react-render-slow',
        'main-thread-longtask',
        'large-resource',
      ]),
    );
    expect(snapshot.cadence).toMatchObject({
      incoming: expect.objectContaining({ maxGapMs: 1500 }),
      paint: expect.objectContaining({ maxGapMs: 620 }),
    });
    expect(snapshot.raw.perfEvents.length).toBeGreaterThan(0);
    expect(snapshot.raw.chatEvents).toEqual([{ event: 'delta', timestamp: 1, payload: '{"delta":"hello"}' }]);

    perfSpy.mockRestore();
  });

  it('includes optional capture metadata in diagnostic snapshots', () => {
    const snapshot = buildPerfDiagnosticSnapshot({
      sessionId: 'session-capture',
      capture: {
        status: 'stopped',
        startedAt: '2026-05-30T10:00:00.000Z',
        endedAt: '2026-05-30T10:00:07.500Z',
        durationMs: 7500,
      },
    });

    expect(snapshot.capture).toEqual({
      status: 'stopped',
      startedAt: '2026-05-30T10:00:00.000Z',
      endedAt: '2026-05-30T10:00:07.500Z',
      durationMs: 7500,
    });
  });

  it('does not treat expected stream aborts from session switching as frontend failures', () => {
    let now = 0;
    const perfSpy = jest.spyOn(performance, 'now').mockImplementation(() => now);

    recordPerfEvent('chat.sse.error', {
      sessionId: 'session-a',
      error: 'signal is aborted without reason',
      aborted: true,
      elapsedMs: 19_227,
    });
    recordPerfEvent('browser.fetch', {
      url: '/api/sessions/session-a/events/stream',
      method: 'GET',
      name: 'AbortError',
      message: 'signal is aborted without reason',
      error: true,
      durationMs: 19_227,
      startTime: 0,
    });
    now = 42;
    recordPerfEvent('browser.fetch', {
      url: '/api/sessions?workspaceId=default',
      method: 'GET',
      status: 200,
      ok: true,
      durationMs: 42,
      startTime: 0,
    });

    const summary = summarizePerfEvents();
    const snapshot = buildPerfDiagnosticSnapshot();

    expect(summary).toMatchObject({
      stream: {
        sseErrors: 0,
      },
      browser: {
        fetches: 2,
        failedFetches: 0,
        maxFetchDurationMs: 42,
      },
    });
    expect(snapshot.diagnosis.map(item => item.code)).not.toContain('fetch-slow-or-failed');

    perfSpy.mockRestore();
  });

  it('keeps enough raw perf events to cover a full diagnostic capture', () => {
    let now = 0;
    const perfSpy = jest.spyOn(performance, 'now').mockImplementation(() => now);

    for (let i = 0; i < 5_100; i++) {
      now = i;
      recordPerfEvent('chat.sse.event', { sequenceNum: i });
    }

    const snapshot = buildPerfDiagnosticSnapshot();
    expect(summarizePerfEvents().totalEvents).toBe(5_000);
    expect(snapshot.raw.perfEvents).toHaveLength(5_000);
    expect(snapshot.raw.perfEvents[0]?.payload?.sequenceNum).toBe(100);
    expect(snapshot.raw.perfEvents[snapshot.raw.perfEvents.length - 1]?.payload?.sequenceNum).toBe(5_099);

    perfSpy.mockRestore();
  });

  it('includes a page state snapshot for loading and layout diagnosis', () => {
    document.body.innerHTML = `
      <main data-testid="chat-main">
        <article data-testid="message">Assistant answer is rendering</article>
        <textarea data-testid="chat-input">draft message</textarea>
      </main>
    `;

    const snapshot = buildPerfDiagnosticSnapshot({ sessionId: 'session-page' });

    expect(snapshot.pageSnapshot).toEqual(expect.objectContaining({
      readyState: expect.any(String),
      scroll: expect.objectContaining({
        x: expect.any(Number),
        y: expect.any(Number),
      }),
      elements: expect.objectContaining({
        textareas: 1,
      }),
    }));
    expect(snapshot.pageSnapshot.visibleTextSample).toContain('Assistant answer is rendering');
  });

  it('records browser environment, errors, and fetch timings in frontend diagnostics', async () => {
    const originalFetch = globalThis.fetch;
    const fetchMock = jest.fn().mockResolvedValue({
      status: 200,
      ok: true,
      headers: { get: () => 'application/json' },
    });
    Object.defineProperty(globalThis, 'fetch', {
      configurable: true,
      writable: true,
      value: fetchMock,
    });

    installPerfDiagnostics();
    await fetch('/api/sessions?workspaceId=default', { method: 'GET' });
    const errorEvent = new Event('error') as ErrorEvent;
    Object.assign(errorEvent, {
      message: 'render failed',
      filename: 'MessageItem.tsx',
      lineno: 12,
      colno: 3,
    });
    window.dispatchEvent(errorEvent);
    const rejectionEvent = new Event('unhandledrejection') as PromiseRejectionEvent;
    Object.assign(rejectionEvent, {
      reason: new Error('stream failed'),
    });
    window.dispatchEvent(rejectionEvent);

    const snapshot = buildPerfDiagnosticSnapshot({ sessionId: 'session-js' });
    expect(snapshot.environment).toEqual(expect.objectContaining({
      viewport: expect.objectContaining({
        width: expect.any(Number),
        height: expect.any(Number),
      }),
    }));
    expect(snapshot.raw.perfEvents.map(event => event.name)).toEqual(expect.arrayContaining([
      'browser.environment',
      'browser.fetch',
      'browser.error',
      'browser.unhandledrejection',
    ]));

    Object.defineProperty(globalThis, 'fetch', {
      configurable: true,
      writable: true,
      value: originalFetch,
    });
  });

  it('summarizes workflow step timings for chat loading diagnosis', () => {
    let now = 10;
    const perfSpy = jest.spyOn(performance, 'now').mockImplementation(() => now);

    const cacheStartedAt = now;
    now = 42;
    recordPerfStep('agent.select', 'cache.loadConversation', cacheStartedAt, {
      traceId: 'switch-1',
      agentId: 'agent-a',
      status: 'ok',
      reason: 'cache hit',
    });
    const fetchStartedAt = now;
    now = 1_242;
    recordPerfStep('agent.select', 'api.getConversation', fetchStartedAt, {
      traceId: 'switch-1',
      agentId: 'agent-a',
      status: 'ok',
      messageCount: 2,
    });

    const summary = summarizePerfEvents();
    const snapshot = buildPerfDiagnosticSnapshot();

    expect(summary).toMatchObject({
      workflow: {
        steps: 2,
        traces: 1,
        maxStepMs: 1200,
        slowSteps: 1,
      },
    });
    expect(snapshot.top.workflowSteps[0]).toMatchObject({
      name: 'chat.workflow.step',
      payload: expect.objectContaining({
        workflow: 'agent.select',
        step: 'api.getConversation',
        durationMs: 1200,
        traceId: 'switch-1',
      }),
    });
    expect(snapshot.diagnosis.map(item => item.code)).toContain('workflow-step-slow');
    expect(getPerfEvents().map(event => event.name)).toContain('chat.workflow.step');

    perfSpy.mockRestore();
  });
});
