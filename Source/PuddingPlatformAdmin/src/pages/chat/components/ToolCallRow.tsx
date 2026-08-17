// ── ToolCallRow：工具调用行（P1-1，对齐 deepseek-harness D5 ToolRow）──────────
// 单行 24px 摘要（StateDot + 工具名 + 2×2 分隔点 + summary FILL）+ 整行展开 IN/OUT 卡。
// 数据源：processItems: TimelineItem[]（types.ts）。call/result 仅按 canonical
// toolCallId 精确配对（乱序到达也正确）；缺失或不相等时保持未配对状态。
//   thinking / subagent_* 条目不进本组件（由 MessageProcessSummary 时间线呈现，共存）。
// 简化（RISKS）：无 subCalls 递归、无 filePath 宿主链接、无 Inspect pill。
import React, { useMemo, useState } from 'react';
import { useToolCallStyles } from '../styles/toolcall.styles';
import type { TimelineItem } from '../types';
import { summarizeError } from '../utils/summarizeError';
import { getToolDisplayName, sanitizeProcessText } from './processPreview';
import StateDot from './StateDot';

export type ToolCallRowStatus = 'running' | 'done' | 'error';

export interface ToolCallRowData {
  /** tool_call 条目 id（行 key） */
  id: string;
  call: TimelineItem;
  /** 配对的 tool_result；未配对 = running */
  result?: TimelineItem;
  status: ToolCallRowStatus;
  name: string;
  /** 折叠态单行摘要 */
  summary: string;
  /** 摘要全量原文（title 悬浮） */
  summaryFull: string;
}

/** 单行截断摘要（空白压缩 + 超长追加 …） */
const truncateSingleLine = (text: string, max = 140): string => {
  const clean = text.replace(/\s+/g, ' ').trim();
  if (!clean) return '';
  return clean.length > max ? `${clean.slice(0, max)}…` : clean;
};

/** 首个非空行（output/message 摘要用） */
const firstNonEmptyLine = (text?: string): string => {
  const safe = sanitizeProcessText(text, { compact: false });
  if (!safe) return '';
  return (
    safe
      .split(/\r?\n/)
      .map((line) => line.trim())
      .find((line) => line.length > 0) ?? ''
  );
};

const tryParseObject = (text?: string): Record<string, unknown> | null => {
  const safe = sanitizeProcessText(text, { compact: false });
  if (!safe?.startsWith('{')) return null;
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

/**
 * 参数摘要（简化 presenter，对齐 D5 5.3；与 processPreview.formatActivityInput 同规则但省略前缀标签）：
 * 任务/指令、命令/脚本、查询/路径 → 取对应字段原文；无法结构化时安全截断原文。
 * 摘要绝不把原始 JSON 字段名带进默认面板。
 */
const summarizeArguments = (
  call: TimelineItem,
  fallback: string,
): { summary: string; summaryFull: string } => {
  const parsed = tryParseObject(call.arguments);
  const lowerName = (call.name ?? '').toLowerCase();
  const task = getStringField(parsed, [
    'task',
    'prompt',
    'instruction',
    'instructions',
    'message',
  ]);
  if (task) {
    return { summary: truncateSingleLine(task), summaryFull: task };
  }
  const command = getStringField(parsed, ['command', 'cmd', 'script', 'shell']);
  if (
    command ||
    lowerName.includes('shell') ||
    lowerName.includes('terminal')
  ) {
    const raw =
      command ||
      sanitizeProcessText(call.arguments, { compact: false, maxLength: 260 });
    return { summary: truncateSingleLine(raw), summaryFull: raw };
  }
  const query = getStringField(parsed, [
    'query',
    'pattern',
    'keyword',
    'keywords',
    'path',
  ]);
  if (query) {
    return { summary: truncateSingleLine(query), summaryFull: query };
  }
  const safe = sanitizeProcessText(call.arguments, { compact: false });
  if (parsed) {
    return { summary: '参数已记录', summaryFull: '' };
  }
  return { summary: truncateSingleLine(safe) || fallback, summaryFull: safe };
};

const buildSummary = (
  call: TimelineItem,
  result: TimelineItem | undefined,
  status: ToolCallRowStatus,
): { summary: string; summaryFull: string } => {
  if (status === 'running') {
    return summarizeArguments(call, '执行中');
  }
  if (status === 'error') {
    const raw =
      result?.output || result?.message || call.status || call.message;
    const err = summarizeError(firstNonEmptyLine(raw) || raw);
    return {
      summary: err.summary || '执行失败',
      summaryFull: sanitizeProcessText(raw, { compact: false }),
    };
  }
  const output = sanitizeProcessText(result?.output, { compact: false });
  const outputLines = output
    ? output.split(/\r?\n/).filter((line) => line.trim().length > 0)
    : [];
  // 单行输出：直接以输出首行（即全文）为摘要；
  // 多行输出：首行不进默认面板（对齐既有 tail-preview UX 与 D5 presenter），回退参数摘要。
  if (outputLines.length === 1) {
    return { summary: truncateSingleLine(outputLines[0]), summaryFull: output };
  }
  return summarizeArguments(call, '已完成');
};

/**
 * 行状态：未配对 = running；已配对 = error（显式非零 exitCode 或状态文案含 error/fail/cancel）否则 done。
 * 注意：与 processPreview.getToolStatusTone 不同，undefined exitCode 不视为错误
 * （tool_result.exitCode 为可选字段，避免无 exitCode 的合法结果被误判失败）。
 */
const resolveStatus = (result: TimelineItem | undefined): ToolCallRowStatus => {
  if (!result) return 'running';
  const s = sanitizeProcessText(result.status).toLowerCase();
  if (
    s.includes('error') ||
    s.includes('fail') ||
    s.includes('cancel') ||
    (typeof result.exitCode === 'number' && result.exitCode !== 0)
  ) {
    return 'error';
  }
  return 'done';
};

/**
 * 配对 tool_call → tool_result 并生成行数据（纯函数，可单测）。
 * - call 与 result 均携带 toolCallId 时按 id 精确配对（乱序到达也正确）；
 * - 不按名称或顺序猜测，缺失 id 与不同 id 均不配对；
 * - 未配对 tool_call = running；孤儿 tool_result 不进行（时间线仍呈现）。
 */
export const buildToolCallRows = (items: TimelineItem[]): ToolCallRowData[] => {
  const calls: TimelineItem[] = [];
  const results: TimelineItem[] = [];
  for (const item of items) {
    if (item.type === 'tool_call') calls.push(item);
    else if (item.type === 'tool_result') results.push(item);
  }
  const used = new Set<string>();

  const takeById = (call: TimelineItem): TimelineItem | undefined => {
    if (!call.toolCallId) return undefined;
    return results.find(
      (result) => !used.has(result.id) && result.toolCallId === call.toolCallId,
    );
  };

  return calls.map((call) => {
    const result = takeById(call);
    if (result) used.add(result.id);

    const status: ToolCallRowStatus = resolveStatus(result);
    const { summary, summaryFull } = buildSummary(call, result, status);
    return {
      id: call.id,
      call,
      result,
      status,
      name: getToolDisplayName(call),
      summary,
      summaryFull,
    };
  });
};

interface OutBody {
  full: string;
  firstLine: string;
  rest: string;
}

/** OUT 卡内容：output / message / exit code；error 时拆出首行（红） */
const buildOutBody = (row: ToolCallRowData): OutBody => {
  const rawOutput = sanitizeProcessText(row.result?.output, {
    compact: false,
  });
  const rawMessage = sanitizeProcessText(row.result?.message, {
    compact: false,
  });
  const parts: string[] = [];
  if (rawOutput) parts.push(rawOutput);
  if (rawMessage && rawMessage !== rawOutput) parts.push(rawMessage);
  const exitCode = row.result?.exitCode;
  if (typeof exitCode === 'number') parts.push(`exit code: ${exitCode}`);
  const full = parts.join('\n\n');
  if (row.status !== 'error') return { full, firstLine: '', rest: '' };
  const lines = full.split(/\r?\n/);
  const firstIdx = lines.findIndex((line) => line.trim().length > 0);
  if (firstIdx === -1) return { full, firstLine: '', rest: '' };
  const firstLine = lines[firstIdx];
  const rest = lines
    .slice(firstIdx + 1)
    .join('\n')
    .trim();
  return { full, firstLine, rest };
};

interface ToolCallRowProps {
  row: ToolCallRowData;
}

/** 单个工具调用行：单行摘要 + 整行展开（IN/OUT 卡） */
const ToolCallRow: React.FC<ToolCallRowProps> = ({ row }) => {
  const { styles, cx } = useToolCallStyles();
  const [expanded, setExpanded] = useState(false);
  const toggle = () => setExpanded((value) => !value);

  const dotState: 'ongoing' | 'error' | 'done' =
    row.status === 'running'
      ? 'ongoing'
      : row.status === 'error'
        ? 'error'
        : 'done';
  const inText = sanitizeProcessText(row.call.arguments, { compact: false });
  const outBody = buildOutBody(row);
  const showOut = outBody.full.length > 0;

  return (
    <>
      {/* biome-ignore lint/a11y/useSemanticElements: DisclosureRow 整行可点（D5 范式）需 div role=button + Enter/Space 键盘 + aria-expanded，原生 button 与 sweep/hover 布局及测试契约冲突 */}
      <div
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        aria-label={`${row.name} 工具调用`}
        data-testid="toolcall-row"
        data-status={row.status}
        data-toolname={row.name}
        className={cx(
          styles.row,
          row.status === 'running' && styles.rowRunning,
        )}
        onClick={toggle}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            toggle();
          }
        }}
      >
        <span className={styles.leading}>
          <StateDot state={dotState} size={10} />
        </span>
        <span className={styles.title} data-testid="toolcall-title">
          {row.name}
        </span>
        <span className={styles.dotGrid} aria-hidden="true">
          <span className={styles.dot} />
          <span className={styles.dot} />
          <span className={styles.dot} />
          <span className={styles.dot} />
        </span>
        <span
          className={cx(
            styles.summary,
            row.status === 'error' && styles.summaryError,
          )}
          data-testid="toolcall-summary"
          title={row.summaryFull || undefined}
        >
          {row.summary}
        </span>
        <span
          className={cx(styles.chevron, expanded && styles.chevronOpen)}
          aria-hidden="true"
        >
          ▾
        </span>
      </div>
      {expanded && (inText || showOut) && (
        <div className={styles.expanded} data-testid="toolcall-expanded">
          {inText && (
            <div className={styles.card} data-testid="toolcall-in">
              <div className={styles.cardLabel} data-testid="toolcall-in-label">
                IN
              </div>
              <pre className={styles.cardPre}>{inText}</pre>
            </div>
          )}
          {showOut && (
            <div className={styles.card} data-testid="toolcall-out">
              <div
                className={cx(
                  styles.cardLabel,
                  row.status === 'error' && styles.cardLabelError,
                )}
                data-testid="toolcall-out-label"
              >
                OUT
              </div>
              <pre className={styles.cardPre}>
                {row.status === 'error' && outBody.firstLine ? (
                  <>
                    <span
                      className={styles.errorText}
                      data-testid="toolcall-out-error-line"
                    >
                      {outBody.firstLine}
                    </span>
                    {outBody.rest && `\n${outBody.rest}`}
                  </>
                ) : (
                  outBody.full
                )}
              </pre>
            </div>
          )}
        </div>
      )}
    </>
  );
};

interface ToolCallRowListProps {
  items?: TimelineItem[];
}

/**
 * 工具调用行列表：processItems 中仅 tool_call 条目生成行（配对 tool_result）；
 * 无 tool_call 时返回 null（thinking/subagent_* 由 MessageProcessSummary 呈现）。
 */
export const ToolCallRowList: React.FC<ToolCallRowListProps> = ({ items }) => {
  const { styles } = useToolCallStyles();
  const rows = useMemo(() => buildToolCallRows(items ?? []), [items]);
  if (rows.length === 0) return null;
  return (
    <div className={styles.list} data-testid="toolcall-list">
      {rows.map((row) => (
        <ToolCallRow key={row.id} row={row} />
      ))}
    </div>
  );
};

export default ToolCallRowList;
