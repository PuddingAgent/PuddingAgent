// ── summarizeError：错误文本摘要（P0-1，对齐 deepseek-harness D3 / 队列 summarizeQueueError 同规则）──
// 规则：JSON 优先提取 .message / .error 字符串字段（message 优先）；非 JSON 按原文。
// summary 截断 ≤ max（默认 80，超长追加 …）；full 保留全量原文（供 title 悬浮展示）。
// 与 MessageQueueDropdown.summarizeQueueError 独立实现同规则（该文件有他人未提交改动，禁止触碰）。
// 纯函数，可单测。

export interface SummarizeErrorResult {
  /** 展示用摘要（≤ max 字符，超长追加 …） */
  summary: string;
  /** 全量原文（title 用，保留 JSON 原始结构以便查看 message/code 等全部字段） */
  full: string;
}

export function summarizeError(
  raw: string | undefined | null,
  max = 80,
): SummarizeErrorResult {
  if (!raw) return { summary: '', full: '' };
  const trimmed = raw.trim();
  if (!trimmed) return { summary: '', full: '' };
  if (max < 1) return { summary: '', full: trimmed };

  const full = trimmed;
  let summarySource = trimmed;
  try {
    const parsed = JSON.parse(trimmed) as {
      message?: unknown;
      error?: unknown;
    } | null;
    if (parsed && typeof parsed === 'object') {
      const candidate =
        typeof parsed.message === 'string' && parsed.message.trim()
          ? (parsed.message as string).trim()
          : typeof parsed.error === 'string' && parsed.error.trim()
            ? (parsed.error as string).trim()
            : '';
      if (candidate) summarySource = candidate;
    }
  } catch {
    // 非 JSON：按原文截断处理
  }

  const summary =
    summarySource.length > max
      ? `${summarySource.slice(0, max)}…`
      : summarySource;
  return { summary, full };
}

export default summarizeError;
