// ── Search renderer（行为链 P3，§3.5）──
// 搜索卡：banner（查询词 + 命中数 pill + 复制）+ 分组匹配预览窗口。
// 命中数来自 meta.total/totalCount/hits/count（G1/G2 契约）或 payload 中的
// hits/results/matches 数组长度；不可推导时省略 pill（不伪造计数）。
import React from 'react';
import type { PresentationRenderer } from '../PresentationRegistry';
import {
  parsePayloadObject,
  payloadText,
  readMetaNumber,
  readMetaString,
  RendererCopyButton,
  useRendererStyles,
} from './rendererKit';

const readArrayField = (
  obj: Record<string, unknown> | null,
  keys: string[],
): unknown[] | null => {
  if (!obj) return null;
  for (const key of keys) {
    const value = obj[key];
    if (Array.isArray(value)) return value;
  }
  return null;
};

export const SearchRenderer: PresentationRenderer = ({ meta, payload }) => {
  const { styles, cx } = useRendererStyles();
  const payloadObject = parsePayloadObject(payload);
  const query =
    readMetaString(meta, ['query', 'pattern', 'keyword', 'keywords']) ??
    readMetaString(payloadObject, ['query', 'pattern', 'keyword', 'keywords']);
  const content =
    readMetaString(meta, ['output', 'result', 'text']) ?? payloadText(payload);

  const hits =
    readArrayField(payloadObject, ['hits', 'results', 'matches', 'items']) ??
    readArrayField(meta ?? null, ['hits', 'results', 'matches', 'items']);
  const count =
    readMetaNumber(meta, ['total', 'totalCount', 'total_count', 'count', 'hitCount']) ??
    (hits ? hits.length : null);

  if (!query && !content.trim()) return null;

  // 命中列表逐条展示（有界：前 20 条 + 省略标记），否则原文窗口。
  let body: React.ReactNode = null;
  if (hits && hits.length > 0) {
    const shown = hits.slice(0, 20).map((hit, index) => ({
      id: `hit${index}`,
      text: typeof hit === 'string' ? hit : payloadText(hit),
    }));
    body = (
      <pre className={styles.body} data-testid="presentation-search-body">
        {shown.map((hit) => (
          <span key={hit.id} className={styles.diffLine}>
            {hit.text}
          </span>
        ))}
        {hits.length > shown.length && (
          <span className={styles.diffLine}>…（共 {hits.length} 条命中）</span>
        )}
      </pre>
    );
  } else if (content.trim()) {
    // 内容是调用参数 JSON（含 query 类字段）时不重复展示。
    const payloadIsInvocation =
      Boolean(payloadObject) &&
      typeof payload === 'string' &&
      readMetaString(payloadObject, ['query', 'pattern', 'keyword', 'keywords']) !== null;
    const bodyText = payloadIsInvocation ? '' : content;
    if (bodyText) {
      body = (
        <pre className={styles.body} data-testid="presentation-search-body">
          {bodyText}
        </pre>
      );
    }
  }

  return (
    <div className={styles.card} data-testid="presentation-search">
      <div className={styles.banner}>
        <span className={styles.bannerText}>{query ?? '搜索结果'}</span>
        {count !== null && count > 0 && (
          <span
            className={cx(styles.pill, styles.pillNeutral)}
            data-testid="presentation-search-count"
          >
            {count} 命中
          </span>
        )}
        <RendererCopyButton text={content || query || ''} />
      </div>
      {body}
    </div>
  );
};

export default SearchRenderer;
