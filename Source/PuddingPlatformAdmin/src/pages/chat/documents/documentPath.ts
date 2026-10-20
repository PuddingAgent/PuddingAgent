/**
 * 文档预览的路径规整与查询参数解析（纯函数，不做任何 IO）。
 *
 * 为什么单独抽成模块：`/doc-preview?path=...` 的 path 来自 URL，是**外部输入**，
 * 而它会一路传到宿主侧的文件读取接口。路径穿越（`..`）与盘符绝对路径如果不在
 * 这一层拦掉，就要指望后端再拦一次；一处漏掉就是把工作区外任意文件读进浏览器。
 *
 * 设计取舍：
 * - **只接受工作区相对路径**。`C:\x`、`/etc/passwd`、`\\server\share` 一律拒绝，
 *   而不是"帮用户改写成相对路径"——猜错了就是把用户指向别的文件。
 * - **分隔符统一为 `/`**。Windows 用户粘贴 `Docs\a.md` 很常见，若不统一会被当成
 *   单个文件名（含反斜杠），既是坏体验也绕过了按段判断的穿越检查。
 * - **`.` 与空段静默丢弃**（`a//b`、`./a` → `a/b`），但 `..` 直接拒绝。
 *   丢弃是安全的归约，`..` 是语义上"往上走"，两者不能同样对待。
 */

/** 路径长度上限：足够覆盖深层目录，又不给超长输入留空间。 */
const MAX_PATH_LENGTH = 512;

/**
 * 允许在预览页渲染的扩展名。
 *
 * 只放**纯文本类**：PDF / HTML 需要各自的沙箱与渲染器，不能混进 Markdown 渲染路径
 * （HTML 走 MarkdownPreview 会被当纯文本显示，用户会以为是渲染坏了）。
 */
const PREVIEWABLE_EXTENSIONS = ['.md', '.markdown', '.txt'];

export type DocumentPathResult =
  | { ok: true; path: string }
  | { ok: false; reason: string };

/**
 * 校验并规整工作区相对路径。
 *
 * 返回的 `path` 是**规整后**的形式，调用方应当用返回值（而不是原始输入）去请求。
 */
export function normalizeDocumentPath(raw: unknown): DocumentPathResult {
  if (typeof raw !== 'string') {
    return { ok: false, reason: '缺少 path 参数' };
  }

  const trimmed = raw.trim();
  if (!trimmed) {
    return { ok: false, reason: '缺少 path 参数' };
  }

  if (trimmed.length > MAX_PATH_LENGTH) {
    return { ok: false, reason: `路径过长（上限 ${MAX_PATH_LENGTH} 字符）` };
  }

  // 控制字符（含 \0、换行）会让下游日志与文件 API 行为不可预期，直接拒绝。
  if (/[\u0000-\u001f\u007f]/.test(trimmed)) {
    return { ok: false, reason: '路径包含控制字符' };
  }

  const unified = trimmed.replace(/\\/g, '/');

  // 绝对路径：POSIX 前导斜杠、UNC（`//server`）、Windows 盘符。
  if (unified.startsWith('/')) {
    return { ok: false, reason: '只接受工作区相对路径（不能以 / 开头）' };
  }
  if (/^[A-Za-z]:/.test(unified)) {
    return { ok: false, reason: '只接受工作区相对路径（不能带盘符）' };
  }

  const segments = unified.split('/');
  const cleaned: string[] = [];
  for (const segment of segments) {
    if (segment === '' || segment === '.') {
      continue;
    }
    if (segment === '..') {
      return { ok: false, reason: '路径不得包含 ..（禁止穿越工作区）' };
    }
    cleaned.push(segment);
  }

  if (cleaned.length === 0) {
    return { ok: false, reason: '路径为空' };
  }

  const path = cleaned.join('/');
  const lower = path.toLowerCase();
  if (!PREVIEWABLE_EXTENSIONS.some((ext) => lower.endsWith(ext))) {
    return {
      ok: false,
      reason: `只支持预览 ${PREVIEWABLE_EXTENSIONS.join(' / ')} 文件`,
    };
  }

  return { ok: true, path };
}

export interface DocPreviewQuery {
  /** 原始 path 参数（未校验），交给 normalizeDocumentPath 处理。 */
  rawPath: string | null;
  workspaceId: string;
}

/**
 * 解析 `/doc-preview` 的查询串。
 *
 * 接受带或不带前导 `?` 的形式——Shell 侧拼 URL 与浏览器 `location.search` 两种来源都会出现。
 */
export function parseDocPreviewQuery(search: string): DocPreviewQuery {
  const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search);
  const workspaceId = (params.get('ws') ?? '').trim() || 'default';
  return {
    rawPath: params.get('path'),
    workspaceId,
  };
}

/**
 * 拼出宿主侧的文件读取 URL。
 *
 * 这个接口**目前还不存在**（`VisionArtifactApiController` 只有 POST Upload）；
 * 契约先在这里定下来，后端补齐后前端不用再改：
 *   GET /api/workspaces/{ws}/files/content?path=<相对路径>
 *   → 200 { content: string, size?: number, lastModifiedUtc?: string, truncated?: boolean }
 */
export function buildDocumentContentUrl(workspaceId: string, path: string): string {
  const params = new URLSearchParams({ path });
  return `/api/workspaces/${encodeURIComponent(workspaceId)}/files/content?${params.toString()}`;
}
