export type PuddingDebugApi = {
  getSessionState(sessionId: string): any | null;
  getLastTraceId(): string | null;
  getLastSessionId(): string | null;
  getLastMessageId(): string | null;
  exportTimeline(): any | null;
  clearDebugEvents(): void;
};

export type PuddingPerfEvent = {
  name: string;
  at: number;
  payload?: Record<string, unknown>;
};

export type PuddingPerfApi = {
  enabled: boolean;
  events: PuddingPerfEvent[];
  clear(): void;
  mark(name: string): void;
  measure(name: string, startMark: string, endMark?: string): number | null;
  summary(): Record<string, unknown>;
  snapshot(): PuddingPerfDiagnosticSnapshot;
};

export type PuddingPerfGapStats = {
  samples: number;
  avgGapMs: number | null;
  maxGapMs: number | null;
  gapsOver500ms: number;
  gapsOver1000ms: number;
};

export type PuddingPerfDiagnosis = {
  code: string;
  severity: 'info' | 'warn' | 'critical';
  title: string;
  evidence: string;
  nextStep: string;
};

export type PuddingPerfCaptureMetadata = {
  status: 'idle' | 'recording' | 'stopped';
  startedAt?: string;
  endedAt?: string;
  durationMs?: number;
};

export type PuddingBrowserEnvironmentSnapshot = {
  viewport: {
    width: number;
    height: number;
    devicePixelRatio: number;
  };
  page: {
    visibilityState?: string;
    hasFocus?: boolean;
  };
  memory?: {
    usedJSHeapSize?: number;
    totalJSHeapSize?: number;
    jsHeapSizeLimit?: number;
  };
  connection?: {
    effectiveType?: string;
    downlink?: number;
    rtt?: number;
    saveData?: boolean;
  };
  navigation?: {
    type?: string;
    startTime?: number;
    responseStart?: number;
    domContentLoadedEventEnd?: number;
    loadEventEnd?: number;
    duration?: number;
  };
};

export type PuddingPageStateSnapshot = {
  title: string;
  url: string;
  readyState: string;
  visibilityState?: string;
  hasFocus?: boolean;
  scroll: {
    x: number;
    y: number;
    maxX: number;
    maxY: number;
  };
  viewport: {
    width: number;
    height: number;
    devicePixelRatio: number;
  };
  document: {
    bodyTextLength: number;
    bodyChildCount: number;
    documentElementClassName?: string;
    bodyClassName?: string;
  };
  elements: {
    nodes: number;
    images: number;
    buttons: number;
    inputs: number;
    textareas: number;
    links: number;
    scripts: number;
    stylesheets: number;
    messageLikeNodes: number;
  };
  activeElement?: string;
  selectionText?: string;
  visibleTextSample: string;
};

export type PuddingPerfDiagnosticSnapshot = {
  schemaVersion: 1;
  capturedAt: string;
  capture?: PuddingPerfCaptureMetadata;
  context: {
    workspaceId?: string;
    sessionId?: string | null;
    url?: string;
    userAgent?: string;
  };
  environment: PuddingBrowserEnvironmentSnapshot;
  pageSnapshot: PuddingPageStateSnapshot;
  summary: Record<string, unknown>;
  cadence: {
    incoming: PuddingPerfGapStats;
    paint: PuddingPerfGapStats;
    eventApply: PuddingPerfGapStats;
  };
  diagnosis: PuddingPerfDiagnosis[];
  top: {
    slowPaints: PuddingPerfEvent[];
    slowEventApplies: PuddingPerfEvent[];
    slowMarkdownRenders: PuddingPerfEvent[];
    longTasks: PuddingPerfEvent[];
    layoutShifts: PuddingPerfEvent[];
    resources: PuddingPerfEvent[];
    fetches: PuddingPerfEvent[];
    sseEvents: PuddingPerfEvent[];
    replayEvents: PuddingPerfEvent[];
    workflowSteps: PuddingPerfEvent[];
    errors: PuddingPerfEvent[];
  };
  raw: {
    perfEvents: PuddingPerfEvent[];
    chatEvents: unknown[];
  };
};

/** 判断 debug mode 是否启用（通过 URL 参数 ?debug=1） */
export function isDebugMode(): boolean {
  const urlParams = new URLSearchParams(window.location.search);
  return urlParams.get('debug') === '1';
}

const PERF_DIAGNOSTICS_STORAGE_KEY = 'pudding_perf';

/** 判断聊天性能诊断是否启用：?perf=1 / ?debug=1 / localStorage.pudding_perf=1 */
export function isPerfDiagnosticsEnabled(): boolean {
  try {
    const urlParams = new URLSearchParams(window.location.search);
    return urlParams.get('perf') === '1'
      || urlParams.get('debug') === '1'
      || localStorage.getItem(PERF_DIAGNOSTICS_STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

export function setPerfDiagnosticsEnabled(enabled: boolean): void {
  try {
    if (enabled) {
      localStorage.setItem(PERF_DIAGNOSTICS_STORAGE_KEY, '1');
    } else {
      localStorage.removeItem(PERF_DIAGNOSTICS_STORAGE_KEY);
    }
  } catch {
    // Storage can be unavailable in restricted browser contexts.
  }
}

const perfEvents: PuddingPerfEvent[] = [];
const perfThrottle = new Map<string, number>();
const MAX_PERF_EVENTS = 5000;
const DEFAULT_PERF_SNAPSHOT_EVENT_LIMIT = 5000;
const DEFAULT_RAW_CHAT_EVENT_LIMIT = 2000;
let longTaskObserverInstalled = false;
let layoutShiftObserverInstalled = false;
let resourceObserverInstalled = false;
let browserEventListenersInstalled = false;
const recordedResourceNames = new Set<string>();
const PUDDING_FETCH_WRAPPED = '__puddingPerfFetchWrapped';

function trimPerfEvents() {
  if (perfEvents.length > MAX_PERF_EVENTS) perfEvents.splice(0, perfEvents.length - MAX_PERF_EVENTS);
}

export function getPerfEvents(): PuddingPerfEvent[] {
  return [...perfEvents];
}

export function clearPerfEvents(): void {
  perfEvents.length = 0;
  perfThrottle.clear();
  recordedResourceNames.clear();
}

function numericPayload(event: PuddingPerfEvent | null | undefined, key: string): number | null {
  if (!event) return null;
  const value = event.payload?.[key];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function isExpectedAbortPayload(payload?: Record<string, unknown>): boolean {
  if (!payload) return false;
  const name = typeof payload.name === 'string' ? payload.name : '';
  const message = typeof payload.message === 'string' ? payload.message : '';
  const error = typeof payload.error === 'string' ? payload.error : '';
  return payload.aborted === true ||
    name === 'AbortError' ||
    /abort/i.test(message) ||
    /abort/i.test(error);
}

function isExpectedAbortEvent(event: PuddingPerfEvent): boolean {
  return isExpectedAbortPayload(event.payload);
}

function isSessionEventStreamFetch(event: PuddingPerfEvent): boolean {
  const url = typeof event.payload?.url === 'string' ? event.payload.url : '';
  return url.includes('/events/stream');
}

function average(values: number[]): number | null {
  if (values.length === 0) return null;
  return Math.round(values.reduce((sum, value) => sum + value, 0) / values.length);
}

function max(values: number[]): number | null {
  return values.length > 0 ? Math.max(...values) : null;
}

function sum(values: number[]): number {
  return values.reduce((total, value) => total + value, 0);
}

function roundMetric(value: number): number {
  return Math.round(value * 1000) / 1000;
}

function truncate(value: string, maxLength = 500): string {
  return value.length > maxLength ? `${value.slice(0, maxLength)}...` : value;
}

function getBrowserEnvironmentSnapshot(): PuddingBrowserEnvironmentSnapshot {
  const memory = (performance as Performance & {
    memory?: {
      usedJSHeapSize?: number;
      totalJSHeapSize?: number;
      jsHeapSizeLimit?: number;
    };
  }).memory;
  const connection = (navigator as Navigator & {
    connection?: {
      effectiveType?: string;
      downlink?: number;
      rtt?: number;
      saveData?: boolean;
    };
  }).connection;
  const navigation = typeof performance.getEntriesByType === 'function'
    ? performance.getEntriesByType('navigation')[0] as
      | (PerformanceNavigationTiming & { type?: string })
      | undefined
    : undefined;

  let hasFocus: boolean | undefined;
  try {
    hasFocus = typeof document.hasFocus === 'function' ? document.hasFocus() : undefined;
  } catch {
    hasFocus = undefined;
  }

  return {
    viewport: {
      width: window.innerWidth,
      height: window.innerHeight,
      devicePixelRatio: window.devicePixelRatio || 1,
    },
    page: {
      visibilityState: document.visibilityState,
      hasFocus,
    },
    memory: memory ? {
      usedJSHeapSize: memory.usedJSHeapSize,
      totalJSHeapSize: memory.totalJSHeapSize,
      jsHeapSizeLimit: memory.jsHeapSizeLimit,
    } : undefined,
    connection: connection ? {
      effectiveType: connection.effectiveType,
      downlink: connection.downlink,
      rtt: connection.rtt,
      saveData: connection.saveData,
    } : undefined,
    navigation: navigation ? {
      type: navigation.type,
      startTime: Math.round(navigation.startTime),
      responseStart: Math.round(navigation.responseStart),
      domContentLoadedEventEnd: Math.round(navigation.domContentLoadedEventEnd),
      loadEventEnd: Math.round(navigation.loadEventEnd),
      duration: Math.round(navigation.duration),
    } : undefined,
  };
}

function describeElementForSnapshot(element: Element | null | undefined): string | undefined {
  if (!element) return undefined;
  const id = element.id ? `#${element.id}` : '';
  const className = typeof element.className === 'string' && element.className.trim()
    ? `.${element.className.trim().split(/\s+/).slice(0, 3).join('.')}`
    : '';
  const testId = element.getAttribute('data-testid');
  const role = element.getAttribute('role');
  const ariaLabel = element.getAttribute('aria-label');
  const suffixes = [
    testId ? `[data-testid="${truncate(testId, 50)}"]` : '',
    role ? `[role="${truncate(role, 50)}"]` : '',
    ariaLabel ? `[aria-label="${truncate(ariaLabel, 80)}"]` : '',
  ].join('');
  return `${element.tagName.toLowerCase()}${id}${className}${suffixes}`;
}

function getPageStateSnapshot(): PuddingPageStateSnapshot {
  const bodyText = (document.body?.innerText || document.body?.textContent || '').replace(/\s+/g, ' ').trim();
  const documentElement = document.documentElement;
  const body = document.body;
  const maxX = Math.max(0, (documentElement?.scrollWidth ?? 0) - window.innerWidth);
  const maxY = Math.max(0, (documentElement?.scrollHeight ?? 0) - window.innerHeight);

  let hasFocus: boolean | undefined;
  try {
    hasFocus = typeof document.hasFocus === 'function' ? document.hasFocus() : undefined;
  } catch {
    hasFocus = undefined;
  }

  return {
    title: document.title,
    url: window.location.href,
    readyState: document.readyState,
    visibilityState: document.visibilityState,
    hasFocus,
    scroll: {
      x: Math.round(window.scrollX || documentElement?.scrollLeft || 0),
      y: Math.round(window.scrollY || documentElement?.scrollTop || 0),
      maxX: Math.round(maxX),
      maxY: Math.round(maxY),
    },
    viewport: {
      width: window.innerWidth,
      height: window.innerHeight,
      devicePixelRatio: window.devicePixelRatio || 1,
    },
    document: {
      bodyTextLength: bodyText.length,
      bodyChildCount: body?.children.length ?? 0,
      documentElementClassName: typeof documentElement?.className === 'string'
        ? truncate(documentElement.className, 300)
        : undefined,
      bodyClassName: typeof body?.className === 'string' ? truncate(body.className, 300) : undefined,
    },
    elements: {
      nodes: document.querySelectorAll('*').length,
      images: document.images.length,
      buttons: document.querySelectorAll('button,[role="button"]').length,
      inputs: document.querySelectorAll('input').length,
      textareas: document.querySelectorAll('textarea').length,
      links: document.links.length,
      scripts: document.scripts.length,
      stylesheets: document.styleSheets.length,
      messageLikeNodes: document.querySelectorAll('[data-testid*="message"],[class*="message"],article').length,
    },
    activeElement: describeElementForSnapshot(document.activeElement),
    selectionText: truncate(String(window.getSelection?.()?.toString() ?? ''), 500),
    visibleTextSample: truncate(bodyText, 2_000),
  };
}

function sanitizeUrl(input: unknown): string {
  try {
    const raw = typeof input === 'string' ? input : String(input ?? 'unknown');
    const url = new URL(raw, window.location.href);
    const sameOrigin = url.origin === window.location.origin;
    return truncate(sameOrigin ? `${url.pathname}${url.search}` : `${url.origin}${url.pathname}`, 500);
  } catch {
    return truncate(String(input ?? 'unknown'), 500);
  }
}

function describeThrowable(value: unknown): Record<string, unknown> {
  if (value instanceof Error) {
    return {
      name: value.name,
      message: truncate(value.message || 'Error', 500),
      stack: value.stack ? truncate(value.stack, 1_500) : undefined,
    };
  }
  if (typeof value === 'string') {
    return { message: truncate(value, 500) };
  }
  return { message: truncate(String(value ?? 'unknown'), 500) };
}

function describeFetchRequest(input: unknown, init?: RequestInit): { url: string; method: string } {
  let rawUrl: unknown = input;
  let method = init?.method;
  if (typeof Request !== 'undefined' && input instanceof Request) {
    rawUrl = input.url;
    method = method ?? input.method;
  } else if (typeof input === 'object' && input !== null && 'url' in input) {
    rawUrl = (input as { url?: unknown }).url;
    if ('method' in input) method = method ?? String((input as { method?: unknown }).method ?? '');
  }
  return {
    url: sanitizeUrl(rawUrl),
    method: (method || 'GET').toUpperCase(),
  };
}

function nestedNumber(obj: Record<string, unknown>, path: string): number | null {
  const value = path.split('.').reduce<unknown>((current, key) => {
    if (!current || typeof current !== 'object') return undefined;
    return (current as Record<string, unknown>)[key];
  }, obj);
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function gapStats(events: PuddingPerfEvent[]): PuddingPerfGapStats {
  if (events.length < 2) {
    return {
      samples: Math.max(events.length - 1, 0),
      avgGapMs: null,
      maxGapMs: null,
      gapsOver500ms: 0,
      gapsOver1000ms: 0,
    };
  }

  const gaps: number[] = [];
  for (let index = 1; index < events.length; index += 1) {
    gaps.push(Math.max(0, events[index].at - events[index - 1].at));
  }

  return {
    samples: gaps.length,
    avgGapMs: average(gaps),
    maxGapMs: max(gaps),
    gapsOver500ms: gaps.filter(gap => gap > 500).length,
    gapsOver1000ms: gaps.filter(gap => gap > 1000).length,
  };
}

function sortByPayloadNumber(events: PuddingPerfEvent[], key: string, limit: number): PuddingPerfEvent[] {
  return [...events]
    .sort((a, b) => (numericPayload(b, key) ?? -1) - (numericPayload(a, key) ?? -1))
    .slice(0, limit);
}

function sortResources(events: PuddingPerfEvent[], limit: number): PuddingPerfEvent[] {
  return [...events]
    .sort((a, b) => {
      const aSize = numericPayload(a, 'decodedBodySize') ?? numericPayload(a, 'transferSize') ?? 0;
      const bSize = numericPayload(b, 'decodedBodySize') ?? numericPayload(b, 'transferSize') ?? 0;
      const aDuration = numericPayload(a, 'durationMs') ?? 0;
      const bDuration = numericPayload(b, 'durationMs') ?? 0;
      return (bSize + bDuration * 1024) - (aSize + aDuration * 1024);
    })
    .slice(0, limit);
}

function addDiagnosis(
  target: PuddingPerfDiagnosis[],
  diagnosis: PuddingPerfDiagnosis,
): void {
  target.push(diagnosis);
}

function positiveDeltaRate(
  events: PuddingPerfEvent[],
  valueKey: string,
  options: { maxGapMs?: number } = {},
): { chars: number; windowMs: number; charsPerSecond: number | null } {
  const maxGapMs = options.maxGapMs ?? 2_000;
  let previousValue: number | null = null;
  let previousAt: number | null = null;
  let chars = 0;
  let windowMs = 0;

  for (const event of events) {
    const value = numericPayload(event, valueKey);
    if (value == null) continue;
    if (previousValue != null && previousAt != null) {
      const delta = value - previousValue;
      const gap = Math.max(0, event.at - previousAt);
      if (delta > 0 && gap > 0 && gap <= maxGapMs) {
        chars += delta;
        windowMs += gap;
      }
    }
    previousValue = value;
    previousAt = event.at;
  }

  return {
    chars,
    windowMs,
    charsPerSecond: windowMs > 0 ? Math.round((chars / windowMs) * 1000) : null,
  };
}

function positivePayloadSumRate(
  events: PuddingPerfEvent[],
  payloadKey: string,
  options: { maxGapMs?: number } = {},
): { chars: number; windowMs: number; charsPerSecond: number | null } {
  const maxGapMs = options.maxGapMs ?? 2_000;
  let previousAt: number | null = null;
  let chars = 0;
  let windowMs = 0;

  for (const event of events) {
    const value = numericPayload(event, payloadKey);
    if (value == null || value <= 0) {
      previousAt = event.at;
      continue;
    }
    if (previousAt != null) {
      const gap = Math.max(0, event.at - previousAt);
      if (gap > 0 && gap <= maxGapMs) {
        chars += value;
        windowMs += gap;
      }
    }
    previousAt = event.at;
  }

  return {
    chars,
    windowMs,
    charsPerSecond: windowMs > 0 ? Math.round((chars / windowMs) * 1000) : null,
  };
}

export function summarizePerfEvents(): Record<string, unknown> {
  const counts: Record<string, number> = {};
  for (const event of perfEvents) counts[event.name] = (counts[event.name] ?? 0) + 1;

  const first = perfEvents[0];
  const last = perfEvents[perfEvents.length - 1];
  const outputPaints = perfEvents.filter(event => event.name === 'chat.output.paint');
  const outputCommits = perfEvents.filter(event => event.name === 'chat.output.commit');
  const eventApplies = perfEvents.filter(event => event.name === 'chat.event.apply');
  const typewriterInputs = perfEvents.filter(event => event.name === 'chat.typewriter.input');
  const markdownRenders = perfEvents.filter(event => event.name === 'chat.markdown.render');
  const longTasks = perfEvents.filter(event => event.name === 'browser.longtask');
  const layoutShifts = perfEvents.filter(event => event.name === 'browser.layoutShift');
  const resourceLoads = perfEvents.filter(event => event.name === 'browser.resource');
  const fetches = perfEvents.filter(event => event.name === 'browser.fetch');
  const sseChunks = perfEvents.filter(event => event.name === 'chat.sse.chunk');
  const sseEvents = perfEvents.filter(event => event.name === 'chat.sse.event');
  const sseErrors = perfEvents.filter(event =>
    (event.name === 'chat.sse.error' || event.name === 'chat.sse.parseError') &&
    !isExpectedAbortEvent(event));
  const replayPages = perfEvents.filter(event => event.name === 'chat.replay.page');
  const replayPolls = perfEvents.filter(event => event.name === 'chat.replay.pollComplete');
  const replayErrors = perfEvents.filter(event => event.name === 'chat.replay.error' || event.name === 'chat.replay.pollError');
  const workflowSteps = perfEvents.filter(event => event.name === 'chat.workflow.step');
  const browserErrors = perfEvents.filter(event => event.name === 'browser.error');
  const browserRejections = perfEvents.filter(event => event.name === 'browser.unhandledrejection');

  const paintDurations = outputPaints
    .map(event => numericPayload(event, 'commitToPaintMs'))
    .filter((value): value is number => value != null);
  const totalRenderToPaint = outputPaints
    .map(event => numericPayload(event, 'renderToPaintMs'))
    .filter((value): value is number => value != null);
  const domChars = outputPaints
    .map(event => numericPayload(event, 'domTextChars'))
    .filter((value): value is number => value != null);
  const eventApplyMs = eventApplies
    .map(event => numericPayload(event, 'applyMs'))
    .filter((value): value is number => value != null);
  const markdownCommitMs = markdownRenders
    .map(event => numericPayload(event, 'commitMs'))
    .filter((value): value is number => value != null);
  const longTaskMs = longTasks
    .map(event => numericPayload(event, 'durationMs'))
    .filter((value): value is number => value != null);
  const layoutShiftValues = layoutShifts
    .map(event => numericPayload(event, 'value'))
    .filter((value): value is number => value != null);
  const clsValues = layoutShifts
    .filter(event => event.payload?.hadRecentInput !== true)
    .map(event => numericPayload(event, 'value'))
    .filter((value): value is number => value != null);
  const resourceBytes = resourceLoads
    .map(event => numericPayload(event, 'decodedBodySize') ?? numericPayload(event, 'transferSize'))
    .filter((value): value is number => value != null);
  const resourceDurations = resourceLoads
    .map(event => numericPayload(event, 'durationMs'))
    .filter((value): value is number => value != null);
  const diagnosticFetches = fetches.filter(event =>
    !isExpectedAbortEvent(event) &&
    !isSessionEventStreamFetch(event));
  const fetchDurations = diagnosticFetches
    .map(event => numericPayload(event, 'durationMs'))
    .filter((value): value is number => value != null);
  const failedFetches = fetches.filter(event =>
    (event.payload?.ok === false || event.payload?.error != null) &&
    !isExpectedAbortEvent(event));
  const workflowStepMs = workflowSteps
    .map(event => numericPayload(event, 'durationMs'))
    .filter((value): value is number => value != null);
  const workflowTraceIds = new Set(
    workflowSteps
      .map(event => typeof event.payload?.traceId === 'string' ? event.payload.traceId : null)
      .filter((value): value is string => Boolean(value)),
  );

  const firstPaint = outputPaints[0];
  const lastPaint = outputPaints[outputPaints.length - 1];
  const firstChars = numericPayload(firstPaint, 'domTextChars') ?? 0;
  const lastChars = numericPayload(lastPaint, 'domTextChars') ?? 0;
  const outputWindowMs = firstPaint && lastPaint ? Math.max(0, lastPaint.at - firstPaint.at) : 0;
  const charsPerSecond = outputWindowMs > 0
    ? Math.round(((lastChars - firstChars) / outputWindowMs) * 1000)
    : null;
  const visibleRate = positiveDeltaRate(outputPaints, 'domTextChars');
  const incomingRate = positivePayloadSumRate(typewriterInputs, 'incomingDelta');

  return {
    totalEvents: perfEvents.length,
    windowMs: first && last ? Math.max(0, last.at - first.at) : 0,
    counts,
    output: {
      commits: outputCommits.length,
      paints: outputPaints.length,
      outputWindowMs,
      charsPerSecond,
      activeChars: visibleRate.chars,
      activeWindowMs: visibleRate.windowMs,
      activeCharsPerSecond: visibleRate.charsPerSecond,
      firstDomChars: firstChars,
      lastDomChars: lastChars,
      maxDomChars: max(domChars),
      avgCommitToPaintMs: average(paintDurations),
      maxCommitToPaintMs: max(paintDurations),
      avgRenderToPaintMs: average(totalRenderToPaint),
      maxRenderToPaintMs: max(totalRenderToPaint),
    },
    stream: {
      incomingChars: incomingRate.chars,
      incomingWindowMs: incomingRate.windowMs,
      incomingCharsPerSecond: incomingRate.charsPerSecond,
      sseChunks: sseChunks.length,
      sseEvents: sseEvents.length,
      sseErrors: sseErrors.length,
      replayPages: replayPages.length,
      replayPolls: replayPolls.length,
      replayErrors: replayErrors.length,
      maxSseEventGapMs: gapStats(sseEvents).maxGapMs,
      maxReplayPageGapMs: gapStats(replayPages).maxGapMs,
    },
    react: {
      avgEventApplyMs: average(eventApplyMs),
      maxEventApplyMs: max(eventApplyMs),
      avgMarkdownCommitMs: average(markdownCommitMs),
      maxMarkdownCommitMs: max(markdownCommitMs),
    },
    browser: {
      longTasks: longTasks.length,
      maxLongTaskMs: max(longTaskMs),
      totalLongTaskMs: sum(longTaskMs),
      layoutShifts: layoutShifts.length,
      cumulativeLayoutShift: roundMetric(sum(clsValues)),
      maxLayoutShift: layoutShiftValues.length > 0 ? roundMetric(Math.max(...layoutShiftValues)) : null,
      resources: resourceLoads.length,
      totalResourceBytes: sum(resourceBytes),
      maxResourceBytes: max(resourceBytes),
      maxResourceDurationMs: max(resourceDurations),
      fetches: fetches.length,
      failedFetches: failedFetches.length,
      maxFetchDurationMs: max(fetchDurations),
      errors: browserErrors.length,
      unhandledRejections: browserRejections.length,
    },
    workflow: {
      steps: workflowSteps.length,
      traces: workflowTraceIds.size,
      avgStepMs: average(workflowStepMs),
      maxStepMs: max(workflowStepMs),
      slowSteps: workflowStepMs.filter(value => value > 500).length,
      lastStep: workflowSteps[workflowSteps.length - 1]?.payload ?? null,
    },
    lastEvent: last ?? null,
  };
}

export function buildPerfDiagnosticSnapshot(options: {
  workspaceId?: string;
  sessionId?: string | null;
  rawEvents?: unknown[];
  capture?: PuddingPerfCaptureMetadata;
  perfEventLimit?: number;
  rawEventLimit?: number;
} = {}): PuddingPerfDiagnosticSnapshot {
  const perfEventLimit = options.perfEventLimit ?? DEFAULT_PERF_SNAPSHOT_EVENT_LIMIT;
  const rawEventLimit = options.rawEventLimit ?? DEFAULT_RAW_CHAT_EVENT_LIMIT;
  const summary = summarizePerfEvents();
  const incomingEvents = perfEvents.filter(event => event.name === 'chat.typewriter.input');
  const paintEvents = perfEvents.filter(event => event.name === 'chat.output.paint');
  const eventApplyEvents = perfEvents.filter(event => event.name === 'chat.event.apply');
  const markdownEvents = perfEvents.filter(event => event.name === 'chat.markdown.render');
  const longTaskEvents = perfEvents.filter(event => event.name === 'browser.longtask');
  const layoutShiftEvents = perfEvents.filter(event => event.name === 'browser.layoutShift');
  const resourceEvents = perfEvents.filter(event => event.name === 'browser.resource');
  const fetchEvents = perfEvents.filter(event => event.name === 'browser.fetch');
  const errorEvents = perfEvents.filter(event => event.name === 'browser.error' || event.name === 'browser.unhandledrejection');
  const sseEvents = perfEvents.filter(event => event.name.startsWith('chat.sse.'));
  const replayEvents = perfEvents.filter(event => event.name.startsWith('chat.replay.'));
  const workflowSteps = perfEvents.filter(event => event.name === 'chat.workflow.step');

  const cadence = {
    incoming: gapStats(incomingEvents),
    paint: gapStats(paintEvents),
    eventApply: gapStats(eventApplyEvents),
  };

  const diagnosis: PuddingPerfDiagnosis[] = [];
  const maxIncomingGap = cadence.incoming.maxGapMs ?? 0;
  const maxPaintGap = cadence.paint.maxGapMs ?? 0;
  const maxCommitToPaint = nestedNumber(summary, 'output.maxCommitToPaintMs') ?? 0;
  const maxEventApply = nestedNumber(summary, 'react.maxEventApplyMs') ?? 0;
  const maxMarkdownCommit = nestedNumber(summary, 'react.maxMarkdownCommitMs') ?? 0;
  const maxLongTask = nestedNumber(summary, 'browser.maxLongTaskMs') ?? 0;
  const cls = nestedNumber(summary, 'browser.cumulativeLayoutShift') ?? 0;
  const maxResourceBytes = nestedNumber(summary, 'browser.maxResourceBytes') ?? 0;
  const maxResourceDuration = nestedNumber(summary, 'browser.maxResourceDurationMs') ?? 0;
  const maxFetchDuration = nestedNumber(summary, 'browser.maxFetchDurationMs') ?? 0;
  const failedFetches = nestedNumber(summary, 'browser.failedFetches') ?? 0;
  const browserErrors = nestedNumber(summary, 'browser.errors') ?? 0;
  const unhandledRejections = nestedNumber(summary, 'browser.unhandledRejections') ?? 0;
  const maxWorkflowStep = nestedNumber(summary, 'workflow.maxStepMs') ?? 0;
  const incomingChars = nestedNumber(summary, 'stream.incomingChars') ?? 0;
  const outputPaints = nestedNumber(summary, 'output.paints') ?? 0;
  const sseEventCount = nestedNumber(summary, 'stream.sseEvents') ?? 0;
  const replayPageCount = nestedNumber(summary, 'stream.replayPages') ?? 0;
  const maxReplayPageGap = nestedNumber(summary, 'stream.maxReplayPageGapMs') ?? 0;

  if (incomingChars > 0 && outputPaints === 0) {
    addDiagnosis(diagnosis, {
      code: 'input-without-paint',
      severity: 'critical',
      title: '收到流式输入但没有输出绘制记录',
      evidence: `incomingChars=${incomingChars}, paints=${outputPaints}`,
      nextStep: '检查 MessageItem 是否挂载、是否进入稳定 Markdown 渲染路径，以及 perf 是否在输出组件内启用。',
    });
  }
  if (maxIncomingGap > 1000) {
    addDiagnosis(diagnosis, {
      code: 'stream-input-gap',
      severity: maxIncomingGap > 2500 ? 'critical' : 'warn',
      title: '模型输出到达存在明显断流',
      evidence: `incoming.maxGapMs=${maxIncomingGap}`,
      nextStep: '优先对比后端 SSE/轮询事件时间戳；如果后端也有同等间隔，瓶颈不在前端渲染。',
    });
  }
  if (replayPageCount > 0 && sseEventCount === 0) {
    addDiagnosis(diagnosis, {
      code: 'sse-not-observed',
      severity: 'critical',
      title: '未观察到 SSE 主通道事件，正在依赖 replay 补偿',
      evidence: `sseEvents=${sseEventCount}, replayPages=${replayPageCount}`,
      nextStep: '检查 /events/stream 是否建立、是否有 chunk 到达；如果 SSE 不可用，流式期间 replay 必须保持短间隔。',
    });
  }
  if (maxReplayPageGap > 2500) {
    addDiagnosis(diagnosis, {
      code: 'replay-poll-gap',
      severity: 'critical',
      title: 'replay 补偿轮询间隔过大',
      evidence: `stream.maxReplayPageGapMs=${maxReplayPageGap}`,
      nextStep: '确认当前会话是否处于流式生成状态；生成期间应使用短间隔补偿，空闲后再回到低频轮询。',
    });
  }
  if (maxPaintGap > 500) {
    addDiagnosis(diagnosis, {
      code: 'paint-gap',
      severity: maxPaintGap > 1200 ? 'critical' : 'warn',
      title: '可见输出绘制存在停顿',
      evidence: `paint.maxGapMs=${maxPaintGap}`,
      nextStep: '查看同一时间段 longtask、markdown.render 和 output.paint 的 Top 慢记录，判断是主线程阻塞还是批处理节奏过慢。',
    });
  }
  if (maxCommitToPaint > 50 || maxEventApply > 30 || maxMarkdownCommit > 50) {
    addDiagnosis(diagnosis, {
      code: 'react-render-slow',
      severity: Math.max(maxCommitToPaint, maxEventApply, maxMarkdownCommit) > 120 ? 'critical' : 'warn',
      title: 'React 更新或 Markdown 渲染偏慢',
      evidence: `maxCommitToPaintMs=${maxCommitToPaint}, maxEventApplyMs=${maxEventApply}, maxMarkdownCommitMs=${maxMarkdownCommit}`,
      nextStep: '优先拆分流式 Markdown 的稳定区/活动区，减少每个 delta 触发的 Markdown 全量重算和 DOM 文本增长。',
    });
  }
  if (maxLongTask > 80) {
    addDiagnosis(diagnosis, {
      code: 'main-thread-longtask',
      severity: maxLongTask > 200 ? 'critical' : 'warn',
      title: '主线程长任务会打断打字机输出',
      evidence: `maxLongTaskMs=${maxLongTask}`,
      nextStep: '结合 longtask.startTime 附近事件，定位是否由 Markdown、资源解码、布局或第三方脚本造成。',
    });
  }
  if (cls > 0.1) {
    addDiagnosis(diagnosis, {
      code: 'layout-shift',
      severity: cls > 0.25 ? 'critical' : 'warn',
      title: '布局偏移可能造成滚动和输出抖动',
      evidence: `CLS=${cls}`,
      nextStep: '检查 layoutShift.sources，给消息区、输入区、图片/头像等元素增加稳定尺寸和滚动锚点约束。',
    });
  }
  if (maxResourceBytes > 1024 * 1024 || maxResourceDuration > 500) {
    addDiagnosis(diagnosis, {
      code: 'large-resource',
      severity: maxResourceBytes > 3 * 1024 * 1024 || maxResourceDuration > 1500 ? 'critical' : 'warn',
      title: '大资源或慢资源可能抢占加载和解码时间',
      evidence: `maxResourceBytes=${maxResourceBytes}, maxResourceDurationMs=${maxResourceDuration}`,
      nextStep: '检查 Top resources，优先懒加载非 Chat 首屏资产，压缩大图/精灵图，隔离工作室相关依赖。',
    });
  }
  if (maxFetchDuration > 800 || failedFetches > 0) {
    addDiagnosis(diagnosis, {
      code: 'fetch-slow-or-failed',
      severity: failedFetches > 0 || maxFetchDuration > 2000 ? 'critical' : 'warn',
      title: '前端请求耗时或失败会推迟消息链路',
      evidence: `failedFetches=${failedFetches}, maxFetchDurationMs=${maxFetchDuration}`,
      nextStep: '检查 Top fetches 中的 URL、status、durationMs；若发送后首 token 慢，优先对齐 chat.post、SSE start/firstEvent 和 fetch 记录。',
    });
  }
  if (maxWorkflowStep > 800) {
    const slowest = sortByPayloadNumber(workflowSteps, 'durationMs', 1)[0];
    const workflow = typeof slowest?.payload?.workflow === 'string' ? slowest.payload.workflow : 'unknown';
    const step = typeof slowest?.payload?.step === 'string' ? slowest.payload.step : 'unknown';
    addDiagnosis(diagnosis, {
      code: 'workflow-step-slow',
      severity: maxWorkflowStep > 2000 ? 'critical' : 'warn',
      title: '聊天切换或消息加载步骤耗时偏高',
      evidence: `${workflow}.${step} durationMs=${maxWorkflowStep}`,
      nextStep: '查看 Top workflow steps 中相同 traceId 的前后步骤，区分是本地缓存、主会话确认、消息请求还是状态同步慢。',
    });
  }
  if (browserErrors > 0 || unhandledRejections > 0) {
    addDiagnosis(diagnosis, {
      code: 'browser-runtime-error',
      severity: 'critical',
      title: '浏览器运行时错误可能中断渲染或事件处理',
      evidence: `errors=${browserErrors}, unhandledRejections=${unhandledRejections}`,
      nextStep: '查看 Top errors 和 raw.perfEvents 中的 message/stack，先排除渲染异常、事件处理异常和未捕获的异步错误。',
    });
  }
  if (diagnosis.length === 0) {
    addDiagnosis(diagnosis, {
      code: 'no-obvious-frontend-bottleneck',
      severity: 'info',
      title: '当前采样没有明显前端瓶颈信号',
      evidence: '关键阈值未触发',
      nextStep: '复制更长一次真实输出过程的诊断包，最好从发送前清空开始，到输出结束后复制。',
    });
  }

  return {
    schemaVersion: 1,
    capturedAt: new Date().toISOString(),
    capture: options.capture,
    context: {
      workspaceId: options.workspaceId,
      sessionId: options.sessionId,
      url: typeof window !== 'undefined' ? window.location.href : undefined,
      userAgent: typeof navigator !== 'undefined' ? navigator.userAgent : undefined,
    },
    environment: getBrowserEnvironmentSnapshot(),
    pageSnapshot: getPageStateSnapshot(),
    summary,
    cadence,
    diagnosis,
    top: {
      slowPaints: sortByPayloadNumber(paintEvents, 'commitToPaintMs', 8),
      slowEventApplies: sortByPayloadNumber(eventApplyEvents, 'applyMs', 8),
      slowMarkdownRenders: sortByPayloadNumber(markdownEvents, 'commitMs', 8),
      longTasks: sortByPayloadNumber(longTaskEvents, 'durationMs', 8),
      layoutShifts: sortByPayloadNumber(layoutShiftEvents, 'value', 8),
      resources: sortResources(resourceEvents, 8),
      fetches: sortByPayloadNumber(fetchEvents, 'durationMs', 8),
      sseEvents: sseEvents.slice(-12).reverse(),
      replayEvents: replayEvents.slice(-12).reverse(),
      workflowSteps: sortByPayloadNumber(workflowSteps, 'durationMs', 12),
      errors: errorEvents.slice(-8).reverse(),
    },
    raw: {
      perfEvents: perfEvents.slice(-perfEventLimit),
      chatEvents: (options.rawEvents ?? []).slice(-rawEventLimit),
    },
  };
}

export function recordPerfEvent(
  name: string,
  payload?: Record<string, unknown>,
  options?: { throttleMs?: number },
): void {
  if (!isPerfDiagnosticsEnabled()) return;
  const now = performance.now();
  const throttleMs = options?.throttleMs ?? 0;
  if (throttleMs > 0) {
    const last = perfThrottle.get(name) ?? 0;
    if (now - last < throttleMs) return;
    perfThrottle.set(name, now);
  }

  const event: PuddingPerfEvent = { name, at: Math.round(now), payload };
  perfEvents.push(event);
  trimPerfEvents();
  if (localStorage.getItem('pudding_perf_console') === '1') {
    console.debug('[Pudding Perf]', name, payload ?? {});
  }
}

export function recordPerfStep(
  workflow: string,
  step: string,
  startedAt: number,
  payload: Record<string, unknown> = {},
): void {
  if (!isPerfDiagnosticsEnabled()) return;
  const durationMs = Math.max(0, Math.round(performance.now() - startedAt));
  const status = typeof payload.status === 'string' ? payload.status : 'ok';
  recordPerfEvent('chat.workflow.step', {
    ...payload,
    workflow,
    step,
    status,
    durationMs,
  });
}

export function markPerf(name: string): void {
  if (!isPerfDiagnosticsEnabled()) return;
  try {
    performance.mark(`pudding:${name}`);
  } catch {
    // performance.mark can fail in constrained test/browser contexts.
  }
}

export function measurePerf(name: string, startMark: string, endMark?: string): number | null {
  if (!isPerfDiagnosticsEnabled()) return null;
  try {
    const fullStart = `pudding:${startMark}`;
    const fullEnd = endMark ? `pudding:${endMark}` : undefined;
    const entryName = `pudding:${name}`;
    performance.measure(entryName, fullStart, fullEnd);
    const entries = performance.getEntriesByName(entryName, 'measure');
    const duration = entries.length > 0 ? entries[entries.length - 1].duration : null;
    if (duration != null) {
      recordPerfEvent(name, { durationMs: Math.round(duration) });
      return duration;
    }
  } catch {
    return null;
  }
  return null;
}

function describeLayoutShiftSource(source: unknown): string {
  const node = (source as { node?: unknown } | null | undefined)?.node;
  if (!(node instanceof Element)) return 'unknown';
  const id = node.id ? `#${node.id}` : '';
  const classes = typeof node.className === 'string' && node.className.trim()
    ? `.${node.className.trim().split(/\s+/).slice(0, 2).join('.')}`
    : '';
  const testId = node.getAttribute('data-testid');
  const ariaLabel = node.getAttribute('aria-label');
  const testIdSuffix = testId ? `[data-testid="${testId.slice(0, 40)}"]` : '';
  const ariaSuffix = ariaLabel ? `[aria-label="${ariaLabel.slice(0, 40)}"]` : '';
  return `${node.tagName.toLowerCase()}${id}${classes}${testIdSuffix}${ariaSuffix}`;
}

function installLongTaskObserver(): void {
  if (longTaskObserverInstalled || typeof PerformanceObserver === 'undefined') return;
  try {
    const observer = new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        recordPerfEvent('browser.longtask', {
          durationMs: Math.round(entry.duration),
          startTime: Math.round(entry.startTime),
        });
      }
    });
    observer.observe({ entryTypes: ['longtask'] });
    longTaskObserverInstalled = true;
  } catch {
    // Some browsers do not expose longtask entries. Other perf logs remain useful.
  }
}

function installLayoutShiftObserver(): void {
  if (layoutShiftObserverInstalled || typeof PerformanceObserver === 'undefined') return;
  try {
    const observer = new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        const shift = entry as PerformanceEntry & {
          value?: number;
          hadRecentInput?: boolean;
          sources?: unknown[];
        };
        if (typeof shift.value !== 'number' || shift.value <= 0) continue;
        recordPerfEvent('browser.layoutShift', {
          value: roundMetric(shift.value),
          hadRecentInput: Boolean(shift.hadRecentInput),
          startTime: Math.round(entry.startTime),
          sources: Array.isArray(shift.sources)
            ? shift.sources.slice(0, 3).map(describeLayoutShiftSource)
            : [],
        });
      }
    });
    observer.observe({ type: 'layout-shift', buffered: true } as PerformanceObserverInit);
    layoutShiftObserverInstalled = true;
  } catch {
    // Chromium-only metric. Ignore where unsupported.
  }
}

function isInterestingResource(entry: PerformanceEntry & Partial<PerformanceResourceTiming>): boolean {
  const name = entry.name || '';
  const size = Math.max(entry.decodedBodySize ?? 0, entry.transferSize ?? 0);
  if (size >= 32 * 1024 || entry.duration >= 250) return true;
  return /(?:\/assets\/|umi|vendor|chat|spritesheet|sprite|swagger|katex|prism|phaser)/i.test(name);
}

function recordResourceEntry(entry: PerformanceEntry & Partial<PerformanceResourceTiming>): void {
  if (!isInterestingResource(entry)) return;
  const resourceKey = `${entry.name}:${Math.round(entry.startTime)}`;
  if (recordedResourceNames.has(resourceKey)) return;
  recordedResourceNames.add(resourceKey);
  recordPerfEvent('browser.resource', {
    name: entry.name,
    initiatorType: entry.initiatorType,
    durationMs: Math.round(entry.duration),
    startTime: Math.round(entry.startTime),
    decodedBodySize: entry.decodedBodySize ?? 0,
    transferSize: entry.transferSize ?? 0,
  });
}

function installResourceObserver(): void {
  if (resourceObserverInstalled) return;
  try {
    for (const entry of performance.getEntriesByType('resource')) {
      recordResourceEntry(entry as PerformanceEntry & Partial<PerformanceResourceTiming>);
    }
  } catch {
    // Ignore resource snapshot failures.
  }
  if (typeof PerformanceObserver === 'undefined') return;
  try {
    const observer = new PerformanceObserver((list) => {
      for (const entry of list.getEntries()) {
        recordResourceEntry(entry as PerformanceEntry & Partial<PerformanceResourceTiming>);
      }
    });
    observer.observe({ type: 'resource', buffered: true } as PerformanceObserverInit);
    resourceObserverInstalled = true;
  } catch {
    // Resource timing observer may be unavailable in restricted browsers.
  }
}

function installBrowserEventDiagnostics(): void {
  recordPerfEvent('browser.environment', getBrowserEnvironmentSnapshot() as unknown as Record<string, unknown>);
  if (browserEventListenersInstalled) return;
  browserEventListenersInstalled = true;

  window.addEventListener('error', (event) => {
    const errorEvent = event as ErrorEvent;
    recordPerfEvent('browser.error', {
      message: truncate(errorEvent.message || 'unknown error', 500),
      filename: errorEvent.filename ? truncate(errorEvent.filename, 500) : undefined,
      lineno: errorEvent.lineno,
      colno: errorEvent.colno,
      ...describeThrowable(errorEvent.error),
    });
  }, true);

  window.addEventListener('unhandledrejection', (event) => {
    const reason = (event as PromiseRejectionEvent).reason;
    recordPerfEvent('browser.unhandledrejection', describeThrowable(reason));
  });

  document.addEventListener('visibilitychange', () => {
    recordPerfEvent('browser.visibility', {
      visibilityState: document.visibilityState,
      hasFocus: typeof document.hasFocus === 'function' ? document.hasFocus() : undefined,
    });
  });

  window.addEventListener('focus', () => {
    recordPerfEvent('browser.focus', { focused: true });
  });
  window.addEventListener('blur', () => {
    recordPerfEvent('browser.focus', { focused: false });
  });
  window.addEventListener('pagehide', () => {
    recordPerfEvent('browser.pagehide', getBrowserEnvironmentSnapshot() as unknown as Record<string, unknown>);
  });
  window.addEventListener('pageshow', () => {
    recordPerfEvent('browser.pageshow', getBrowserEnvironmentSnapshot() as unknown as Record<string, unknown>);
  });
  window.addEventListener('resize', () => {
    recordPerfEvent('browser.resize', {
      width: window.innerWidth,
      height: window.innerHeight,
      devicePixelRatio: window.devicePixelRatio || 1,
    }, { throttleMs: 1_000 });
  });
}

function installFetchDiagnostics(): void {
  const target = globalThis as typeof globalThis & {
    fetch?: typeof fetch & {
      [PUDDING_FETCH_WRAPPED]?: boolean;
    };
  };
  const currentFetch = target.fetch;
  if (typeof currentFetch !== 'function' || currentFetch[PUDDING_FETCH_WRAPPED]) return;

  const wrappedFetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const startedAt = performance.now();
    const request = describeFetchRequest(input, init);
    try {
      const response = await currentFetch(input, init);
      recordPerfEvent('browser.fetch', {
        ...request,
        status: response.status,
        ok: response.ok,
        contentType: response.headers?.get?.('content-type') ?? undefined,
        durationMs: Math.round(performance.now() - startedAt),
        startTime: Math.round(startedAt),
      });
      return response;
    } catch (error) {
      recordPerfEvent('browser.fetch', {
        ...request,
        ...describeThrowable(error),
        error: true,
        durationMs: Math.round(performance.now() - startedAt),
        startTime: Math.round(startedAt),
      });
      throw error;
    }
  }) as typeof fetch & { [PUDDING_FETCH_WRAPPED]?: boolean };

  wrappedFetch[PUDDING_FETCH_WRAPPED] = true;
  target.fetch = wrappedFetch;
}

export function installPerfDiagnostics(): void {
  if (!isPerfDiagnosticsEnabled()) return;
  const win = window as unknown as { __PUDDING_PERF__?: PuddingPerfApi };
  win.__PUDDING_PERF__ = {
    enabled: true,
    events: perfEvents,
    clear: clearPerfEvents,
    mark: markPerf,
    measure: measurePerf,
    summary: summarizePerfEvents,
    snapshot: buildPerfDiagnosticSnapshot,
  };

  installLongTaskObserver();
  installLayoutShiftObserver();
  installResourceObserver();
  installBrowserEventDiagnostics();
  installFetchDiagnostics();
  recordPerfEvent('diagnostics.enabled', { mode: 'chat' });
}

/** 写入 last session/message（仅 debug mode 下启用） */
export function writeDebugSessionState(sessionId: string, messageId: string): void {
  if (!isDebugMode()) return;
  sessionStorage.setItem('pudding_last_session_id', sessionId);
  sessionStorage.setItem('pudding_last_message_id', messageId);
  console.log('[Pudding Debug] Wrote session', sessionId, 'message', messageId);
}

/** 写入 last trace（仅 debug mode 下启用） */
export function writeDebugTrace(traceId: string): void {
  if (!isDebugMode()) return;
  sessionStorage.setItem('pudding_last_trace_id', traceId);
  console.log('[Pudding Debug] Wrote trace', traceId);
}

/** 注册 debug API 到 window.__PUDDING_DEBUG__ */
export function registerDebugApi(api: PuddingDebugApi): void {
  if (isDebugMode()) {
    (window as any).__PUDDING_DEBUG__ = api;
    console.log('[Pudding Debug] Debug mode enabled');
  }
}
