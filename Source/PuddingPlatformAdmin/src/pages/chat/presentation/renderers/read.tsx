// ── Read renderer（行为链 P3，§3.5）──
// 文件读取卡：banner（相对路径 + 行范围 + 复制）+ 内容窗口（12px mono，224px 内滚）。
// 数据源：meta.path/file + meta.startLine/endLine（G1/G2 契约优先）；
// payload 回退解析（路径字段 / 行标记「lines X-Y」）。
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

export const ReadRenderer: PresentationRenderer = ({ meta, payload }) => {
  const { styles } = useRendererStyles();
  const payloadObject = parsePayloadObject(payload);
  const path =
    readMetaString(meta, ['path', 'file', 'filePath', 'filename']) ??
    readMetaString(payloadObject, ['path', 'file', 'filePath', 'filename']);
  const content =
    readMetaString(meta, ['content', 'text', 'output']) ?? payloadText(payload);
  if (!path && !content.trim()) return null;

  let startLine = readMetaNumber(meta, ['startLine', 'start_line', 'fromLine']);
  let endLine = readMetaNumber(meta, ['endLine', 'end_line', 'toLine']);
  if (startLine === null || endLine === null) {
    const fromPayload =
      readMetaNumber(payloadObject, ['startLine', 'start_line', 'fromLine']) ??
      readMetaNumber(payloadObject, ['offset']);
    const match = /lines?\s+(\d+)\s*[-–~]\s*(\d+)/i.exec(content.slice(0, 200));
    if (match) {
      startLine = startLine ?? Number(match[1]);
      endLine = endLine ?? Number(match[2]);
    } else if (fromPayload !== null) {
      startLine = startLine ?? fromPayload + 1;
      endLine =
        endLine ??
        fromPayload + Math.max(1, content.split(/\r?\n/).filter(Boolean).length);
    }
  }

  const rangeText =
    startLine !== null && endLine !== null && endLine >= startLine
      ? ` · 行 ${startLine}–${endLine}`
      : '';

  // 内容本身是调用参数 JSON（含 path 类字段）时不重复展示，IN 卡承载。
  const payloadIsInvocation =
    Boolean(payloadObject) &&
    typeof payload === 'string' &&
    readMetaString(payloadObject, ['path', 'file', 'filePath', 'filename']) !== null;
  const bodyText = payloadIsInvocation ? '' : content;

  return (
    <div className={styles.card} data-testid="presentation-read">
      <div className={styles.banner}>
        <span className={styles.bannerText}>
          {path ?? '文件内容'}
          {rangeText}
        </span>
        <RendererCopyButton text={content} />
      </div>
      {bodyText.trim() && (
        <pre className={styles.body} data-testid="presentation-read-content">
          {bodyText}
        </pre>
      )}
    </div>
  );
};

export default ReadRenderer;
