// ── RendererKit：presentation renderer 共享套件（行为链 P3，§3.5 卡片家族）──
// 五类专用 renderer（terminal/diff/read/search/web）共用的：
//  - 卡片家族样式：banner + 内容窗口，圆角走 --pudding-chat-radius-md、
//    表面走 --pudding-chat-surface(-muted)、内容窗口 maxHeight 224px 内滚；
//  - meta / payload 安全读取（G1/G2 契约字段优先，payload 回退解析）；
//  - 复制按钮（复制成功 1.5s 反馈，对齐 CodeBlock/reasoningCopy 交互）。
import { createStyles } from 'antd-style';
import React, { useCallback, useState } from 'react';

const MONO_FONT =
  "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace";

export const useRendererStyles = createStyles(() => ({
  /** 卡片家族外壳：统一圆角/边框/表面（§3.5） */
  card: {
    borderRadius: 'var(--pudding-chat-radius-md)',
    border: '1px solid var(--pudding-chat-border)',
    background: 'var(--pudding-chat-surface-muted)',
    overflow: 'hidden',
  },
  /** banner：路径/命令/查询词行（mono、单行截断） */
  banner: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    padding: '5px 10px',
    background: 'var(--pudding-chat-surface)',
    borderBottom: '1px solid var(--pudding-chat-border)',
  },
  bannerText: {
    flex: 1,
    minWidth: 0,
    fontSize: 12,
    lineHeight: '18px',
    fontFamily: MONO_FONT,
    color: 'var(--pudding-chat-text-secondary)',
    whiteSpace: 'nowrap' as const,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
  },
  bannerTextError: {
    color: 'var(--pudding-status-error)',
  },
  /** 状态 pill：exit code / 命中数等 */
  pill: {
    flexShrink: 0,
    fontSize: 10,
    fontWeight: 600,
    lineHeight: '16px',
    padding: '0 8px',
    borderRadius: 999,
    fontFamily: MONO_FONT,
    fontVariantNumeric: 'tabular-nums' as const,
    whiteSpace: 'nowrap' as const,
  },
  pillOk: {
    color: 'var(--pudding-status-success)',
    background:
      'color-mix(in srgb, var(--pudding-status-success) 12%, transparent)',
  },
  pillErr: {
    color: 'var(--pudding-status-error)',
    background:
      'color-mix(in srgb, var(--pudding-status-error) 12%, transparent)',
  },
  pillNeutral: {
    color: 'var(--pudding-chat-text-caption)',
    background:
      'color-mix(in srgb, var(--pudding-chat-text-caption) 12%, transparent)',
  },
  /** 内容窗口：mono + 224px 内滚（§3.5） */
  body: {
    margin: 0,
    padding: '8px 10px',
    maxHeight: 224,
    overflow: 'auto',
    fontSize: 12,
    lineHeight: 1.55,
    fontFamily: MONO_FONT,
    color: 'var(--pudding-chat-text)',
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
  },
  /** 复制按钮 */
  copy: {
    flexShrink: 0,
    fontSize: 11,
    lineHeight: '18px',
    padding: '0 6px',
    color: 'var(--pudding-chat-text-caption)',
    background: 'transparent',
    border: 'none' as const,
    cursor: 'pointer',
    userSelect: 'none' as const,
    '&:hover': { color: 'var(--pudding-chat-text-secondary)' },
    '&:focus-visible': {
      outline: '2px solid var(--pudding-status-running)',
      outlineOffset: -2,
    },
  },
  // ── diff 专用 ──
  diffLine: {
    display: 'block',
    whiteSpace: 'pre' as const,
    minHeight: '18px',
  },
  diffAdd: {
    background:
      'color-mix(in srgb, var(--pudding-status-success) 10%, transparent)',
    color: 'var(--pudding-status-success)',
  },
  diffDel: {
    background:
      'color-mix(in srgb, var(--pudding-status-error) 10%, transparent)',
    color: 'var(--pudding-status-error)',
  },
  diffHunk: {
    color: 'var(--pudding-chat-text-caption)',
    background: 'var(--pudding-chat-surface)',
  },
  diffFile: {
    color: 'var(--pudding-chat-text-tertiary)',
  },
}));

// ── meta / payload 安全读取 ───────────────────────────────────────────────

export const readMetaString = (
  meta: Record<string, unknown> | null | undefined,
  keys: string[],
): string | null => {
  if (!meta) return null;
  for (const key of keys) {
    const value = meta[key];
    if (typeof value === 'string' && value.trim()) return value;
    if (typeof value === 'number' && Number.isFinite(value)) return String(value);
  }
  return null;
};

export const readMetaNumber = (
  meta: Record<string, unknown> | null | undefined,
  keys: string[],
): number | null => {
  if (!meta) return null;
  for (const key of keys) {
    const value = meta[key];
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    if (typeof value === 'string' && value.trim() && Number.isFinite(Number(value))) {
      return Number(value);
    }
  }
  return null;
};

/** payload → 对象（字符串尝试 JSON.parse；对象原样；失败 null）。 */
export const parsePayloadObject = (
  payload: unknown,
): Record<string, unknown> | null => {
  if (!payload) return null;
  if (typeof payload === 'object' && !Array.isArray(payload)) {
    return payload as Record<string, unknown>;
  }
  if (typeof payload === 'string' && payload.trim().startsWith('{')) {
    try {
      const parsed = JSON.parse(payload);
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
        ? (parsed as Record<string, unknown>)
        : null;
    } catch {
      return null;
    }
  }
  return null;
};

/** payload → 展示文本（字符串原样；对象 JSON.stringify；其余 String()）。 */
export const payloadText = (payload: unknown): string => {
  if (payload === undefined || payload === null) return '';
  if (typeof payload === 'string') return payload;
  try {
    return JSON.stringify(payload, null, 2) ?? '';
  } catch {
    return String(payload);
  }
};

// ── CopyButton ────────────────────────────────────────────────────────────

export const RendererCopyButton: React.FC<{ text: string; label?: string }> = ({
  text,
  label = '复制',
}) => {
  const { styles } = useRendererStyles();
  const [copied, setCopied] = useState(false);
  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // clipboard 不可用时静默失败
    }
  }, [text]);
  if (!text) return null;
  return (
    <button
      type="button"
      className={styles.copy}
      data-testid="renderer-copy"
      onClick={handleCopy}
    >
      {copied ? '已复制' : label}
    </button>
  );
};
