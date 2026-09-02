/**
 * DirectLlmClient 写入运行事实的两种 canonical 重试摘要。
 *
 * 这里故意不接受普通的 `retry` 文本：模型思考、工具输出和用户正文都可能
 * 合法包含该单词，不能据此推断 LLM 网关状态。
 */
const CANONICAL_MODEL_RETRY_RE =
  /^(?:🧠\s*)?LLM (?:call retry|stream retry before first delta)\s+(\d+)\s*\/\s*(\d+)\.(?:\s|$)/i;

export interface CanonicalModelRetrySummary {
  attempt: number;
  maxRetries: number;
}

export const parseCanonicalModelRetrySummary = (
  value?: string,
): CanonicalModelRetrySummary | null => {
  const match = CANONICAL_MODEL_RETRY_RE.exec(value?.trim() ?? '');
  if (!match) return null;

  const attempt = Number(match[1]);
  const maxRetries = Number(match[2]);
  if (
    !Number.isInteger(attempt) ||
    !Number.isInteger(maxRetries) ||
    attempt < 1 ||
    maxRetries < 1 ||
    attempt > maxRetries
  ) {
    return null;
  }

  return { attempt, maxRetries };
};
