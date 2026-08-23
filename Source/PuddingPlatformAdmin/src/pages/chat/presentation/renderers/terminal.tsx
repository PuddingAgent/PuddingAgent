// ── Terminal renderer（行为链 P3，§3.5）──
// 终端命令卡：banner（mono 命令 + exit code pill + 复制）+ 输出窗口（224px 内滚）。
// 数据源：meta.command/cmd/script/shell + meta.exitCode（G1/G2 契约字段优先）；
// payload（工具 output/arguments）回退解析命令与输出。缺命令时 banner 退化为输出摘要头。
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

export const TerminalRenderer: PresentationRenderer = ({ meta, payload }) => {
  const { styles, cx } = useRendererStyles();
  const payloadObject = parsePayloadObject(payload);
  const command =
    readMetaString(meta, ['command', 'cmd', 'script', 'shell']) ??
    readMetaString(payloadObject, ['command', 'cmd', 'script', 'shell']);
  const cwd =
    readMetaString(meta, ['cwd', 'workingDirectory', 'working_directory']) ??
    readMetaString(payloadObject, ['cwd', 'workingDirectory']);
  const exitCode =
    readMetaNumber(meta, ['exitCode', 'exit_code']) ??
    readMetaNumber(payloadObject, ['exitCode', 'exit_code']);
  const output =
    readMetaString(meta, ['output', 'result']) ?? payloadText(payload);

  if (!command && !output.trim()) return null;

  const bannerText =
    [cwd ? `${cwd}>` : null, command].filter(Boolean).join(' ') ||
    '终端输出';
  // payload 即调用参数 JSON（含 command 类字段且可完整解析为该对象）时不在正文
  // 重复展示：命令已在 banner，完整参数由 IN 卡承载；其余情况展示输出窗口。
  const payloadIsInvocation =
    Boolean(payloadObject) &&
    typeof payload === 'string' &&
    readMetaString(payloadObject, ['command', 'cmd', 'script', 'shell']) !== null;
  const bodyText = payloadIsInvocation ? '' : output;

  return (
    <div
      className={styles.card}
      data-testid="presentation-terminal"
    >
      <div className={styles.banner}>
        <span className={cx(styles.bannerText, exitCode !== null && exitCode !== 0 && styles.bannerTextError)}>
          {bannerText}
        </span>
        {exitCode !== null && (
          <span
            className={cx(
              styles.pill,
              exitCode === 0 ? styles.pillOk : styles.pillErr,
            )}
            data-testid="presentation-terminal-exit"
          >
            exit {exitCode}
          </span>
        )}
        <RendererCopyButton text={command || output} />
      </div>
      {bodyText.trim() && (
        <pre className={styles.body} data-testid="presentation-terminal-output">
          {bodyText}
        </pre>
      )}
    </div>
  );
};

export default TerminalRenderer;
