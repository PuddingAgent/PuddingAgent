// ── ToolCallRow：工具调用行（CU-07，基于 ToolNode，迁移自 P1-1 TimelineItem 版）──
// 单行 32px 摘要（StateDot + 工具名 + 2×2 分隔点 + summary FILL）+ 整行展开 IN/OUT 卡。
// 数据源：ExecutionFlowNode kind='tool' → ToolNode（executionFlowProjector.ts）。
//  - call/result 已在投影器内按 canonical toolCallId 精确配对（乱序也正确）；
//  - state 三态：running / completed / failed（投影器终态单调守卫保障）。
// 复用共享 chrome ExecutionDisclosureRow（CU-05 §5.1 + §6.1）：leading 16px 槽 /
// chevron 占位稳定 / 整行可点（≥32px）/ Enter+Space 键盘展开 / 32px 缩进展开体。
// 保留旧实现算法：summarizeArguments / truncateSingleLine / firstNonEmptyLine /
// tryParseObject / getStringField / buildOutBody（迁移自 components/ToolCallRow.tsx）。
// 摘要映射对齐 G1/G2 服务端契约：ToolNode.presentation.kind（八类 intent 词表）。
import React, { useState } from 'react';
import type { ToolNode } from '../../projections/executionFlowProjector';
import type { ToolPresentationKind } from '@/services/platform/api';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import { useToolCallStyles } from '../../styles/toolcall.styles';
import { sanitizeProcessText } from '../processPreview';
import { summarizeError } from '../../utils/summarizeError';
import { formatDurationMs } from '../../utils/formatDuration';
import {
  getPresentationKind,
  resolveRenderer,
} from '../../presentation/PresentationRegistry';
import StateDot from '../StateDot';
import { ExecutionDisclosureRow } from './ExecutionDisclosureRow';

export type ToolCallRowStatus = 'running' | 'done' | 'error';

/** ToolNode.state → 行状态（验收 1 四态映射：running→running；completed→done；failed→error）。 */
export const mapToolStateToRowStatus = (
  state: ToolNode['state'],
): ToolCallRowStatus => {
  if (state === 'running') return 'running';
  if (state === 'failed') return 'error';
  return 'done';
};

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
 * presentation.kind → 优先提取的参数字段名（G1/G2 服务端表对齐；缺失回落 generic）。
 * kind 词表见 services/platform/api.ts ToolPresentationKind（八类 intent）。
 */
const PRESENTATION_ARGUMENT_FIELDS: Partial<
  Record<ToolPresentationKind, string[]>
> = {
  terminal: ['command', 'cmd', 'script', 'shell'],
  search: ['query', 'pattern', 'keyword', 'keywords', 'path'],
  read: ['path', 'file', 'filePath', 'filename'],
  diff: ['path', 'file', 'filePath'],
  web: ['url', 'uri', 'link', 'target'],
  delegation: ['task', 'prompt', 'instruction', 'message'],
  job: ['task', 'command', 'cmd', 'script'],
  generic: ['task', 'prompt', 'instruction', 'message'],
};

/** presentation.kind → 工具名覆盖（无覆盖时用 node.name）。 */
const PRESENTATION_LABELS: Partial<Record<ToolPresentationKind, string>> = {
  delegation: '委派子代理',
  web: '访问网页',
  search: '搜索',
  terminal: '终端',
  read: '读取文件',
  diff: '代码变更',
  job: '任务',
};

/**
 * 参数摘要（简化 presenter，对齐 D5 5.3；与 processPreview.formatActivityInput 同规则但省略前缀标签）：
 * 按 presentation.kind 优先取对应字段原文；无法结构化时安全截断原文。
 * 摘要绝不把原始 JSON 字段名带进默认面板。
 */
const summarizeArguments = (
  node: ToolNode,
  fallback: string,
): { summary: string; summaryFull: string } => {
  const parsed = tryParseObject(node.arguments);
  const lowerName = (node.name ?? '').toLowerCase();
  const kind = node.presentation?.kind ?? 'generic';
    const preferredFields = PRESENTATION_ARGUMENT_FIELDS[kind] ?? [];
  const effectiveFields =
    preferredFields.length > 0
      ? preferredFields
      : (PRESENTATION_ARGUMENT_FIELDS.generic ?? []);

  const task = getStringField(parsed, effectiveFields);
  if (task) {
    return { summary: truncateSingleLine(task), summaryFull: task };
  }
  const command = getStringField(parsed, [
    'command',
    'cmd',
    'script',
    'shell',
  ]);
  if (
    command ||
    lowerName.includes('shell') ||
    lowerName.includes('terminal')
  ) {
    const raw =
      command ||
      sanitizeProcessText(node.arguments, { compact: false, maxLength: 260 });
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
  const safe = sanitizeProcessText(node.arguments, { compact: false });
  if (parsed) {
    return { summary: '参数已记录', summaryFull: '' };
  }
  return { summary: truncateSingleLine(safe) || fallback, summaryFull: safe };
};

const buildSummary = (
  node: ToolNode,
  status: ToolCallRowStatus,
): { summary: string; summaryFull: string } => {
  if (status === 'running') {
    return summarizeArguments(node, '执行中');
  }
  if (status === 'error') {
    const raw = node.error || node.output;
    const err = summarizeError(firstNonEmptyLine(raw) || raw);
    return {
      summary: err.summary || '执行失败',
      summaryFull: sanitizeProcessText(raw, { compact: false }),
    };
  }
  const output = sanitizeProcessText(node.output, { compact: false });
  const outputLines = output
    ? output.split(/\r?\n/).filter((line) => line.trim().length > 0)
    : [];
  // 单行输出：直接以输出首行（即全文）为摘要；
  // 多行输出：首行不进默认面板（对齐既有 tail-preview UX 与 D5 presenter），回退参数摘要。
  if (outputLines.length === 1) {
    return { summary: truncateSingleLine(outputLines[0]), summaryFull: output };
  }
  return summarizeArguments(node, '已完成');
};

/** 超长输出 preview 阈值（验收 4：超长默认 DOM 仅 preview，禁全量挂历史消息 DOM）。 */
const OUT_PREVIEW_THRESHOLD = 2000;
const OUT_PREVIEW_HEAD = 400;
const OUT_PREVIEW_TAIL = 400;

/** 截断为「头 + 省略标记 + 尾」预览（保留可读头尾，中间省略）。 */
const buildPreview = (text: string): string => {
  if (text.length <= OUT_PREVIEW_THRESHOLD) return text;
  const head = text.slice(0, OUT_PREVIEW_HEAD);
  const tail = text.slice(
    Math.max(OUT_PREVIEW_HEAD, text.length - OUT_PREVIEW_TAIL),
  );
  return `${head}\n…（输出过长，已折叠）\n${tail}`;
};

interface OutBody {
  full: string;
  firstLine: string;
  rest: string;
  /** 超长时的头尾预览；未超长时 == full。 */
  preview: string;
  /** 是否超过阈值（决定默认渲染 preview 而非 full）。 */
  isLong: boolean;
}

/** OUT 卡内容：output / error / exit code / duration；error 时拆出首行（红） */
const buildOutBody = (node: ToolNode, status: ToolCallRowStatus): OutBody => {
  const rawOutput = sanitizeProcessText(node.output, { compact: false });
  const rawError = sanitizeProcessText(node.error, { compact: false });
  const parts: string[] = [];
  if (rawOutput) parts.push(rawOutput);
  if (rawError && rawError !== rawOutput) parts.push(rawError);
  const exitCode = node.exitCode;
  if (typeof exitCode === 'number') parts.push(`exit code: ${exitCode}`);
  if (typeof node.durationMs === 'number' && node.durationMs >= 0) {
    parts.push(`duration: ${node.durationMs}ms`);
  }
  const full = parts.join('\n\n');
  const isLong = full.length > OUT_PREVIEW_THRESHOLD;
  const preview = isLong ? buildPreview(full) : full;
  if (status !== 'error') return { full, firstLine: '', rest: '', preview, isLong };
  const lines = full.split(/\r?\n/);
  const firstIdx = lines.findIndex((line) => line.trim().length > 0);
  if (firstIdx === -1) return { full, firstLine: '', rest: '', preview, isLong };
  const firstLine = lines[firstIdx];
  const rest = lines
    .slice(firstIdx + 1)
    .join('\n')
    .trim();
  return { full, firstLine, rest, preview, isLong };
};

export interface ToolCallRowProps {
  /** 单个工具节点（投影器已配对 + 建树后的 ToolNode）。 */
  node: ToolNode;
  /** 受控展开（TurnContentStream 注册表）；未传时内部自管。 */
  expanded?: boolean;
  onExpandedChange?: (expanded: boolean) => void;
}

/** 单个工具调用行：单行摘要 + 整行展开（IN/OUT 卡 + presentation renderer 分派）。 */
export const ToolCallRow: React.FC<ToolCallRowProps> = ({
  node,
  expanded,
  onExpandedChange,
}) => {
  const { styles, cx } = useToolCallStyles();
  const { styles: flowStyles, cx: flowCx } = useExecutionFlowStyles();
  const [showFullOut, setShowFullOut] = useState(false);
  const status = mapToolStateToRowStatus(node.state);

  const dotState: 'ongoing' | 'error' | 'done' =
    status === 'running' ? 'ongoing' : status === 'error' ? 'error' : 'done';
  // CU-10：卡片挂载必须走 resolveRenderer 分派路径（按 presentation.kind，
  // 禁止按 toolName 分支；未注册七类暂回落 Generic，分派保持活跃）。
  const presentationKind = getPresentationKind(node.presentation);
  const PresentationCard = resolveRenderer(presentationKind);
  const kind = node.presentation?.kind;
  const name =
    (kind ? PRESENTATION_LABELS[kind] : undefined) ||
    sanitizeProcessText(node.name, { maxLength: 40 }) ||
    '工具调用';
  const { summary, summaryFull } = buildSummary(node, status);
  const inText = sanitizeProcessText(node.arguments, { compact: false });
  const outBody = buildOutBody(node, status);
  const showOut = outBody.full.length > 0;
  // 完成态计量（§3.3）：耗时 + 非零 exit code 上折叠行尾部（caption 灰 tabular-nums，
  // error 时 exit code 染红）；running 无 durationMs 自然不渲染，不伪造。
  const durationText = status === 'running' ? null : formatDurationMs(node.durationMs);
  const exitCodeText =
    status === 'error' &&
    typeof node.exitCode === 'number' &&
    node.exitCode !== 0
      ? `exit ${node.exitCode}`
      : null;

  return (
    <ExecutionDisclosureRow
      leading={<StateDot state={dotState} size={10} />}
      testId="toolcall-row"
      // 无障碍（2026-08-24 验收 6）：StateDot 是装饰性 aria-hidden，状态
      // 语义必须进可达名称，不能只靠颜色区分。
      ariaLabel={`${name} 工具调用（${
        status === 'running' ? '执行中' : status === 'error' ? '失败' : '成功'
      }）`}
      className={cx(status === 'running' && styles.rowRunning)}
      expanded={expanded}
      onExpandedChange={onExpandedChange}
      dataAttrs={{
        'data-status': status,
        'data-toolname': name,
      }}
      expandedContent={
        inText || showOut || node.presentation ? (
          <div className={styles.expanded} data-testid="toolcall-expanded">
            {node.presentation && (
              <div
                className={styles.card}
                data-testid="toolcall-presentation-card"
              >
                <PresentationCard
                  meta={node.presentation.meta}
                  payload={node.output ?? node.arguments}
                />
              </div>
            )}
            {inText && (
              <div className={styles.docCard} data-testid="toolcall-in">
                <div
                  className={styles.docLabel}
                  data-testid="toolcall-in-label"
                >
                  IN
                </div>
                <pre className={styles.docPre}>{inText}</pre>
              </div>
            )}
            {showOut && (
              <div className={styles.docCard} data-testid="toolcall-out">
                <div
                  className={cx(
                    styles.docLabel,
                    status === 'error' && styles.cardLabelError,
                  )}
                  data-testid="toolcall-out-label"
                >
                  OUT
                </div>
                <pre className={styles.docPre}>
                  {status === 'error' && outBody.firstLine ? (
                    <>
                      <span
                        className={styles.errorText}
                        data-testid="toolcall-out-error-line"
                      >
                        {outBody.firstLine}
                      </span>
                      {(() => {
                        const rest = outBody.isLong && !showFullOut
                          ? buildPreview(outBody.rest)
                          : outBody.rest;
                        return rest ? `\n${rest}` : '';
                      })()}
                    </>
                  ) : outBody.isLong && !showFullOut ? (
                    outBody.preview
                  ) : (
                    outBody.full
                  )}
                </pre>
                {outBody.isLong && !showFullOut && (
                  <button
                    type="button"
                    className={styles.outExpand}
                    onClick={() => setShowFullOut(true)}
                    data-testid="toolcall-out-expand"
                  >
                    查看完整输出
                  </button>
                )}
              </div>
            )}
          </div>
        ) : undefined
      }
    >
      <span className={styles.title} data-testid="toolcall-title">
        {name}
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
          status === 'error' && styles.summaryError,
        )}
        data-testid="toolcall-summary"
        title={summaryFull || undefined}
      >
        {summary}
      </span>
      {(durationText || exitCodeText) && (
        <span
          className={flowCx(
            flowStyles.duration,
            exitCodeText !== null && flowStyles.durationError,
          )}
          data-testid="toolcall-duration"
        >
          {[durationText, exitCodeText].filter(Boolean).join(' · ')}
        </span>
      )}
    </ExecutionDisclosureRow>
  );
};

export default React.memo(ToolCallRow);
