import type { TimelineItem } from '../types';

interface SanitizeProcessTextOptions {
  compact?: boolean;
  maxLength?: number;
}

export interface ProcessRound {
  id: string;
  index: number;
  items: TimelineItem[];
  thinkingItems: TimelineItem[];
  toolItems: TimelineItem[];
  otherItems: TimelineItem[];
  startedAt: number;
  endedAt: number;
}

export interface ProcessMetrics {
  thinkingRounds: number;
  thinkingSteps: number;
  toolCalls: number;
  toolResults: number;
  subAgentCalls: number;
  runningSubAgents: number;
  failedSubAgents: number;
  failedTools: number;
  failureBreakdown: ProcessFailureBreakdown;
  durationMs: number;
}

export interface CurrentRunActivity {
  kind: 'thinking' | 'tool' | 'subagent' | 'system';
  title: string;
  subject?: string;
  subjectFull?: string;
  status:
    | 'running'
    | 'waiting_output'
    | 'processing_result'
    | 'completed'
    | 'failed';
  startedAt?: number;
  updatedAt?: number;
  inputPreview?: string;
  inputFull?: string;
  outputPreview?: string;
  outputFull?: string;
  outputTruncated?: boolean;
}

export interface ProcessFailureBreakdown {
  approvalMissing: number;
  approvalMismatch: number;
  approvalScopeInvalid: number;
  approvalExpired: number;
  commandExit: number;
  cancelled: number;
  runtime: number;
}

export const sanitizeProcessText = (
  text?: unknown,
  options: SanitizeProcessTextOptions = {},
): string => {
  if (typeof text !== 'string') return '';
  const compact = options.compact ?? true;
  let cleaned = text
    .replace(/(?:undefined|null|NaN)+/gi, '')
    .split('\u0000')
    .join('')
    .replace(/[ \t]{2,}/g, ' ')
    .trim();

  cleaned = compact
    ? cleaned.replace(/\s+/g, ' ')
    : cleaned.replace(/\n{3,}/g, '\n\n');

  if (options.maxLength && cleaned.length > options.maxLength) {
    cleaned = `${cleaned.slice(0, options.maxLength).trim()}...`;
  }

  return /^[\s，,。.！!？?：:；;、·]+$/.test(cleaned) ? '' : cleaned;
};

const hasAny = (text: string, words: string[]) =>
  words.some((word) => text.toLowerCase().includes(word.toLowerCase()));

const tailForPreview = (text?: string, maxLength = 520) => {
  if (typeof text !== 'string') return '';
  return text.length > maxLength ? text.slice(text.length - maxLength) : text;
};

const compactSingleLine = (text?: string, maxLength = 160): string => {
  const safe = sanitizeProcessText(text, { maxLength });
  return safe;
};

const buildTailPreview = (
  text?: string,
  maxLines = 5,
  maxChars = 520,
): { preview: string; full: string; truncated: boolean } => {
  const full = sanitizeProcessText(text, { compact: false });
  if (!full) return { preview: '', full: '', truncated: false };
  const lines = full.split(/\r?\n/).filter((line) => line.trim().length > 0);
  const tailLines = lines
    .slice(-maxLines)
    .map((line) => sanitizeProcessText(line, { maxLength: 180 }));
  let preview = tailLines.join('\n');
  let truncated =
    lines.length > maxLines ||
    tailLines.some((line, index) => line !== lines.slice(-maxLines)[index]);
  if (preview.length > maxChars) {
    preview = preview.slice(preview.length - maxChars).trim();
    truncated = true;
  }
  if (truncated) {
    preview = `${preview}\n输出较长，已截取最近 ${maxLines} 行 · 查看过程`;
  }
  return { preview, full, truncated };
};

const tryParseObject = (text?: string): Record<string, unknown> | null => {
  const safe = sanitizeProcessText(text, { compact: false });
  if (!safe || !safe.startsWith('{')) return null;
  try {
    const parsed = JSON.parse(safe);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
};

const getStringField = (
  obj: Record<string, unknown> | null,
  names: string[],
): string => {
  if (!obj) return '';
  for (const name of names) {
    const value = obj[name];
    if (typeof value === 'string' && value.trim()) return value;
  }
  for (const value of Object.values(obj)) {
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      const nested = getStringField(value as Record<string, unknown>, names);
      if (nested) return nested;
    }
  }
  return '';
};

const formatActivityInput = (
  toolName: string,
  rawArguments?: string,
  fallbackMessage?: string,
): string => {
  const parsed = tryParseObject(rawArguments);
  const lowerName = toolName.toLowerCase();
  const task = getStringField(parsed, [
    'task',
    'prompt',
    'instruction',
    'instructions',
    'message',
  ]);
  if (task) return `任务：${compactSingleLine(task, 160)}`;

  const command = getStringField(parsed, ['command', 'cmd', 'script', 'shell']);
  if (
    command ||
    lowerName.includes('shell') ||
    lowerName.includes('terminal')
  ) {
    const raw =
      command ||
      sanitizeProcessText(rawArguments || fallbackMessage, {
        compact: false,
        maxLength: 260,
      });
    return raw ? `命令：${raw}` : '';
  }

  const query = getStringField(parsed, [
    'query',
    'pattern',
    'keyword',
    'keywords',
    'path',
  ]);
  if (query) return `查询：${compactSingleLine(query, 160)}`;

  const safeArgs = sanitizeProcessText(rawArguments, {
    compact: false,
    maxLength: 260,
  });
  if (!safeArgs)
    return sanitizeProcessText(fallbackMessage, {
      compact: false,
      maxLength: 220,
    });
  if (parsed) return '参数：已记录，点击“查看过程”查看完整参数';
  return `参数：${safeArgs}`;
};

const isMeaningfulThinkingText = (text: string): boolean => {
  const safe = sanitizeProcessText(text);
  if (!safe) return false;
  if (/^[a-z_.$-]{1,18}$/i.test(safe)) return false;
  return safe.length >= 8 || /[\u4e00-\u9fff]/.test(safe);
};

export const summarizeThinkingText = (text?: string): string => {
  const safe = sanitizeProcessText(text);
  if (!safe) return '正在整理上下文';

  const steps: string[] = [];
  const add = (label: string) => {
    if (!steps.includes(label)) steps.push(label);
  };

  if (hasAny(safe, ['用户', '请求', '要求', 'user', 'ask', 'wants']))
    add('理解用户意图');
  if (hasAny(safe, ['上下文', 'context', '约束', 'constraint']))
    add('整理上下文与约束');
  if (hasAny(safe, ['记忆', '检索', 'memory', 'recall', 'search']))
    add('检索相关记忆');
  if (hasAny(safe, ['工具', 'tool', 'function'])) add('判断工具需求');
  if (hasAny(safe, ['回复', '回答', 'reply', 'answer', 'format']))
    add('确认回答格式');
  if (hasAny(safe, ['生成', '输出', 'generate', 'compose']))
    add('准备生成回复');

  if (steps.length === 0) add('整理中间推理');
  return steps.slice(0, 3).join(' · ');
};

export const summarizeThinkingItems = (
  items?: TimelineItem[],
): string | null => {
  const lastThinking = [...(items ?? [])]
    .reverse()
    .find((item) => item.type === 'thinking');
  return lastThinking ? summarizeThinkingText(lastThinking.text) : null;
};

const isToolItem = (item: TimelineItem) =>
  item.type === 'tool_call' || item.type === 'tool_result';
const isSubAgentItem = (item: TimelineItem) =>
  item.type === 'subagent_spawned' ||
  item.type === 'subagent_progress' ||
  item.type === 'subagent_completed';

export const getToolDisplayName = (item: TimelineItem): string =>
  sanitizeProcessText(item.name, { maxLength: 40 }) ||
  sanitizeProcessText(item.status, { maxLength: 40 }) ||
  '工具';

export const getToolStatusTone = (
  item: TimelineItem,
): 'running' | 'success' | 'error' => {
  const s = sanitizeProcessText(item.status).toLowerCase();
  if (
    s.includes('error') ||
    s.includes('fail') ||
    s.includes('cancel') ||
    (item.type === 'tool_result' && item.exitCode !== 0)
  ) {
    return 'error';
  }
  if (
    s.includes('success') ||
    s.includes('done') ||
    s.includes('complete') ||
    (item.type === 'tool_result' && item.exitCode === 0)
  ) {
    return 'success';
  }
  return 'running';
};

const emptyFailureBreakdown = (): ProcessFailureBreakdown => ({
  approvalMissing: 0,
  approvalMismatch: 0,
  approvalScopeInvalid: 0,
  approvalExpired: 0,
  commandExit: 0,
  cancelled: 0,
  runtime: 0,
});

const getFailureText = (item: TimelineItem): string =>
  sanitizeProcessText(
    [item.status, item.message, item.output, item.arguments, item.name]
      .filter(Boolean)
      .join(' '),
    { maxLength: 2000 },
  ).toLowerCase();

const classifyToolFailure = (
  item: TimelineItem,
): keyof ProcessFailureBreakdown => {
  const text = getFailureText(item);
  if (text.includes('requested_scope') || text.includes('once, session, timed'))
    return 'approvalScopeInvalid';
  if (
    text.includes('does not match') ||
    text.includes('ticketmismatch') ||
    text.includes('approval_mismatch')
  )
    return 'approvalMismatch';
  if (
    text.includes('no matching automatic approval') ||
    text.includes('approval_missing')
  )
    return 'approvalMissing';
  if (
    text.includes('already consumed') ||
    text.includes('expired') ||
    text.includes('approval_expired')
  )
    return 'approvalExpired';
  if (text.includes('cancel')) return 'cancelled';
  if (item.exitCode !== undefined && item.exitCode !== 0) return 'commandExit';
  return 'runtime';
};

const getFailureBreakdown = (
  toolResults: TimelineItem[],
): ProcessFailureBreakdown => {
  const breakdown = emptyFailureBreakdown();
  for (const item of toolResults) {
    if (getToolStatusTone(item) !== 'error') continue;
    breakdown[classifyToolFailure(item)] += 1;
  }
  return breakdown;
};

export const formatProcessDuration = (durationMs: number): string | null => {
  if (!Number.isFinite(durationMs) || durationMs <= 0) return null;
  if (durationMs < 1000) return `${Math.max(1, Math.round(durationMs))} ms`;
  const seconds = Math.round(durationMs / 1000);
  if (seconds < 60) return `${seconds} 秒`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return rest > 0 ? `${minutes} 分 ${rest} 秒` : `${minutes} 分`;
};

export const buildProcessRounds = (items?: TimelineItem[]): ProcessRound[] => {
  const rounds: ProcessRound[] = [];
  let current: ProcessRound | null = null;
  let sawToolInCurrentRound = false;

  const createRound = (item: TimelineItem): ProcessRound => {
    const round: ProcessRound = {
      id: `round-${rounds.length + 1}-${item.id}`,
      index: rounds.length + 1,
      items: [],
      thinkingItems: [],
      toolItems: [],
      otherItems: [],
      startedAt: item.timestamp,
      endedAt: item.timestamp,
    };
    current = round;
    sawToolInCurrentRound = false;
    rounds.push(round);
    return round;
  };

  for (const item of items ?? []) {
    let round: ProcessRound | null = current;
    if (!round || (item.type === 'thinking' && sawToolInCurrentRound)) {
      round = createRound(item);
    }
    const activeRound = round;
    if (!activeRound) continue;

    activeRound.items.push(item);
    activeRound.startedAt = Math.min(activeRound.startedAt, item.timestamp);
    activeRound.endedAt = Math.max(activeRound.endedAt, item.timestamp);

    if (item.type === 'thinking') {
      activeRound.thinkingItems.push(item);
    } else if (isToolItem(item)) {
      activeRound.toolItems.push(item);
      sawToolInCurrentRound = true;
    } else {
      activeRound.otherItems.push(item);
    }
  }

  return rounds;
};

export const getProcessMetrics = (items?: TimelineItem[]): ProcessMetrics => {
  const safeItems = items ?? [];
  const timestamps = safeItems
    .map((item) => item.timestamp)
    .filter(Number.isFinite);
  const rounds = buildProcessRounds(safeItems);
  const toolResults = safeItems.filter((item) => item.type === 'tool_result');
  const subAgentItems = safeItems.filter(isSubAgentItem);
  const failureBreakdown = getFailureBreakdown(toolResults);
  return {
    thinkingRounds: rounds.filter((round) => round.thinkingItems.length > 0)
      .length,
    thinkingSteps: safeItems.filter((item) => item.type === 'thinking').length,
    toolCalls: safeItems.filter((item) => item.type === 'tool_call').length,
    toolResults: toolResults.length,
    subAgentCalls: safeItems.filter((item) => item.type === 'subagent_spawned')
      .length,
    runningSubAgents: subAgentItems.filter(
      (item) => item.type !== 'subagent_completed',
    ).length,
    failedSubAgents: subAgentItems.filter(
      (item) =>
        item.type === 'subagent_completed' &&
        getToolStatusTone(item) === 'error',
    ).length,
    failedTools: toolResults.filter(
      (item) => getToolStatusTone(item) === 'error',
    ).length,
    failureBreakdown,
    durationMs:
      timestamps.length > 1
        ? Math.max(...timestamps) - Math.min(...timestamps)
        : 0,
  };
};

const getFailureBreakdownSummary = (
  breakdown: ProcessFailureBreakdown,
): string | null => {
  const parts = [
    ['审批缺失', breakdown.approvalMissing],
    ['审批不匹配', breakdown.approvalMismatch],
    ['审批范围错误', breakdown.approvalScopeInvalid],
    ['审批过期', breakdown.approvalExpired],
    ['命令失败', breakdown.commandExit],
    ['已取消', breakdown.cancelled],
    ['运行异常', breakdown.runtime],
  ]
    .filter(([, count]) => Number(count) > 0)
    .map(([label, count]) => `${label} ${count}`);
  return parts.length > 0 ? parts.join(' / ') : null;
};

export const getProcessSummaryText = (
  items?: TimelineItem[],
): string | null => {
  if (!items?.length) return null;
  const metrics = getProcessMetrics(items);
  const parts: string[] = [];
  if (metrics.thinkingRounds > 0)
    parts.push(`已思考 ${metrics.thinkingRounds} 轮`);
  if (metrics.toolCalls > 0) parts.push(`调用 ${metrics.toolCalls} 个工具`);
  if (metrics.subAgentCalls > 0)
    parts.push(`派生 ${metrics.subAgentCalls} 个子代理`);
  if (metrics.failedTools > 0) {
    const failureSummary = getFailureBreakdownSummary(metrics.failureBreakdown);
    parts.push(
      failureSummary
        ? `${metrics.failedTools} 个失败（${failureSummary}）`
        : `${metrics.failedTools} 个失败`,
    );
  }
  if (metrics.failedSubAgents > 0)
    parts.push(`${metrics.failedSubAgents} 个子代理失败`);
  const duration = formatProcessDuration(metrics.durationMs);
  if (duration) parts.push(`用时 ${duration}`);
  if (parts.length === 0 && metrics.thinkingSteps > 0)
    parts.push(`已思考 ${metrics.thinkingSteps} 步`);
  return parts.length > 0 ? parts.join(' · ') : '已完成';
};

export const getThinkingRawText = (items?: TimelineItem[]): string => {
  const rawText = (items ?? [])
    .filter((item) => item.type === 'thinking')
    .map((item) => (typeof item.text === 'string' ? item.text : ''))
    .join('');
  return sanitizeProcessText(rawText, { compact: false });
};

export const getTimelinePreview = (items?: TimelineItem[]): string | null => {
  const last = [...(items ?? [])].reverse().find((item) => {
    if (item.type === 'thinking')
      return Boolean(sanitizeProcessText(item.text));
    if (isToolItem(item)) return true;
    return Boolean(sanitizeProcessText(item.message));
  });
  if (!last) return null;

  if (last.type === 'thinking') {
    const previewText = tailForPreview(last.text);
    return (
      sanitizeProcessText(previewText, { maxLength: 520 }) ||
      summarizeThinkingText(previewText)
    );
  }
  if (last.type === 'tool_call') {
    return `正在调用工具：${getToolDisplayName(last)}`;
  }
  if (last.type === 'tool_result') {
    const tone = getToolStatusTone(last);
    return tone === 'error'
      ? `工具返回异常：${getToolDisplayName(last)}`
      : `已获得工具结果：${getToolDisplayName(last)}`;
  }
  if (isSubAgentItem(last)) {
    const tone = getToolStatusTone(last);
    const name = getToolDisplayName(last);
    if (last.type === 'subagent_completed') {
      return tone === 'error'
        ? `子代理返回异常：${name}`
        : `子代理已完成：${name}`;
    }
    return `子代理运行中：${name}`;
  }
  return sanitizeProcessText(tailForPreview(last.message, 320), {
    maxLength: 320,
  });
};

const latestByTimestamp = (items: TimelineItem[]) =>
  [...items].sort((a, b) => b.timestamp - a.timestamp)[0];

/**
 * 当前活动区只呈现运行时真实事件，不从前端编造“理解任务/规划路径”一类心智阶段。
 * 完整累计过程仍由 MessageProcessSummary 展开区承载；这里仅用于回答“现在实际在和什么交互”。
 */
export const getCurrentRunActivity = (
  items?: TimelineItem[],
  status?: string,
): CurrentRunActivity | null => {
  const safeItems = items ?? [];
  const latestActiveToolCall = latestByTimestamp(
    safeItems.filter((item) => item.type === 'tool_call'),
  );
  const latestToolResult = latestByTimestamp(
    safeItems.filter((item) => item.type === 'tool_result'),
  );
  const latestSubAgent = latestByTimestamp(safeItems.filter(isSubAgentItem));
  const latestThinking = latestByTimestamp(
    safeItems.filter(
      (item) =>
        item.type === 'thinking' && Boolean(sanitizeProcessText(item.text)),
    ),
  );
  const latestOther = latestByTimestamp(
    safeItems.filter(
      (item) =>
        item.type === 'subconscious_step' &&
        Boolean(sanitizeProcessText(item.message || item.text)),
    ),
  );

  const latestToolAt = Math.max(
    latestActiveToolCall?.timestamp ?? 0,
    latestToolResult?.timestamp ?? 0,
  );
  const candidates = [
    latestSubAgent
      ? {
          item: latestSubAgent,
          group: 'subagent' as const,
          timestamp: latestSubAgent.timestamp,
        }
      : null,
    latestToolAt > 0
      ? {
          item:
            latestToolResult &&
            latestToolResult.timestamp >= (latestActiveToolCall?.timestamp ?? 0)
              ? latestToolResult
              : latestActiveToolCall!,
          group: 'tool' as const,
          timestamp: latestToolAt,
        }
      : null,
    latestThinking
      ? {
          item: latestThinking,
          group: 'thinking' as const,
          timestamp: latestThinking.timestamp,
        }
      : null,
    latestOther
      ? {
          item: latestOther,
          group: 'system' as const,
          timestamp: latestOther.timestamp,
        }
      : null,
  ].filter(Boolean) as Array<{
    item: TimelineItem;
    group: CurrentRunActivity['kind'];
    timestamp: number;
  }>;

  const current = candidates.sort((a, b) => b.timestamp - a.timestamp)[0];
  if (!current) return null;

  if (current.group === 'tool') {
    const item = current.item;
    const name = getToolDisplayName(item);
    if (item.type === 'tool_result') {
      const tone = getToolStatusTone(item);
      const activityStatus =
        tone === 'error'
          ? 'failed'
          : tone === 'success'
            ? 'completed'
            : status === 'success'
              ? 'completed'
              : 'processing_result';
      const output = buildTailPreview(item.output || item.message);
      return {
        kind: 'tool',
        title:
          activityStatus === 'failed'
            ? `工具调用失败：${name}`
            : activityStatus === 'completed'
              ? `工具调用完成：${name}`
              : `正在处理工具结果：${name}`,
        status: activityStatus,
        startedAt: latestActiveToolCall?.timestamp ?? item.timestamp,
        updatedAt: item.timestamp,
        outputPreview: output.preview,
        outputFull: output.full,
        outputTruncated: output.truncated,
      };
    }
    const inputFull = sanitizeProcessText(item.arguments || item.message, {
      compact: false,
    });
    return {
      kind: 'tool',
      title: `正在调用工具：${name}`,
      status: 'running',
      startedAt: item.timestamp,
      updatedAt: item.timestamp,
      subject: formatActivityInput(name, item.arguments, item.message),
      subjectFull: inputFull,
    };
  }

  if (current.group === 'subagent') {
    const item = current.item;
    const name = getToolDisplayName(item);
    const tone = getToolStatusTone(item);
    const completed = item.type === 'subagent_completed';
    const activityStatus = completed
      ? tone === 'error'
        ? 'failed'
        : tone === 'success' || status === 'success'
          ? 'completed'
          : 'processing_result'
      : 'running';
    const taskFull = sanitizeProcessText(item.arguments, { compact: false });
    const output = buildTailPreview(item.output);
    return {
      kind: 'subagent',
      title:
        activityStatus === 'failed'
          ? `子代理失败：${name}`
          : activityStatus === 'completed'
            ? `子代理已完成：${name}`
            : completed
              ? `正在处理子代理结果：${name}`
              : `子代理运行中：${name}`,
      subject: taskFull
        ? `任务：${compactSingleLine(taskFull, 160)}`
        : undefined,
      subjectFull: taskFull,
      status: activityStatus,
      startedAt: item.timestamp,
      updatedAt: item.timestamp,
      outputPreview: output.preview,
      outputFull: output.full,
      outputTruncated: output.truncated,
    };
  }

  if (current.group === 'thinking') {
    const text = sanitizeProcessText(tailForPreview(current.item.text), {
      maxLength: 360,
    });
    return {
      kind: 'thinking',
      title: '模型过程',
      status: 'running',
      startedAt: current.item.timestamp,
      updatedAt: current.item.timestamp,
      outputPreview: isMeaningfulThinkingText(text) ? text : undefined,
      outputFull: isMeaningfulThinkingText(text)
        ? sanitizeProcessText(current.item.text, { compact: false })
        : undefined,
    };
  }

  return {
    kind: 'system',
    title:
      sanitizeProcessText(current.item.status, { maxLength: 80 }) ||
      '正在处理运行事件',
    status: getToolStatusTone(current.item) === 'error' ? 'failed' : 'running',
    startedAt: current.item.timestamp,
    updatedAt: current.item.timestamp,
    outputPreview: sanitizeProcessText(
      current.item.message || current.item.text,
      { compact: false, maxLength: 360 },
    ),
  };
};
