import * as React from 'react';
import type {
  AgentAvatarRenderState,
  AgentAvatarStatus,
} from '../hooks/agentAvatarRuntime';

interface AgentAvatarRuntimeViewProps {
  renderState: AgentAvatarRenderState;
  agentName: string;
  statusDetail?: string;
  onVisibilityChange?: (visible: boolean) => void;
}

const statusText: Record<AgentAvatarStatus, string> = {
  idle: '待命',
  listening: '正在听',
  seeing: '正在看',
  thinking: '正在思考',
  speaking: '正在说话',
  tool: '正在使用工具',
  error: '异常',
  sleeping: '休眠',
};

const shellStyle: React.CSSProperties = {
  width: 176,
  minHeight: 220,
  display: 'flex',
  flexDirection: 'column',
  alignItems: 'center',
  gap: 8,
  padding: 10,
  border: '1px solid var(--pudding-chat-border)',
  borderRadius: 8,
  background: 'var(--pudding-chat-surface)',
  color: 'var(--pudding-chat-text)',
};

const portraitStyle: React.CSSProperties = {
  width: 112,
  height: 124,
  flexShrink: 0,
  borderRadius: 8,
  backgroundColor: 'var(--pudding-chat-surface-muted)',
  backgroundRepeat: 'no-repeat',
  imageRendering: 'pixelated',
};

const metaStyle: React.CSSProperties = {
  width: '100%',
  display: 'flex',
  flexDirection: 'column',
  alignItems: 'center',
  gap: 2,
  minWidth: 0,
};

const agentNameStyle: React.CSSProperties = {
  maxWidth: '100%',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
  fontSize: 13,
  fontWeight: 600,
};

const statusStyle: React.CSSProperties = {
  fontSize: 12,
  color: 'var(--pudding-chat-text-subtle)',
};

const detailStyle: React.CSSProperties = {
  maxWidth: '100%',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
  fontSize: 11,
  color: 'var(--pudding-chat-text-subtle)',
};

const closeStyle: React.CSSProperties = {
  height: 24,
  padding: '0 8px',
  border: '1px solid var(--pudding-chat-border)',
  borderRadius: 6,
  background: 'transparent',
  color: 'var(--pudding-chat-text-subtle)',
  fontSize: 12,
  cursor: 'pointer',
};

const spriteFrameWidth = 192;
const spriteFrameHeight = 208;

function getSpriteBackgroundPosition(
  renderState: AgentAvatarRenderState,
): string | undefined {
  if (renderState.spriteRow === undefined) return undefined;
  return `0px -${renderState.spriteRow * spriteFrameHeight}px`;
}

const AgentAvatarRuntimeView: React.FC<AgentAvatarRuntimeViewProps> = ({
  renderState,
  agentName,
  statusDetail,
  onVisibilityChange,
}) => {
  if (!renderState.visible) return null;

  const label = statusText[renderState.status];
  const spriteStyle: React.CSSProperties = {
    ...portraitStyle,
    backgroundImage: renderState.spriteSheetUrl
      ? `url(${renderState.spriteSheetUrl})`
      : undefined,
    backgroundPosition: getSpriteBackgroundPosition(renderState),
    backgroundSize: `${spriteFrameWidth}px auto`,
  };

  return (
    <aside style={shellStyle} aria-label={`${agentName} 虚拟形象`}>
      {renderState.runtimeKind === 'static' && renderState.staticAvatarUrl ? (
        <img
          src={renderState.staticAvatarUrl}
          alt={renderState.ariaLabel}
          style={portraitStyle}
          data-runtime-kind={renderState.runtimeKind}
          data-avatar-status={renderState.status}
        />
      ) : (
        <div
          role="img"
          aria-label={renderState.ariaLabel}
          style={spriteStyle}
          data-runtime-kind={renderState.runtimeKind}
          data-avatar-status={renderState.status}
          data-sprite-row={renderState.spriteRow}
          data-sprite-frame-count={renderState.spriteFrameCount}
        />
      )}
      <div style={metaStyle}>
        <div style={agentNameStyle}>{agentName}</div>
        <div style={statusStyle}>{label}</div>
        {statusDetail ? <div style={detailStyle}>{statusDetail}</div> : null}
      </div>
      {onVisibilityChange ? (
        <button
          type="button"
          style={closeStyle}
          onClick={() => onVisibilityChange(false)}
        >
          隐藏虚拟形象
        </button>
      ) : null}
    </aside>
  );
};

export default AgentAvatarRuntimeView;
