// ── P2#10 sandboxBoundary 纯逻辑测试 ─────────────────
import {
  createDefaultSandboxBoundary,
  DEFAULT_SANDBOX_PROTECTED_PATHS,
  getSandboxNetworkModeColor,
  isProtectedPath,
  SANDBOX_NETWORK_MODE_DESCRIPTIONS,
  SANDBOX_NETWORK_MODE_LABELS,
} from './sandboxBoundary';

describe('sandboxBoundary', () => {
  describe('createDefaultSandboxBoundary', () => {
    it('derives workspace root from workspaceId', () => {
      const boundary = createDefaultSandboxBoundary('ws-42');
      expect(boundary.workspaceRoot).toBe('/workspaces/ws-42');
      expect(boundary.networkMode).toBe('allowlist');
      expect(boundary.protectedPaths).toEqual(DEFAULT_SANDBOX_PROTECTED_PATHS);
      expect(boundary.envVars?.PUDDING_SANDBOX).toBe('1');
    });

    it('handles null workspaceId with placeholder root', () => {
      const boundary = createDefaultSandboxBoundary(null);
      expect(boundary.workspaceRoot).toBe('/workspaces/<unselected>');
    });

    it('honors explicit network mode', () => {
      expect(createDefaultSandboxBoundary('ws-1', 'none').networkMode).toBe(
        'none',
      );
      expect(createDefaultSandboxBoundary('ws-1', 'full').networkMode).toBe(
        'full',
      );
    });
  });

  describe('isProtectedPath', () => {
    const protectedPaths = ['.git', '.env', 'node_modules'];

    it('matches exact paths', () => {
      expect(isProtectedPath('.git', protectedPaths)).toBe(true);
      expect(isProtectedPath('.env', protectedPaths)).toBe(true);
    });

    it('matches nested paths under protected roots', () => {
      expect(isProtectedPath('.git/config', protectedPaths)).toBe(true);
      expect(isProtectedPath('node_modules/pkg/index.js', protectedPaths)).toBe(
        true,
      );
    });

    it('normalizes leading slashes and backslashes', () => {
      expect(isProtectedPath('/.git/config', protectedPaths)).toBe(true);
      expect(isProtectedPath('.git\\config', protectedPaths)).toBe(true);
      expect(isProtectedPath('src\\.env.local', protectedPaths)).toBe(false);
    });

    it('rejects unprotected and empty paths', () => {
      expect(isProtectedPath('src/main.ts', protectedPaths)).toBe(false);
      expect(isProtectedPath('', protectedPaths)).toBe(false);
      expect(isProtectedPath('.gitignore', protectedPaths)).toBe(false);
    });
  });

  describe('labels and colors', () => {
    it('provides labels for all modes', () => {
      expect(SANDBOX_NETWORK_MODE_LABELS.none).toBe('无网络');
      expect(SANDBOX_NETWORK_MODE_LABELS.allowlist).toBe('白名单');
      expect(SANDBOX_NETWORK_MODE_LABELS.full).toBe('全网络');
    });

    it('provides descriptions for all modes', () => {
      for (const mode of ['none', 'allowlist', 'full'] as const) {
        expect(SANDBOX_NETWORK_MODE_DESCRIPTIONS[mode].length).toBeGreaterThan(
          0,
        );
      }
    });

    it('maps modes to tag colors', () => {
      expect(getSandboxNetworkModeColor('none')).toBe('default');
      expect(getSandboxNetworkModeColor('allowlist')).toBe('processing');
      expect(getSandboxNetworkModeColor('full')).toBe('warning');
    });
  });
});
