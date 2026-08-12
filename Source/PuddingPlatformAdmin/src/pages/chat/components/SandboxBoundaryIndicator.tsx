// ── P2#10 SandboxBoundaryIndicator：Sandbox 边界可视化指示器 ──
// 对齐 Codex /status 与 Cursor run-modes 的沙箱边界展示：
// - workspace 根（可写根目录）；
// - 保护路径（只读）；
// - 网络模式（none / allowlist / full）。
// 点击 chip 弹出 Popover 查看完整边界详情。
import {
  GlobalOutlined,
  LockOutlined,
  SafetyOutlined,
} from '@ant-design/icons';
import { Popover, Tag, Tooltip, Typography } from 'antd';
import React from 'react';
import {
  getSandboxNetworkModeColor,
  SANDBOX_NETWORK_MODE_DESCRIPTIONS,
  SANDBOX_NETWORK_MODE_LABELS,
  type SandboxBoundaryInfo,
  type SandboxNetworkMode,
} from '../sandbox/sandboxBoundary';

interface SandboxBoundaryIndicatorProps {
  /** Sandbox 边界信息；未提供时显示「未连接」占位 */
  boundary?: SandboxBoundaryInfo | null;
  /** 是否禁用（未选择工作空间等） */
  disabled?: boolean;
  /** 允许用户调整网络模式（当前为展示，回调可后续接后端策略） */
  onNetworkModeChange?: (mode: SandboxNetworkMode) => void;
}

const chipBase: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 5,
  height: 34,
  padding: '0 9px',
  border: 'none',
  borderRadius: 17,
  fontSize: 12,
  cursor: 'pointer',
  background: 'color-mix(in srgb, var(--pudding-chat-border) 22%, transparent)',
  color: 'var(--pudding-chat-text-muted)',
  transition: 'background 140ms ease, color 140ms ease',
};

const networkIcon: Record<SandboxNetworkMode, React.ReactNode> = {
  none: <LockOutlined />,
  allowlist: <GlobalOutlined />,
  full: <GlobalOutlined />,
};

const SandboxBoundaryIndicator: React.FC<SandboxBoundaryIndicatorProps> = ({
  boundary,
  disabled = false,
  onNetworkModeChange,
}) => {
  const root = boundary?.workspaceRoot;
  const shortRoot = React.useMemo(() => {
    if (!root) return '未选择工作空间';
    const segments = root.split('/').filter(Boolean);
    return segments.length > 0 ? segments[segments.length - 1] : root;
  }, [root]);

  const chip = (
    <span
      style={{
        ...chipBase,
        opacity: disabled ? 0.45 : 1,
        cursor: disabled ? 'not-allowed' : 'pointer',
      }}
      data-testid="sandbox-boundary-chip"
      role="button"
      aria-label="查看 Sandbox 边界"
    >
      <SafetyOutlined />
      <span>{shortRoot}</span>
      {boundary && (
        <Tag
          color={getSandboxNetworkModeColor(boundary.networkMode)}
          style={{ marginInlineEnd: 0, fontSize: 10, lineHeight: '16px' }}
          data-testid="sandbox-network-mode"
        >
          {SANDBOX_NETWORK_MODE_LABELS[boundary.networkMode]}
        </Tag>
      )}
    </span>
  );

  if (disabled || !boundary) {
    return (
      <Tooltip title="Sandbox 边界（workspace 根 / 保护路径 / 网络模式）">
        {chip}
      </Tooltip>
    );
  }

  return (
    <Popover
      trigger="click"
      placement="topRight"
      content={
        <div
          style={{ width: 'min(360px, calc(100vw - 48px))' }}
          data-testid="sandbox-boundary-popover"
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              gap: 8,
              marginBottom: 8,
            }}
          >
            <Typography.Text strong style={{ fontSize: 13 }}>
              Sandbox 边界
            </Typography.Text>
            <Tag
              color={getSandboxNetworkModeColor(boundary.networkMode)}
              style={{ marginInlineEnd: 0 }}
              data-testid="sandbox-network-mode-tag"
            >
              {SANDBOX_NETWORK_MODE_LABELS[boundary.networkMode]}
            </Tag>
          </div>

          <div style={{ marginBottom: 8 }}>
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>
              Workspace 根（可写）
            </Typography.Text>
            <div
              style={{
                fontSize: 12,
                fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
                wordBreak: 'break-all',
                padding: '4px 6px',
                borderRadius: 6,
                background:
                  'color-mix(in srgb, var(--pudding-chat-border) 18%, transparent)',
              }}
              data-testid="sandbox-workspace-root"
            >
              {boundary.workspaceRoot}
            </div>
          </div>

          <div style={{ marginBottom: 8 }}>
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>
              保护路径（只读）
            </Typography.Text>
            <div
              style={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 4,
                marginTop: 4,
              }}
              data-testid="sandbox-protected-paths"
            >
              {boundary.protectedPaths.map((path) => (
                <Tag key={path} style={{ marginInlineEnd: 0, fontSize: 10 }}>
                  {path}
                </Tag>
              ))}
            </div>
          </div>

          <div>
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>
              网络模式
            </Typography.Text>
            <div
              style={{ fontSize: 11, lineHeight: 1.6, marginTop: 2 }}
              data-testid="sandbox-network-description"
            >
              {SANDBOX_NETWORK_MODE_DESCRIPTIONS[boundary.networkMode]}
            </div>
          </div>

          {onNetworkModeChange && (
            <div
              style={{
                display: 'flex',
                gap: 4,
                marginTop: 10,
                borderTop:
                  '1px solid color-mix(in srgb, var(--pudding-chat-border) 60%, transparent)',
                paddingTop: 8,
              }}
            >
              {(
                ['none', 'allowlist', 'full'] as SandboxNetworkMode[]
              ).map((mode) => (
                <button
                  key={mode}
                  type="button"
                  style={{
                    flex: 1,
                    height: 26,
                    border:
                      boundary.networkMode === mode
                        ? '1px solid var(--pudding-chat-accent, #8b5cf6)'
                        : '1px solid color-mix(in srgb, var(--pudding-chat-border) 70%, transparent)',
                    borderRadius: 6,
                    background:
                      boundary.networkMode === mode
                        ? 'color-mix(in srgb, var(--pudding-chat-accent, #8b5cf6) 10%, transparent)'
                        : 'transparent',
                    fontSize: 11,
                    cursor: 'pointer',
                    color: 'var(--pudding-chat-text)',
                  }}
                  onClick={() => onNetworkModeChange(mode)}
                  data-testid={`sandbox-network-option-${mode}`}
                >
                  {SANDBOX_NETWORK_MODE_LABELS[mode]}
                </button>
              ))}
            </div>
          )}
        </div>
      }
    >
      {chip}
    </Popover>
  );
};

export default React.memo(SandboxBoundaryIndicator);
