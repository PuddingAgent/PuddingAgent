// ── P2#10 Sandbox 边界可视化（纯逻辑 + 常量）──────────────────
// 对齐 Codex /status 与 Cursor run-modes：
// - workspace 根：Agent 可写根目录；
// - 保护路径：强制只读（.git、.vscode、.cursorignore、.env 等）；
// - 网络模式：none（无网络）/ allowlist（白名单）/ full（全网络）。
// 前端为展示层：默认值来自当前 workspaceId 推导，后续后端 sandbox 状态
// 端点就绪后可整体替换 boundary 数据源。
export type SandboxNetworkMode = 'none' | 'allowlist' | 'full';

export const SANDBOX_NETWORK_MODES: SandboxNetworkMode[] = [
  'none',
  'allowlist',
  'full',
];

export const SANDBOX_NETWORK_MODE_LABELS: Record<SandboxNetworkMode, string> = {
  none: '无网络',
  allowlist: '白名单',
  full: '全网络',
};

export const SANDBOX_NETWORK_MODE_DESCRIPTIONS: Record<
  SandboxNetworkMode,
  string
> = {
  none: '禁止所有外联；仅本地进程间通信',
  allowlist: '仅允许白名单域名与本地回环',
  full: '允许全部网络访问',
};

/** 默认保护路径（对齐 Cursor/Codex 常见 writable-root 只读路径） */
export const DEFAULT_SANDBOX_PROTECTED_PATHS = [
  '.git',
  '.vscode',
  '.cursorignore',
  '.env',
  '.agents',
  '.codex',
  'node_modules',
];

export interface SandboxBoundaryInfo {
  /** workspace 可写根目录 */
  workspaceRoot: string;
  /** 只读保护路径（相对 workspaceRoot） */
  protectedPaths: string[];
  /** 网络模式 */
  networkMode: SandboxNetworkMode;
  /** 快照时间 */
  updatedAt: number;
  /** 沙箱环境变量注入示例（如 PUDDING_SANDBOX=1），仅为可视化展示 */
  envVars?: Record<string, string>;
}

/** 从 workspaceId 推导默认 boundary（后端端点就绪前使用） */
export const createDefaultSandboxBoundary = (
  workspaceId?: string | null,
  networkMode: SandboxNetworkMode = 'allowlist',
): SandboxBoundaryInfo => {
  const root = workspaceId
    ? `/workspaces/${workspaceId}`
    : '/workspaces/<unselected>';
  return {
    workspaceRoot: root,
    protectedPaths: [...DEFAULT_SANDBOX_PROTECTED_PATHS],
    networkMode,
    updatedAt: Date.now(),
    envVars: {
      PUDDING_SANDBOX: '1',
      PUDDING_WORKSPACE_ROOT: root,
    },
  };
};

/** 判断某个相对路径是否落在保护路径下（前缀匹配） */
export const isProtectedPath = (
  relativePath: string,
  protectedPaths: readonly string[],
): boolean => {
  const normalized = relativePath.replace(/\\/g, '/').replace(/^\.?\//, '');
  if (!normalized) return false;
  return protectedPaths.some((protectedPath) => {
    const target = protectedPath.replace(/\\/g, '/').replace(/^\.?\//, '');
    return (
      normalized === target ||
      normalized.startsWith(`${target}/`) ||
      normalized.startsWith(`${target}\\`)
    );
  });
};

/** 网络模式 → AntD Tag 颜色 */
export const getSandboxNetworkModeColor = (
  mode: SandboxNetworkMode,
): string => {
  switch (mode) {
    case 'none':
      return 'default';
    case 'allowlist':
      return 'processing';
    case 'full':
      return 'warning';
    default:
      return 'default';
  }
};
