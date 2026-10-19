// ── Markdown 下载 ─────────────────────────────────────────────
// 右侧文档视图的「下载」按钮。浏览器端 Blob 生成，不依赖后端接口
// （artifact 的数据/列表接口目前都不存在，见设计文档 §6.7）。
//
// 文件名要在这里清洗：`name` 来自消息内容，可能是 `Docs/12_features/x.md`
// 这类含路径的值。直接当 download 属性会让浏览器创建奇怪的相对路径，
// 而 Windows 上还有一组非法字符（`<>:"|?*` 与控制字符）。
// ─────────────────────────────────────────────────────────────

const ILLEGAL_FILENAME_CHARS = /[<>:"|?*\u0000-\u001f]/g;
const FALLBACK_NAME = 'document';

/** 只保留最后一段并替换非法字符；空结果回退到固定名。 */
export function sanitizeFilename(name: string): string {
  const base = (name ?? '').split(/[\\/]/).pop() ?? '';
  const cleaned = base.replace(ILLEGAL_FILENAME_CHARS, '_').trim();
  return cleaned || FALLBACK_NAME;
}

export interface MarkdownDownload {
  blob: Blob;
  filename: string;
}

/**
 * 生成下载载荷。
 *
 * `type` 明确带 charset：否则某些浏览器会按系统编码解释，中文全部变乱码。
 */
export function createMarkdownDownload(name: string, markdown: string): MarkdownDownload {
  const safe = sanitizeFilename(name);
  const filename = /\.md$/i.test(safe) ? safe : `${safe}.md`;

  return {
    blob: new Blob([markdown ?? ''], { type: 'text/markdown;charset=utf-8' }),
    filename,
  };
}

/**
 * 触发浏览器下载。
 *
 * 刻意做成独立函数并在 finally 里 revoke：这个 URL 只要不释放，
 * Blob 就一直留在内存里；文档视图反复打开几次就是几 MB 的泄漏。
 */
export function triggerDownload(download: MarkdownDownload): void {
  if (typeof document === 'undefined' || typeof URL?.createObjectURL !== 'function') {
    return; // 非浏览器环境（单测之外的服务端渲染）静默跳过
  }
  const url = URL.createObjectURL(download.blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = download.filename;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}
