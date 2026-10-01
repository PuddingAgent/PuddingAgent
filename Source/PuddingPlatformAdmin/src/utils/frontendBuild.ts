/**
 * 前端构建信息（版本号 / git 哈希 / 提交时间 / 构建时间）。
 *
 * 由 `config/config.ts` 在构建期通过 umi `define` 注入 `__PUDDING_FRONTEND__`；
 * 未注入时（单测、未重新构建的环境、注入被禁用）回退到显式的 unknown 占位值，
 * 绝不用假版本号冒充真实构建 —— 徽标显示 `v0.0.0 · unknown` 本身就是“没构建”的信号。
 *
 * 版本号真源 = `Source/PuddingPlatformAdmin/package.json` 的 `version`，
 * 规则见 AGENTS.md「版本号约定」（每次修改前端必须递增）。
 */
export interface FrontendBuildInfo {
  /** package.json 的 version */
  version: string;
  /** 完整 commit 哈希（取不到为空串） */
  commit: string;
  /** 短哈希；取不到为 'unknown' */
  commitShort: string;
  /** HEAD 提交时间（ISO，取不到为空串） */
  commitTime: string;
  /** 构建时工作树是否有未提交改动 */
  dirty: boolean;
  /** 构建时间（ISO） */
  builtAt: string;
}

declare const __PUDDING_FRONTEND__: FrontendBuildInfo | string | undefined;

export const FALLBACK_FRONTEND_BUILD: FrontendBuildInfo = {
  version: '0.0.0',
  commit: '',
  commitShort: 'unknown',
  commitTime: '',
  dirty: false,
  builtAt: '',
};

/**
 * 归一化构建期注入值。构建器可能给到对象字面量，也可能给到 JSON 字符串
 * （umi/mako 会再 stringify 一次），两者都必须正确解析 —— 否则 spread 字符串
 * 会产出字符键，徽标静默退化为 v0.0.0 · unknown。
 */
const normalizeInjected = (injected: unknown): FrontendBuildInfo | null => {
  let value = injected;
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value);
    } catch {
      return null;
    }
  }
  if (!value || typeof value !== 'object') return null;
  const candidate = value as Partial<FrontendBuildInfo>;
  if (typeof candidate.version !== 'string') return null;
  return { ...FALLBACK_FRONTEND_BUILD, ...candidate };
};

/** 读取构建期注入的信息；缺失/形态异常时退回占位值（不抛错）。 */
export const getFrontendBuildInfo = (): FrontendBuildInfo => {
  if (typeof __PUDDING_FRONTEND__ === 'undefined') {
    return FALLBACK_FRONTEND_BUILD;
  }
  return normalizeInjected(__PUDDING_FRONTEND__) ?? FALLBACK_FRONTEND_BUILD;
};

/** ISO 时间 → 本地 `YYYY-MM-DD HH:mm`；空值或非法值返回空串（不显示假时间）。 */
export const formatBuildTime = (iso: string): string => {
  if (!iso) return '';
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
  const pad = (value: number) => String(value).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(
    date.getDate(),
  )} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
};

/**
 * 徽标短文本：`v6.1.0 · a1b2c3d · 2026-10-01 22:57`。
 * 时间优先用构建时间，缺失时退回提交时间；`+dirty` 表示构建时工作树不干净。
 */
export const formatFrontendVersionLabel = (info: FrontendBuildInfo): string => {
  const tag = `v${info.version}${info.dirty ? '+dirty' : ''}`;
  const time = formatBuildTime(info.builtAt) || formatBuildTime(info.commitTime);
  return [tag, info.commitShort, time].filter(Boolean).join(' · ');
};

/** 悬停详情（多行）：版本、提交、提交时间、构建时间，以及脏工作树说明。 */
export const formatFrontendVersionDetail = (info: FrontendBuildInfo): string => {
  const lines = [
    `前端版本 v${info.version}${
      info.dirty ? '（+dirty：构建时工作树有未提交改动）' : ''
    }`,
    `提交 ${info.commit || 'unknown'}`,
  ];
  const commitTime = formatBuildTime(info.commitTime);
  if (commitTime) lines.push(`提交时间 ${commitTime}`);
  const builtAt = formatBuildTime(info.builtAt);
  if (builtAt) lines.push(`构建时间 ${builtAt}`);
  return lines.join('\n');
};
