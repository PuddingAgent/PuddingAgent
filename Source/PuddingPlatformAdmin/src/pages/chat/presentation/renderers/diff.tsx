// ── Diff renderer（行为链 P3，§3.5）──
// unified diff 卡：banner（文件路径 + 增删计数 + 复制）+ 着色 diff 体
// （+ 行绿 / − 行红 / @@ hunk 灰 / 文件头灰）。非 diff 形态的 payload 原样 mono 展示。
import React from 'react';
import type { PresentationRenderer } from '../PresentationRegistry';
import {
  payloadText,
  readMetaString,
  RendererCopyButton,
  useRendererStyles,
} from './rendererKit';

interface DiffLine {
  id: string;
  kind: 'add' | 'del' | 'hunk' | 'file' | 'context';
  text: string;
}

/** 解析 unified diff 行；非 diff 文本（无 +/-/@@ 行）返回 null（回退原文展示）。 */
export const parseDiffLines = (text: string): DiffLine[] | null => {
  const lines = text.split(/\r?\n/);
  const parsed: DiffLine[] = [];
  let hasDiffMarker = false;
  lines.forEach((line, index) => {
    const id = `dl${index}`;
    if (line.startsWith('+++') || line.startsWith('---')) {
      parsed.push({ id, kind: 'file', text: line });
      hasDiffMarker = true;
    } else if (line.startsWith('@@')) {
      parsed.push({ id, kind: 'hunk', text: line });
      hasDiffMarker = true;
    } else if (line.startsWith('+')) {
      parsed.push({ id, kind: 'add', text: line });
      hasDiffMarker = true;
    } else if (line.startsWith('-')) {
      parsed.push({ id, kind: 'del', text: line });
      hasDiffMarker = true;
    } else {
      parsed.push({ id, kind: 'context', text: line });
    }
  });
  return hasDiffMarker ? parsed : null;
};

/** 从 +++ / meta 提取展示路径（去 a/ b/ 前缀与时戳）。 */
const extractFilePath = (
  lines: DiffLine[],
  meta: Record<string, unknown> | null | undefined,
): string | null => {
  const fromMeta = readMetaString(meta, ['path', 'file', 'filePath']);
  if (fromMeta) return fromMeta;
  const fileLine = lines.find(
    (line) => line.kind === 'file' && line.text.startsWith('+++'),
  );
  if (!fileLine) return null;
  const raw = fileLine.text.slice(3).trim().split('\t')[0];
  return raw.replace(/^b\//, '').trim() || null;
};

export const DiffRenderer: PresentationRenderer = ({ meta, payload }) => {
  const { styles, cx } = useRendererStyles();
  const text =
    readMetaString(meta, ['patch', 'diff', 'content']) ?? payloadText(payload);
  if (!text.trim()) return null;

  const lines = parseDiffLines(text);
  if (!lines) {
    // 非 diff 形态：按通用文本展示（Generic 兜底语义，但保持卡片家族外观）。
    return (
      <div className={styles.card} data-testid="presentation-diff">
        <pre className={styles.body} data-testid="presentation-diff-body">
          {text}
        </pre>
      </div>
    );
  }

  const adds = lines.filter((line) => line.kind === 'add').length;
  const dels = lines.filter((line) => line.kind === 'del').length;
  const filePath = extractFilePath(lines, meta);

  return (
    <div className={styles.card} data-testid="presentation-diff">
      <div className={styles.banner}>
        <span className={styles.bannerText}>{filePath ?? '代码变更'}</span>
        <span className={cx(styles.pill, styles.pillOk)} data-testid="presentation-diff-adds">
          +{adds}
        </span>
        <span className={cx(styles.pill, styles.pillErr)} data-testid="presentation-diff-dels">
          −{dels}
        </span>
        <RendererCopyButton text={text} />
      </div>
      <pre className={styles.body} data-testid="presentation-diff-body">
        {lines.map((line) => (
          <span
            key={line.id}
            className={cx(
              styles.diffLine,
              line.kind === 'add' && styles.diffAdd,
              line.kind === 'del' && styles.diffDel,
              line.kind === 'hunk' && styles.diffHunk,
              line.kind === 'file' && styles.diffFile,
            )}
          >
            {line.text || ' '}
          </span>
        ))}
      </pre>
    </div>
  );
};

export default DiffRenderer;
