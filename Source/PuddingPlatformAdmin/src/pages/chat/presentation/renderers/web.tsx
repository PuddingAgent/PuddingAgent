// ── Web renderer（行为链 P3，§3.5）──
// 浏览器动作卡：banner（动作 + URL + 页面标题 pill + 复制）+ 结果摘要窗口。
// 数据源：meta.action/url/uri/title/status（G1/G2 契约优先）；payload 回退解析。
import React from 'react';
import type { PresentationRenderer } from '../PresentationRegistry';
import {
  parsePayloadObject,
  payloadText,
  readMetaString,
  RendererCopyButton,
  useRendererStyles,
} from './rendererKit';

export const WebRenderer: PresentationRenderer = ({ meta, payload }) => {
  const { styles, cx } = useRendererStyles();
  const payloadObject = parsePayloadObject(payload);
  const url =
    readMetaString(meta, ['url', 'uri', 'link', 'target']) ??
    readMetaString(payloadObject, ['url', 'uri', 'link', 'target']);
  const action =
    readMetaString(meta, ['action', 'operation']) ??
    readMetaString(payloadObject, ['action', 'operation']);
  const title =
    readMetaString(meta, ['title', 'pageTitle', 'page_title']) ??
    readMetaString(payloadObject, ['title', 'pageTitle']);
  const status =
    readMetaString(meta, ['status']) ?? readMetaString(payloadObject, ['status']);
  const content =
    readMetaString(meta, ['output', 'result', 'text']) ?? payloadText(payload);

  if (!url && !title && !content.trim()) return null;

  const bannerText = url ?? title ?? '浏览器操作';
  // payload 即调用参数 JSON（含 url 类字段）时不重复展示。
  const payloadIsInvocation =
    Boolean(payloadObject) &&
    typeof payload === 'string' &&
    readMetaString(payloadObject, ['url', 'uri', 'link', 'target']) !== null;
  const bodyText = payloadIsInvocation ? '' : content;
  const isFailure = /fail|error|timeout/i.test(status ?? '');

  return (
    <div className={styles.card} data-testid="presentation-web">
      <div className={styles.banner}>
        {action && (
          <span
            className={cx(styles.pill, isFailure ? styles.pillErr : styles.pillNeutral)}
            data-testid="presentation-web-action"
          >
            {action}
          </span>
        )}
        <span
          className={cx(styles.bannerText, isFailure && styles.bannerTextError)}
          title={bannerText}
        >
          {bannerText}
        </span>
        {title && url && (
          <span className={cx(styles.pill, styles.pillNeutral)}>{title}</span>
        )}
        <RendererCopyButton text={url || content} />
      </div>
      {bodyText.trim() && (
        <pre className={styles.body} data-testid="presentation-web-body">
          {bodyText}
        </pre>
      )}
    </div>
  );
};

export default WebRenderer;
