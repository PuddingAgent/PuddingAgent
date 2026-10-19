// ── 文档引用识别 ───────────────────────────────────────────────
// 把消息里的 `Docs/xx.md` 变成可点击的文档卡片。
//
// 识别规则刻意收得很窄（防误伤）：
//   1. 只认**行内 code**（单反引号包住的内容），不认裸文本；
//   2. 只认 `.md / .markdown / .pdf / .html / .htm`；
//   3. 跳过围栏代码块——代码示例里的路径是"示例"，不是"引用"，
//      变成蓝色链接只会让代码更难读。
//
// 不这么做的话，每个讲路径的段落都会长出一堆链接。
// ─────────────────────────────────────────────────────────────

export type DocumentRefKind = 'workspace' | 'artifact';

export interface DocumentRef {
  kind: DocumentRefKind;
  /** 行内 code 的原始内容。 */
  raw: string;
  /** workspace：工作区相对路径；artifact：artifactId。 */
  target: string;
  /** 小写扩展名（artifact 为空）。 */
  ext: string;
}

const FENCE_PATTERN = /^\s*(```|~~~)/;
const INLINE_CODE_PATTERN = /`([^`\n]+)`/g;
const DOC_EXT_PATTERN = /\.(md|markdown|pdf|html?)$/i;
const ARTIFACT_PATTERN = /^artifact:\/\/([A-Za-z0-9._-]+)$/;
/** 路径里不允许空白与常见标点：宁可漏认，也不要把一句话当路径。 */
const INVALID_PATH_CHARS = /[\s，。、；：！？"'<>|*?]/;
const MAX_PATH_LENGTH = 260;

function toRef(raw: string): DocumentRef | null {
  const value = raw.trim();
  if (!value) return null;

  const artifact = ARTIFACT_PATTERN.exec(value);
  if (artifact) {
    return { kind: 'artifact', raw: value, target: artifact[1], ext: '' };
  }

  if (!DOC_EXT_PATTERN.test(value)) return null;
  // 路径里不允许空白与常见标点：宁可漏认，也不要把一句话当路径。
  // 刻意**允许中文**：本仓库 Docs 下就有中文文件名，早期用 ASCII-only 规则
  // 会把 `Docs/…/方案.md` 判掉（实测踩到）。
  if (INVALID_PATH_CHARS.test(value) || value.length > MAX_PATH_LENGTH) return null;
  // 纯文件名（如 `README.md`）也算合法引用。
  const ext = DOC_EXT_PATTERN.exec(value)?.[1]?.toLowerCase() ?? '';
  return { kind: 'workspace', raw: value, target: value, ext };
}

/**
 * 从 markdown 里提取文档引用。按出现顺序返回，自动去重。
 */
export function extractDocumentRefs(markdown: string): DocumentRef[] {
  const refs: DocumentRef[] = [];
  const seen = new Set<string>();
  let inFence = false;

  for (const line of markdown.split('\n')) {
    if (FENCE_PATTERN.test(line)) {
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;

    INLINE_CODE_PATTERN.lastIndex = 0;
    let match: RegExpExecArray | null;
    while ((match = INLINE_CODE_PATTERN.exec(line)) !== null) {
      const ref = toRef(match[1]);
      if (!ref) continue;
      const key = `${ref.kind}:${ref.target}`;
      if (seen.has(key)) continue;
      seen.add(key);
      refs.push(ref);
    }
  }

  return refs;
}

/** 展示用短名：路径取最后一段。 */
export function displayName(ref: DocumentRef): string {
  if (ref.kind === 'artifact') return ref.target;
  const normalized = ref.target.replace(/\\/g, '/');
  const index = normalized.lastIndexOf('/');
  return index >= 0 ? normalized.slice(index + 1) : normalized;
}
