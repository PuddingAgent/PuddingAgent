// ── ContextMenu：消息右键菜单（Portal 渲染）────────────────
import {
  BranchesOutlined,
  CopyOutlined,
  DeleteOutlined,
  EditOutlined,
  MessageOutlined,
  PushpinOutlined,
  ReloadOutlined,
  StarOutlined,
} from '@ant-design/icons';
import React, { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import ReactDOM from 'react-dom';

// ── 菜单样式常量 ─────────────────────────────────────────────
const MENU_MIN_WIDTH = 200;

const menuContainerStyle: React.CSSProperties = {
  position: 'fixed',
  zIndex: 9999,
  background: 'rgb(from var(--soft-white) r g b / 0.95)',
  backdropFilter: 'blur(20px)',
  WebkitBackdropFilter: 'blur(20px)',
  border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
  borderRadius: 8,
  boxShadow: '0 4px 24px rgba(0,0,0,0.06)',
  padding: 4,
  minWidth: MENU_MIN_WIDTH,
  userSelect: 'none',
};

const dividerStyle: React.CSSProperties = {
  borderTop: '1px solid rgb(from var(--earth-brown) r g b / 0.1)',
  margin: '2px 0',
};

function menuItemStyle(disabled: boolean): React.CSSProperties {
  return {
    padding: '8px 12px',
    borderRadius: 6,
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    fontSize: 13,
    color: disabled
      ? 'color-mix(in srgb, var(--text-primary) 40%, transparent)'
      : 'var(--text-primary)',
    cursor: disabled ? 'not-allowed' : 'pointer',
    transition: 'background 0.15s',
    background: 'transparent',
  };
}

function menuItemHoverStyle(disabled: boolean): React.CSSProperties {
  if (disabled) return {};
  return { background: 'rgb(from var(--misty-blue) r g b / 0.4)' };
}

// ── 菜单项配置 ───────────────────────────────────────────────
interface MenuItem {
  icon: React.ReactNode;
  label: string;
  disabled: boolean;
  onClick: () => void;
}

function buildMenuItems(
  turnId: string,
  isUser: boolean,
  callbacks: ContextMenuCallbacks,
): MenuItem[][] {
  return [
    [
      { icon: <CopyOutlined />, label: '复制', disabled: false, onClick: () => callbacks.onCopy(turnId) },
      { icon: <MessageOutlined />, label: '引用回复', disabled: false, onClick: () => callbacks.onQuote(turnId) },
      { icon: <DeleteOutlined />, label: '删除', disabled: false, onClick: () => callbacks.onDelete(turnId) },
    ],
    [
      { icon: <ReloadOutlined />, label: '重新执行', disabled: !isUser, onClick: () => callbacks.onRerun(turnId) },
      { icon: <EditOutlined />, label: '修改指令并重跑', disabled: !isUser, onClick: () => callbacks.onEditAndRerun(turnId) },
      { icon: <StarOutlined />, label: '加入记忆', disabled: false, onClick: () => callbacks.onAddToMemory(turnId) },
      { icon: <PushpinOutlined />, label: '固定为上下文', disabled: false, onClick: () => callbacks.onPinContext(turnId) },
    ],
    [
      { icon: <BranchesOutlined />, label: '从这里创建分支', disabled: false, onClick: () => callbacks.onBranch(turnId) },
    ],
  ];
}

// ── Props ────────────────────────────────────────────────────
export interface ContextMenuCallbacks {
  onCopy: (turnId: string) => void;
  onQuote: (turnId: string) => void;
  onDelete: (turnId: string) => void;
  onRerun: (turnId: string) => void;
  onEditAndRerun: (turnId: string) => void;
  onAddToMemory: (turnId: string) => void;
  onPinContext: (turnId: string) => void;
  onBranch: (turnId: string) => void;
}

export interface ContextMenuState {
  visible: boolean;
  x: number;
  y: number;
  turnId: string;
  role: 'user' | 'assistant';
}

interface ContextMenuProps extends ContextMenuCallbacks {
  state: ContextMenuState;
  onClose: () => void;
}

// ── 边缘翻转检测 Hook ────────────────────────────────────────
function useEdgeFlip(
  visible: boolean,
  x: number,
  y: number,
  menuRef: React.RefObject<HTMLDivElement | null>,
) {
  const [pos, setPos] = useState({ x, y });

  useLayoutEffect(() => {
    if (!visible) return;
    const el = menuRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    let nx = x;
    let ny = y;
    // 右边缘溢出 → 翻转到左侧
    if (x + rect.width > window.innerWidth - 8) {
      nx = Math.max(8, x - rect.width);
    }
    // 下边缘溢出 → 翻转到上方
    if (y + rect.height > window.innerHeight - 8) {
      ny = Math.max(8, y - rect.height);
    }
    // 左/上边界保护
    if (nx < 8) nx = 8;
    if (ny < 8) ny = 8;
    setPos({ x: nx, y: ny });
  }, [visible, x, y, menuRef]);

  return pos;
}

// ── ContextMenu 组件 ─────────────────────────────────────────
const ContextMenu: React.FC<ContextMenuProps> = ({ state, onClose, ...callbacks }) => {
  const { visible, x, y, turnId, role } = state;
  const menuRef = useRef<HTMLDivElement>(null);
  const pos = useEdgeFlip(visible, x, y, menuRef);
  const isUser = role === 'user';
  const itemGroups = buildMenuItems(turnId, isUser, callbacks);

  // 点击外部 / ESC 关闭
  useEffect(() => {
    if (!visible) return;
    const handleMouse = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        onClose();
      }
    };
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('mousedown', handleMouse, true);
    document.addEventListener('keydown', handleKey, true);
    return () => {
      document.removeEventListener('mousedown', handleMouse, true);
      document.removeEventListener('keydown', handleKey, true);
    };
  }, [visible, onClose]);

  if (!visible) return null;

  return ReactDOM.createPortal(
    <div ref={menuRef} style={{ ...menuContainerStyle, left: pos.x, top: pos.y }}>
      {itemGroups.map((group, gi) => (
        <React.Fragment key={gi}>
          {gi > 0 && <div style={dividerStyle} />}
          {group.map((item, ii) => (
            <MenuItemView key={ii} item={item} />
          ))}
        </React.Fragment>
      ))}
    </div>,
    document.body,
  );
};

// ── 单个菜单项（管理 hover 状态）───────────────────────────
const MenuItemView: React.FC<{ item: MenuItem }> = ({ item }) => {
  const [hovered, setHovered] = useState(false);

  const handleClick = useCallback(() => {
    if (!item.disabled) item.onClick();
  }, [item]);

  const currentStyle: React.CSSProperties = {
    ...menuItemStyle(item.disabled),
    ...(hovered ? menuItemHoverStyle(item.disabled) : {}),
  };

  return (
    <div
      style={currentStyle}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onClick={handleClick}
    >
      {item.icon}
      <span>{item.label}</span>
    </div>
  );
};

export default ContextMenu;
