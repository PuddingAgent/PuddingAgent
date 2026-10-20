/**
 * 文档内容加载（宿主侧文件读取接口的薄封装）。
 *
 * 这一层存在的理由：页面只该关心"加载成功/失败 + 原因"，不该关心 HTTP 状态码映射、
 * JSON 与纯文本两种响应体、以及接口还没实现时的措辞。把这些放在这里，页面就是纯渲染。
 *
 * 三条被刻意写死的规则：
 * 1. **不伪造成功**。接口不存在时给出明确原因，而不是渲染一个空文档——空文档会被
 *    读成"这个文件是空的"，那是错误结论。
 * 2. **不猜路径**。path 必须先过 normalizeDocumentPath；本函数不自己改写。
 * 3. **响应体两种形态都收**：后端可能返回 JSON 信封（带 size/truncated），也可能
 *    直接 text/markdown。只认 JSON 会让后端实现方式绑死前端。
 */

import {
  buildDocumentContentUrl,
  normalizeDocumentPath,
  type DocumentPathResult,
} from './documentPath';

export interface LoadedDocument {
  /** 文件名（取路径最后一段）。 */
  name: string;
  markdown: string;
  /** 展示用附加信息，如「12.3 KB」。 */
  meta?: string;
}

export type DocumentLoadResult =
  | { ok: true; document: LoadedDocument }
  | { ok: false; reason: string; hint?: string };

export interface LoadDocumentOptions {
  workspaceId: string;
  path: string;
  signal?: AbortSignal;
  /** 注入点：测试用假 fetch，生产走全局 fetch。 */
  fetchImpl?: typeof fetch;
  /** 注入点：测试用令牌读取；生产读 localStorage。 */
  readToken?: () => string | null;
}

/**
 * 认证令牌的存储键。必须与全局 request 拦截器保持一致
 * （`requestErrorConfig.ts` 读的就是这一个 key）。
 */
const TOKEN_STORAGE_KEY = 'pudding_token';

/**
 * 构造认证头。
 *
 * **为什么需要这个函数**：本模块用的是原生 `fetch`，绕过了 umi 的 request 拦截器
 * （拦截器会从 localStorage 读 `pudding_token` 并注入 `Authorization`）。少了这一步，
 * 请求虽然能发出去，但一定 401 —— 而页面会把原因报成「需要登录」，把排查方向
 * 引到登录态上，实际只是漏了一个 header。（2026-10-20 实测：登录后用浏览器打开
 * doc-preview 仍然 401，根因就是这里。）
 *
 * 无令牌时不加头：让后端自己返 401，前端文案才有依据。不要在客户端伪造成
 * 「未登录」以外的结论。
 */
export function buildAuthHeaders(readToken?: () => string | null): Record<string, string> {
  const read =
    readToken ??
    (() => (typeof localStorage === 'undefined' ? null : localStorage.getItem(TOKEN_STORAGE_KEY)));
  let token: string | null;
  try {
    token = read();
  } catch {
    // 隐私模式或存储被禁时 getItem 会抛；读不到就是没令牌，不该让整个预览失败。
    token = null;
  }
  return token ? { Authorization: `Bearer ${token}` } : {};
}

function formatBytes(size: number): string {
  if (!Number.isFinite(size) || size < 0) return '';
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / 1024 / 1024).toFixed(1)} MB`;
}

/** 从规整后的路径取文件名。 */
export function documentNameFromPath(path: string): string {
  const segments = path.split('/');
  return segments[segments.length - 1] || path;
}

/**
 * 解析响应体：优先 JSON 信封，否则按纯文本 Markdown 处理。
 *
 * 注意 JSON.parse 的失败是**预期内**的（后端可能直接吐 markdown），所以不能当成错误。
 */
function parseBody(text: string): { markdown: string; size?: number; truncated?: boolean } {
  const trimmed = text.trimStart();
  if (trimmed.startsWith('{')) {
    try {
      const parsed = JSON.parse(text) as {
        content?: unknown;
        size?: unknown;
        truncated?: unknown;
      };
      if (typeof parsed.content === 'string') {
        return {
          markdown: parsed.content,
          size: typeof parsed.size === 'number' ? parsed.size : undefined,
          truncated: parsed.truncated === true,
        };
      }
    } catch {
      // 落到纯文本分支——这是允许的响应形态，不是缺陷。
    }
  }
  return { markdown: text };
}

/**
 * 按路径加载文档内容。
 *
 * 失败原因分三类，措辞不同，因为它们对用户意味着完全不同的下一步：
 * - 路径非法 → 改 URL
 * - 接口不存在 → 等宿主补齐（当前状态）
 * - 文件不存在 → 改路径
 */
export async function loadDocumentContent(
  options: LoadDocumentOptions,
): Promise<DocumentLoadResult> {
  const normalized: DocumentPathResult = normalizeDocumentPath(options.path);
  if (!normalized.ok) {
    return { ok: false, reason: normalized.reason };
  }

  const doFetch = options.fetchImpl ?? (typeof fetch === 'function' ? fetch : undefined);
  if (!doFetch) {
    return { ok: false, reason: '当前环境没有 fetch，无法读取文件' };
  }

  let response: Response;
  try {
    response = await doFetch(buildDocumentContentUrl(options.workspaceId, normalized.path), {
      method: 'GET',
      credentials: 'include',
      headers: {
        Accept: 'application/json, text/markdown;q=0.9, text/plain;q=0.8',
        ...buildAuthHeaders(options.readToken),
      },
      signal: options.signal,
    });
  } catch (error) {
    // 主动取消不算失败原因，交给调用方判断 signal.aborted。
    const message = error instanceof Error ? error.message : String(error);
    return { ok: false, reason: `请求失败：${message}` };
  }

  if (!response.ok) {
    // 401 与 403 必须分开：前者是「没登录」（用户可自助解决），后者是「登录了但没权限」
    // （要找管理员）。混成一句会让排查方向整体跑偏。
    if (response.status === 401) {
      return {
        ok: false,
        reason: '需要登录后才能预览工作区文件',
        hint: '在宿主里登录工作台后重试；Shell 内嵌视图若与主窗口不共享登录态就会停在这里',
      };
    }
    if (response.status === 404) {
      // 后端对「工作区根之外」与「文件不存在」返回同一个 404（刻意不提供存在性探测），
      // 所以这里不能断言「接口未提供」——那会让排查方向整体跑偏。
      return {
        ok: false,
        reason: '找不到该文件，或它超出了工作区范围',
        hint: '检查 path 是否为工作区内的相对路径，例如 Docs/14_reports/xxx.md',
      };
    }
    if (response.status === 501 || response.status === 503) {
      return {
        ok: false,
        reason: `宿主未提供文件读取能力（HTTP ${response.status}）`,
        hint: '接口补齐后无需改动本页',
      };
    }
    if (response.status === 403) {
      return { ok: false, reason: '无权读取该文件（可能超出工作区范围）' };
    }
    return { ok: false, reason: `读取失败：HTTP ${response.status}` };
  }

  let text: string;
  try {
    text = await response.text();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return { ok: false, reason: `读取响应体失败：${message}` };
  }

  const body = parseBody(text);
  const metaParts: string[] = [];
  if (typeof body.size === 'number') {
    const formatted = formatBytes(body.size);
    if (formatted) metaParts.push(formatted);
  }
  if (body.truncated) metaParts.push('内容已截断');

  return {
    ok: true,
    document: {
      name: documentNameFromPath(normalized.path),
      markdown: body.markdown,
      meta: metaParts.length > 0 ? metaParts.join(' · ') : normalized.path,
    },
  };
}
